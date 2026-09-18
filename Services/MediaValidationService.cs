using System.Diagnostics;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Validates remuxed media before it can replace the original file.
/// </summary>
public sealed class MediaValidationService : IMediaValidationService
{
    private readonly PluginConfigurationService _configurationService;
    private readonly MediaProbeService _mediaProbeService;
    private readonly ILogger<MediaValidationService> _logger;

    public MediaValidationService(
        PluginConfigurationService configurationService,
        MediaProbeService mediaProbeService,
        ILogger<MediaValidationService> logger)
    {
        _configurationService = configurationService;
        _mediaProbeService = mediaProbeService;
        _logger = logger;
    }

    public async Task<MediaValidationResult> ValidateAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? intentionallyRetimedStreamIndexes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var configuration =
            _configurationService.GetValidatedConfiguration();

        var errors =
            new List<string>();

        if (!File.Exists(sourcePath))
        {
            errors.Add(
                "Source media file does not exist.");
        }

        if (!File.Exists(outputPath))
        {
            errors.Add(
                "Remux output file does not exist.");
        }

        if (errors.Count > 0)
        {
            return new MediaValidationResult
            {
                Success = false,
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Errors = errors
            };
        }

        var sourceProbe =
            await _mediaProbeService.ProbeAsync(
                sourcePath,
                configuration.ProbeTimeoutSeconds,
                cancellationToken);

        var outputProbe =
            await _mediaProbeService.ProbeAsync(
                outputPath,
                configuration.ProbeTimeoutSeconds,
                cancellationToken);

        if (sourceProbe.ExitCode != 0)
        {
            errors.Add(
                $"Source ffprobe failed with exit code "
                + $"{sourceProbe.ExitCode}.");
        }

        if (outputProbe.ExitCode != 0)
        {
            errors.Add(
                $"Output ffprobe failed with exit code "
                + $"{outputProbe.ExitCode}.");
        }

        if (outputProbe.ScanResult.Status
            is MediaIntegrityStatus.Corrupted
            or MediaIntegrityStatus.Unreadable)
        {
            errors.Add(
                $"Output integrity status is "
                + $"{outputProbe.ScanResult.Status}.");
        }

        CompareStreams(
            sourceProbe.ScanResult,
            outputProbe.ScanResult,
            errors);

        var timelineIssues =
            StreamTimelineValidator.Validate(
                sourceProbe.ScanResult,
                outputProbe.ScanResult,
                configuration,
                intentionallyRetimedStreamIndexes);

        foreach (var timelineIssue in timelineIssues)
        {
            errors.Add(
                $"{timelineIssue.Code}: {timelineIssue.Message}");

            if (timelineIssue.Code
                == "AudioVideoDriftIntroduced")
            {
                _logger.LogWarning(
                    "Audio/video timeline drift introduced for {OutputPath}: {Message}",
                    outputPath,
                    timelineIssue.Message);
            }
            else
            {
                _logger.LogWarning(
                    "Stream timeline validation failed for {OutputPath}: {Message}",
                    outputPath,
                    timelineIssue.Message);
            }
        }

        CompareDuration(
            sourceProbe.ScanResult,
            outputProbe.ScanResult,
            configuration.DurationToleranceSeconds,
            errors);

        CompareChapters(
            sourceProbe.ScanResult,
            outputProbe.ScanResult,
            errors);

        var packetValidationExitCode = 0;
        var packetValidationError =
            string.Empty;

        if (configuration.ValidateFullPacketPass)
        {
            var packetResult =
                await ValidatePacketsAsync(
                    outputPath,
                    configuration.ValidationTimeoutSeconds,
                    cancellationToken);

            packetValidationExitCode =
                packetResult.ExitCode;

            packetValidationError =
                packetResult.StandardError;

            if (packetResult.ExitCode != 0)
            {
                errors.Add(
                    $"Full packet-copy validation failed "
                    + $"with exit code {packetResult.ExitCode}.");
            }

            if (!string.IsNullOrWhiteSpace(
                    packetResult.StandardError))
            {
                errors.Add(
                    "Full packet-copy validation reported errors.");
            }
        }

        var success =
            errors.Count == 0;

        if (success)
        {
            _logger.LogInformation(
                "Media validation succeeded for {OutputPath}.",
                outputPath);
        }
        else
        {
            _logger.LogWarning(
                "Media validation failed for {OutputPath}: {Errors}",
                outputPath,
                string.Join(" | ", errors));
        }

        return new MediaValidationResult
        {
            Success = success,
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Errors = errors,
            TimelineIssues = timelineIssues.ToList(),
            SourceScan = sourceProbe.ScanResult,
            OutputScan = outputProbe.ScanResult,
            PacketValidationExitCode =
                packetValidationExitCode,
            PacketValidationError =
                packetValidationError
        };
    }

    private static void CompareStreams(
        MediaScanResult source,
        MediaScanResult output,
        ICollection<string> errors)
    {
        if (source.Streams.Count
            != output.Streams.Count)
        {
            errors.Add(
                $"Stream count mismatch: source="
                + $"{source.Streams.Count}, output="
                + $"{output.Streams.Count}.");

            return;
        }

        for (var index = 0;
             index < source.Streams.Count;
             index++)
        {
            var sourceStream =
                source.Streams[index];

            var outputStream =
                output.Streams[index];

            if (sourceStream.Index
                != outputStream.Index)
            {
                errors.Add(
                    $"Stream order/index mismatch at position {index}: "
                    + $"source={sourceStream.Index}, output={outputStream.Index}.");
            }

            if (!string.Equals(
                    sourceStream.CodecType,
                    outputStream.CodecType,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"Stream {index} type mismatch: "
                    + $"{sourceStream.CodecType} != "
                    + $"{outputStream.CodecType}.");
            }

            if (!string.Equals(
                    sourceStream.CodecName,
                    outputStream.CodecName,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"Stream {index} codec mismatch: "
                    + $"{sourceStream.CodecName} != "
                    + $"{outputStream.CodecName}.");
            }

            if (string.Equals(
                    sourceStream.CodecType,
                    "video",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (sourceStream.Width
                    != outputStream.Width
                    || sourceStream.Height
                    != outputStream.Height)
                {
                    errors.Add(
                        $"Stream {index} resolution mismatch.");
                }
            }

            if (string.Equals(
                    sourceStream.CodecType,
                    "audio",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (sourceStream.SampleRate
                    != outputStream.SampleRate)
                {
                    errors.Add(
                        $"Stream {index} sample rate mismatch.");
                }

                if (sourceStream.Channels
                    != outputStream.Channels)
                {
                    errors.Add(
                        $"Stream {index} channel count mismatch.");
                }
            }
        }
    }

    private static void CompareDuration(
        MediaScanResult source,
        MediaScanResult output,
        double toleranceSeconds,
        ICollection<string> errors)
    {
        if (source.DurationSeconds is null
            || output.DurationSeconds is null)
        {
            return;
        }

        var difference =
            Math.Abs(
                source.DurationSeconds.Value
                - output.DurationSeconds.Value);

        if (difference > toleranceSeconds)
        {
            errors.Add(
                $"Duration mismatch: difference is "
                + $"{difference:F3}s, tolerance is "
                + $"{toleranceSeconds:F3}s.");
        }
    }

    private static void CompareChapters(
        MediaScanResult source,
        MediaScanResult output,
        ICollection<string> errors)
    {
        if (source.Chapters.Count
            != output.Chapters.Count)
        {
            errors.Add(
                $"Chapter count mismatch: source="
                + $"{source.Chapters.Count}, output="
                + $"{output.Chapters.Count}.");

            return;
        }

        for (var index = 0;
             index < source.Chapters.Count;
             index++)
        {
            var sourceChapter =
                source.Chapters[index];

            var outputChapter =
                output.Chapters[index];

            if (!AreClose(
                    sourceChapter.StartSeconds,
                    outputChapter.StartSeconds,
                    0.05))
            {
                errors.Add(
                    $"Chapter {index} start time mismatch.");
            }

            if (!AreClose(
                    sourceChapter.EndSeconds,
                    outputChapter.EndSeconds,
                    0.05))
            {
                errors.Add(
                    $"Chapter {index} end time mismatch.");
            }
        }
    }

    private async Task<PacketValidationResult>
        ValidatePacketsAsync(
            string outputPath,
            int timeoutSeconds,
            CancellationToken cancellationToken)
    {
        var ffmpegPath =
            ResolveFfmpegPath();

        var startInfo =
            new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

        startInfo.ArgumentList.Add("-hide_banner");

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");

        startInfo.ArgumentList.Add("-nostdin");

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(outputPath);

        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0");

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");

        startInfo.ArgumentList.Add("-");

        using var process =
            new Process
            {
                StartInfo = startInfo
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Unable to start FFmpeg validation "
                + $"for '{outputPath}'.");
        }

        var stdoutTask =
            process.StandardOutput.ReadToEndAsync(
                cancellationToken);

        var stderrTask =
            process.StandardError.ReadToEndAsync(
                cancellationToken);

        using var timeoutCts =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        timeoutCts.CancelAfter(
            TimeSpan.FromSeconds(
                timeoutSeconds));

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
                $"FFmpeg packet validation timed out "
                + $"after {timeoutSeconds} seconds "
                + $"for '{outputPath}'.");
        }

        _ = await stdoutTask;

        var stderr =
            await stderrTask;

        return new PacketValidationResult(
            process.ExitCode,
            stderr);
    }

    private static bool AreClose(
        double? left,
        double? right,
        double tolerance)
    {
        if (left is null
            && right is null)
        {
            return true;
        }

        if (left is null
            || right is null)
        {
            return false;
        }

        return Math.Abs(
            left.Value
            - right.Value)
            <= tolerance;
    }

    private static string ResolveFfmpegPath()
    {
        const string jellyfinPath =
            "/usr/lib/jellyfin-ffmpeg/ffmpeg";

        if (OperatingSystem.IsLinux()
            && File.Exists(jellyfinPath))
        {
            return jellyfinPath;
        }

        return "ffmpeg";
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

    private sealed record PacketValidationResult(
        int ExitCode,
        string StandardError);
}
