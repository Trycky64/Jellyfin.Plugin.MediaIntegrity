using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Selects bounded retries while leaving interrupted transactions for review.
/// </summary>
public static class RepairRetryPolicy
{
    public static bool IsDeterministicTimelineFailure(string error)
    {
        return error.Contains(
                   "StreamDurationChanged:",
                   StringComparison.Ordinal)
            || error.Contains(
                   "StreamStartTimeChanged:",
                   StringComparison.Ordinal)
            || error.Contains(
                   "AudioVideoDriftIntroduced:",
                   StringComparison.Ordinal)
            || error.Contains(
                   "StreamDurationUnknown:",
                   StringComparison.Ordinal)
            || error.Contains(
                   "StreamStartTimeUnknown:",
                   StringComparison.Ordinal)
            || error.Contains(
                   "StreamTimeBaseChanged:",
                   StringComparison.Ordinal)
            || error.Contains(
                   "StreamCodecChanged:",
                   StringComparison.Ordinal);
    }

    public static bool IsCandidate(RepairQueueItem item, PluginConfiguration configuration)
    {
        if (item.Status == RepairQueueItemStatus.Pending)
        {
            return true;
        }

        // Zero-attempt failures include rejected paths. Never retry those
        // automatically. Processing after a crash has an ambiguous outcome.
        return item.Status == RepairQueueItemStatus.Failed
            && item.Attempts > 0
            && item.Attempts < configuration.MaxRepairAttempts;
    }
}
