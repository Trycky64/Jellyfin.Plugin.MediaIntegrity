using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Detects media files currently being used by Jellyfin playback sessions.
/// </summary>
public sealed class MediaUseService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MediaUseService> _logger;

    public MediaUseService(
        ISessionManager sessionManager,
        ILogger<MediaUseService> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the specified source media file is currently
    /// being played or transcoded by an active Jellyfin session.
    /// </summary>
    public bool IsMediaInUse(
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sourcePath);

        var normalizedSource =
            NormalizePath(sourcePath);

        foreach (var session
                 in _sessionManager.Sessions)
        {
            var currentPath =
                GetCurrentMediaPath(session);

            if (string.IsNullOrWhiteSpace(
                    currentPath))
            {
                continue;
            }

            if (!PathsEqual(
                    normalizedSource,
                    NormalizePath(currentPath)))
            {
                continue;
            }

            if (session.TranscodingInfo is not null)
            {
                _logger.LogInformation(
                    "[MediaIntegrity] [Repair] "
                    + "Media is currently being transcoded: {Path}.",
                    normalizedSource);
            }
            else
            {
                _logger.LogInformation(
                    "[MediaIntegrity] [Repair] "
                    + "Media is currently being played: {Path}.",
                    normalizedSource);
            }

            return true;
        }

        return false;
    }

    private static string? GetCurrentMediaPath(
        SessionInfo session)
    {
        /*
         * FullNowPlayingItem is the server-side BaseItem and is
         * therefore preferred when available.
         */
        if (!string.IsNullOrWhiteSpace(
                session.FullNowPlayingItem?.Path))
        {
            return session.FullNowPlayingItem.Path;
        }

        /*
         * Fall back to the DTO exposed on the session.
         */
        if (!string.IsNullOrWhiteSpace(
                session.NowPlayingItem?.Path))
        {
            return session.NowPlayingItem.Path;
        }

        return null;
    }

    private static string NormalizePath(
        string path)
    {
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(
        string left,
        string right)
    {
        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
