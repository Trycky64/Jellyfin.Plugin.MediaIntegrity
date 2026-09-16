namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents chapter metadata discovered by ffprobe.
/// </summary>
public sealed class MediaChapterInfo
{
    public int Id { get; set; }

    public double? StartSeconds { get; set; }

    public double? EndSeconds { get; set; }

    public string Title { get; set; } = string.Empty;
}
