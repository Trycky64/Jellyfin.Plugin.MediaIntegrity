using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Executes one <see cref="AvRepairPlan"/> with FFmpeg, writing the candidate
/// to a temporary path outside the source tree. Video is never re-encoded by
/// any strategy. Only <see cref="AvRepairStrategy.TimestampShift"/> is a pure
/// stream copy; the other executable strategies decode and re-encode exactly
/// the one targeted audio track because they rely on FFmpeg audio filters
/// (<c>atempo</c>, <c>apad</c>, <c>atrim</c>).
/// </summary>
public sealed class AvRepairExecutionService
{
    private readonly PluginConfigurationService _configurationService;
    private readonly PathMapper _pathMapper;
    private readonly ILogger<AvRepairExecutionService> _logger;

    public AvRepairExecutionService(
        PluginConfigurationService configurationService,
        PathMapper pathMapper,
        ILogger<AvRepairExecutionService> logger)
    {
        _configurationService = configurationService;
        _pathMapper = pathMapper;
        _logger = logger;
    }

    /// <summary>
    /// Executes one plan, reading from <paramref name="inputPath"/> (either
    /// the real source, or a previous stage's already-validated candidate
    /// when chaining multiple plans for the same media file) and writing a
    /// fresh, uniquely named candidate under <c>TempRoot</c>. The caller is
    /// responsible for validating <paramref name="inputPath"/> itself;
    /// <paramref name="originalSourcePath"/> is only used to derive the
    /// output's relative path and must always be the real, validated source.
    /// </summary>
    public async Task<MediaRemuxResult> ExecuteAsync(
        AvRepairPlan plan,
        string inputPath,
        IReadOnlyList<MediaStreamInfo> inputStreams,
        string originalSourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(inputStreams);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalSourcePath);

        if (plan.Strategy is AvRepairStrategy.None or AvRepairStrategy.ManualOnly)
        {
            throw new InvalidOperationException(
                $"A/V repair strategy '{plan.Strategy}' cannot be executed.");
        }

        var configuration = _configurationService.GetValidatedConfiguration();

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input media file does not exist.", inputPath);
        }

        var outputPath = _pathMapper.MapToTemporaryPath(originalSourcePath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Unable to determine temporary output directory.");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var ffmpegPath = ResolveFfmpegPath();
        var startInfo = BuildStartInfo(
            ffmpegPath, inputPath, outputPath, inputStreams, plan, configuration.AudioReencodeCodec);

        _logger.LogInformation(
            "[MediaIntegrity] [AvRepair] Starting {Strategy} for {SourcePath} (video={Video}, audio={Audio}).",
            plan.Strategy, inputPath, plan.VideoStreamIndex, plan.AudioStreamIndex);
        _logger.LogDebug(
            "[MediaIntegrity] [AvRepair] FFmpeg arguments: {Arguments}",
            string.Join(' ', startInfo.ArgumentList));

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start FFmpeg for '{inputPath}'.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.RemuxTimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException(
                    $"FFmpeg A/V repair timed out after {configuration.RemuxTimeoutSeconds} seconds " +
                    $"for '{inputPath}'.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();

            var success = process.ExitCode == 0 && File.Exists(outputPath)
                && new FileInfo(outputPath).Length > 0;
            var outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;

            if (!success)
            {
                _logger.LogError(
                    "[MediaIntegrity] [AvRepair] {Strategy} failed for {SourcePath}. Exit code: {ExitCode}.",
                    plan.Strategy, inputPath, process.ExitCode);
                TryDeleteOutput(outputPath);
            }
            else
            {
                _logger.LogInformation(
                    "[MediaIntegrity] [AvRepair] {Strategy} completed for {SourcePath}. " +
                    "Size: {OutputSize} bytes, duration: {Duration}.",
                    plan.Strategy, inputPath, outputSize, stopwatch.Elapsed);
            }

            return new MediaRemuxResult
            {
                Success = success,
                ExitCode = process.ExitCode,
                SourcePath = inputPath,
                OutputPath = outputPath,
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
            TryDeleteOutput(outputPath);
            throw;
        }
    }

    /// <summary>
    /// Builds the FFmpeg invocation for one plan. Every stream is mapped
    /// explicitly, in source order, so the output stream order and count
    /// always match the source exactly (required by
    /// <see cref="StreamTimelineValidator"/> and <see cref="MediaValidationService"/>).
    /// Only the one targeted audio stream is ever re-timed or re-encoded.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        IReadOnlyList<MediaStreamInfo> streams,
        AvRepairPlan plan,
        string audioReencodeCodec)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Strategy is AvRepairStrategy.None or AvRepairStrategy.ManualOnly)
        {
            throw new InvalidOperationException($"Strategy {plan.Strategy} cannot be executed.");
        }

        var orderedStreams = streams.OrderBy(static stream => stream.Index).ToList();
        var audioIndexesInOrder = orderedStreams
            .Where(static stream => string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
            .Select(static stream => stream.Index)
            .ToList();
        var audioOrdinal = audioIndexesInOrder.IndexOf(plan.AudioStreamIndex);
        if (audioOrdinal < 0)
        {
            throw new InvalidOperationException(
                $"Audio stream {plan.AudioStreamIndex} was not found in the source stream list.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var args = startInfo.ArgumentList;

        args.Add("-hide_banner");
        args.Add("-v");
        args.Add("warning");
        args.Add("-nostdin");
        args.Add("-i");
        args.Add(sourcePath);

        var usesShiftedSecondInput = plan.Strategy == AvRepairStrategy.TimestampShift;
        if (usesShiftedSecondInput)
        {
            args.Add("-itsoffset");
            args.Add((plan.TimestampShiftSeconds ?? 0).ToString("0.000000", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(sourcePath);
        }

        foreach (var stream in orderedStreams)
        {
            args.Add("-map");
            args.Add(usesShiftedSecondInput && stream.Index == plan.AudioStreamIndex
                ? $"1:{stream.Index}"
                : $"0:{stream.Index}");
        }

        args.Add("-map_metadata");
        args.Add("0");
        args.Add("-map_chapters");
        args.Add("0");
        args.Add("-c");
        args.Add("copy");
        args.Add("-copy_unknown");

        switch (plan.Strategy)
        {
            case AvRepairStrategy.TimestampShift:
                // Pure stream copy; the shift is entirely carried by -itsoffset above.
                break;

            case AvRepairStrategy.AudioTimeStretch:
                AddAudioFilter(args, audioOrdinal, audioReencodeCodec, orderedStreams[FindIndex(orderedStreams, plan.AudioStreamIndex)],
                    BuildAtempoExpression(plan.AtempoFactor ?? 1.0));
                break;

            case AvRepairStrategy.AudioPad:
                {
                    var audioStream = orderedStreams[FindIndex(orderedStreams, plan.AudioStreamIndex)];
                    var encoder = ResolvePreservingEncoder(audioStream.CodecName);
                    AddAudioFilter(args, audioOrdinal, encoder, audioStream,
                        $"apad=pad_dur={(plan.PadSeconds ?? 0).ToString("0.000000", CultureInfo.InvariantCulture)}");
                    break;
                }

            case AvRepairStrategy.AudioTrim:
                {
                    var audioStream = orderedStreams[FindIndex(orderedStreams, plan.AudioStreamIndex)];
                    var targetDuration = (audioStream.DurationSeconds ?? 0) - (plan.TrimSeconds ?? 0);
                    if (targetDuration <= 0)
                    {
                        throw new InvalidOperationException(
                            "Computed trim target duration is not positive; refusing to build an unsafe trim.");
                    }

                    var encoder = ResolvePreservingEncoder(audioStream.CodecName);
                    AddAudioFilter(args, audioOrdinal, encoder, audioStream,
                        $"atrim=end={targetDuration.ToString("0.000000", CultureInfo.InvariantCulture)}");
                    break;
                }

            case AvRepairStrategy.StreamCopyRemux:
                // No per-stream filter: the container itself is rewritten with pure stream copy.
                break;

            default:
                throw new InvalidOperationException($"Unsupported A/V repair strategy: {plan.Strategy}.");
        }

        args.Add("-y");
        args.Add(outputPath);

        return startInfo;
    }

    /// <summary>
    /// Builds a safe <c>atempo</c> filter chain. FFmpeg's single-filter range
    /// is [0.5, 2.0]; factors outside that range are chained, even though the
    /// planner's own safety margin currently never requests one.
    /// </summary>
    internal static string BuildAtempoExpression(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        var stages = new List<double>();
        var remaining = factor;
        while (remaining > 2.0)
        {
            stages.Add(2.0);
            remaining /= 2.0;
        }

        while (remaining < 0.5)
        {
            stages.Add(0.5);
            remaining /= 0.5;
        }

        stages.Add(remaining);
        return string.Join(",", stages.Select(
            static stage => $"atempo={stage.ToString("0.000000", CultureInfo.InvariantCulture)}"));
    }

    /// <summary>
    /// Audio codecs for which FFmpeg's encoder name is known and safe to use
    /// to preserve codec identity across an AudioPad/AudioTrim edit. Deliberately
    /// narrow: an unlisted codec (for example a lossless format with no free
    /// encoder, such as TrueHD) is never guessed at.
    /// </summary>
    private static readonly Dictionary<string, string> PreservingEncoderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["aac"] = "aac",
            ["ac3"] = "ac3",
            ["eac3"] = "eac3",
            ["flac"] = "flac",
            ["mp3"] = "libmp3lame",
            ["opus"] = "libopus",
            ["vorbis"] = "libvorbis",
            ["alac"] = "alac",
            ["pcm_s16le"] = "pcm_s16le",
            ["pcm_s24le"] = "pcm_s24le",
            ["pcm_s32le"] = "pcm_s32le"
        };

    /// <summary>
    /// True when <paramref name="sourceCodecName"/> has a known FFmpeg encoder
    /// that preserves codec identity. Used by <see cref="AvRepairPlanner"/> to
    /// refuse AudioPad/AudioTrim upfront, before any FFmpeg invocation, when the
    /// source codec cannot be safely preserved.
    /// </summary>
    public static bool CanPreserveCodec(string sourceCodecName) =>
        !string.IsNullOrEmpty(sourceCodecName) && PreservingEncoderMap.ContainsKey(sourceCodecName);

    private static string ResolvePreservingEncoder(string sourceCodecName)
    {
        if (!string.IsNullOrEmpty(sourceCodecName) && PreservingEncoderMap.TryGetValue(sourceCodecName, out var encoder))
        {
            return encoder;
        }

        throw new InvalidOperationException(
            $"No safe codec-preserving encoder is known for source audio codec '{sourceCodecName}'.");
    }

    private static int FindIndex(IReadOnlyList<MediaStreamInfo> streams, int index)
    {
        for (var i = 0; i < streams.Count; i++)
        {
            if (streams[i].Index == index)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Stream {index} was not found.");
    }

    private static void AddAudioFilter(
        System.Collections.ObjectModel.Collection<string> args,
        int audioOrdinal,
        string codec,
        MediaStreamInfo sourceAudioStream,
        string filterExpression)
    {
        args.Add($"-c:a:{audioOrdinal}");
        args.Add(codec);
        args.Add($"-filter:a:{audioOrdinal}");
        args.Add(filterExpression);

        if (sourceAudioStream.SampleRate is { } sampleRate)
        {
            args.Add($"-ar:a:{audioOrdinal}");
            args.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        }

        if (sourceAudioStream.Channels is { } channels)
        {
            args.Add($"-ac:a:{audioOrdinal}");
            args.Add(channels.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string ResolveFfmpegPath()
    {
        const string jellyfinFfmpegPath = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        return OperatingSystem.IsLinux() && File.Exists(jellyfinFfmpegPath) ? jellyfinFfmpegPath : "ffmpeg";
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
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

    private static void TryDeleteOutput(string path)
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
