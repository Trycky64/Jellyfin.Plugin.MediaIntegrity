namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the integrity scan result for a media file.
/// </summary>
public sealed class MediaScanResult
{
    public string Path { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string Container { get; set; } = string.Empty;

    public double? DurationSeconds { get; set; }

    public long SizeBytes { get; set; }

    public MediaIntegrityStatus Status { get; set; }

    public List<MediaStreamInfo> Streams { get; set; } = [];

    public List<MediaChapterInfo> Chapters { get; set; } = [];

    public List<MediaIssue> Issues { get; set; } = [];

    public DateTimeOffset ScanTimestamp { get; set; } =
        DateTimeOffset.UtcNow;
}
