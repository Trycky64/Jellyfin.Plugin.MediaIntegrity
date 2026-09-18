using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Provides validated Media Integrity plugin configuration.
/// </summary>
public sealed class PluginConfigurationService
{
    private readonly ILogger<PluginConfigurationService> _logger;
    private readonly PluginConfiguration? _overrideConfiguration;

    public PluginConfigurationService(
        ILogger<PluginConfigurationService> logger)
        : this(logger, null)
    {
    }

    public PluginConfigurationService(
        ILogger<PluginConfigurationService> logger,
        PluginConfiguration? overrideConfiguration)
    {
        _logger = logger;
        _overrideConfiguration = overrideConfiguration;
    }

    public PluginConfiguration GetConfiguration()
    {
        return _overrideConfiguration
            ?? Plugin.Instance?.Configuration
            ?? new PluginConfiguration();
    }

    public PluginConfiguration GetValidatedConfiguration()
    {
        var configuration = GetConfiguration();

        Validate(configuration);

        return configuration;
    }

    public void Validate(
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.EnableVideoFiles
            && !configuration.EnableAudioFiles)
        {
            _logger.LogWarning(
                "Both video and audio scanning are disabled.");
        }

        if (configuration.ProbeTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                "ProbeTimeoutSeconds must be greater than zero.");
        }

        if (configuration.MaxParallelProbes <= 0)
        {
            throw new InvalidOperationException(
                "MaxParallelProbes must be greater than zero.");
        }

        if (configuration.MaxRepairAttempts <= 0)
        {
            throw new InvalidOperationException(
                "MaxRepairAttempts must be greater than zero.");
        }

        if (configuration.MaxRepairsPerRun <= 0)
        {
            throw new InvalidOperationException(
                "MaxRepairsPerRun must be greater than zero.");
        }

        if (configuration.RemuxTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                "RemuxTimeoutSeconds must be greater than zero.");
        }

        if (configuration.ValidationTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                "ValidationTimeoutSeconds must be greater than zero.");
        }

        if (!double.IsFinite(configuration.DurationToleranceSeconds)
            || configuration.DurationToleranceSeconds < 0)
        {
            throw new InvalidOperationException(
                "DurationToleranceSeconds must be finite and non-negative.");
        }

        ValidateNonNegativeFinite(
            configuration.StreamDurationToleranceSeconds,
            nameof(configuration.StreamDurationToleranceSeconds));

        ValidateNonNegativeFinite(
            configuration.StreamStartTimeToleranceSeconds,
            nameof(configuration.StreamStartTimeToleranceSeconds));

        ValidateAbsolutePath(
            configuration.SourceRoot,
            nameof(configuration.SourceRoot));

        ValidateAbsolutePath(
            configuration.RepairRoot,
            nameof(configuration.RepairRoot));

        ValidateAbsolutePath(
            configuration.BackupRoot,
            nameof(configuration.BackupRoot));

        ValidateAbsolutePath(
            configuration.TempRoot,
            nameof(configuration.TempRoot));

        var sourceRoot =
            NormalizeRoot(configuration.SourceRoot);

        var repairRoot =
            NormalizeRoot(configuration.RepairRoot);

        var backupRoot =
            NormalizeRoot(configuration.BackupRoot);

        var tempRoot =
            NormalizeRoot(configuration.TempRoot);

        if (PathEquals(sourceRoot, repairRoot))
        {
            throw new InvalidOperationException(
                "SourceRoot and RepairRoot must be different.");
        }

        if (PathEquals(sourceRoot, backupRoot))
        {
            throw new InvalidOperationException(
                "SourceRoot and BackupRoot must be different.");
        }

        if (PathEquals(repairRoot, backupRoot))
        {
            throw new InvalidOperationException(
                "RepairRoot and BackupRoot must be different.");
        }

        if (IsChildPath(repairRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "RepairRoot must not be located inside SourceRoot.");
        }

        if (IsChildPath(backupRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "BackupRoot must not be located inside SourceRoot.");
        }

        if (PathEquals(tempRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "TempRoot must not equal SourceRoot.");
        }

        if (IsChildPath(tempRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "TempRoot must not be located inside SourceRoot.");
        }

        if (configuration.MaxAudioVideoRepairsPerRun <= 0)
        {
            throw new InvalidOperationException(
                "MaxAudioVideoRepairsPerRun must be greater than zero.");
        }

        ValidateNonNegativeFinite(
            configuration.MaxAutoRepairOffsetSeconds,
            nameof(configuration.MaxAutoRepairOffsetSeconds));

        ValidateNonNegativeFinite(
            configuration.MaxAutoRepairDurationDeltaSeconds,
            nameof(configuration.MaxAutoRepairDurationDeltaSeconds));

        ValidateNonNegativeFinite(
            configuration.MaxAutoRepairDriftRatio,
            nameof(configuration.MaxAutoRepairDriftRatio));

        if (!double.IsFinite(configuration.MinRepairConfidence)
            || configuration.MinRepairConfidence is < 0 or > 1)
        {
            throw new InvalidOperationException(
                "MinRepairConfidence must be finite and between 0 and 1.");
        }
    }

    private static void ValidateAbsolutePath(
        string value,
        string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{propertyName} cannot be empty.");
        }

        if (!Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                $"{propertyName} must be an absolute path.");
        }
    }

    private static void ValidateNonNegativeFinite(
        double value,
        string propertyName)
    {
        if (!double.IsFinite(value)
            || value < 0)
        {
            throw new InvalidOperationException(
                $"{propertyName} must be finite and non-negative.");
        }
    }

    private static string NormalizeRoot(
        string path)
    {
        var fullPath =
            Path.GetFullPath(path);

        var root =
            Path.GetPathRoot(fullPath);

        if (!string.IsNullOrEmpty(root)
            && string.Equals(
                fullPath,
                root,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static bool PathEquals(
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

    private static bool IsChildPath(
        string candidate,
        string parent)
    {
        var comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        var parentPrefix =
            parent.EndsWith(
                Path.DirectorySeparatorChar)
                ? parent
                : parent + Path.DirectorySeparatorChar;

        return candidate.StartsWith(
            parentPrefix,
            comparison);
    }
}
