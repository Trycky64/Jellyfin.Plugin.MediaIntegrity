namespace Jellyfin.Plugin.MediaIntegrity.Utils;

/// <summary>
/// Logical container families used by the remux system.
/// </summary>
public enum ContainerFamily
{
    Unknown,
    Mp4,
    Matroska,
    WebM,
    Mov,
    Avi,
    MpegTs,
    MpegProgramStream,
    RawAudio
}
