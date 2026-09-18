using System.Globalization;
using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Classifies audio/video timeline anomalies into a small, deterministic set
/// of explainable categories before any repair is planned. This class never
/// performs I/O, never runs ffmpeg, and never decides to repair anything by
/// itself; see <see cref="AvRepairPlanner"/> for policy-gated planning.
///
/// Design note: with only two independent packet samples (start window, end
/// window of the file) it is not possible to prove a timeline is *uniformly*
/// drifting versus *bounded and concentrated at one edge*. This classifier
/// resolves that ambiguity using magnitude: a packet-confirmed offset
/// evolution that stays within <see cref="PluginConfiguration.MaxAutoRepairDurationDeltaSeconds"/>
/// while the file starts in sync is treated as a single-sided end mismatch
/// (safely pad/trim-able); a larger or start-offset-compounded evolution is
/// treated as a genuine progressive drift (only correctable, if at all, with
/// an opt-in proportional audio time-stretch).
/// </summary>
public static class AvRepairClassifier
{
    /// <summary>
    /// Hard, non-configurable safety ceiling. Any offset, duration delta or
    /// packet-measured drift beyond this magnitude is always
    /// <see cref="AvRepairClassification.UnsafeToAutoRepair"/>, regardless of
    /// confidence or configuration. A large silent intro/outro or an
    /// intentionally shorter track must never be auto-repaired.
    /// </summary>
    public const double GrossMismatchThresholdSeconds = 30.0;

    /// <summary>Minimum |offset| that is considered a meaningful signal at all.</summary>
    public const double OffsetSignalFloorSeconds = AudioVideoTimelineAnalyzer.StartOffsetThresholdSeconds;

    /// <summary>Minimum |drift|/|duration delta| that is considered a meaningful signal at all.</summary>
    public const double DriftSignalFloorSeconds = AudioVideoTimelineAnalyzer.DurationMismatchThresholdSeconds;

    public static AvRepairDiagnosis Classify(
        MediaStreamInfo video,
        MediaStreamInfo audio,
        AvPacketEvidence? packetEvidence,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(configuration);

        var diagnosis = new AvRepairDiagnosis
        {
            VideoStreamIndex = video.Index,
            AudioStreamIndex = audio.Index,
            PacketEvidence = packetEvidence,
            AudioLanguage = audio.Language,
            AudioTitle = audio.Title,
            AudioIsDefault = audio.IsDefault,
            AudioIsForced = audio.IsForced,
            AudioIsCommentary = audio.IsCommentary,
            AudioIsDescription = audio.IsAudioDescription
        };

        if (video.StartTimeSeconds is not { } videoStart || audio.StartTimeSeconds is not { } audioStart
            || video.DurationSeconds is not { } videoDuration || audio.DurationSeconds is not { } audioDuration
            || !double.IsFinite(videoStart) || !double.IsFinite(audioStart)
            || !double.IsFinite(videoDuration) || !double.IsFinite(audioDuration))
        {
            return Finalize(diagnosis, AvRepairClassification.Ambiguous, 0.0,
                "Timeline data is incomplete or invalid; no classification can be made.");
        }

        var startOffset = audioStart - videoStart;
        var durationDelta = audioDuration - videoDuration;
        var endOffset = startOffset + durationDelta;
        diagnosis.StartOffsetSeconds = startOffset;
        diagnosis.DurationDeltaSeconds = durationDelta;
        diagnosis.EndOffsetSeconds = endOffset;
        diagnosis.VideoDurationSeconds = videoDuration;
        diagnosis.AudioDurationSeconds = audioDuration;
        diagnosis.DurationRatio = videoDuration > 0 && double.IsFinite(audioDuration / videoDuration)
            ? audioDuration / videoDuration
            : null;

        var confirmed = packetEvidence is not null && !packetEvidence.HasDiscontinuity;
        var offsetForGate = packetEvidence?.StartOffsetSeconds ?? startOffset;
        var driftForGate = packetEvidence?.Drift ?? durationDelta;

        // Step 0a: hard safety ceiling, evaluated across every available signal.
        var grossMagnitude = Max(
            Math.Abs(startOffset), Math.Abs(durationDelta), Math.Abs(endOffset),
            Math.Abs(offsetForGate), Math.Abs(driftForGate));

        if (grossMagnitude > GrossMismatchThresholdSeconds)
        {
            return Finalize(diagnosis, AvRepairClassification.UnsafeToAutoRepair, 1.0,
                $"Magnitude {Format(grossMagnitude)}s exceeds the {Format(GrossMismatchThresholdSeconds)}s " +
                "safety ceiling; this is never auto-repaired, regardless of pattern.");
        }

        // Step 0b: a discontinuous packet timeline invalidates any evolution reading.
        if (packetEvidence is { HasDiscontinuity: true })
        {
            return Finalize(diagnosis, AvRepairClassification.UnsafeToAutoRepair, 0.4,
                "Packet timestamps show a discontinuity within the sampled window; " +
                "the offset pattern cannot be trusted for an automatic repair.");
        }

        // Step 1: no meaningful signal at all.
        if (Math.Abs(startOffset) < OffsetSignalFloorSeconds
            && Math.Abs(durationDelta) < DriftSignalFloorSeconds
            && Math.Abs(offsetForGate) < OffsetSignalFloorSeconds
            && Math.Abs(driftForGate) < DriftSignalFloorSeconds)
        {
            return Finalize(diagnosis, AvRepairClassification.None, 1.0,
                "No significant audio/video timeline anomaly was measured.");
        }

        // Step 2: a stable offset with negligible evolution -> constant offset.
        if (Math.Abs(offsetForGate) >= OffsetSignalFloorSeconds && Math.Abs(driftForGate) < DriftSignalFloorSeconds)
        {
            return confirmed
                ? Finalize(diagnosis, AvRepairClassification.ConstantOffset, 0.92,
                    $"Packet timestamps confirm a stable {Format(offsetForGate)}s audio/video offset " +
                    "from the start to the end of the file.")
                : Finalize(diagnosis, AvRepairClassification.TimelineMetadataOnly, 0.5,
                    $"Container metadata reports a {Format(startOffset)}s start offset with no matching " +
                    "duration change, but no independent packet evidence confirms it is stable. " +
                    "Packet-based confirmation is required before automatic repair.");
        }

        // Step 3: the offset evolves. Distinguish a bounded, single-sided end
        // mismatch from a genuine proportional drift by magnitude and by
        // whether the file starts in sync.
        if (Math.Abs(driftForGate) >= DriftSignalFloorSeconds)
        {
            if (!confirmed)
            {
                if (Math.Abs(durationDelta) <= configuration.MaxAutoRepairDurationDeltaSeconds)
                {
                    var classification = durationDelta < 0
                        ? AvRepairClassification.AudioEndsEarly
                        : AvRepairClassification.AudioEndsLate;

                    return Finalize(diagnosis, classification, 0.55,
                        $"Container metadata shows a bounded {Format(durationDelta)}s audio/video duration " +
                        "delta with no independent packet evidence; treated as a single-sided end mismatch " +
                        "pending packet confirmation.");
                }

                return Finalize(diagnosis, AvRepairClassification.DurationMismatch, 0.45,
                    $"Container metadata shows a {Format(durationDelta)}s audio/video duration delta with " +
                    "no independent packet evidence; magnitude requires manual review.");
            }

            var startsInSync = Math.Abs(offsetForGate) < OffsetSignalFloorSeconds;
            var isBounded = Math.Abs(driftForGate) <= configuration.MaxAutoRepairDurationDeltaSeconds;

            if (startsInSync && isBounded)
            {
                var classification = driftForGate < 0
                    ? AvRepairClassification.AudioEndsEarly
                    : AvRepairClassification.AudioEndsLate;

                return Finalize(diagnosis, classification, 0.85,
                    $"Packet timestamps confirm the file starts in sync and the audio track " +
                    $"{(driftForGate < 0 ? "ends" : "runs")} {Format(Math.Abs(driftForGate))}s " +
                    $"{(driftForGate < 0 ? "early" : "late")}, within the bounded single-sided repair range.");
            }

            return Finalize(diagnosis, AvRepairClassification.ProgressiveDrift, 0.85,
                $"Packet timestamps show the audio/video offset evolving from {Format(offsetForGate)}s " +
                $"to {Format(offsetForGate + driftForGate)}s across the file (drift={Format(driftForGate)}s).");
        }

        return Finalize(diagnosis, AvRepairClassification.Ambiguous, 0.3,
            "Available evidence does not match a known, explainable repair pattern.");
    }

    /// <summary>
    /// Classifies every temporal-video/audio stream pair in a scan result.
    /// </summary>
    public static IReadOnlyList<AvRepairDiagnosis> ClassifyAll(
        MediaScanResult scan,
        IReadOnlyDictionary<(int VideoIndex, int AudioIndex), AvPacketEvidence>? packetEvidence,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(configuration);

        var diagnoses = new List<AvRepairDiagnosis>();
        var videos = scan.Streams.Where(TemporalVideoStreamPolicy.IsEligible);
        var audios = scan.Streams.Where(static stream =>
            string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase));

        foreach (var video in videos)
        {
            foreach (var audio in audios)
            {
                AvPacketEvidence? evidence = null;
                packetEvidence?.TryGetValue((video.Index, audio.Index), out evidence);

                var diagnosis = Classify(video, audio, evidence, configuration);
                if (diagnosis.Classification != AvRepairClassification.None)
                {
                    diagnoses.Add(diagnosis);
                }
            }
        }

        return diagnoses;
    }

    private static AvRepairDiagnosis Finalize(
        AvRepairDiagnosis diagnosis, AvRepairClassification classification, double confidence, string reason)
    {
        diagnosis.Classification = classification;
        diagnosis.Confidence = Math.Clamp(confidence, 0.0, 1.0);
        diagnosis.Reason = reason;
        return diagnosis;
    }

    private static double Max(params double[] values) => values.Max();

    private static string Format(double value) =>
        value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
}
