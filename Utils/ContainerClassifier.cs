namespace Jellyfin.Plugin.MediaIntegrity.Utils;

/// <summary>
/// Classifies ffprobe format names into logical container families.
/// </summary>
public static class ContainerClassifier
{
    public static ContainerFamily Classify(string? formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
        {
            return ContainerFamily.Unknown;
        }

        var value = formatName.ToLowerInvariant();

        if (value.Contains("matroska"))
        {
            return ContainerFamily.Matroska;
        }

        if (value.Contains("webm"))
        {
            return ContainerFamily.WebM;
        }

        if (value.Contains("mov")
            || value.Contains("mp4")
            || value.Contains("m4a")
            || value.Contains("3gp")
            || value.Contains("3g2")
            || value.Contains("mj2"))
        {
            return ContainerFamily.Mp4;
        }

        if (value.Contains("avi"))
        {
            return ContainerFamily.Avi;
        }

        if (value.Contains("mpegts"))
        {
            return ContainerFamily.MpegTs;
        }

        if (value.Contains("mpeg"))
        {
            return ContainerFamily.MpegProgramStream;
        }

        if (value.Contains("mp3")
            || value.Contains("flac")
            || value.Contains("ogg")
            || value.Contains("aac"))
        {
            return ContainerFamily.RawAudio;
        }

        return ContainerFamily.Unknown;
    }
}
