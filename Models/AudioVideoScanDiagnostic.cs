namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>A bounded last-scan diagnostic shown only to Jellyfin administrators.</summary>
public sealed record AudioVideoScanDiagnostic
{
    public string FileName { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public MediaIssueSeverity Severity { get; init; }
    public int? VideoStreamIndex { get; init; }
    public int? AudioStreamIndex { get; init; }
    public string AudioLanguage { get; init; } = string.Empty;
    public string AudioTitle { get; init; } = string.Empty;
    public bool? AudioIsDefault { get; init; }
    public double? StartDeltaSeconds { get; init; }
    public double? DurationDeltaSeconds { get; init; }
    public double? EndDeltaSeconds { get; init; }
    public double? AudioVideoDurationRatio { get; init; }
}
