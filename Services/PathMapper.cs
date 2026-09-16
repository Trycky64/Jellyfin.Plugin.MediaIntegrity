using Jellyfin.Plugin.MediaIntegrity.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Safely maps read-only Jellyfin media paths to writable repair,
/// backup and temporary paths.
/// </summary>
public sealed class PathMapper
{
    private readonly PluginConfigurationService _configurationService;
    private readonly PathSecurityService _pathSecurityService;
    private readonly ILogger<PathMapper> _logger;

    public PathMapper(
        PluginConfigurationService configurationService,
        PathSecurityService pathSecurityService,
        ILogger<PathMapper> logger)
    {
        _configurationService = configurationService;
        _pathSecurityService = pathSecurityService;
        _logger = logger;
    }

    public MappedMediaPaths MapAll(
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var configuration =
            _configurationService.GetValidatedConfiguration();

        var validatedSourcePath =
            _pathSecurityService.ValidateSourceFile(
                sourcePath,
                configuration.SourceRoot);

        return new MappedMediaPaths
        {
            SourcePath = validatedSourcePath,
            RepairPath =
                MapPath(
                    validatedSourcePath,
                    configuration.SourceRoot,
                    configuration.RepairRoot,
                    "repair"),
            BackupPath =
                MapPath(
                    validatedSourcePath,
                    configuration.SourceRoot,
                    configuration.BackupRoot,
                    "backup"),
            TemporaryPath =
                MapToTemporaryPath(
                    validatedSourcePath)
        };
    }

    public string MapToRepairPath(
        string sourcePath)
    {
        var configuration =
            _configurationService.GetValidatedConfiguration();

        var validatedSource =
            _pathSecurityService.ValidateSourceFile(
                sourcePath,
                configuration.SourceRoot);

        return MapPath(
            validatedSource,
            configuration.SourceRoot,
            configuration.RepairRoot,
            "repair");
    }

    public string MapToBackupPath(
        string sourcePath)
    {
        var configuration =
            _configurationService.GetValidatedConfiguration();

        var validatedSource =
            _pathSecurityService.ValidateSourceFile(
                sourcePath,
                configuration.SourceRoot);

        return MapPath(
            validatedSource,
            configuration.SourceRoot,
            configuration.BackupRoot,
            "backup");
    }

    public string MapToTemporaryPath(
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var configuration =
            _configurationService.GetValidatedConfiguration();

        var sourceFullPath =
            _pathSecurityService.ValidateSourceFile(
                sourcePath,
                configuration.SourceRoot);

        var sourceRoot =
            NormalizeRoot(
                configuration.SourceRoot);

        var tempRoot =
            NormalizeRoot(
                configuration.TempRoot);

        var relativePath =
            Path.GetRelativePath(
                sourceRoot,
                sourceFullPath);

        ValidateRelativePath(
            relativePath);

        var relativeDirectory =
            Path.GetDirectoryName(
                relativePath);

        var fileName =
            Path.GetFileNameWithoutExtension(
                sourceFullPath);

        var extension =
            Path.GetExtension(
                sourceFullPath);

        var uniqueFileName =
            $"{fileName}.{Guid.NewGuid():N}.repairing{extension}";

        var targetDirectory =
            string.IsNullOrWhiteSpace(
                relativeDirectory)
                ? tempRoot
                : Path.Combine(
                    tempRoot,
                    relativeDirectory);

        var targetPath =
            Path.GetFullPath(
                Path.Combine(
                    targetDirectory,
                    uniqueFileName));

        _pathSecurityService.ValidateDestination(
            targetPath,
            tempRoot,
            "temporary path");

        _logger.LogDebug(
            "Mapped source path {SourcePath} "
            + "to temporary path {TemporaryPath}.",
            sourceFullPath,
            targetPath);

        return targetPath;
    }

    private string MapPath(
        string sourcePath,
        string sourceRootValue,
        string targetRootValue,
        string mappingName)
    {
        var sourceFullPath =
            Path.GetFullPath(
                sourcePath);

        var sourceRoot =
            NormalizeRoot(
                sourceRootValue);

        var targetRoot =
            NormalizeRoot(
                targetRootValue);

        _pathSecurityService.ValidateExistingFile(
            sourceFullPath,
            sourceRoot,
            "source media path");

        var relativePath =
            Path.GetRelativePath(
                sourceRoot,
                sourceFullPath);

        ValidateRelativePath(
            relativePath);

        var targetPath =
            Path.GetFullPath(
                Path.Combine(
                    targetRoot,
                    relativePath));

        _pathSecurityService.ValidateDestination(
            targetPath,
            targetRoot,
            $"{mappingName} path");

        _logger.LogDebug(
            "Mapped source path {SourcePath} "
            + "to {MappingName} path {TargetPath}.",
            sourceFullPath,
            mappingName,
            targetPath);

        return targetPath;
    }

    private static void ValidateRelativePath(
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(
                relativePath)
            || relativePath == ".")
        {
            throw new InvalidOperationException(
                "Source path must refer to a file "
                + "below SourceRoot.");
        }

        if (Path.IsPathRooted(
                relativePath))
        {
            throw new InvalidOperationException(
                "Mapped relative path unexpectedly "
                + "became absolute.");
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

        if (segments.Any(
                static segment =>
                    segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "Path traversal segments are not allowed.");
        }
    }

    private static string NormalizeRoot(
        string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }
}
