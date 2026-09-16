namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the overall integrity state of a media file.
/// </summary>
public enum MediaIntegrityStatus
{
    Ok,
    Warning,
    RemuxRecommended,
    Corrupted,
    Unreadable
}
