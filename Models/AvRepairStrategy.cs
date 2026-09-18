namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// A deterministic repair action selected for one audio/video classification.
/// A video stream is never re-encoded by any strategy.
/// </summary>
public enum AvRepairStrategy
{
    /// <summary>No repair is applicable or necessary.</summary>
    None,

    /// <summary>
    /// Shift the audio track's presentation timestamps by a constant amount.
    /// Both tracks are stream-copied; nothing is re-encoded.
    /// </summary>
    TimestampShift,

    /// <summary>
    /// Remux the container with stream copy only, without altering any
    /// timestamps. Used when the container itself carries the mismatch
    /// (for example a stale/incorrect edit list or metadata-only offset).
    /// </summary>
    StreamCopyRemux,

    /// <summary>
    /// Re-encode only the affected audio track(s) with an `atempo` factor to
    /// correct a progressive drift. Requires <c>AllowAudioReencode=true</c>.
    /// Video is always stream-copied.
    /// </summary>
    AudioTimeStretch,

    /// <summary>
    /// Append silence to the end of the audio track to compensate for a
    /// small, bounded duration deficit. Video is always stream-copied, but
    /// the target audio track is still decoded and re-encoded because
    /// <c>apad</c> is a filter-graph operation. Requires
    /// <c>AllowAudioReencode=true</c>.
    /// </summary>
    AudioPad,

    /// <summary>
    /// Trim a small, bounded amount of excess audio from the end of the
    /// track. Video is always stream-copied, but the target audio track is
    /// still decoded and re-encoded because <c>atrim</c> is a filter-graph
    /// operation. Requires <c>AllowAudioReencode=true</c>.
    /// </summary>
    AudioTrim,

    /// <summary>
    /// The anomaly requires human review. No automatic repair is attempted
    /// regardless of configuration.
    /// </summary>
    ManualOnly
}
