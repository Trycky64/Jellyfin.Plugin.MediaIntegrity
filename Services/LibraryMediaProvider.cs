using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaIntegrity.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Provides physical media files known to Jellyfin libraries.
/// </summary>
public sealed class LibraryMediaProvider
{
    private static readonly string[] IgnoredNameFragments =
    [
        ".part",
        ".tmp",
        ".bak",
        ".repairing.",
        ".original."
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryMediaProvider> _logger;

    public LibraryMediaProvider(
        ILibraryManager libraryManager,
        ILogger<LibraryMediaProvider> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets all supported physical media files known to Jellyfin.
    /// </summary>
    public IReadOnlyList<string> GetMediaFiles(
        bool includeVideo,
        bool includeAudio)
    {
        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var itemTypes = BuildIncludedItemTypes(
            includeVideo,
            includeAudio);

        if (itemTypes.Length == 0)
        {
            _logger.LogInformation(
                "Media enumeration skipped because both video and audio scanning are disabled.");

            return Array.Empty<string>();
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = itemTypes,
            EnableTotalRecordCount = false
        };

        _logger.LogInformation(
            "Enumerating Jellyfin media library using {TypeCount} supported item type(s).",
            itemTypes.Length);

        var items = _libraryManager.GetItemList(query);

        foreach (var item in items)
        {
            if (!TryGetPhysicalPath(
                    item,
                    out var path))
            {
                continue;
            }

            if (ShouldIgnore(path))
            {
                continue;
            }

            var extension = Path.GetExtension(path);

            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }

            var supported =
                (includeVideo
                    && SupportedMediaFormats.IsVideo(extension))
                || (includeAudio
                    && SupportedMediaFormats.IsAudio(extension));

            if (!supported)
            {
                continue;
            }

            result.Add(
                NormalizePath(path));
        }

        var orderedResult = result
            .OrderBy(
                static path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _logger.LogInformation(
            "Library media enumeration completed. Found {Count} supported physical media file(s).",
            orderedResult.Length);

        return orderedResult;
    }

    private static BaseItemKind[] BuildIncludedItemTypes(
        bool includeVideo,
        bool includeAudio)
    {
        var types = new List<BaseItemKind>();

        if (includeVideo)
        {
            types.Add(BaseItemKind.Movie);
            types.Add(BaseItemKind.Episode);
            types.Add(BaseItemKind.Video);
            types.Add(BaseItemKind.MusicVideo);
        }

        if (includeAudio)
        {
            types.Add(BaseItemKind.Audio);
            types.Add(BaseItemKind.AudioBook);
        }

        return types.ToArray();
    }

    private static bool TryGetPhysicalPath(
        BaseItem item,
        out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        if (!Path.IsPathRooted(item.Path))
        {
            return false;
        }

        path = item.Path;

        return true;
    }

    private static string NormalizePath(
        string path)
    {
        return Path.GetFullPath(path);
    }

    private static bool ShouldIgnore(
        string path)
    {
        var fileName = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return true;
        }

        foreach (var fragment in IgnoredNameFragments)
        {
            if (fileName.Contains(
                    fragment,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return IsInfrastructurePath(path);
    }

    private static bool IsInfrastructurePath(
        string path)
    {
        var normalized = path
            .Replace('\\', '/');

        return normalized.StartsWith(
                   "/config/",
                   StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(
                   "/cache/",
                   StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(
                   "/transcodes/",
                   StringComparison.OrdinalIgnoreCase);
    }
}
