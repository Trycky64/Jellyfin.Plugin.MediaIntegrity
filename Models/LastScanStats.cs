namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Snapshot of the last completed scan, independent of later queue repairs.
/// </summary>
public sealed record LastScanStats
{
    public DateTimeOffset LastScanDate { get; init; }

    public int Checked { get; init; }

    public int Healthy { get; init; }

    public int Warnings { get; init; }

    public int Queued { get; init; }

    public int Unreadable { get; init; }

    public int Corrupted { get; init; }

    public static LastScanStats FromQueue(RepairQueue queue) => new()
    {
        LastScanDate = queue.GeneratedAt,
        Checked = queue.Summary.Checked,
        Healthy = queue.Summary.Ok,
        Warnings = queue.Summary.Warning,
        Queued = queue.Files.Count,
        Unreadable = queue.Summary.Unreadable,
        Corrupted = queue.Summary.Corrupted
    };
}
