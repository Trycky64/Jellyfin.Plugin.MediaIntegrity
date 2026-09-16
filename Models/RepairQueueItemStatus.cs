namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the processing state of a repair queue item.
/// </summary>
public enum RepairQueueItemStatus
{
    Pending,
    Processing,
    Repaired,
    Skipped,
    Failed
}
