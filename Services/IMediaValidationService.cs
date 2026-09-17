using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Validates a remux candidate against its source before and after replacement.
/// </summary>
public interface IMediaValidationService
{
    Task<MediaValidationResult> ValidateAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken);
}


