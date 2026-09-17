namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents a single integrity issue detected in a media file.
/// </summary>
public sealed class MediaIssue
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public MediaIssueSeverity Severity { get; set; }

    public string RawMessage { get; set; } = string.Empty;

    public int? VideoStreamIndex { get; set; }

    public int? AudioStreamIndex { get; set; }

    public double? StartDeltaSeconds { get; set; }

    public double? DurationDeltaSeconds { get; set; }

    public double? EndDeltaSeconds { get; set; }

    public double? AudioVideoDurationRatio { get; set; }

    public string AudioLanguage { get; set; } = string.Empty;

    public string AudioTitle { get; set; } = string.Empty;

    public bool? AudioIsDefault { get; set; }

    public bool? AudioIsForced { get; set; }

    public bool? AudioIsCommentary { get; set; }

    public bool? AudioIsDescription { get; set; }

    public double? VideoStartTimeSeconds { get; set; }

    public double? AudioStartTimeSeconds { get; set; }

    public double? VideoDurationSeconds { get; set; }

    public double? AudioDurationSeconds { get; set; }

    public double? OffsetEvolutionSeconds { get; set; }
}
