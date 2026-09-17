using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Executes ffprobe and converts its output into media scan results.
/// </summary>
public sealed class MediaProbeService
{
    private readonly ILogger<MediaProbeService> _logger;
    private readonly MediaIssueDetector _issueDetector;

    public MediaProbeService(
        ILogger<MediaProbeService> logger,
        MediaIssueDetector issueDetector)
    {
        _logger = logger;
        _issueDetector = issueDetector;
    }

    /// <summary>
    /// Probes a media file with ffprobe.
    /// </summary>
    public async Task<MediaProbeResult> ProbeAsync(
        string path,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool enableAudioVideoSyncCheck = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutSeconds),
                "Timeout must be greater than zero.");
        }

        var ffprobePath = ResolveFfprobePath();

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("warning");

        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-show_chapters");

        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");

        startInfo.ArgumentList.Add(path);

        _logger.LogDebug(
            "Running ffprobe for {Path}.",
            path);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Unable to start ffprobe for '{path}'.");
        }

        var stdoutTask =
            process.StandardOutput.ReadToEndAsync(
                cancellationToken);

        var stderrTask =
            process.StandardError.ReadToEndAsync(
                cancellationToken);

        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeoutCts.CancelAfter(
            TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(
                timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                $"ffprobe timed out after {timeoutSeconds} seconds for '{path}'.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var scanResult =
            ParseProbeOutput(
                path,
                stdout,
                process.ExitCode);

        _issueDetector.Analyze(
            scanResult,
            stderr,
            process.ExitCode);

        foreach (var issue in enableAudioVideoSyncCheck ? AudioVideoTimelineAnalyzer.Analyze(scanResult) : [])
        {
            scanResult.Issues.Add(issue);
            if (scanResult.Status == MediaIntegrityStatus.Ok
                && issue.Severity == MediaIssueSeverity.Warning)
            {
                scanResult.Status = MediaIntegrityStatus.Warning;
            }
        }

        return new MediaProbeResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            ScanResult = scanResult
        };
    }

    private static MediaScanResult ParseProbeOutput(
        string path,
        string stdout,
        int exitCode)
    {
        var result = new MediaScanResult
        {
            Path = path,
            Extension = Path.GetExtension(path),
            ScanTimestamp = DateTimeOffset.UtcNow,
            Status = exitCode == 0
                ? MediaIntegrityStatus.Ok
                : MediaIntegrityStatus.Unreadable
        };

        if (File.Exists(path))
        {
            result.SizeBytes =
                new FileInfo(path).Length;
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return result;
        }

        try
        {
            using var document =
                JsonDocument.Parse(stdout);

            var root =
                document.RootElement;

            ParseFormat(
                root,
                result);

            ParseStreams(
                root,
                result);

            ParseChapters(
                root,
                result);
        }
        catch (JsonException)
        {
            result.Status =
                MediaIntegrityStatus.Unreadable;
        }

        return result;
    }

    private static void ParseFormat(
        JsonElement root,
        MediaScanResult result)
    {
        if (!root.TryGetProperty(
                "format",
                out var format))
        {
            return;
        }

        if (format.TryGetProperty(
                "format_name",
                out var formatName))
        {
            result.Container =
                formatName.GetString()
                ?? string.Empty;
        }

        if (!format.TryGetProperty(
                "duration",
                out var durationElement))
        {
            return;
        }

        var durationString =
            durationElement.GetString();

        if (double.TryParse(
                durationString,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration) && double.IsFinite(duration))
        {
            result.DurationSeconds =
                duration;
        }
    }

    private static void ParseStreams(
        JsonElement root,
        MediaScanResult result)
    {
        if (!root.TryGetProperty(
                "streams",
                out var streamsElement))
        {
            return;
        }

        if (streamsElement.ValueKind
            != JsonValueKind.Array)
        {
            return;
        }

        foreach (var streamElement
                 in streamsElement.EnumerateArray())
        {
            result.Streams.Add(
                ParseStream(streamElement));
        }
    }

    private static void ParseChapters(
        JsonElement root,
        MediaScanResult result)
    {
        if (!root.TryGetProperty(
                "chapters",
                out var chaptersElement))
        {
            return;
        }

        if (chaptersElement.ValueKind
            != JsonValueKind.Array)
        {
            return;
        }

        foreach (var chapter
                 in chaptersElement.EnumerateArray())
        {
            result.Chapters.Add(
                ParseChapter(chapter));
        }
    }

    private static MediaChapterInfo ParseChapter(
        JsonElement chapter)
    {
        var result =
            new MediaChapterInfo();

        if (chapter.TryGetProperty(
                "id",
                out var id)
            && id.TryGetInt32(
                out var idValue))
        {
            result.Id = idValue;
        }

        result.StartSeconds =
            ParseDoubleStringProperty(
                chapter,
                "start_time");

        result.EndSeconds =
            ParseDoubleStringProperty(
                chapter,
                "end_time");

        if (chapter.TryGetProperty(
                "tags",
                out var tags)
            && tags.ValueKind
                == JsonValueKind.Object
            && tags.TryGetProperty(
                "title",
                out var title))
        {
            result.Title =
                title.GetString()
                ?? string.Empty;
        }

        return result;
    }

    private static MediaStreamInfo ParseStream(
        JsonElement stream)
    {
        var result =
            new MediaStreamInfo();

        if (stream.TryGetProperty(
                "index",
                out var index)
            && index.TryGetInt32(
                out var streamIndex))
        {
            result.Index =
                streamIndex;
        }

        result.CodecType =
            GetString(
                stream,
                "codec_type");

        result.CodecName =
            GetString(
                stream,
                "codec_name");

        result.TimeBase =
            GetString(
                stream,
                "time_base");

        result.StartTimeSeconds =
            ParseDoubleStringProperty(
                stream,
                "start_time");

        result.DurationSeconds =
            ParseStreamDuration(stream);

        result.Profile =
            GetString(
                stream,
                "profile");

        result.PixelFormat =
            GetString(
                stream,
                "pix_fmt");

        result.FrameRate =
            GetString(
                stream,
                "r_frame_rate");

        result.AverageFrameRate =
            GetString(
                stream,
                "avg_frame_rate");

        if (stream.TryGetProperty(
                "width",
                out var width)
            && width.TryGetInt32(
                out var widthValue))
        {
            result.Width =
                widthValue;
        }

        if (stream.TryGetProperty(
                "height",
                out var height)
            && height.TryGetInt32(
                out var heightValue))
        {
            result.Height =
                heightValue;
        }

        if (stream.TryGetProperty(
                "channels",
                out var channels)
            && channels.TryGetInt32(
                out var channelsValue))
        {
            result.Channels =
                channelsValue;
        }

        var sampleRate =
            GetString(
                stream,
                "sample_rate");

        if (int.TryParse(
                sampleRate,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var sampleRateValue))
        {
            result.SampleRate =
                sampleRateValue;
        }

        if (stream.TryGetProperty("tags", out var tags)
            && tags.ValueKind == JsonValueKind.Object)
        {
            result.Language = GetTag(tags, "language");
            result.Title = GetTag(tags, "title");
        }

        if (stream.TryGetProperty("disposition", out var disposition)
            && disposition.ValueKind == JsonValueKind.Object)
        {
            result.IsDefault = GetDisposition(disposition, "default");
            result.IsForced = GetDisposition(disposition, "forced");
            result.IsCommentary = GetDisposition(disposition, "comment");
            result.IsAudioDescription = GetDisposition(disposition, "visual_impaired")
                || GetDisposition(disposition, "descriptions");
            result.IsAttachedPicture = GetDisposition(disposition, "attached_pic");
        }

        return result;
    }

    private static bool GetDisposition(JsonElement disposition, string name) =>
        disposition.TryGetProperty(name, out var value)
        && ((value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0)
            || (value.ValueKind == JsonValueKind.String
                && (value.GetString() == "1" || bool.TryParse(value.GetString(), out var parsed) && parsed)));

    private static string GetTag(JsonElement tags, string name)
    {
        foreach (var tag in tags.EnumerateObject())
        {
            if (!string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : null;
            return value is null || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase)
                ? string.Empty : value;
        }

        return string.Empty;
    }

    private static double? ParseDoubleStringProperty(
        JsonElement element,
        string propertyName)
    {
        var value =
            GetString(
                element,
                propertyName);

        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) && double.IsFinite(parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? ParseStreamDuration(JsonElement stream)
    {
        var directDuration = ParseDoubleStringProperty(stream, "duration");
        if (directDuration is not null)
        {
            return directDuration;
        }

        // Matroska commonly exposes a stream-local DURATION tag instead of
        // stream.duration. It is still a per-stream value and avoids falling
        // back to the container duration.
        if (!stream.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parts = GetTag(tags, "DURATION").Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || hours < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60)
        {
            return null;
        }

        var duration = hours * 3600d + minutes * 60d + seconds;
        return double.IsFinite(duration) ? duration : null;
    }

    private static string GetString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(
                propertyName,
                out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String =>
                property.GetString()
                ?? string.Empty,

            JsonValueKind.Number =>
                property.GetRawText(),

            _ => string.Empty
        };
    }

    private static string ResolveFfprobePath()
    {
        const string jellyfinPath =
            "/usr/lib/jellyfin-ffmpeg/ffprobe";

        if (OperatingSystem.IsLinux()
            && File.Exists(jellyfinPath))
        {
            return jellyfinPath;
        }

        return "ffprobe";
    }

    private static void TryKillProcess(
        Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only.
        }
    }
}
