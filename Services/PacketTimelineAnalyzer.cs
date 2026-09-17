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
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(scan);
        if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        cancellationToken.ThrowIfCancellationRequested();
        var duration = scan.DurationSeconds;
        if (duration is null || !double.IsFinite(duration.Value) || duration <= 0)
        {
            return [];
        }

        var finalStart = Math.Max(0, duration.Value - 2);
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
            return process.ExitCode == 0 ? AnalyzeSamples(scan, packets) : [];
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
