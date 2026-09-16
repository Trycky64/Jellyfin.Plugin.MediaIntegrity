namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents aggregate scan statistics stored with the repair queue.
/// </summary>
public sealed class RepairQueueSummary
{
    public int Checked { get; set; }

    public int Ok { get; set; }

    public int Warning { get; set; }

    public int Repairable { get; set; }

    public int Corrupted { get; set; }

    public int Unreadable { get; set; }
}
