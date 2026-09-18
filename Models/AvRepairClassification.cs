namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Classifies a detected audio/video timeline anomaly before any repair is planned.
/// Classification is diagnostic; it never performs a repair by itself.
/// </summary>
public enum AvRepairClassification
{
    /// <summary>No anomaly, or the anomaly is too small to classify.</summary>
    None,

    /// <summary>
    /// The audio track is shifted from the video track by a stable amount from
    /// start to end. Corrigible with a timestamp shift or stream-copy remux,
    /// without re-encoding either track.
    /// </summary>
    ConstantOffset,

    /// <summary>
    /// The audio and video overall durations differ by more than tolerance,
    /// but the available evidence does not yet establish whether this is a
    /// constant offset, a progressive drift, or an intentionally shorter track.
    /// </summary>
    DurationMismatch,

    /// <summary>
    /// The offset between audio and video measurably evolves between the start
    /// and the end of the file (packet-confirmed). Only correctable by an
    /// opt-in audio time-stretch; never by re-encoding video.
    /// </summary>
    ProgressiveDrift,

    /// <summary>The audio track measurably ends earlier than the video track.</summary>
    AudioEndsEarly,

    /// <summary>The audio track measurably ends later than the video track.</summary>
    AudioEndsLate,

    /// <summary>
    /// The anomaly is visible only in container/stream metadata (start_time or
    /// duration tags); no independent packet-level evidence confirms it. Not
    /// eligible for automatic repair by default.
    /// </summary>
    TimelineMetadataOnly,

    /// <summary>
    /// Multiple signals conflict, or evidence is below the confidence
    /// threshold required to select a single explanation. Never auto-repaired.
    /// </summary>
    Ambiguous,

    /// <summary>
    /// The anomaly is too large, the timeline is discontinuous, or a policy
    /// limit was exceeded. Always requires manual review; never auto-repaired.
    /// </summary>
    UnsafeToAutoRepair
}
