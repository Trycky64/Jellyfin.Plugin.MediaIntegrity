namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents a single media item queued for repair.
/// </summary>
public sealed class RepairQueueItem
{
    public string Path { get; set; } =
        string.Empty;

    public string Extension { get; set; } =
        string.Empty;

    public string Container { get; set; } =
        string.Empty;

    public MediaIntegrityStatus IntegrityStatus { get; set; }

    public List<MediaIssue> Issues { get; set; } =
        [];

    public RepairQueueItemStatus Status { get; set; } =
        RepairQueueItemStatus.Pending;

    public int Attempts { get; set; }

    public string LastError { get; set; } =
        string.Empty;

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// Auto-repair-eligible A/V plans for this media file, populated only
    /// when a classified anomaly's plan is not ManualOnly/None. An empty list
    /// means no automatic A/V repair applies, even if diagnostics exist.
    /// </summary>
    public List<AvRepairPlan> AvRepairPlans { get; set; } = [];
}
