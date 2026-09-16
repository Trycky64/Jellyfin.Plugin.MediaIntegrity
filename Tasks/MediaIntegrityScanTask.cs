using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Tasks;

/// <summary>
/// Scans Jellyfin media files for known integrity problems.
/// </summary>
public sealed class MediaIntegrityScanTask : IScheduledTask
{
    private readonly PluginConfigurationService _configurationService;
    private readonly LibraryMediaProvider _libraryMediaProvider;
    private readonly MediaProbeService _mediaProbeService;
    private readonly RepairQueueService _repairQueueService;
    private readonly ScanStatisticsService _statistics;
    private readonly ILogger<MediaIntegrityScanTask> _logger;

    public MediaIntegrityScanTask(
        PluginConfigurationService configurationService,
        LibraryMediaProvider libraryMediaProvider,
        MediaProbeService mediaProbeService,
        RepairQueueService repairQueueService,
        ILogger<MediaIntegrityScanTask> logger,
        ScanStatisticsService statistics)
    {
        _configurationService = configurationService;
        _libraryMediaProvider = libraryMediaProvider;
        _mediaProbeService = mediaProbeService;
        _repairQueueService = repairQueueService;
        _logger = logger;
        _statistics = statistics;
    }

    public string Name => "Media Integrity Scan";

    public string Key => "MediaIntegrityScan";

    public string Description =>
        "Scans media files for container and stream integrity problems.";

    public string Category => "Media Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        using var execution = await _repairQueueService.AcquireExecutionAsync(cancellationToken);

        _logger.LogInformation(
            "[MediaIntegrity] [Scan] Scan started.");

        progress.Report(0);

        var configuration =
            _configurationService.GetValidatedConfiguration();

        var mediaFiles =
            _libraryMediaProvider.GetMediaFiles(
                configuration.EnableVideoFiles,
                configuration.EnableAudioFiles);

        var total =
            mediaFiles.Count;

        _logger.LogInformation(
            "[MediaIntegrity] [Scan] "
            + "Found {Count} supported media file(s).",
            total);

        var queue =
            RepairQueueService.CreateEmpty();

        var summary =
            queue.Summary;

        if (total == 0)
        {
            await _repairQueueService.SaveAsync(
                queue,
                cancellationToken);

            await _statistics.SaveAsync(LastScanStats.FromQueue(queue), cancellationToken);

            progress.Report(100);

            _logger.LogInformation(
                "[MediaIntegrity] [Scan] "
                + "Scan completed: no supported media files found.");

            return;
        }

        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path =
                mediaFiles[index];

            try
            {
                _logger.LogDebug(
                    "[MediaIntegrity] [Scan] "
                    + "Scanning {Current}/{Total}: {Path}.",
                    index + 1,
                    total,
                    path);

                var probeResult =
                    await _mediaProbeService.ProbeAsync(
                        path,
                        configuration.ProbeTimeoutSeconds,
                        cancellationToken);

                ProcessScanResult(
                    probeResult.ScanResult,
                    summary,
                    queue);

                LogScanResult(
                    probeResult.ScanResult);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "[MediaIntegrity] [Scan] "
                    + "Scan cancellation requested.");

                throw;
            }
            catch (TimeoutException ex)
            {
                summary.Checked++;
                summary.Unreadable++;

                _logger.LogWarning(
                    ex,
                    "[MediaIntegrity] [Scan] "
                    + "ffprobe timed out for {Path}.",
                    path);
            }
            catch (Exception ex)
            {
                summary.Checked++;
                summary.Unreadable++;

                _logger.LogError(
                    ex,
                    "[MediaIntegrity] [Scan] "
                    + "Unexpected failure while scanning {Path}. "
                    + "Continuing with next media.",
                    path);
            }

            progress.Report(
                (index + 1) * 100.0 / total);
        }

        cancellationToken.ThrowIfCancellationRequested();

        queue.GeneratedAt =
            DateTimeOffset.UtcNow;

        await _repairQueueService.SaveAsync(
            queue,
            cancellationToken);

        await _statistics.SaveAsync(LastScanStats.FromQueue(queue), cancellationToken);

        progress.Report(100);

        _logger.LogInformation(
            "[MediaIntegrity] [Scan] "
            + "Scan completed. Checked: {Checked}, "
            + "Healthy: {Ok}, Warnings: {Warnings}, "
            + "Repairable: {Repairable}, "
            + "Corrupted: {Corrupted}, "
            + "Unreadable: {Unreadable}, "
            + "Queued: {Queued}.",
            summary.Checked,
            summary.Ok,
            summary.Warning,
            summary.Repairable,
            summary.Corrupted,
            summary.Unreadable,
            queue.Files.Count);
    }

    private static void ProcessScanResult(
        MediaScanResult scanResult,
        RepairQueueSummary summary,
        RepairQueue queue)
    {
        summary.Checked++;

        switch (scanResult.Status)
        {
            case MediaIntegrityStatus.Ok:
                summary.Ok++;
                break;

            case MediaIntegrityStatus.Warning:
                summary.Warning++;
                break;

            case MediaIntegrityStatus.RemuxRecommended:
                summary.Repairable++;

                queue.Files.Add(
                    CreateQueueItem(scanResult));

                break;

            case MediaIntegrityStatus.Corrupted:
                summary.Corrupted++;

                if (IsSafelyQueueableCorruption(
                        scanResult))
                {
                    queue.Files.Add(
                        CreateQueueItem(scanResult));
                }

                break;

            case MediaIntegrityStatus.Unreadable:
                summary.Unreadable++;
                break;

            default:
                summary.Warning++;
                break;
        }
    }

    private static RepairQueueItem CreateQueueItem(
        MediaScanResult scanResult)
    {
        return new RepairQueueItem
        {
            Path = scanResult.Path,
            Extension = scanResult.Extension,
            Container = scanResult.Container,
            IntegrityStatus = scanResult.Status,
            Issues = scanResult.Issues
                .Select(
                    static issue => new MediaIssue
                    {
                        Code = issue.Code,
                        Message = issue.Message,
                        Severity = issue.Severity,
                        RawMessage = issue.RawMessage
                    })
                .ToList(),
            Status = RepairQueueItemStatus.Pending,
            Attempts = 0,
            LastError = string.Empty,
            LastAttemptAt = null
        };
    }

    private static bool IsSafelyQueueableCorruption(
        MediaScanResult scanResult)
    {
        var hasCriticalIssue =
            scanResult.Issues.Any(
                static issue =>
                    issue.Severity
                    == MediaIssueSeverity.Critical);

        if (hasCriticalIssue)
        {
            return false;
        }

        return scanResult.Issues.Any(
            static issue =>
                issue.Severity
                == MediaIssueSeverity.Repairable);
    }

    private void LogScanResult(
        MediaScanResult scanResult)
    {
        if (scanResult.Status
            == MediaIntegrityStatus.Ok)
        {
            _logger.LogDebug(
                "[MediaIntegrity] [Scan] "
                + "Healthy media: {Path}.",
                scanResult.Path);

            return;
        }

        var issueCodes =
            scanResult.Issues.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    scanResult.Issues.Select(
                        static issue => issue.Code));

        _logger.LogWarning(
            "[MediaIntegrity] [Scan] "
            + "Media status {Status}: {Path}. "
            + "Issues: {Issues}.",
            scanResult.Status,
            scanResult.Path,
            issueCodes);
    }
}
