using System.Diagnostics;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Performs lossless media container remuxing using FFmpeg stream copy.
/// </summary>
public sealed class MediaRemuxService
{
    private static readonly HashSet<string> FastStartExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4",
            ".m4v",
            ".mov",
            ".m4a"
        };

    private readonly PluginConfigurationService _configurationService;
    private readonly PathMapper _pathMapper;
    private readonly ILogger<MediaRemuxService> _logger;

    public MediaRemuxService(
        PluginConfigurationService configurationService,
        PathMapper pathMapper,
        ILogger<MediaRemuxService> logger)
    {
        _configurationService = configurationService;
        _pathMapper = pathMapper;
        _logger = logger;
    }

    public async Task<MediaRemuxResult> RemuxAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var configuration =
            _configurationService.GetValidatedConfiguration();

        var paths =
            _pathMapper.MapAll(sourcePath);

        if (!File.Exists(paths.SourcePath))
        {
            throw new FileNotFoundException(
                "Source media file does not exist.",
                paths.SourcePath);
        }

        var outputDirectory =
            Path.GetDirectoryName(
                paths.TemporaryPath);

        if (string.IsNullOrWhiteSpace(
                outputDirectory))
        {
            throw new InvalidOperationException(
                "Unable to determine temporary output directory.");
        }

        Directory.CreateDirectory(
            outputDirectory);

        if (File.Exists(paths.TemporaryPath))
        {
            File.Delete(paths.TemporaryPath);
        }

        var ffmpegPath =
            ResolveFfmpegPath();

        var startInfo =
            BuildStartInfo(
                ffmpegPath,
                paths.SourcePath,
                paths.TemporaryPath);

        _logger.LogDebug(
            "[MediaIntegrity] [Repair] "
            + "FFmpeg remux: {Executable} "
            + "-i {SourcePath} -map 0 -c copy ... {OutputPath}",
            ffmpegPath,
            paths.SourcePath,
            paths.TemporaryPath);

        _logger.LogInformation(
            "[MediaIntegrity] [Repair] "
            + "Starting lossless remux: {SourcePath}.",
            paths.SourcePath);

        var stopwatch =
            Stopwatch.StartNew();

        using var process =
            new Process
            {
                StartInfo = startInfo
            };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Unable to start FFmpeg for '{paths.SourcePath}'.");
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
                    configuration.RemuxTimeoutSeconds));

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
                    _logger.LogInformation(
                        "[MediaIntegrity] [Repair] "
                        + "Remux cancelled: {SourcePath}.",
                        paths.SourcePath);

                    throw;
                }

                throw new TimeoutException(
                    $"FFmpeg remux timed out after "
                    + $"{configuration.RemuxTimeoutSeconds} seconds "
                    + $"for '{paths.SourcePath}'.");
            }

            var stdout =
                await stdoutTask;

            var stderr =
                await stderrTask;

            stopwatch.Stop();

            var success =
                process.ExitCode == 0
                && File.Exists(paths.TemporaryPath)
                && new FileInfo(
                    paths.TemporaryPath).Length > 0;

            var outputSize =
                File.Exists(paths.TemporaryPath)
                    ? new FileInfo(
                        paths.TemporaryPath).Length
                    : 0;

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug(
                    "[MediaIntegrity] [Repair] "
                    + "FFmpeg stderr for {SourcePath}: {Stderr}",
                    paths.SourcePath,
                    stderr.Trim());
            }

            if (!success)
            {
                _logger.LogError(
                    "[MediaIntegrity] [Repair] "
                    + "Lossless remux failed for {SourcePath}. "
                    + "Exit code: {ExitCode}.",
                    paths.SourcePath,
                    process.ExitCode);

                TryDeleteOutput(
                    paths.TemporaryPath);
            }
            else
            {
                _logger.LogInformation(
                    "[MediaIntegrity] [Repair] "
                    + "Lossless remux completed for {SourcePath}. "
                    + "Size: {OutputSize} bytes, "
                    + "duration: {Duration}.",
                    paths.SourcePath,
                    outputSize,
                    stopwatch.Elapsed);
            }

            return new MediaRemuxResult
            {
                Success = success,
                ExitCode = process.ExitCode,
                SourcePath = paths.SourcePath,
                OutputPath = paths.TemporaryPath,
                StandardOutput = stdout,
                StandardError = stderr,
                Duration = stopwatch.Elapsed,
                OutputSizeBytes = outputSize
            };
        }
        catch
        {
            stopwatch.Stop();

            TryKillProcess(process);
            TryDeleteOutput(paths.TemporaryPath);

            throw;
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string ffmpegPath,
        string sourcePath,
        string outputPath)
    {
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
        startInfo.ArgumentList.Add("warning");

        startInfo.ArgumentList.Add("-nostdin");

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);

        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0");

        startInfo.ArgumentList.Add("-map_metadata");
        startInfo.ArgumentList.Add("0");

        startInfo.ArgumentList.Add("-map_chapters");
        startInfo.ArgumentList.Add("0");

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");

        startInfo.ArgumentList.Add("-copy_unknown");

        if (ShouldUseFastStart(outputPath))
        {
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("+faststart");
        }

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);

        return startInfo;
    }

    private static bool ShouldUseFastStart(
        string outputPath)
    {
        return FastStartExtensions.Contains(
            Path.GetExtension(outputPath));
    }

    private static string ResolveFfmpegPath()
    {
        const string jellyfinFfmpegPath =
            "/usr/lib/jellyfin-ffmpeg/ffmpeg";

        if (OperatingSystem.IsLinux()
            && File.Exists(jellyfinFfmpegPath))
        {
            return jellyfinFfmpegPath;
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
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort only.
        }
    }

    private static void TryDeleteOutput(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}
