using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaIntegrity;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool DryRun { get; set; } = true;

    public bool EnableVideoFiles { get; set; } = true;

    public bool EnableAudioFiles { get; set; } = true;

    public int ProbeTimeoutSeconds { get; set; } = 120;

    public int MaxParallelProbes { get; set; } = 1;

    public string SourceRoot { get; set; } = "/media";

    public string RepairRoot { get; set; } = "/repair-media";

    public string BackupRoot { get; set; } = "/repair-backups";

    public string TempRoot { get; set; } = "/cache/media-integrity";

    public bool PreserveOriginalContainer { get; set; } = true;

    public bool AllowRepairOfCorrupted { get; set; } = false;

    public bool ValidateFullPacketPass { get; set; } = true;

    /// <summary>Enables bounded first/last packet PTS analysis during source scans.</summary>
    public bool EnableAudioVideoSyncCheck { get; set; } = true;

    public bool EnablePacketTimelineAnalysis { get; set; } = false;

    /// <summary>
    /// Legacy compatibility setting. Backups are always retained; there is no
    /// automatic cleanup in these releases, even when this value is false.
    /// </summary>
    public bool KeepBackups { get; set; } = true;

    public int MaxRepairAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum number of media files that may be processed by one
    /// non-dry-run repair task execution.
    /// </summary>
    public int MaxRepairsPerRun { get; set; } = 1;

    public int RemuxTimeoutSeconds { get; set; } = 3600;

    public int ValidationTimeoutSeconds { get; set; } = 3600;

    public double DurationToleranceSeconds { get; set; } = 2.0;

    /// <summary>
    /// Maximum permitted source-to-candidate duration change for each audio
    /// or video stream. This is intentionally independent from container
    /// duration tolerance.
    /// </summary>
    public double StreamDurationToleranceSeconds { get; set; } = 0.05;

    /// <summary>
    /// Maximum permitted source-to-candidate start timestamp change for each
    /// audio or video stream.
    /// </summary>
    public double StreamStartTimeToleranceSeconds { get; set; } = 0.01;

    // ---------------------------------------------------------------
    // Audio/video repair (v1.2.0). All destructive behavior is opt-in;
    // defaults never modify a media file.
    // ---------------------------------------------------------------

    /// <summary>Master switch for A/V repair. When false, anomalies are only classified, never repaired.</summary>
    public bool EnableAudioVideoRepair { get; set; } = false;

    /// <summary>
    /// Allows strategies that re-encode an audio track
    /// (<see cref="Models.AvRepairStrategy.AudioTimeStretch"/>). Video is
    /// never re-encoded by any strategy regardless of this setting.
    /// </summary>
    public bool AllowAudioReencode { get; set; } = false;

    /// <summary>Maximum number of A/V repairs performed by one non-dry-run task execution.</summary>
    public int MaxAudioVideoRepairsPerRun { get; set; } = 1;

    /// <summary>
    /// Maximum absolute constant offset, in seconds, eligible for automatic
    /// <see cref="Models.AvRepairStrategy.TimestampShift"/> repair. Larger
    /// offsets are always classified <see cref="Models.AvRepairClassification.UnsafeToAutoRepair"/>.
    /// </summary>
    public double MaxAutoRepairOffsetSeconds { get; set; } = 5.0;

    /// <summary>
    /// Maximum absolute audio/video duration delta, in seconds, eligible for
    /// automatic <see cref="Models.AvRepairStrategy.AudioPad"/> or
    /// <see cref="Models.AvRepairStrategy.AudioTrim"/> repair.
    /// </summary>
    public double MaxAutoRepairDurationDeltaSeconds { get; set; } = 2.0;

    /// <summary>
    /// Maximum absolute progressive drift, expressed as a fraction of media
    /// duration, eligible for automatic
    /// <see cref="Models.AvRepairStrategy.AudioTimeStretch"/> repair.
    /// </summary>
    public double MaxAutoRepairDriftRatio { get; set; } = 0.02;

    /// <summary>Minimum classification confidence (0.0-1.0) required for a plan to be auto-repair eligible.</summary>
    public double MinRepairConfidence { get; set; } = 0.75;

    /// <summary>
    /// When true, the backup created for a successful A/V repair is deleted
    /// once post-replacement validation has fully passed. Never deletes a
    /// backup after a failed repair or a rollback.
    /// </summary>
    public bool DeleteBackupAfterSuccessfulValidation { get; set; } = false;

    /// <summary>
    /// FFmpeg audio encoder used whenever a strategy must re-encode an audio
    /// track (<see cref="Models.AvRepairStrategy.AudioTimeStretch"/>,
    /// <see cref="Models.AvRepairStrategy.AudioPad"/>,
    /// <see cref="Models.AvRepairStrategy.AudioTrim"/>). A widely compatible,
    /// lossy-but-transparent default; never used unless one of those
    /// strategies is actually selected.
    /// </summary>
    public string AudioReencodeCodec { get; set; } = "aac";
}
