namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// A deterministic, explainable repair plan derived from an
/// <see cref="AvRepairDiagnosis"/> and the current plugin configuration.
/// Building a plan never performs any I/O or ffmpeg invocation.
/// </summary>
public sealed class AvRepairPlan
{
    public int VideoStreamIndex { get; set; }

    public int AudioStreamIndex { get; set; }

    public AvRepairClassification Classification { get; set; } = AvRepairClassification.None;

    public AvRepairStrategy Strategy { get; set; } = AvRepairStrategy.None;

    /// <summary>Explains why this strategy (or ManualOnly) was chosen.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>True when the selected strategy requires re-encoding an audio track.</summary>
    public bool RequiresAudioReencode { get; set; }

    /// <summary>True when the selected strategy can be executed with stream copy only (no re-encode of any track).</summary>
    public bool IsStreamCopySafe { get; set; }

    public double Confidence { get; set; }

    /// <summary>For <see cref="AvRepairStrategy.TimestampShift"/>: seconds to add to the audio track's timestamps.</summary>
    public double? TimestampShiftSeconds { get; set; }

    /// <summary>For <see cref="AvRepairStrategy.AudioTimeStretch"/>: the atempo speed factor to apply to audio (candidate duration = source duration / factor).</summary>
    public double? AtempoFactor { get; set; }

    /// <summary>For <see cref="AvRepairStrategy.AudioPad"/>: seconds of silence to insert.</summary>
    public double? PadSeconds { get; set; }

    /// <summary>For <see cref="AvRepairStrategy.AudioTrim"/>: seconds of audio to remove.</summary>
    public double? TrimSeconds { get; set; }

    /// <summary>True when this plan is eligible to run automatically, subject to the caller's own batch/run limits.</summary>
    public bool IsAutoRepairEligible =>
        Strategy is not (AvRepairStrategy.None or AvRepairStrategy.ManualOnly);
}
