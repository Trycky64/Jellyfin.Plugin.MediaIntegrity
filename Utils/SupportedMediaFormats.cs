namespace Jellyfin.Plugin.MediaIntegrity.Utils;

/// <summary>
/// Defines the media file extensions supported by the integrity scanner.
/// </summary>
public static class SupportedMediaFormats
{
    /// <summary>
    /// Supported video file extensions.
    /// </summary>
    public static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4",
            ".m4v",
            ".mkv",
            ".webm",
            ".mov",
            ".avi",
            ".ts",
            ".m2ts",
            ".mts",
            ".mpg",
            ".mpeg"
        };

    /// <summary>
    /// Supported audio file extensions.
    /// </summary>
    public static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".m4a",
            ".mp3",
            ".flac",
            ".ogg",
            ".opus",
            ".aac"
        };

    /// <summary>
    /// Returns true when the extension is supported.
    /// </summary>
    public static bool IsSupported(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return VideoExtensions.Contains(extension)
            || AudioExtensions.Contains(extension);
    }

    /// <summary>
    /// Returns true when the extension belongs to a supported video format.
    /// </summary>
    public static bool IsVideo(string? extension)
    {
        return !string.IsNullOrWhiteSpace(extension)
            && VideoExtensions.Contains(extension);
    }

    /// <summary>
    /// Returns true when the extension belongs to a supported audio format.
    /// </summary>
    public static bool IsAudio(string? extension)
    {
        return !string.IsNullOrWhiteSpace(extension)
            && AudioExtensions.Contains(extension);
    }
}
