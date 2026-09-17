namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents metadata for a single media stream.
/// </summary>
public sealed class MediaStreamInfo
{
    public int Index { get; set; }

    public string CodecType { get; set; } = string.Empty;

    public string CodecName { get; set; } = string.Empty;

    /// <summary>
    /// FFmpeg stream clock representation, for example <c>1/90000</c>.
    /// </summary>
    public string TimeBase { get; set; } = string.Empty;

    /// <summary>
    /// Start timestamp reported by ffprobe, in seconds when available.
    /// </summary>
    public double? StartTimeSeconds { get; set; }

    /// <summary>
    /// Stream duration reported by ffprobe, in seconds when available.
    /// </summary>
    public double? DurationSeconds { get; set; }

    public string Profile { get; set; } = string.Empty;

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string PixelFormat { get; set; } = string.Empty;

    public string FrameRate { get; set; } = string.Empty;

    public string AverageFrameRate { get; set; } = string.Empty;

    public int? SampleRate { get; set; }

    public int? Channels { get; set; }

    public string Language { get; set; } = string.Empty;
}
