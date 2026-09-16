namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// Represents the severity of a detected media issue.
/// </summary>
public enum MediaIssueSeverity
{
    Info,
    Warning,
    Repairable,
    Critical
}
