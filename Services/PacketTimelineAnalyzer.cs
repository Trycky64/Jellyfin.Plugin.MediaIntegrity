using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>Optional, bounded packet timestamp check for source A/V drift.</summary>
public static class PacketTimelineAnalyzer
{
    private const int MaxPacketLines = 20000;

    public static async Task<IReadOnlyList<MediaIssue>> AnalyzeAsync(
        string path, MediaScanResult scan, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var duration = scan.DurationSeconds;
        if (duration is null || !double.IsFinite(duration.Value) || duration <= 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }

        var packets = await FetchPacketsAsync(path, duration.Value, timeoutSeconds, cancellationToken);
        return AnalyzeSamples(scan, packets);
    }

    /// <summary>
    /// Runs the bounded packet timestamp probe and returns the raw samples,
    /// without interpreting them. Shared by <see cref="AnalyzeAsync"/> (which
    /// derives diagnostic issues) and callers that need packet-level evidence
    /// for A/V repair classification via <see cref="ComputeEvidence"/>,
    /// avoiding a second ffprobe pass over the same file.
    /// </summary>
    public static async Task<IReadOnlyList<PacketTimestamp>> FetchPacketsAsync(
        string path, double duration, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return [];
        }

        var finalStart = Math.Max(0, duration - 2);
        var intervals = finalStart <= 2
            ? "0%+4"
            : $"0%+2,{finalStart.ToString("F6", CultureInfo.InvariantCulture)}%+2";
        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsLinux() && File.Exists("/usr/lib/jellyfin-ffmpeg/ffprobe")
                ? "/usr/lib/jellyfin-ffmpeg/ffprobe" : "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] {
            "-v", "error", "-read_intervals", intervals, "-show_packets",
            "-show_entries", "packet=stream_index,pts_time,duration_time",
            "-of", "csv=p=0", path })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        if (!process.Start()) throw new InvalidOperationException("Unable to start packet timeline probe.");
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var packets = new List<PacketTimestamp>();
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token)) is not null)
            {
                if (packets.Count >= MaxPacketLines)
                {
                    process.Kill(entireProcessTree: true);
                    return [];
                }

                if (TryParsePacket(line, out var packet)) packets.Add(packet);
            }

            await process.WaitForExitAsync(timeout.Token);
            _ = await stderr;
            return process.ExitCode == 0 ? packets : [];
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Packet timeline analysis timed out.");
        }
    }

    public static IReadOnlyList<MediaIssue> AnalyzeSamples(MediaScanResult scan, IEnumerable<PacketTimestamp> packets)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(packets);
        var ranges = packets
            .Where(static packet => double.IsFinite(packet.PtsSeconds) && double.IsFinite(packet.DurationSeconds)
                && packet.DurationSeconds >= 0)
            .GroupBy(static packet => packet.StreamIndex)
            .ToDictionary(static group => group.Key,
                static group => (First: group.Min(static packet => packet.PtsSeconds),
                    End: group.Max(static packet => packet.PtsSeconds + packet.DurationSeconds)));
        var issues = new List<MediaIssue>();
        foreach (var video in scan.Streams.Where(TemporalVideoStreamPolicy.IsEligible))
        {
            foreach (var audio in scan.Streams.Where(static stream => stream.CodecType == "audio"))
            {
                if (!ranges.TryGetValue(video.Index, out var v) || !ranges.TryGetValue(audio.Index, out var a)
                    || !double.IsFinite(v.End) || !double.IsFinite(a.End)) continue;
                var startOffset = a.First - v.First;
                var endOffset = a.End - v.End;
                var drift = endOffset - startOffset;
                var threshold = Math.Max(AudioVideoTimelineAnalyzer.DurationMismatchThresholdSeconds,
                    Math.Max(scan.DurationSeconds ?? 0, 0) * AudioVideoTimelineAnalyzer.RelativeDurationThreshold);
                if (Math.Abs(drift) <= threshold) continue;
                issues.Add(new MediaIssue
                {
                    Code = "AudioVideoPacketDriftSuspected",
                    Message = $"Packet timestamps suggest A/V offset evolution of {drift.ToString("+0.000000;-0.000000;0.000000", CultureInfo.InvariantCulture)}s between video {video.Index} and audio {audio.Index}.",
                    Severity = MediaIssueSeverity.Warning,
                    VideoStreamIndex = video.Index,
                    AudioStreamIndex = audio.Index,
                    StartDeltaSeconds = startOffset,
                    EndDeltaSeconds = endOffset,
                    OffsetEvolutionSeconds = drift,
                    AudioLanguage = audio.Language,
                    AudioTitle = audio.Title,
                    AudioIsDefault = audio.IsDefault
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Minimum gap, in seconds, between consecutive packets sampled from the
    /// same edge window of one audio stream before that window is considered
    /// discontinuous. Ordinary audio codecs do not leave silent gaps this
    /// large within a 2-4 second sampled window; a larger gap means the
    /// sampled evidence cannot be trusted to represent a simple offset or
    /// uniform drift.
    /// </summary>
    public const double DiscontinuityGapThresholdSeconds = 0.75;

    /// <summary>
    /// Computes independent packet-level start/end offset evidence for every
    /// temporal-video/audio stream pair, regardless of whether the evolution
    /// exceeds the diagnostic issue threshold used by
    /// <see cref="AnalyzeSamples"/>. Used by <see cref="AvRepairClassifier"/>
    /// to confirm or reject container-metadata-level signals.
    ///
    /// The result is a bounded sample, not a measurement of the stream ends.
    /// The tail window is requested at <c>duration - 2</c>, but ffprobe places
    /// the end of a "%+2" interval two seconds after the first packet read
    /// following the seek (which lands on an earlier keyframe). When one
    /// stream ends before the container duration, or keyframes are sparse,
    /// the other stream's real end is not in the window and
    /// <see cref="AvPacketEvidence.EndOffsetSeconds"/> can under-report the
    /// real difference by many seconds. The true end cannot be recovered from
    /// a partial window without reading the whole file, so it is not guessed:
    /// consumers must check the evidence against stream-level metadata with
    /// <see cref="AvEvidenceConsistency"/> and never let it lower a metadata
    /// anomaly.
    /// </summary>
    public static IReadOnlyDictionary<(int VideoIndex, int AudioIndex), AvPacketEvidence> ComputeEvidence(
        MediaScanResult scan, IEnumerable<PacketTimestamp> packets)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(packets);

        var packetList = packets
            .Where(static packet => double.IsFinite(packet.PtsSeconds) && double.IsFinite(packet.DurationSeconds)
                && packet.DurationSeconds >= 0)
            .ToList();

        var ranges = packetList
            .GroupBy(static packet => packet.StreamIndex)
            .ToDictionary(static group => group.Key,
                static group => (First: group.Min(static packet => packet.PtsSeconds),
                    End: group.Max(static packet => packet.PtsSeconds + packet.DurationSeconds)));

        var midpoint = (scan.DurationSeconds ?? 0) / 2;

        var evidence = new Dictionary<(int, int), AvPacketEvidence>();
        foreach (var video in scan.Streams.Where(TemporalVideoStreamPolicy.IsEligible))
        {
            foreach (var audio in scan.Streams.Where(static stream => stream.CodecType == "audio"))
            {
                if (!ranges.TryGetValue(video.Index, out var v) || !ranges.TryGetValue(audio.Index, out var a)
                    || !double.IsFinite(v.End) || !double.IsFinite(a.End))
                {
                    continue;
                }

                var audioPackets = packetList.Where(packet => packet.StreamIndex == audio.Index);
                var hasDiscontinuity = HasSignificantGap(audioPackets, midpoint);

                evidence[(video.Index, audio.Index)] = new AvPacketEvidence
                {
                    VideoStreamIndex = video.Index,
                    AudioStreamIndex = audio.Index,
                    StartOffsetSeconds = a.First - v.First,
                    EndOffsetSeconds = a.End - v.End,
                    HasDiscontinuity = hasDiscontinuity
                };
            }
        }

        return evidence;
    }

    private static bool HasSignificantGap(IEnumerable<PacketTimestamp> streamPackets, double midpoint)
    {
        var head = streamPackets.Where(packet => packet.PtsSeconds < midpoint).OrderBy(static p => p.PtsSeconds).ToList();
        var tail = streamPackets.Where(packet => packet.PtsSeconds >= midpoint).OrderBy(static p => p.PtsSeconds).ToList();
        return HasGapWithinWindow(head) || HasGapWithinWindow(tail);
    }

    private static bool HasGapWithinWindow(IReadOnlyList<PacketTimestamp> window)
    {
        for (var index = 1; index < window.Count; index++)
        {
            var previous = window[index - 1];
            var gap = window[index].PtsSeconds - (previous.PtsSeconds + previous.DurationSeconds);
            if (gap > DiscontinuityGapThresholdSeconds)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParsePacket(string line, out PacketTimestamp packet)
    {
        packet = default;
        var fields = line.Split(',');
        if (fields.Length < 3 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)) return false;
        packet = new PacketTimestamp(index, pts, duration);
        return true;
    }
}
