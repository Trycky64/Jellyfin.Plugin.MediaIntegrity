namespace Jellyfin.Plugin.MediaIntegrity.Models;

/// <summary>
/// A deterministic reason a remux candidate cannot preserve stream timing.
/// </summary>
public sealed class TimelineValidationIssue
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}


