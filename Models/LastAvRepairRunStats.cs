namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Snapshot of the most recent A/V repair task execution. Rebuilt each time
/// the task runs; historical runs are not accumulated.
/// </summary>
public sealed record LastAvRepairRunStats
{
    public DateTimeOffset RunDate { get; init; }

    public bool DryRun { get; init; }

    public int Attempted { get; init; }

    public int Succeeded { get; init; }

    public int Failed { get; init; }

    public int RolledBack { get; init; }

    public int Skipped { get; init; }

    public int BackupsDeleted { get; init; }

    public long BytesReclaimed { get; init; }
}
