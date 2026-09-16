namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents all paths involved in a media repair operation.
/// </summary>
public sealed class MappedMediaPaths
{
    public string SourcePath { get; init; } = string.Empty;

    public string RepairPath { get; init; } = string.Empty;

    public string BackupPath { get; init; } = string.Empty;

    public string TemporaryPath { get; init; } = string.Empty;
}
