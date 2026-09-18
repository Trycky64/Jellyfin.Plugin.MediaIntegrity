namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// The result of classifying one video/audio stream pair's timeline anomaly.
/// Carries enough structured context to plan a repair and to explain the
/// decision without re-deriving it from raw ffprobe output.
/// </summary>
public sealed class AvRepairDiagnosis
{
    public int VideoStreamIndex { get; set; }

    public int AudioStreamIndex { get; set; }

    public AvRepairClassification Classification { get; set; } = AvRepairClassification.None;

    /// <summary>Confidence in the classification, from 0.0 (no confidence) to 1.0 (fully packet-confirmed).</summary>
    public double Confidence { get; set; }

    /// <summary>Human-readable explanation of why this classification was chosen.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Metadata-level start offset (audio start minus video start), in seconds.</summary>
    public double StartOffsetSeconds { get; set; }

    /// <summary>Metadata-level duration delta (audio duration minus video duration), in seconds.</summary>
    public double DurationDeltaSeconds { get; set; }

    /// <summary>Metadata-level end offset (audio end minus video end), in seconds.</summary>
    public double EndOffsetSeconds { get; set; }

    /// <summary>Audio duration divided by video duration, when both are known and positive.</summary>
    public double? DurationRatio { get; set; }

    /// <summary>Video stream duration in seconds, kept for downstream ratio/factor calculations.</summary>
    public double VideoDurationSeconds { get; set; }

    /// <summary>Audio stream duration in seconds, kept for downstream ratio/factor calculations.</summary>
    public double AudioDurationSeconds { get; set; }

    /// <summary>Independent packet-level evidence for this pair, when available.</summary>
    public AvPacketEvidence? PacketEvidence { get; set; }

    /// <summary>True when <see cref="PacketEvidence"/> was available and used for this classification.</summary>
    public bool HasPacketEvidence => PacketEvidence is not null;

    public string AudioLanguage { get; set; } = string.Empty;

    public string AudioTitle { get; set; } = string.Empty;

    public bool AudioIsDefault { get; set; }

    public bool AudioIsForced { get; set; }

    public bool AudioIsCommentary { get; set; }

    public bool AudioIsDescription { get; set; }

    /// <summary>
    /// True when this classification is, in principle, a candidate for
    /// automatic repair. Policy limits (config thresholds, MinRepairConfidence,
    /// EnableAudioVideoRepair, AllowAudioReencode) are applied separately by
    /// the planner and can still force <see cref="AvRepairStrategy.ManualOnly"/>.
    /// </summary>
    public bool IsPotentiallyAutoRepairable =>
        Classification is AvRepairClassification.ConstantOffset
            or AvRepairClassification.ProgressiveDrift
            or AvRepairClassification.AudioEndsEarly
            or AvRepairClassification.AudioEndsLate;
}
