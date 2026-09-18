using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Tasks;

/// <summary>
/// Repairs media files with classified, auto-repair-eligible audio/video
/// timeline anomalies. Every candidate is built outside the source tree,
/// validated against the original before replacement, and rolled back
/// immediately if post-replacement validation fails. Disabled by default
/// (<see cref="PluginConfiguration.EnableAudioVideoRepair"/>).
/// </summary>
public sealed class AvRepairTask : IScheduledTask
{
    private readonly PluginConfigurationService _configurationService;
    private readonly RepairQueueService _repairQueueService;
    private readonly MediaUseService _mediaUseService;
    private readonly MediaProbeService _mediaProbeService;
    private readonly AvRepairExecutionService _avRepairExecutionService;
    private readonly IMediaValidationService _mediaValidationService;
    private readonly MediaReplacementService _mediaReplacementService;
    private readonly PathSecurityService _pathSecurityService;
    private readonly AvRepairStatisticsService _avRepairStatisticsService;
    private readonly ILogger<AvRepairTask> _logger;

    public AvRepairTask(
        PluginConfigurationService configurationService,
        RepairQueueService repairQueueService,
        MediaUseService mediaUseService,
        MediaProbeService mediaProbeService,
        AvRepairExecutionService avRepairExecutionService,
        IMediaValidationService mediaValidationService,
        MediaReplacementService mediaReplacementService,
        PathSecurityService pathSecurityService,
        AvRepairStatisticsService avRepairStatisticsService,
        ILogger<AvRepairTask> logger)
    {
        _configurationService = configurationService;
        _repairQueueService = repairQueueService;
        _mediaUseService = mediaUseService;
        _mediaProbeService = mediaProbeService;
        _avRepairExecutionService = avRepairExecutionService;
        _mediaValidationService = mediaValidationService;
        _mediaReplacementService = mediaReplacementService;
        _pathSecurityService = pathSecurityService;
        _avRepairStatisticsService = avRepairStatisticsService;
        _logger = logger;
    }

    public string Name => "A/V Repair";

    public string Key => "AvRepair";

    public string Description =>
        "Repairs classified, auto-eligible audio/video timeline anomalies with validated, bounded FFmpeg operations.";

    public string Category => "Media Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var execution = await _repairQueueService.AcquireExecutionAsync(cancellationToken);

        _logger.LogInformation("[MediaIntegrity] [AvRepair] Task started.");
        progress.Report(0);

        var configuration = _configurationService.GetValidatedConfiguration();
        var queue = await _repairQueueService.LoadAsync(cancellationToken);

        var stats = new RunStats { DryRun = configuration.DryRun };

        if (!configuration.EnableAudioVideoRepair)
        {
            // Defensive: configuration may have been disabled after the scan
            // that populated these plans. Never execute a plan under a
            // disabled policy, regardless of what is persisted in the queue.
            _logger.LogInformation(
                "[MediaIntegrity] [AvRepair] EnableAudioVideoRepair=false; no A/V repair performed.");
            await _avRepairStatisticsService.SaveAsync(stats.ToSnapshot(), cancellationToken);
            progress.Report(100);
            return;
        }

        var allPending = queue.Files
            .Where(item => item.AvRepairPlans.Count > 0 && RepairRetryPolicy.IsCandidate(item, configuration))
            .ToArray();

        var pendingItems = configuration.DryRun
            ? allPending
            : allPending.Take(configuration.MaxAudioVideoRepairsPerRun).ToArray();

        _logger.LogInformation(
            "[MediaIntegrity] [AvRepair] {Pending} pending A/V item(s), {Selected} selected. " +
            "DryRun={DryRun}, MaxAudioVideoRepairsPerRun={Max}.",
            allPending.Length, pendingItems.Length, configuration.DryRun, configuration.MaxAudioVideoRepairsPerRun);

        if (pendingItems.Length == 0)
        {
            await _avRepairStatisticsService.SaveAsync(stats.ToSnapshot(), cancellationToken);
            progress.Report(100);
            _logger.LogInformation("[MediaIntegrity] [AvRepair] Task completed: no selected pending items.");
            return;
        }

        for (var index = 0; index < pendingItems.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = pendingItems[index];
            var temporaryOutputs = new List<string>();

            try
            {
                var validatedSource = _pathSecurityService.ValidateSourceFile(item.Path, configuration.SourceRoot);

                if (!configuration.DryRun && item.Attempts >= configuration.MaxRepairAttempts)
                {
                    item.Status = RepairQueueItemStatus.Failed;
                    item.LastError = $"{AvRepairReasonCodes.AudioVideoRepairRejected}: " +
                        $"Maximum repair attempts ({configuration.MaxRepairAttempts}) reached.";
                    stats.Failed++;
                    await _repairQueueService.SaveAsync(queue, cancellationToken);
                    continue;
                }

                if (_mediaUseService.IsMediaInUse(validatedSource))
                {
                    item.Status = RepairQueueItemStatus.Pending;
                    item.LastError = string.Empty;
                    stats.Skipped++;
                    await _repairQueueService.SaveAsync(queue, cancellationToken);
                    _logger.LogInformation(
                        "[MediaIntegrity] [AvRepair] Skipped because media is in use: {Path}.", item.Path);
                    continue;
                }

                item.Status = RepairQueueItemStatus.Processing;
                if (!configuration.DryRun)
                {
                    item.Attempts++;
                }

                item.LastAttemptAt = DateTimeOffset.UtcNow;
                item.LastError = string.Empty;
                await _repairQueueService.SaveAsync(queue, cancellationToken);
                stats.Attempted++;

                _logger.LogInformation(
                    "[MediaIntegrity] [AvRepair] {Code} {Path}: {PlanCount} plan(s).",
                    AvRepairReasonCodes.AudioVideoRepairPlanned, item.Path, item.AvRepairPlans.Count);

                // Chain plans sequentially: each stage reads the previous
                // stage's already-validated candidate. Original source stream
                // indices are always mapped in original relative order, so
                // they remain valid identifiers across every stage.
                var currentInputPath = validatedSource;
                var currentStreams = (await _mediaProbeService.ProbeAsync(
                    validatedSource, configuration.ProbeTimeoutSeconds, cancellationToken, enableAudioVideoSyncCheck: false))
                    .ScanResult.Streams;
                string? finalCandidatePath = null;

                foreach (var plan in item.AvRepairPlans)
                {
                    var stageResult = await _avRepairExecutionService.ExecuteAsync(
                        plan, currentInputPath, currentStreams, validatedSource, cancellationToken);

                    if (!stageResult.Success)
                    {
                        throw new InvalidOperationException(
                            $"{AvRepairReasonCodes.AudioVideoRepairValidationFailed}: " +
                            $"FFmpeg stage '{plan.Strategy}' failed with exit code {stageResult.ExitCode}. " +
                            NormalizeError(stageResult.StandardError));
                    }

                    temporaryOutputs.Add(stageResult.OutputPath);
                    var stageProbe = await _mediaProbeService.ProbeAsync(
                        stageResult.OutputPath, configuration.ProbeTimeoutSeconds, cancellationToken,
                        enableAudioVideoSyncCheck: false);
                    currentStreams = stageProbe.ScanResult.Streams;
                    currentInputPath = stageResult.OutputPath;
                    finalCandidatePath = stageResult.OutputPath;
                }

                if (finalCandidatePath is null)
                {
                    throw new InvalidOperationException("No A/V repair stage executed.");
                }

                var retimedStreamIndexes = item.AvRepairPlans
                    .Select(plan => plan.AudioStreamIndex)
                    .ToHashSet();

                var validation = await _mediaValidationService.ValidateAsync(
                    validatedSource, finalCandidatePath, cancellationToken, retimedStreamIndexes);

                if (!validation.Success)
                {
                    throw new InvalidOperationException(
                        $"{AvRepairReasonCodes.AudioVideoRepairValidationFailed}: " +
                        string.Join(" | ", validation.Errors));
                }

                if (validation.OutputScan is not null)
                {
                    var candidateDiagnoses = AvRepairClassifier.ClassifyAll(
                        validation.OutputScan, packetEvidence: null, configuration);

                    foreach (var plan in item.AvRepairPlans)
                    {
                        var stillAnomalous = candidateDiagnoses.FirstOrDefault(diagnosis =>
                            diagnosis.VideoStreamIndex == plan.VideoStreamIndex
                            && diagnosis.AudioStreamIndex == plan.AudioStreamIndex
                            && diagnosis.Classification != AvRepairClassification.None);

                        if (stillAnomalous is not null)
                        {
                            throw new InvalidOperationException(
                                $"{AvRepairReasonCodes.AudioVideoRepairValidationFailed}: " +
                                $"candidate still classifies as {stillAnomalous.Classification} for " +
                                $"video={plan.VideoStreamIndex} audio={plan.AudioStreamIndex}; the repair did not " +
                                "measurably improve synchronization.");
                        }
                    }
                }

                if (_mediaUseService.IsMediaInUse(validatedSource))
                {
                    item.Status = RepairQueueItemStatus.Pending;
                    item.LastError = string.Empty;
                    stats.Skipped++;
                    await _repairQueueService.SaveAsync(queue, cancellationToken);
                    _logger.LogInformation(
                        "[MediaIntegrity] [AvRepair] Skipped replacement; media became active: {Path}.", item.Path);
                    continue;
                }

                if (configuration.DryRun)
                {
                    item.Status = RepairQueueItemStatus.Pending;
                    item.LastError = string.Empty;
                    stats.Skipped++;
                    await _repairQueueService.SaveAsync(queue, cancellationToken);
                    _logger.LogInformation(
                        "[MediaIntegrity] [AvRepair] Dry-run validation succeeded for {Path}. No replacement performed.",
                        item.Path);
                    continue;
                }

                var replacement = await _mediaReplacementService.ReplaceAsync(
                    validatedSource, finalCandidatePath, cancellationToken,
                    () => _mediaUseService.IsMediaInUse(validatedSource),
                    retimedStreamIndexes);

                if (!replacement.Success || !replacement.ReplacementCompleted)
                {
                    if (replacement.RollbackAttempted)
                    {
                        stats.RolledBack++;
                    }

                    throw new InvalidOperationException(
                        $"{(replacement.RollbackAttempted ? AvRepairReasonCodes.AudioVideoRepairRolledBack : AvRepairReasonCodes.AudioVideoRepairValidationFailed)}: " +
                        (string.IsNullOrWhiteSpace(replacement.Error)
                            ? "Transactional replacement did not complete."
                            : replacement.Error));
                }

                item.Status = RepairQueueItemStatus.Repaired;
                item.LastError = string.Empty;
                stats.Succeeded++;

                _logger.LogInformation(
                    "[MediaIntegrity] [AvRepair] {Code} {Path}.",
                    AvRepairReasonCodes.AudioVideoRepairSucceeded, item.Path);

                if (configuration.DeleteBackupAfterSuccessfulValidation)
                {
                    DeleteBackup(replacement.BackupPath, ref stats);
                }

                await _repairQueueService.SaveAsync(queue, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (item.Status == RepairQueueItemStatus.Processing)
                {
                    item.Status = RepairQueueItemStatus.Pending;
                    item.LastError = "A/V repair interrupted by task cancellation.";
                    try
                    {
                        await _repairQueueService.SaveAsync(queue, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MediaIntegrity] [AvRepair] Unable to persist queue after cancellation.");
                    }
                }

                throw;
            }
            catch (Exception ex)
            {
                item.Status = RepairQueueItemStatus.Failed;
                item.LastError = ex.Message;
                stats.Failed++;

                _logger.LogError(ex, "[MediaIntegrity] [AvRepair] Repair failed for {Path}.", item.Path);

                try
                {
                    await _repairQueueService.SaveAsync(queue, cancellationToken);
                }
                catch (Exception saveException)
                {
                    _logger.LogError(saveException, "[MediaIntegrity] [AvRepair] Unable to persist failure state for {Path}.", item.Path);
                }
            }
            finally
            {
                foreach (var temporaryPath in temporaryOutputs)
                {
                    TryDeleteTemporaryFile(temporaryPath);
                }

                progress.Report((index + 1) * 100.0 / pendingItems.Length);
            }
        }

        await _avRepairStatisticsService.SaveAsync(stats.ToSnapshot(), cancellationToken);
        progress.Report(100);

        _logger.LogInformation(
            "[MediaIntegrity] [AvRepair] Task completed. Attempted: {Attempted}, Succeeded: {Succeeded}, " +
            "Failed: {Failed}, RolledBack: {RolledBack}, Skipped: {Skipped}, BackupsDeleted: {BackupsDeleted}.",
            stats.Attempted, stats.Succeeded, stats.Failed, stats.RolledBack, stats.Skipped, stats.BackupsDeleted);
    }

    private void DeleteBackup(string backupPath, ref RunStats stats)
    {
        try
        {
            var backupInfo = new FileInfo(backupPath);
            var size = backupInfo.Exists ? backupInfo.Length : 0;
            if (backupInfo.Exists)
            {
                backupInfo.Delete();
            }

            var metadataPath = backupPath + ".metadata.json";
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }

            stats.BackupsDeleted++;
            stats.BytesReclaimed += size;

            _logger.LogInformation(
                "[MediaIntegrity] [AvRepair] Deleted backup after validated success: {BackupPath} ({Size} bytes).",
                backupPath, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "[MediaIntegrity] [AvRepair] Unable to delete backup {BackupPath} after successful validation; " +
                "it remains on disk.", backupPath);
        }
    }

    private static string NormalizeError(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
    }

    private static void TryDeleteTemporaryFile(string path)
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

    private struct RunStats
    {
        public bool DryRun;
        public int Attempted;
        public int Succeeded;
        public int Failed;
        public int RolledBack;
        public int Skipped;
        public int BackupsDeleted;
        public long BytesReclaimed;

        public readonly LastAvRepairRunStats ToSnapshot() => new()
        {
            RunDate = DateTimeOffset.UtcNow,
            DryRun = DryRun,
            Attempted = Attempted,
            Succeeded = Succeeded,
            Failed = Failed,
            RolledBack = RolledBack,
            Skipped = Skipped,
            BackupsDeleted = BackupsDeleted,
            BytesReclaimed = BytesReclaimed
        };
    }
}
