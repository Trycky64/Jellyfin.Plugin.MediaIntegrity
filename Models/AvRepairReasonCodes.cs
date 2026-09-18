namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Stable string reason codes recorded in logs and <see cref="RepairQueueItem.LastError"/>
/// for A/V repair outcomes, following the same convention as existing deterministic
/// failure prefixes (for example <c>StreamDurationChanged:</c>) recognized by
/// <c>Services.RepairRetryPolicy</c>.
/// </summary>
public static class AvRepairReasonCodes
{
    public const string AudioVideoRepairPlanned = "AudioVideoRepairPlanned";
    public const string AudioVideoRepairSucceeded = "AudioVideoRepairSucceeded";
    public const string AudioVideoRepairRejected = "AudioVideoRepairRejected";
    public const string AudioVideoRepairRolledBack = "AudioVideoRepairRolledBack";
    public const string AudioVideoRepairUnsafe = "AudioVideoRepairUnsafe";
    public const string AudioVideoRepairRequiresReencode = "AudioVideoRepairRequiresReencode";
    public const string AudioVideoRepairValidationFailed = "AudioVideoRepairValidationFailed";
}
