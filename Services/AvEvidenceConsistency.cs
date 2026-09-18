using System.Globalization;
using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Single, shared rule deciding whether sampled packet-level evidence agrees
/// with the stream-level metadata of the same audio/video pair. Used by both
/// <see cref="AvRepairClassifier"/> and <see cref="AvRepairPlanner"/> so the
/// rule is defined once.
///
/// Why this exists: <see cref="PacketTimelineAnalyzer"/> only samples two
/// small windows, and the tail window is anchored on a seek target, not on
/// each stream's real end (ffprobe places the end of a "%+N" interval N
/// seconds after the first packet read following the seek). When one stream
/// ends earlier than the container duration, the other stream's real end is
/// never sampled, so <see cref="AvPacketEvidence.EndOffsetSeconds"/> can be far
/// smaller than the real stream-level end difference. Packet evidence is
/// therefore a confirmation of the metadata, never an authority allowed to
/// reduce it.
/// </summary>
public static class AvEvidenceConsistency
{
    /// <summary>
    /// Largest accepted disagreement, in seconds, between a packet-measured
    /// start offset and the metadata start offset. Both come from the first
    /// timestamps of the same streams, so they should agree closely. Equals
    /// the smallest start offset the classifier treats as meaningful
    /// (<see cref="AvRepairClassifier.OffsetSignalFloorSeconds"/>): a smaller
    /// disagreement is indistinguishable from measurement noise, a larger one
    /// is a real conflict.
    /// </summary>
    public const double StartToleranceSeconds = AvRepairClassifier.OffsetSignalFloorSeconds;

    /// <summary>
    /// Largest accepted disagreement, in seconds, between a packet-measured
    /// end offset / drift and the metadata end offset / duration delta.
    /// Equals the smallest drift the classifier treats as meaningful
    /// (<see cref="AvRepairClassifier.DriftSignalFloorSeconds"/>). The sampled
    /// window legitimately misses the last few packets (interval cut-off,
    /// packet durations, B-frame reordering; measured around 0.2 s on
    /// synthetic files), so a smaller gap is noise. A gap above this floor
    /// means the two sources disagree about whether, or by how much, the
    /// stream ends differ, and neither may be used to auto-repair.
    /// </summary>
    public const double EndToleranceSeconds = AvRepairClassifier.DriftSignalFloorSeconds;

    // Absorbs floating-point noise so a difference of exactly the tolerance is accepted.
    private const double Epsilon = 1e-9;

    /// <summary>Outcome of <see cref="Reconcile"/>.</summary>
    /// <param name="Conflict">Description of the disagreement, or <see langword="null"/> when consistent.</param>
    /// <param name="MetadataDriftSeconds">
    /// The metadata-level end-minus-start offset evolution that agrees with the
    /// packet evidence (or the plain duration delta when there is none). The
    /// only metadata magnitude the classifier and planner may act on.
    /// </param>
    public readonly record struct Reconciliation(string? Conflict, double MetadataDriftSeconds)
    {
        public bool IsConsistent => Conflict is null;
    }

    /// <summary>
    /// Compares the diagnosis' metadata-level start offset, end offset and
    /// drift with its packet evidence. With no packet evidence there is
    /// nothing to reconcile and the metadata duration delta is returned.
    ///
    /// Stream "duration" metadata has no single meaning across muxers: some
    /// (MP4, most sources) report a length, so end = start + duration; others
    /// (for example an ffmpeg-written Matroska DURATION tag on a stream with a
    /// start offset) report the end timestamp, so end = duration. The two
    /// readings only differ when the start offset is meaningful; then either
    /// reading may agree with the packets. When the file starts in sync (the
    /// case of a bounded end pad/trim) there is a single reading.
    /// </summary>
    public static Reconciliation Reconcile(AvRepairDiagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        var evidence = diagnosis.PacketEvidence;
        if (evidence is null)
        {
            return new Reconciliation(null, diagnosis.DurationDeltaSeconds);
        }

        var startConflict = Compare("start offset", diagnosis.StartOffsetSeconds, evidence.StartOffsetSeconds, StartToleranceSeconds);
        if (startConflict is not null)
        {
            return new Reconciliation(startConflict, diagnosis.DurationDeltaSeconds);
        }

        // Reading 1: duration is a length. Reading 2 (only when a start offset makes it differ): duration is an end timestamp.
        var readings = new List<(double End, double Drift)>
        {
            (diagnosis.EndOffsetSeconds, diagnosis.DurationDeltaSeconds)
        };
        if (Math.Abs(diagnosis.StartOffsetSeconds) >= AvRepairClassifier.OffsetSignalFloorSeconds)
        {
            readings.Add((diagnosis.DurationDeltaSeconds, diagnosis.DurationDeltaSeconds - diagnosis.StartOffsetSeconds));
        }

        string? firstConflict = null;
        foreach (var (end, drift) in readings)
        {
            var conflict = Compare("end offset", end, evidence.EndOffsetSeconds, EndToleranceSeconds)
                ?? Compare("drift", drift, evidence.Drift, EndToleranceSeconds);
            if (conflict is null)
            {
                return new Reconciliation(null, drift);
            }

            firstConflict ??= conflict;
        }

        return new Reconciliation(firstConflict, diagnosis.DurationDeltaSeconds);
    }

    private static string? Compare(string quantity, double metadata, double packet, double tolerance)
    {
        if (!double.IsFinite(metadata) || !double.IsFinite(packet))
        {
            return $"Packet evidence or metadata {quantity} is not a finite number.";
        }

        if (Math.Abs(metadata - packet) <= tolerance + Epsilon)
        {
            return null;
        }

        var direction = metadata * packet < 0 ? " and in the opposite direction" : string.Empty;
        return $"Packet evidence conflicts with stream metadata: {quantity} is {Format(metadata)}s in metadata " +
            $"but {Format(packet)}s in the sampled packets{direction} (tolerance {Format(tolerance)}s). " +
            "Sampled packets cannot lower or replace stream-level metadata; manual review required.";
    }

    private static string Format(double value) =>
        value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
}
