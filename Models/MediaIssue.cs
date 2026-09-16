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
}
