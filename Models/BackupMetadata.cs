namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Permanent recovery manifest. A future Clean Media Repair Backups task
/// can consume this versioned format; no automatic retention is performed.
/// </summary>
public sealed record BackupMetadata
{
    public int SchemaVersion { get; init; } = 1;

    public required string SourcePath { get; init; }

    public required string BackupPath { get; init; }

    public required string RepairedPath { get; init; }

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public required string SourceHash { get; init; }

    public required string BackupHash { get; init; }

    /// <summary>
    /// Hash of the validated replacement, recorded before the swap.
    /// Compare with the current media to determine whether it was installed.
    /// </summary>
    public required string RepairedHash { get; init; }

    public string Algorithm { get; init; } = "SHA-256";

    public string PluginVersion { get; init; } =
        typeof(BackupMetadata).Assembly.GetName().Version!.ToString();
}
