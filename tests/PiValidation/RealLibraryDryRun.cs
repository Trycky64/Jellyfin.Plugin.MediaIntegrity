using Jellyfin.Plugin.MediaIntegrity;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiValidation;

/// <summary>
/// Read-only classification pass against the real, already-configured media
/// library, used when Jellyfin's HTTP API is unavailable to trigger the
/// Media Integrity Scan scheduled task directly (no JELLYFIN_API_KEY). Runs
/// the exact production probe/classification code (MediaProbeService,
/// PacketTimelineAnalyzer, AvRepairClassifier, AvRepairPlanner) directly
/// against files enumerated from the filesystem. Never invokes ffmpeg for
/// anything other than ffprobe reads; never writes, moves, or deletes
/// anything under the scanned roots.
/// </summary>
public static class RealLibraryDryRun
{
    private static readonly string[] SupportedExtensions =
    [
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg"
    ];

    public static async Task<Dictionary<string, object>> RunAsync(IReadOnlyList<string> roots)
    {
        var configuration = new PluginConfiguration();
        var detector = new MediaIssueDetector();
        var probe = new MediaProbeService(NullLogger<MediaProbeService>.Instance, detector);

        var files = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        var checkedCount = 0;
        var healthy = 0;
        var warning = 0;
        var unreadable = 0;
        var avAffected = 0;
        var classificationCounts = new Dictionary<string, int>();
        var planCounts = new Dictionary<string, int>();
        var errors = new List<string>();

        foreach (var path in files)
        {
            try
            {
                var result = await probe.ProbeAsync(path, 120, CancellationToken.None, enableAudioVideoSyncCheck: true);
                checkedCount++;

                switch (result.ScanResult.Status)
                {
                    case MediaIntegrityStatus.Ok:
                        healthy++;
                        break;
                    case MediaIntegrityStatus.Warning:
                        warning++;
                        break;
                    default:
                        unreadable++;
                        break;
                }

                var packets = await PacketTimelineAnalyzer.FetchPacketsAsync(
                    path, result.ScanResult.DurationSeconds ?? 0, 120, CancellationToken.None);
                var evidence = PacketTimelineAnalyzer.ComputeEvidence(result.ScanResult, packets);
                var diagnoses = AvRepairClassifier.ClassifyAll(result.ScanResult, evidence, configuration);

                if (diagnoses.Count > 0)
                {
                    avAffected++;
                }

                foreach (var diagnosis in diagnoses)
                {
                    var key = diagnosis.Classification.ToString();
                    classificationCounts[key] = classificationCounts.GetValueOrDefault(key) + 1;
                }

                // Planning only, with the safest, most permissive config
                // (repair enabled) purely to observe what the planner WOULD
                // decide; nothing is ever executed by this harness.
                var permissive = new PluginConfiguration { EnableAudioVideoRepair = true, AllowAudioReencode = true };
                var plans = AvRepairPlanner.PlanMedia(
                    AvRepairClassifier.ClassifyAll(result.ScanResult, evidence, permissive), permissive);
                foreach (var plan in plans)
                {
                    var key = plan.Strategy.ToString();
                    planCounts[key] = planCounts.GetValueOrDefault(key) + 1;
                }
            }
            catch (Exception ex)
            {
                checkedCount++;
                unreadable++;
                errors.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new Dictionary<string, object>
        {
            ["filesFound"] = files.Count,
            ["checked"] = checkedCount,
            ["healthy"] = healthy,
            ["warning"] = warning,
            ["unreadable"] = unreadable,
            ["avAffectedMedia"] = avAffected,
            ["classificationCounts"] = classificationCounts,
            ["wouldPlanCounts (with repair hypothetically enabled)"] = planCounts,
            ["errors"] = errors,
            ["writesPerformed"] = 0
        };
    }
}
