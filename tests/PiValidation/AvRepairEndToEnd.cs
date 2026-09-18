using System.Diagnostics;
using System.Security.Cryptography;
using Jellyfin.Plugin.MediaIntegrity;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiValidation;

/// <summary>
/// Real, credential-free end-to-end proof of the v1.2.0 A/V repair engine
/// against synthetic FFmpeg fixtures in a throwaway sandbox. Never touches
/// the real media library. Exercises the exact production classes
/// (MediaProbeService, PacketTimelineAnalyzer, AvRepairClassifier,
/// AvRepairPlanner, AvRepairExecutionService, MediaValidationService,
/// MediaReplacementService) with real ffmpeg/ffprobe processes.
/// </summary>
public static class AvRepairEndToEnd
{
    public static async Task<Dictionary<string, object>> RunAsync()
    {
        var report = new Dictionary<string, object>();
        var root = Path.Combine(Path.GetTempPath(), "av-repair-e2e-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "media");
        var repairRoot = Path.Combine(root, "repair-media");
        var backupRoot = Path.Combine(root, "backups");
        var tempRoot = Path.Combine(root, "cache");
        foreach (var dir in new[] { sourceRoot, repairRoot, backupRoot, tempRoot })
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            var configuration = new PluginConfiguration
            {
                DryRun = false,
                SourceRoot = sourceRoot,
                RepairRoot = repairRoot,
                BackupRoot = backupRoot,
                TempRoot = tempRoot,
                EnableAudioVideoRepair = true,
                AllowAudioReencode = true,
                MinRepairConfidence = 0.75,
                MaxAutoRepairOffsetSeconds = 5.0,
                MaxAutoRepairDurationDeltaSeconds = 2.0,
                DeleteBackupAfterSuccessfulValidation = false
            };

            var configService = new PluginConfigurationService(NullLogger<PluginConfigurationService>.Instance, configuration);
            var security = new PathSecurityService(NullLogger<PathSecurityService>.Instance);
            var mapper = new PathMapper(configService, security, NullLogger<PathMapper>.Instance);
            var detector = new MediaIssueDetector();
            var probe = new MediaProbeService(NullLogger<MediaProbeService>.Instance, detector);
            var validationService = new MediaValidationService(configService, probe, NullLogger<MediaValidationService>.Instance);
            var replacementService = new MediaReplacementService(configService, mapper, security, validationService, NullLogger<MediaReplacementService>.Instance);
            var executionService = new AvRepairExecutionService(configService, mapper, NullLogger<AvRepairExecutionService>.Instance);

            report["constantOffset"] = await RunConstantOffsetCase(sourceRoot, repairRoot, probe, executionService, validationService, replacementService, configuration);
            report["audioEndsEarlyPad"] = await RunAudioPadCase(sourceRoot, repairRoot, probe, executionService, validationService, replacementService, configuration);
            report["multiAudioChained"] = await RunMultiAudioCase(sourceRoot, repairRoot, probe, executionService, validationService, replacementService, configuration);
            report["backupDeletedAfterSuccess"] = await RunBackupDeletionCase(sourceRoot, repairRoot, probe, executionService, validationService, replacementService, configuration);
            report["partialTailWindowMustNotAutoRepair"] = await RunPartialTailWindowCase(sourceRoot, probe);
            report["consistentBoundedTrimStillEligible"] = await RunConsistentTrimPlanningCase(sourceRoot, probe);

            return report;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<object> RunConstantOffsetCase(
        string sourceRoot, string repairRoot, MediaProbeService probe, AvRepairExecutionService executionService,
        MediaValidationService validationService, MediaReplacementService replacementService, PluginConfiguration configuration)
    {
        var relative = Path.Combine("series", "E2E-ConstantOffset", "test.mkv");
        var source = Path.Combine(sourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RunFfmpeg(
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=6",
            "-itsoffset", "1.5", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-g", "5", "-keyint_min", "5", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", source);
        MirrorToRepairRoot(sourceRoot, repairRoot, relative);

        var beforeProbe = await probe.ProbeAsync(source, 30, CancellationToken.None);
        var packets = await PacketTimelineAnalyzer.FetchPacketsAsync(source, beforeProbe.ScanResult.DurationSeconds ?? 0, 30, CancellationToken.None);
        var evidence = PacketTimelineAnalyzer.ComputeEvidence(beforeProbe.ScanResult, packets);
        var diagnoses = AvRepairClassifier.ClassifyAll(beforeProbe.ScanResult, evidence, configuration);
        var plans = AvRepairPlanner.PlanMedia(diagnoses, configuration);
        var plan = plans.Single();

        if (!plan.IsAutoRepairEligible)
        {
            return new
            {
                classification = diagnoses.Single().Classification.ToString(),
                confidence = diagnoses.Single().Confidence,
                reason = plan.Reason,
                startOffset = diagnoses.Single().StartOffsetSeconds,
                durationDelta = diagnoses.Single().DurationDeltaSeconds,
                packetEvidence = diagnoses.Single().PacketEvidence
            };
        }

        var retimed = new HashSet<int> { plan.AudioStreamIndex };
        var stage = await executionService.ExecuteAsync(plan, source, beforeProbe.ScanResult.Streams, source, CancellationToken.None);
        var validation = await validationService.ValidateAsync(source, stage.OutputPath, CancellationToken.None, retimed);
        var afterDiagnoses = AvRepairClassifier.ClassifyAll(validation.OutputScan!, packetEvidence: null, configuration);
        var replacement = await replacementService.ReplaceAsync(source, stage.OutputPath, CancellationToken.None, isMediaInUse: null, intentionallyRetimedStreamIndexes: retimed);
        File.Delete(stage.OutputPath);

        return new
        {
            classification = diagnoses.Single().Classification.ToString(),
            confidence = diagnoses.Single().Confidence,
            strategy = plan.Strategy.ToString(),
            timestampShiftSeconds = plan.TimestampShiftSeconds,
            ffmpegSuccess = stage.Success,
            candidateValidationSuccess = validation.Success,
            candidateValidationErrors = validation.Errors,
            candidateStillAnomalous = afterDiagnoses.Any(d => d.Classification != AvRepairClassification.None),
            replacementSuccess = replacement.Success,
            replacementError = replacement.Error,
            backupCreated = replacement.BackupCreated,
            backupSha256MatchesRecordedManifest = replacement.Success && Sha256(replacement.BackupPath) == ReadRecordedSourceHash(replacement)
        };
    }

    private static async Task<object> RunAudioPadCase(
        string sourceRoot, string repairRoot, MediaProbeService probe, AvRepairExecutionService executionService,
        MediaValidationService validationService, MediaReplacementService replacementService, PluginConfiguration configuration)
    {
        // Deliberately mp3/mkv, not aac/mp4: this exact combination is what a
        // real-library pilot run found broken (AvRepairExecutionService forced
        // the configured AudioReencodeCodec instead of preserving the source
        // codec, which MediaValidationService's codec-identity check then
        // correctly rejected). This fixture reproduces that real failure mode.
        var relative = Path.Combine("Movies", "E2E-AudioPad", "test.mkv");
        var source = Path.Combine(sourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RunFfmpeg(
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=7",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-g", "5", "-keyint_min", "5", "-pix_fmt", "yuv420p",
            "-c:a", "libmp3lame", "-ar", "48000", "-ac", "2", source);
        MirrorToRepairRoot(sourceRoot, repairRoot, relative);

        var beforeProbe = await probe.ProbeAsync(source, 30, CancellationToken.None);
        var packets = await PacketTimelineAnalyzer.FetchPacketsAsync(source, beforeProbe.ScanResult.DurationSeconds ?? 0, 30, CancellationToken.None);
        var evidence = PacketTimelineAnalyzer.ComputeEvidence(beforeProbe.ScanResult, packets);
        var diagnoses = AvRepairClassifier.ClassifyAll(beforeProbe.ScanResult, evidence, configuration);
        var plans = AvRepairPlanner.PlanMedia(diagnoses, configuration);
        var plan = plans.Single();

        if (!plan.IsAutoRepairEligible)
        {
            return new
            {
                classification = diagnoses.Single().Classification.ToString(),
                confidence = diagnoses.Single().Confidence,
                reason = plan.Reason,
                durationDelta = diagnoses.Single().DurationDeltaSeconds,
                packetEvidence = diagnoses.Single().PacketEvidence
            };
        }

        var retimed = new HashSet<int> { plan.AudioStreamIndex };
        var stage = await executionService.ExecuteAsync(plan, source, beforeProbe.ScanResult.Streams, source, CancellationToken.None);
        var validation = await validationService.ValidateAsync(source, stage.OutputPath, CancellationToken.None, retimed);
        var afterDiagnoses = AvRepairClassifier.ClassifyAll(validation.OutputScan!, packetEvidence: null, configuration);
        var candidateDurationDelta = (validation.OutputScan!.Streams.First(s => s.CodecType == "audio").DurationSeconds ?? 0)
            - (validation.OutputScan!.Streams.First(s => s.CodecType == "video").DurationSeconds ?? 0);
        var replacement = await replacementService.ReplaceAsync(source, stage.OutputPath, CancellationToken.None, isMediaInUse: null, intentionallyRetimedStreamIndexes: retimed);
        File.Delete(stage.OutputPath);

        return new
        {
            classification = diagnoses.Single().Classification.ToString(),
            strategy = plan.Strategy.ToString(),
            padSeconds = plan.PadSeconds,
            ffmpegSuccess = stage.Success,
            candidateValidationSuccess = validation.Success,
            candidateValidationErrors = validation.Errors,
            candidateDurationDeltaSeconds = candidateDurationDelta,
            candidateStillAnomalous = afterDiagnoses.Any(d => d.Classification != AvRepairClassification.None),
            replacementSuccess = replacement.Success,
            replacementError = replacement.Error
        };
    }

    private static async Task<object> RunMultiAudioCase(
        string sourceRoot, string repairRoot, MediaProbeService probe, AvRepairExecutionService executionService,
        MediaValidationService validationService, MediaReplacementService replacementService, PluginConfiguration configuration)
    {
        var relative = Path.Combine("series", "E2E-MultiAudio", "test.mkv");
        var source = Path.Combine(sourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RunFfmpeg(
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=6",
            "-itsoffset", "1.2", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=5",
            "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-g", "5", "-keyint_min", "5", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", source);
        MirrorToRepairRoot(sourceRoot, repairRoot, relative);

        var beforeProbe = await probe.ProbeAsync(source, 30, CancellationToken.None);
        var packets = await PacketTimelineAnalyzer.FetchPacketsAsync(source, beforeProbe.ScanResult.DurationSeconds ?? 0, 30, CancellationToken.None);
        var evidence = PacketTimelineAnalyzer.ComputeEvidence(beforeProbe.ScanResult, packets);
        var diagnoses = AvRepairClassifier.ClassifyAll(beforeProbe.ScanResult, evidence, configuration);
        var plans = AvRepairPlanner.PlanMedia(diagnoses, configuration).Where(p => p.IsAutoRepairEligible).ToList();

        if (plans.Count < 2)
        {
            return new
            {
                planCount = plans.Count,
                diagnoses = diagnoses.Select(d => new { d.AudioStreamIndex, d.Classification, d.Confidence, d.Reason }).ToList()
            };
        }

        var currentInput = source;
        var currentStreams = beforeProbe.ScanResult.Streams;
        string? finalCandidate = null;
        var stageResults = new List<object>();
        foreach (var plan in plans)
        {
            var stage = await executionService.ExecuteAsync(plan, currentInput, currentStreams, source, CancellationToken.None);
            stageResults.Add(new { audio = plan.AudioStreamIndex, strategy = plan.Strategy.ToString(), success = stage.Success });
            var stageProbe = await probe.ProbeAsync(stage.OutputPath, 30, CancellationToken.None);
            currentStreams = stageProbe.ScanResult.Streams;
            currentInput = stage.OutputPath;
            finalCandidate = stage.OutputPath;
        }

        var retimed = plans.Select(p => p.AudioStreamIndex).ToHashSet();
        var validation = await validationService.ValidateAsync(source, finalCandidate!, CancellationToken.None, retimed);
        var afterDiagnoses = AvRepairClassifier.ClassifyAll(validation.OutputScan!, packetEvidence: null, configuration);
        var replacement = await replacementService.ReplaceAsync(source, finalCandidate!, CancellationToken.None, isMediaInUse: null, intentionallyRetimedStreamIndexes: retimed);
        if (File.Exists(finalCandidate))
        {
            File.Delete(finalCandidate);
        }

        return new
        {
            planCount = plans.Count,
            stages = stageResults,
            candidateValidationSuccess = validation.Success,
            candidateValidationErrors = validation.Errors,
            candidateStillAnomalous = afterDiagnoses.Any(d => d.Classification != AvRepairClassification.None),
            replacementSuccess = replacement.Success,
            replacementError = replacement.Error
        };
    }

    private static async Task<object> RunBackupDeletionCase(
        string sourceRoot, string repairRoot, MediaProbeService probe, AvRepairExecutionService executionService,
        MediaValidationService validationService, MediaReplacementService replacementService, PluginConfiguration configuration)
    {
        var relative = Path.Combine("Movies", "E2E-BackupDeletion", "test.mp4");
        var source = Path.Combine(sourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RunFfmpeg(
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=6",
            "-itsoffset", "0.8", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-g", "5", "-keyint_min", "5", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", source);
        MirrorToRepairRoot(sourceRoot, repairRoot, relative);

        var beforeProbe = await probe.ProbeAsync(source, 30, CancellationToken.None);
        var packets = await PacketTimelineAnalyzer.FetchPacketsAsync(source, beforeProbe.ScanResult.DurationSeconds ?? 0, 30, CancellationToken.None);
        var evidence = PacketTimelineAnalyzer.ComputeEvidence(beforeProbe.ScanResult, packets);
        var diagnoses = AvRepairClassifier.ClassifyAll(beforeProbe.ScanResult, evidence, configuration);
        var plans = AvRepairPlanner.PlanMedia(diagnoses, configuration);
        var plan = plans.Single();

        if (!plan.IsAutoRepairEligible)
        {
            return new
            {
                classification = diagnoses.Single().Classification.ToString(),
                confidence = diagnoses.Single().Confidence,
                reason = plan.Reason
            };
        }

        var retimed = new HashSet<int> { plan.AudioStreamIndex };
        var stage = await executionService.ExecuteAsync(plan, source, beforeProbe.ScanResult.Streams, source, CancellationToken.None);
        var validation = await validationService.ValidateAsync(source, stage.OutputPath, CancellationToken.None, retimed);
        var replacement = await replacementService.ReplaceAsync(source, stage.OutputPath, CancellationToken.None, isMediaInUse: null, intentionallyRetimedStreamIndexes: retimed);
        File.Delete(stage.OutputPath);

        var backupExistedBeforeDeletion = File.Exists(replacement.BackupPath);
        var metadataPath = replacement.BackupPath + ".metadata.json";
        var metadataExistedBeforeDeletion = File.Exists(metadataPath);

        // Mirrors AvRepairTask.DeleteBackup: only reachable after Success && ReplacementCompleted.
        if (replacement.Success && replacement.ReplacementCompleted)
        {
            File.Delete(replacement.BackupPath);
            File.Delete(metadataPath);
        }

        return new
        {
            replacementSuccess = replacement.Success,
            replacementError = replacement.Error,
            backupExistedBeforeDeletion,
            metadataExistedBeforeDeletion,
            backupRemovedAfterDeletion = !File.Exists(replacement.BackupPath),
            metadataRemovedAfterDeletion = !File.Exists(metadataPath)
        };
    }

    /// <summary>
    /// v1.2.1 regression: the audio track runs 10.8s longer than the video
    /// track (start in sync). The real packet sampler's tail window does not
    /// reach the audio's real end, so packet evidence alone would show a
    /// small, "bounded" drift. Planning only: nothing is executed or modified.
    /// </summary>
    private static async Task<object> RunPartialTailWindowCase(string sourceRoot, MediaProbeService probe)
    {
        var source = Path.Combine(sourceRoot, "Movies", "E2E-PartialTailWindow", "test.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RunFfmpeg(
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=40.8",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-pix_fmt", "yuv420p",
            "-c:a", "aac", source);

        var configuration = PlanningOnlyConfiguration();
        var beforeProbe = await probe.ProbeAsync(source, 30, CancellationToken.None);
        var packets = await PacketTimelineAnalyzer.FetchPacketsAsync(source, beforeProbe.ScanResult.DurationSeconds ?? 0, 30, CancellationToken.None);
        var evidence = PacketTimelineAnalyzer.ComputeEvidence(beforeProbe.ScanResult, packets);
        var diagnosis = AvRepairClassifier.ClassifyAll(beforeProbe.ScanResult, evidence, configuration).Single();
        var plan = AvRepairPlanner.PlanMedia([diagnosis], configuration).Single();

        return new
        {
            metadataDurationDeltaSeconds = diagnosis.DurationDeltaSeconds,
            packetDriftSeconds = diagnosis.PacketEvidence?.Drift,
            packetDriftBelowAutoRepairLimit = Math.Abs(diagnosis.PacketEvidence?.Drift ?? double.MaxValue)
                <= configuration.MaxAutoRepairDurationDeltaSeconds,
            classification = diagnosis.Classification.ToString(),
            strategy = plan.Strategy.ToString(),
            trimSeconds = plan.TrimSeconds,
            autoRepairEligible = plan.IsAutoRepairEligible,
            reason = diagnosis.Reason
        };
    }

    /// <summary>Control: a small, coherent end mismatch is still planned as AudioTrim. Planning only.</summary>
    private static async Task<object> RunConsistentTrimPlanningCase(string sourceRoot, MediaProbeService probe)
    {
        var source = Path.Combine(sourceRoot, "Movies", "E2E-ConsistentTrim", "test.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await RunFfmpeg(
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=9",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-g", "5", "-keyint_min", "5", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", source);

        var configuration = PlanningOnlyConfiguration();
        var beforeProbe = await probe.ProbeAsync(source, 30, CancellationToken.None);
        var packets = await PacketTimelineAnalyzer.FetchPacketsAsync(source, beforeProbe.ScanResult.DurationSeconds ?? 0, 30, CancellationToken.None);
        var evidence = PacketTimelineAnalyzer.ComputeEvidence(beforeProbe.ScanResult, packets);
        var diagnosis = AvRepairClassifier.ClassifyAll(beforeProbe.ScanResult, evidence, configuration).Single();
        var plan = AvRepairPlanner.PlanMedia([diagnosis], configuration).Single();

        return new
        {
            metadataDurationDeltaSeconds = diagnosis.DurationDeltaSeconds,
            packetDriftSeconds = diagnosis.PacketEvidence?.Drift,
            classification = diagnosis.Classification.ToString(),
            strategy = plan.Strategy.ToString(),
            trimSeconds = plan.TrimSeconds,
            autoRepairEligible = plan.IsAutoRepairEligible
        };
    }

    private static PluginConfiguration PlanningOnlyConfiguration() => new()
    {
        DryRun = true,
        EnableAudioVideoRepair = true,
        AllowAudioReencode = true,
        MaxAutoRepairDurationDeltaSeconds = 2.0
    };

    /// <summary>
    /// In production, RepairRoot is a second bind mount of the exact same
    /// host files as the read-only SourceRoot, so a writable mirror always
    /// exists. This sandbox uses independent directories, so the mirror must
    /// be copied explicitly to reproduce that precondition.
    /// </summary>
    private static void MirrorToRepairRoot(string sourceRoot, string repairRoot, string relative)
    {
        var mirrorPath = Path.Combine(repairRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(mirrorPath)!);
        File.Copy(Path.Combine(sourceRoot, relative), mirrorPath, overwrite: true);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ReadRecordedSourceHash(MediaReplacementResult replacement) =>
        File.Exists(replacement.BackupPath + ".metadata.json")
            ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(replacement.BackupPath + ".metadata.json"))
                .RootElement.GetProperty("sourceHash").GetString() ?? string.Empty
            : string.Empty;

    private static async Task RunFfmpeg(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg fixture generation failed: {stderr}");
        }
    }
}
