using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Tasks;

/// <summary>
/// Repairs queued media files using validated lossless stream-copy remuxing.
/// </summary>
public sealed class MediaRemuxRepairTask : IScheduledTask
{
    private readonly PluginConfigurationService _configurationService;
    private readonly RepairQueueService _repairQueueService;
    private readonly MediaUseService _mediaUseService;
    private readonly MediaRemuxService _mediaRemuxService;
    private readonly MediaValidationService _mediaValidationService;
    private readonly MediaReplacementService _mediaReplacementService;
    private readonly PathSecurityService _pathSecurityService;
    private readonly ILogger<MediaRemuxRepairTask> _logger;

    public MediaRemuxRepairTask(
        PluginConfigurationService configurationService,
        RepairQueueService repairQueueService,
        MediaUseService mediaUseService,
        MediaRemuxService mediaRemuxService,
        MediaValidationService mediaValidationService,
        MediaReplacementService mediaReplacementService,
        PathSecurityService pathSecurityService,
        ILogger<MediaRemuxRepairTask> logger)
    {
        _configurationService = configurationService;
        _repairQueueService = repairQueueService;
        _mediaUseService = mediaUseService;
        _mediaRemuxService = mediaRemuxService;
        _mediaValidationService = mediaValidationService;
        _mediaReplacementService = mediaReplacementService;
        _pathSecurityService = pathSecurityService;
        _logger = logger;
    }

    public string Name => "Media Remux Repair";

    public string Key => "MediaRemuxRepair";

    public string Description =>
        "Repairs queued media files using lossless stream-copy remuxing.";

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
            "[MediaIntegrity] [Repair] Repair task started.");

        progress.Report(0);

        var configuration =
            _configurationService.GetValidatedConfiguration();

        var queue =
            await _repairQueueService.LoadAsync(
                cancellationToken);

        var allPendingItems =
            queue.Files
                .Where(
                    item => RepairRetryPolicy.IsCandidate(item, configuration))
                .ToArray();

        /*
         * Dry-run may validate the complete pending queue.
         *
         * A real repair run is explicitly limited by
         * MaxRepairsPerRun.
         */
        var pendingItems =
            configuration.DryRun
                ? allPendingItems
                : allPendingItems
                    .Take(configuration.MaxRepairsPerRun)
                    .ToArray();

        var totalPending =
            allPendingItems.Length;

        var queued =
            pendingItems.Length;

        var deferredByBatchLimit =
            totalPending - queued;

        var repaired = 0;
        var skipped = 0;
        var failed = 0;

        _logger.LogInformation(
            "[MediaIntegrity] [Repair] "
            + "Loaded {Total} queue item(s), "
            + "{Pending} pending or retryable, "
            + "{Selected} selected for this execution. "
            + "DryRun={DryRun}, MaxRepairsPerRun={MaxRepairsPerRun}.",
            queue.Files.Count,
            totalPending,
            queued,
            configuration.DryRun,
            configuration.MaxRepairsPerRun);

        if (deferredByBatchLimit > 0)
        {
            _logger.LogInformation(
                "[MediaIntegrity] [Repair] "
                + "{Deferred} pending item(s) deferred by "
                + "MaxRepairsPerRun and will remain Pending.",
                deferredByBatchLimit);
        }

        if (queued == 0)
        {
            progress.Report(100);

            _logger.LogInformation(
                "[MediaIntegrity] [Repair] "
                + "Repair task completed: no selected pending items.");

            return;
        }

        for (var index = 0; index < pendingItems.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item =
                pendingItems[index];

            string? temporaryPath = null;

            try
            {
                var validatedSourcePath =
                    _pathSecurityService.ValidateSourceFile(
                        item.Path,
                        configuration.SourceRoot);

                if (!IsEligibleForRepair(
                        item,
                        configuration))
                {
                    skipped++;

                    _logger.LogInformation(
                        "[MediaIntegrity] [Repair] "
                        + "Skipped {Path}: integrity status {Status} "
                        + "is not eligible for repair.",
                        item.Path,
                        item.IntegrityStatus);

                    continue;
                }

                if (!configuration.DryRun
                    && item.Attempts
                    >= configuration.MaxRepairAttempts)
                {
                    item.Status =
                        RepairQueueItemStatus.Failed;

                    item.LastError =
                        $"Maximum repair attempts "
                        + $"({configuration.MaxRepairAttempts}) reached.";

                    failed++;

                    await _repairQueueService.SaveAsync(
                        queue,
                        cancellationToken);

                    _logger.LogWarning(
                        "[MediaIntegrity] [Repair] "
                        + "Maximum repair attempts reached for {Path}.",
                        item.Path);

                    continue;
                }

                if (_mediaUseService.IsMediaInUse(
                        validatedSourcePath))
                {
                    skipped++;

                    item.Status =
                        RepairQueueItemStatus.Pending;

                    item.LastError =
                        string.Empty;

                    _logger.LogInformation(
                        "[MediaIntegrity] [Repair] "
                        + "Skipped because media is currently in use: {Path}. "
                        + "Item remains Pending.",
                        item.Path);

                    continue;
                }

                item.Status =
                    RepairQueueItemStatus.Processing;

                if (!configuration.DryRun)
                {
                    item.Attempts++;
                }

                item.LastAttemptAt =
                    DateTimeOffset.UtcNow;

                item.LastError =
                    string.Empty;

                await _repairQueueService.SaveAsync(
                    queue,
                    cancellationToken);

                if (configuration.DryRun)
                {
                    _logger.LogInformation(
                        "[MediaIntegrity] [Repair] "
                        + "Dry-run processing {Current}/{Total}: {Path}.",
                        index + 1,
                        queued,
                        item.Path);
                }
                else
                {
                    _logger.LogInformation(
                        "[MediaIntegrity] [Repair] "
                        + "Processing {Current}/{Total}: {Path}. "
                        + "Attempt {Attempt}/{MaxAttempts}.",
                        index + 1,
                        queued,
                        item.Path,
                        item.Attempts,
                        configuration.MaxRepairAttempts);
                }

                var remuxResult =
                    await _mediaRemuxService.RemuxAsync(
                        validatedSourcePath,
                        cancellationToken);

                temporaryPath =
                    remuxResult.OutputPath;

                if (!remuxResult.Success)
                {
                    throw new InvalidOperationException(
                        $"Lossless remux failed with exit code "
                        + $"{remuxResult.ExitCode}. "
                        + NormalizeError(
                            remuxResult.StandardError));
                }

                var validationResult =
                    await _mediaValidationService.ValidateAsync(
                        validatedSourcePath,
                        temporaryPath,
                        cancellationToken);

                if (!validationResult.Success)
                {
                    throw new InvalidOperationException(
                        "Remux validation failed: "
                        + string.Join(
                            " | ",
                            validationResult.Errors));
                }

                /*
                 * Playback may have started while remuxing.
                 * Check again immediately before the destructive phase.
                 */
                if (_mediaUseService.IsMediaInUse(
                        validatedSourcePath))
                {
                    item.Status =
                        RepairQueueItemStatus.Pending;

                    item.LastError =
                        string.Empty;

                    skipped++;

                    await _repairQueueService.SaveAsync(
                        queue,
                        cancellationToken);

                    _logger.LogInformation(
                        "[MediaIntegrity] [Repair] "
                        + "Skipped replacement because media became "
                        + "active during repair: {Path}. "
                        + "Item remains Pending.",
                        item.Path);

                    continue;
                }

                if (configuration.DryRun)
                {
                    item.Status =
                        RepairQueueItemStatus.Pending;

                    item.LastError =
                        string.Empty;

                    skipped++;

                    await _repairQueueService.SaveAsync(
                        queue,
                        cancellationToken);

                    _logger.LogInformation(
                        "[MediaIntegrity] [Repair] "
                        + "Dry-run validation succeeded for {Path}. "
                        + "No replacement performed; item remains Pending.",
                        item.Path);

                    continue;
                }

                var replacementResult =
                    await _mediaReplacementService.ReplaceAsync(
                        validatedSourcePath,
                        temporaryPath,
                        cancellationToken,
                        () => _mediaUseService.IsMediaInUse(validatedSourcePath));

                if (!replacementResult.Success
                    || !replacementResult.ReplacementCompleted)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(
                            replacementResult.Error)
                            ? "Transactional replacement did not complete."
                            : replacementResult.Error);
                }

                item.Status =
                    RepairQueueItemStatus.Repaired;

                item.LastError =
                    string.Empty;

                repaired++;

                await _repairQueueService.SaveAsync(
                    queue,
                    cancellationToken);

                _logger.LogInformation(
                    "[MediaIntegrity] [Repair] "
                    + "Successfully repaired {Path}.",
                    item.Path);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                if (item.Status
                    == RepairQueueItemStatus.Processing)
                {
                    item.Status =
                        RepairQueueItemStatus.Pending;

                    item.LastError =
                        "Repair interrupted by task cancellation.";

                    try
                    {
                        await _repairQueueService.SaveAsync(
                            queue,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[MediaIntegrity] [Repair] "
                            + "Unable to persist queue after cancellation.");
                    }
                }

                throw;
            }
            catch (Exception ex)
            {
                item.Status =
                    RepairQueueItemStatus.Failed;

                item.LastError =
                    ex.Message;

                failed++;

                _logger.LogError(
                    ex,
                    "[MediaIntegrity] [Repair] "
                    + "Repair failed for {Path}.",
                    item.Path);

                try
                {
                    await _repairQueueService.SaveAsync(
                        queue,
                        cancellationToken);
                }
                catch (Exception saveException)
                {
                    _logger.LogError(
                        saveException,
                        "[MediaIntegrity] [Repair] "
                        + "Unable to persist failure state for {Path}.",
                        item.Path);
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(
                        temporaryPath))
                {
                    TryDeleteTemporaryFile(
                        temporaryPath);
                }

                progress.Report(
                    (index + 1) * 100.0
                    / pendingItems.Length);
            }
        }

        progress.Report(100);

        _logger.LogInformation(
            "[MediaIntegrity] [Repair] "
            + "Repair task completed. "
            + "Pending before run: {TotalPending}, "
            + "Selected: {Selected}, "
            + "Repaired: {Repaired}, "
            + "Skipped: {Skipped}, "
            + "Failed: {Failed}, "
            + "Deferred by batch limit: {Deferred}.",
            totalPending,
            queued,
            repaired,
            skipped,
            failed,
            deferredByBatchLimit);
    }

    private static bool IsEligibleForRepair(
        RepairQueueItem item,
        PluginConfiguration configuration)
    {
        if (item.IntegrityStatus
            == MediaIntegrityStatus.RemuxRecommended)
        {
            return true;
        }

        return item.IntegrityStatus
                == MediaIntegrityStatus.Corrupted
            && configuration.AllowRepairOfCorrupted;
    }

    private static string NormalizeError(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace(
                Environment.NewLine,
                " ",
                StringComparison.Ordinal)
            .Trim();
    }

    private static void TryDeleteTemporaryFile(
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
            // Best effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }
}
