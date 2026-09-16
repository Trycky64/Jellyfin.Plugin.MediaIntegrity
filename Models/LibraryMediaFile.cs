namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents a physical media file discovered through Jellyfin.
/// </summary>
public sealed class LibraryMediaFile
{
    public string Path { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public bool IsVideo { get; set; }

    public bool IsAudio { get; set; }
}
