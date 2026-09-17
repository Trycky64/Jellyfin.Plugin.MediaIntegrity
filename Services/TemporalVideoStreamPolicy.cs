using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Selects video streams that have a usable temporal timeline for A/V diagnostics.
/// </summary>
public static class TemporalVideoStreamPolicy
{
    /// <summary>
    /// A video participates only when it is not an attached picture and ffprobe
    /// provides a finite, strictly positive stream-local duration. A missing
    /// duration is excluded because it cannot establish a usable video timeline.
    /// Codec, dimensions, and file names are deliberately not considered.
    /// </summary>
    public static bool IsEligible(MediaStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase)
            && !stream.IsAttachedPicture
            && stream.DurationSeconds is { } duration
            && double.IsFinite(duration)
            && duration > 0;
    }
}
