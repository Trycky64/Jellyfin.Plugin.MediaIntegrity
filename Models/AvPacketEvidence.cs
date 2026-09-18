namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Independent packet-level (first/last PTS) measurement for one video/audio
/// stream pair, used to confirm or reject container-metadata-level signals
/// before any classification is allowed to become auto-repair eligible.
/// This is a sample of two small windows, not a measurement of the stream
/// ends: it confirms stream-level metadata and must never be used to reduce a
/// metadata-level anomaly (see <see cref="Services.AvEvidenceConsistency"/>).
/// </summary>
public sealed record AvPacketEvidence
{
    public required int VideoStreamIndex { get; init; }

    public required int AudioStreamIndex { get; init; }

    /// <summary>Audio-minus-video offset measured at the start of the file, in seconds.</summary>
    public double StartOffsetSeconds { get; init; }

    /// <summary>Audio-minus-video offset measured in the sampled tail window (not necessarily at each stream's real end), in seconds.</summary>
    public double EndOffsetSeconds { get; init; }

    /// <summary>Evolution of the offset between start and end (<see cref="EndOffsetSeconds"/> minus <see cref="StartOffsetSeconds"/>).</summary>
    public double Drift => EndOffsetSeconds - StartOffsetSeconds;

    /// <summary>
    /// True when a packet-level gap larger than the analyzer's tolerance was
    /// found within a sampled window of the audio track, suggesting the
    /// timeline is not a simple, uniform offset or drift.
    /// </summary>
    public bool HasDiscontinuity { get; init; }
}
