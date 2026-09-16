using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Performs transactional backup and replacement of media files.
/// </summary>
public sealed class MediaReplacementService
{
    private readonly PluginConfigurationService _configurationService;
    private readonly PathMapper _pathMapper;
    private readonly PathSecurityService _pathSecurityService;
    private readonly ILogger<MediaReplacementService> _logger;

    public MediaReplacementService(
        PluginConfigurationService configurationService,
        PathMapper pathMapper,
        PathSecurityService pathSecurityService,
        ILogger<MediaReplacementService> logger)
    {
        _configurationService = configurationService;
        _pathMapper = pathMapper;
        _pathSecurityService = pathSecurityService;
        _logger = logger;
    }

    public async Task<MediaReplacementResult> ReplaceAsync(
        string sourcePath,
        string temporaryPath,
        CancellationToken cancellationToken,
        Func<bool>? isMediaInUse = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);

        var configuration =
            _configurationService.GetValidatedConfiguration();

        var source =
            _pathSecurityService.ValidateSourceFile(
                sourcePath,
                configuration.SourceRoot);

        var temporary =
            _pathSecurityService.ValidateTemporaryFile(
                temporaryPath,
                configuration.TempRoot);

        var paths =
            _pathMapper.MapAll(
                source);

        _pathSecurityService.ValidateDestination(
            paths.RepairPath,
            configuration.RepairRoot,
            "repair destination");

        _pathSecurityService.ValidateDestination(
            paths.BackupPath,
            configuration.BackupRoot,
            "backup destination");

        var sourceInfo =
            new FileInfo(
                source);

        var temporaryInfo =
            new FileInfo(
                temporary);

        if (temporaryInfo.Length <= 0)
        {
            throw new InvalidDataException(
                "Temporary media file is empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (configuration.DryRun)
        {
            _logger.LogInformation(
                "Dry-run enabled. Replacement skipped for {SourcePath}.",
                source);

            return new MediaReplacementResult
            {
                Success = true,
                BackupCreated = false,
                ReplacementCompleted = false,
                RollbackAttempted = false,
                RollbackSucceeded = false,
                SourcePath = source,
                RepairPath = paths.RepairPath,
                BackupPath = paths.BackupPath,
                TemporaryPath = temporary
            };
        }

        var backupCreated = false;
        var replacementCompleted = false;
        var rollbackAttempted = false;
        var rollbackSucceeded = false;

        var actualBackupPath =
            BuildUniqueBackupPath(
                paths.BackupPath);

        try
        {
            CreateAndValidateParentDirectory(
                actualBackupPath,
                configuration.BackupRoot,
                "backup parent directory");

            CreateAndValidateParentDirectory(
                paths.RepairPath,
                configuration.RepairRoot,
                "repair parent directory");

            _pathSecurityService.ValidateDestination(
                actualBackupPath,
                configuration.BackupRoot,
                "backup destination");

            _pathSecurityService.ValidateDestination(
                paths.RepairPath,
                configuration.RepairRoot,
                "repair destination");

            _logger.LogInformation(
                "Creating backup of {SourcePath} at {BackupPath}.",
                source,
                actualBackupPath);

            await CopyFileAsync(
                source,
                actualBackupPath,
                overwrite: false,
                cancellationToken);

            backupCreated = true;

            _pathSecurityService.ValidateExistingFile(
                actualBackupPath,
                configuration.BackupRoot,
                "created backup");

            VerifyBackup(
                sourceInfo,
                actualBackupPath);

            var sourceHash = await HashFileAsync(source, cancellationToken);
            var backupHash = await HashFileAsync(actualBackupPath, cancellationToken);
            if (sourceHash != backupHash)
            {
                throw new IOException("Backup SHA-256 differs from source.");
            }

            var repairedHash = await HashFileAsync(temporary, cancellationToken);
            var metadataPath = actualBackupPath + ".metadata.json";
            _pathSecurityService.ValidateDestination(
                metadataPath, configuration.BackupRoot, "backup metadata");

            // Persist the recovery manifest before any destructive operation.
            await using (var metadataStream = new FileStream(
                metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    metadataStream,
                    new BackupMetadata
                    {
                        SourcePath = source,
                        BackupPath = actualBackupPath,
                        RepairedPath = paths.RepairPath,
                        SourceHash = sourceHash,
                        BackupHash = backupHash,
                        RepairedHash = repairedHash
                    },
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    },
                    cancellationToken);
                await metadataStream.FlushAsync(cancellationToken);
                metadataStream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stagingPath =
                BuildStagingPath(
                    paths.RepairPath);

            _pathSecurityService.ValidateDestination(
                stagingPath,
                configuration.RepairRoot,
                "replacement staging path");

            try
            {
                _logger.LogInformation(
                    "Copying validated remux to staging path {StagingPath}.",
                    stagingPath);

                await CopyFileAsync(
                    temporary,
                    stagingPath,
                    overwrite: false,
                    cancellationToken);

                _pathSecurityService.ValidateExistingFile(
                    stagingPath,
                    configuration.RepairRoot,
                    "replacement staging file");

                VerifyCopiedFile(
                    temporaryInfo,
                    stagingPath);

                if (await HashFileAsync(stagingPath, cancellationToken) != repairedHash)
                {
                    throw new IOException("Staging SHA-256 differs from validated remux.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                /*
                 * Security-critical revalidation immediately before swap.
                 */
                _pathSecurityService.ValidateExistingFile(
                    stagingPath,
                    configuration.RepairRoot,
                    "replacement staging file");

                _pathSecurityService.ValidateDestination(
                    paths.RepairPath,
                    configuration.RepairRoot,
                    "repair destination");

                var repairParent =
                    Path.GetDirectoryName(
                        paths.RepairPath)
                    ?? throw new InvalidOperationException(
                        "Unable to determine repair parent directory.");

                _pathSecurityService.ValidateExistingDirectory(
                    repairParent,
                    configuration.RepairRoot,
                    "repair parent directory");

                // A misconfigured mirror or a file changed during preparation
                // must never be overwritten with a remux of different content.
                _pathSecurityService.ValidateExistingFile(
                    paths.RepairPath, configuration.RepairRoot, "writable source mirror");
                if (await HashFileAsync(paths.RepairPath, cancellationToken) != sourceHash)
                {
                    throw new IOException("Writable mirror SHA-256 differs from the backed-up source.");
                }

                // Backups and staging copies can take minutes on large media.
                if (isMediaInUse?.Invoke() == true)
                {
                    throw new IOException("Media became active during backup or staging; replacement skipped.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation(
                    "Replacing writable media path {RepairPath}.",
                    paths.RepairPath);

                File.Move(
                    stagingPath,
                    paths.RepairPath,
                    overwrite: true);

                replacementCompleted = true;
            }
            finally
            {
                TryDeleteFile(
                    stagingPath);
            }

            _pathSecurityService.ValidateExistingFile(
                paths.RepairPath,
                configuration.RepairRoot,
                "replacement file");

            var replacementInfo =
                new FileInfo(
                    paths.RepairPath);

            if (replacementInfo.Length
                != temporaryInfo.Length)
            {
                throw new IOException(
                    "Replacement file size differs "
                    + "from validated temporary file.");
            }

            _logger.LogInformation(
                "Transactional replacement completed "
                + "for {SourcePath}. Backup stored at {BackupPath}.",
                source,
                actualBackupPath);

            // Backups and manifests are permanent, including when legacy
            // configurations have KeepBackups=false. Cleanup is a future task.

            return new MediaReplacementResult
            {
                Success = true,
                BackupCreated = backupCreated,
                ReplacementCompleted = replacementCompleted,
                RollbackAttempted = false,
                RollbackSucceeded = false,
                SourcePath = source,
                RepairPath = paths.RepairPath,
                BackupPath = actualBackupPath,
                TemporaryPath = temporary
            };
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (replacementCompleted
                && backupCreated)
            {
                rollbackAttempted = true;

                rollbackSucceeded =
                    TryRollback(
                        paths.RepairPath,
                        actualBackupPath,
                        configuration);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (replacementCompleted
                && backupCreated)
            {
                rollbackAttempted = true;

                rollbackSucceeded =
                    TryRollback(
                        paths.RepairPath,
                        actualBackupPath,
                        configuration);
            }

            _logger.LogError(
                ex,
                "Transactional replacement failed for {SourcePath}. "
                + "Rollback attempted: {RollbackAttempted}, "
                + "rollback succeeded: {RollbackSucceeded}.",
                source,
                rollbackAttempted,
                rollbackSucceeded);

            return new MediaReplacementResult
            {
                Success = false,
                BackupCreated = backupCreated,
                ReplacementCompleted = replacementCompleted,
                RollbackAttempted = rollbackAttempted,
                RollbackSucceeded = rollbackSucceeded,
                SourcePath = source,
                RepairPath = paths.RepairPath,
                BackupPath = actualBackupPath,
                TemporaryPath = temporary,
                Error = ex.Message
            };
        }
    }

    private bool TryRollback(
        string repairPath,
        string backupPath,
        PluginConfiguration configuration)
    {
        try
        {
            var validatedBackup =
                _pathSecurityService.ValidateExistingFile(
                    backupPath,
                    configuration.BackupRoot,
                    "rollback backup");

            CreateAndValidateParentDirectory(
                repairPath,
                configuration.RepairRoot,
                "rollback repair parent");

            _pathSecurityService.ValidateDestination(
                repairPath,
                configuration.RepairRoot,
                "rollback destination");

            var rollbackStagingPath =
                BuildStagingPath(
                    repairPath);

            _pathSecurityService.ValidateDestination(
                rollbackStagingPath,
                configuration.RepairRoot,
                "rollback staging path");

            try
            {
                var backupInfo =
                    new FileInfo(
                        validatedBackup);

                File.Copy(
                    validatedBackup,
                    rollbackStagingPath,
                    overwrite: false);

                _pathSecurityService.ValidateExistingFile(
                    rollbackStagingPath,
                    configuration.RepairRoot,
                    "rollback staging file");

                VerifyCopiedFile(
                    backupInfo,
                    rollbackStagingPath);

                _pathSecurityService.ValidateDestination(
                    repairPath,
                    configuration.RepairRoot,
                    "rollback destination");

                File.Move(
                    rollbackStagingPath,
                    repairPath,
                    overwrite: true);
            }
            finally
            {
                TryDeleteFile(
                    rollbackStagingPath);
            }

            _pathSecurityService.ValidateExistingFile(
                repairPath,
                configuration.RepairRoot,
                "rolled back media file");

            _logger.LogWarning(
                "Rollback succeeded for {RepairPath}.",
                repairPath);

            return true;
        }
        catch (Exception ex)
            when (ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            _logger.LogCritical(
                ex,
                "Rollback FAILED for {RepairPath}. "
                + "Manual intervention may be required.",
                repairPath);

            return false;
        }
    }

    private void CreateAndValidateParentDirectory(
        string path,
        string allowedRoot,
        string parameterName)
    {
        var directory =
            Path.GetDirectoryName(
                path);

        if (string.IsNullOrWhiteSpace(
                directory))
        {
            throw new InvalidOperationException(
                $"Unable to determine parent directory for '{path}'.");
        }

        _pathSecurityService.ValidateDestination(
            directory,
            allowedRoot,
            parameterName);

        Directory.CreateDirectory(
            directory);

        /*
         * Validate again after creation so a symbolic-link component
         * cannot silently redirect subsequent file operations.
         */
        _pathSecurityService.ValidateExistingDirectory(
            directory,
            allowedRoot,
            parameterName);
    }

    private static void VerifyBackup(
        FileInfo sourceInfo,
        string backupPath)
    {
        if (!File.Exists(
                backupPath))
        {
            throw new IOException(
                "Backup file was not created.");
        }

        var backupInfo =
            new FileInfo(
                backupPath);

        if (backupInfo.Length
            != sourceInfo.Length)
        {
            throw new IOException(
                "Backup file size differs from source.");
        }
    }

    private static void VerifyCopiedFile(
        FileInfo expected,
        string actualPath)
    {
        if (!File.Exists(
                actualPath))
        {
            throw new IOException(
                $"Copied file '{actualPath}' does not exist.");
        }

        var actual =
            new FileInfo(
                actualPath);

        if (actual.Length
            != expected.Length)
        {
            throw new IOException(
                $"Copied file '{actualPath}' "
                + "has an unexpected size.");
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destinationMode =
            overwrite
                ? FileMode.Create
                : FileMode.CreateNew;

        await using var source =
            new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                useAsync: true);

        await using var destination =
            new FileStream(
                destinationPath,
                destinationMode,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);

        await source.CopyToAsync(
            destination,
            bufferSize: 1024 * 1024,
            cancellationToken);

        await destination.FlushAsync(
            cancellationToken);
        destination.Flush(flushToDisk: true);
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string BuildUniqueBackupPath(
        string preferredPath)
    {
        if (!File.Exists(
                preferredPath)
            && !File.Exists(preferredPath + ".metadata.json"))
        {
            return preferredPath;
        }

        var directory =
            Path.GetDirectoryName(
                preferredPath)
            ?? throw new InvalidOperationException(
                "Unable to determine backup directory.");

        var name =
            Path.GetFileNameWithoutExtension(
                preferredPath);

        var extension =
            Path.GetExtension(
                preferredPath);

        var timestamp =
            DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd-HHmmssfff");

        return Path.Combine(
            directory,
            $"{name}.original-{timestamp}-{Guid.NewGuid():N}{extension}");
    }

    private static string BuildStagingPath(
        string repairPath)
    {
        var directory =
            Path.GetDirectoryName(
                repairPath)
            ?? throw new InvalidOperationException(
                "Unable to determine repair directory.");

        var fileName =
            Path.GetFileName(
                repairPath);

        return Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.replacing");
    }

    private static void TryDeleteFile(
        string path)
    {
        try
        {
            if (File.Exists(
                    path))
            {
                File.Delete(
                    path);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }
}
