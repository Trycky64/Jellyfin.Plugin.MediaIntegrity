namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents metadata for a single media stream.
/// </summary>
public sealed class MediaStreamInfo
{
    public int Index { get; set; }

    public string CodecType { get; set; } = string.Empty;

    public string CodecName { get; set; } = string.Empty;

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
