using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Performs security-sensitive filesystem path validation.
/// </summary>
public sealed class PathSecurityService
{
    private readonly ILogger<PathSecurityService> _logger;

    public PathSecurityService(
        ILogger<PathSecurityService> logger)
    {
        _logger = logger;
    }

    public string ValidateSourceFile(
        string path,
        string sourceRoot)
    {
        return ValidateExistingFile(
            path,
            sourceRoot,
            "source media path");
    }

    public string ValidateTemporaryFile(
        string path,
        string tempRoot)
    {
        return ValidateExistingFile(
            path,
            tempRoot,
            "temporary media path");
    }

    public string ValidateExistingFile(
        string path,
        string allowedRoot,
        string parameterName)
    {
        var fullPath =
            ValidateLexicalContainment(
                path,
                allowedRoot,
                parameterName);

        /*
         * Security check MUST happen before File.Exists().
         *
         * File.Exists() follows symbolic links and simply returns false
         * for broken links. Checking path components first ensures that
         * a hostile or broken symlink is rejected as a security violation
         * rather than being mistaken for a missing file.
         */
        ValidateExistingComponents(
            fullPath,
            allowedRoot,
            includeFinalEntry: true,
            parameterName);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"{parameterName} does not exist.",
                fullPath);
        }

        /*
         * Revalidate after existence check to narrow the window in which
         * filesystem entries could have changed.
         */
        ValidateExistingComponents(
            fullPath,
            allowedRoot,
            includeFinalEntry: true,
            parameterName);

        return fullPath;
    }

    public string ValidateDestination(
        string path,
        string allowedRoot,
        string parameterName)
    {
        var fullPath =
            ValidateLexicalContainment(
                path,
                allowedRoot,
                parameterName);

        /*
         * Always inspect existing ancestors.
         *
         * The destination itself may not exist yet, which is normal for
         * staging files and newly-created outputs.
         */
        ValidateExistingComponents(
            fullPath,
            allowedRoot,
            includeFinalEntry: true,
            parameterName);

        return fullPath;
    }

    public string ValidateExistingDirectory(
        string path,
        string allowedRoot,
        string parameterName)
    {
        var fullPath =
            ValidateLexicalContainment(
                path,
                allowedRoot,
                parameterName);

        ValidateExistingComponents(
            fullPath,
            allowedRoot,
            includeFinalEntry: true,
            parameterName);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"{parameterName} does not exist: '{fullPath}'.");
        }

        ValidateExistingComponents(
            fullPath,
            allowedRoot,
            includeFinalEntry: true,
            parameterName);

        return fullPath;
    }

    public string ValidateLexicalContainment(
        string path,
        string allowedRoot,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);

        var fullPath =
            NormalizePath(path);

        var fullRoot =
            NormalizeRoot(allowedRoot);

        EnsureInsideRoot(
            fullPath,
            fullRoot,
            parameterName);

        return fullPath;
    }

    private void ValidateExistingComponents(
        string path,
        string allowedRoot,
        bool includeFinalEntry,
        string parameterName)
    {
        var fullPath =
            NormalizePath(path);

        var fullRoot =
            NormalizeRoot(allowedRoot);

        EnsureInsideRoot(
            fullPath,
            fullRoot,
            parameterName);

        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"Allowed root does not exist: '{fullRoot}'.");
        }

        EnsureNotSymlink(
            fullRoot,
            $"{parameterName} root");

        var relativePath =
            Path.GetRelativePath(
                fullRoot,
                fullPath);

        if (relativePath == ".")
        {
            return;
        }

        var separators =
            new[]
            {
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            };

        var segments =
            relativePath.Split(
                separators,
                StringSplitOptions.RemoveEmptyEntries);

        var current =
            fullRoot;

        for (var index = 0;
             index < segments.Length;
             index++)
        {
            current =
                Path.Combine(
                    current,
                    segments[index]);

            var isFinalEntry =
                index == segments.Length - 1;

            if (isFinalEntry
                && !includeFinalEntry)
            {
                break;
            }

            /*
             * Do not use File.Exists()/Directory.Exists() alone here.
             *
             * A broken symbolic link returns false for both, while the
             * directory entry itself still exists and must be rejected.
             */
            if (TryGetAttributes(
                    current,
                    out var attributes))
            {
                EnsureNotSymlink(
                    current,
                    parameterName,
                    attributes);

                continue;
            }

            /*
             * Once a path component genuinely does not exist, all later
             * components necessarily do not exist either. Existing parents
             * have already been inspected.
             */
            break;
        }
    }

    private void EnsureNotSymlink(
        string path,
        string parameterName)
    {
        if (!TryGetAttributes(
                path,
                out var attributes))
        {
            throw new InvalidOperationException(
                $"Unable to securely inspect "
                + $"{parameterName} '{path}'.");
        }

        EnsureNotSymlink(
            path,
            parameterName,
            attributes);
    }

    private void EnsureNotSymlink(
        string path,
        string parameterName,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint)
            != 0)
        {
            RejectSymlink(
                path,
                parameterName,
                GetLinkTarget(path));
        }

        FileSystemInfo info =
            (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path)
                : new FileInfo(path);

        string? linkTarget;

        try
        {
            linkTarget =
                info.LinkTarget;
        }
        catch (Exception ex)
            when (ex is IOException
                or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Unable to securely inspect "
                + $"{parameterName} '{path}'.",
                ex);
        }

        if (!string.IsNullOrWhiteSpace(
                linkTarget))
        {
            RejectSymlink(
                path,
                parameterName,
                linkTarget);
        }
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes =
                File.GetAttributes(path);

            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (IOException)
        {
            attributes = default;
            return false;
        }
    }

    private static string? GetLinkTarget(
        string path)
    {
        try
        {
            var attributes =
                File.GetAttributes(path);

            FileSystemInfo info =
                (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(path)
                    : new FileInfo(path);

            return info.LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private void RejectSymlink(
        string path,
        string parameterName,
        string? target)
    {
        _logger.LogError(
            "[MediaIntegrity] [Security] "
            + "Rejected {ParameterName}. "
            + "Path {Path} is a symbolic link or reparse point. "
            + "Target: {Target}.",
            parameterName,
            path,
            target ?? "unknown");

        throw new InvalidOperationException(
            $"{parameterName} contains a symbolic link "
            + $"or reparse point: '{path}'.");
    }

    private static void EnsureInsideRoot(
        string candidate,
        string root,
        string parameterName)
    {
        var comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        if (string.Equals(
                candidate,
                root,
                comparison))
        {
            return;
        }

        var rootPrefix =
            root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(
                rootPrefix,
                comparison))
        {
            throw new InvalidOperationException(
                $"{parameterName} '{candidate}' "
                + $"is outside allowed root '{root}'.");
        }
    }

    private static string NormalizeRoot(
        string path)
    {
        return NormalizePath(path)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }

    private static string NormalizePath(
        string path)
    {
        return Path.GetFullPath(path);
    }
}
