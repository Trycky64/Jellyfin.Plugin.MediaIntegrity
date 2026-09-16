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
}
