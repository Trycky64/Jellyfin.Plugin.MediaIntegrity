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

    public int AudioVideoAffectedMedia { get; init; }
    public int AudioVideoStartOffsets { get; init; }
    public int AudioVideoDurationMismatches { get; init; }
    public int AudioVideoEndMismatches { get; init; }
    public int AudioVideoSuspectedDrifts { get; init; }
    public int TimelineDataIncomplete { get; init; }
    public List<AudioVideoScanDiagnostic> AudioVideoDiagnostics { get; init; } = [];

    public int AvClassifiedConstantOffset { get; init; }
    public int AvClassifiedDurationMismatch { get; init; }
    public int AvClassifiedProgressiveDrift { get; init; }
    public int AvPlannedManualOnly { get; init; }
    public int AvQueuedForRepair { get; init; }

    public static LastScanStats FromQueue(RepairQueue queue, AudioVideoScanCounters? av = null) => new()
    {
        LastScanDate = queue.GeneratedAt,
        Checked = queue.Summary.Checked,
        Healthy = queue.Summary.Ok,
        Warnings = queue.Summary.Warning,
        Queued = queue.Files.Count,
        Unreadable = queue.Summary.Unreadable,
        Corrupted = queue.Summary.Corrupted,
        AudioVideoAffectedMedia = av?.AffectedMedia ?? 0,
        AudioVideoStartOffsets = av?.StartOffsets ?? 0,
        AudioVideoDurationMismatches = av?.DurationMismatches ?? 0,
        AudioVideoEndMismatches = av?.EndMismatches ?? 0,
        AudioVideoSuspectedDrifts = av?.SuspectedDrifts ?? 0,
        TimelineDataIncomplete = av?.IncompleteTimelineData ?? 0,
        AudioVideoDiagnostics = av?.Diagnostics.ToList() ?? [],
        AvClassifiedConstantOffset = av?.AvClassifiedConstantOffset ?? 0,
        AvClassifiedDurationMismatch = av?.AvClassifiedDurationMismatch ?? 0,
        AvClassifiedProgressiveDrift = av?.AvClassifiedProgressiveDrift ?? 0,
        AvPlannedManualOnly = av?.AvPlannedManualOnly ?? 0,
        AvQueuedForRepair = av?.AvQueuedForRepair ?? 0
    };
}
