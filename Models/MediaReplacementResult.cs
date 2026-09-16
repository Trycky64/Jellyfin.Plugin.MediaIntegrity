namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the result of a transactional media replacement.
/// </summary>
public sealed class MediaReplacementResult
{
    public bool Success { get; init; }

    public bool BackupCreated { get; init; }

    public bool ReplacementCompleted { get; init; }

    public bool RollbackAttempted { get; init; }

    public bool RollbackSucceeded { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public string RepairPath { get; init; } = string.Empty;

    public string BackupPath { get; init; } = string.Empty;

    public string TemporaryPath { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;
}
