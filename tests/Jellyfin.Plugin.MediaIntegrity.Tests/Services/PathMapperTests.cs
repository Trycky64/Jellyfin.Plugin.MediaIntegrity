using Jellyfin.Plugin.MediaIntegrity.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class PathMapperTests
{
    private static PathMapper CreateMapper()
    {
        var configuration =
            new PluginConfiguration
            {
                SourceRoot = Path.GetFullPath(
                    Path.Combine(
                        Path.GetTempPath(),
                        "media-integrity-tests",
                        "media")),

                RepairRoot = Path.GetFullPath(
                    Path.Combine(
                        Path.GetTempPath(),
                        "media-integrity-tests",
                        "repair-media")),

                BackupRoot = Path.GetFullPath(
                    Path.Combine(
                        Path.GetTempPath(),
                        "media-integrity-tests",
                        "repair-backups")),

                TempRoot = Path.GetFullPath(
                    Path.Combine(
                        Path.GetTempPath(),
                        "media-integrity-tests",
                        "cache"))
            };

        Directory.CreateDirectory(configuration.SourceRoot);
        Directory.CreateDirectory(configuration.RepairRoot);
        Directory.CreateDirectory(configuration.BackupRoot);
        Directory.CreateDirectory(configuration.TempRoot);

        var configurationService =
            new PluginConfigurationService(
                NullLogger<PluginConfigurationService>.Instance,
                configuration);

        var pathSecurityService =
            new PathSecurityService(
                NullLogger<PathSecurityService>.Instance);

        return new PathMapper(
            configurationService,
            pathSecurityService,
            NullLogger<PathMapper>.Instance);
    }

    [Fact]
    public void MapToRepairPath_PreservesRelativePath()
    {
        var mapper = CreateMapper();

        var sourceRoot =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "media-integrity-tests",
                    "media"));

        var sourcePath =
            Path.Combine(
                sourceRoot,
                "series",
                "Bleach",
                "Season 04",
                "Episode 06.mp4");

        var expected =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "media-integrity-tests",
                    "repair-media",
                    "series",
                    "Bleach",
                        "Season 04",
                        "Episode 06.mp4"));

        CreateSourceFile(sourcePath);

        Assert.Equal(
            expected,
            mapper.MapToRepairPath(sourcePath));
    }

    [Fact]
    public void MapToBackupPath_PreservesRelativePath()
    {
        var mapper = CreateMapper();

        var sourceRoot =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "media-integrity-tests",
                    "media"));

        var sourcePath =
            Path.Combine(
                sourceRoot,
                "Movies",
                "Doctor Strange (2016)",
                "Doctor Strange (2016).mkv");

        var expected =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "media-integrity-tests",
                    "repair-backups",
                    "Movies",
                        "Doctor Strange (2016)",
                        "Doctor Strange (2016).mkv"));

        CreateSourceFile(sourcePath);

        Assert.Equal(
            expected,
            mapper.MapToBackupPath(sourcePath));
    }

    [Fact]
    public void MapToTemporaryPath_UsesRepairingSuffix()
    {
        var mapper = CreateMapper();

        var sourceRoot =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "media-integrity-tests",
                    "media"));

        var sourcePath =
            Path.Combine(
                sourceRoot,
                "series",
                "Bleach",
                "Episode 06.mp4");

        CreateSourceFile(sourcePath);

        var result =
            mapper.MapToTemporaryPath(sourcePath);

        Assert.Contains(
            ".repairing.mp4",
            result,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapToRepairPath_RejectsPathOutsideSourceRoot()
    {
        var mapper = CreateMapper();

        var sourcePath =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "outside",
                    "movie.mp4"));

        Assert.Throws<InvalidOperationException>(
            () => mapper.MapToRepairPath(sourcePath));
    }

    [Fact]
    public void MapToRepairPath_RejectsTraversalOutsideSourceRoot()
    {
        var mapper = CreateMapper();

        var sourceRoot =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "media-integrity-tests",
                    "media"));

        var sourcePath =
            Path.Combine(
                sourceRoot,
                "..",
                "outside",
                "movie.mp4");

        Assert.Throws<InvalidOperationException>(
            () => mapper.MapToRepairPath(sourcePath));
    }

    [Fact]
    public void MapAll_ReturnsAllExpectedRoots()
    {
        var mapper = CreateMapper();

        var baseRoot =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "media-integrity-tests"));

        var sourcePath =
            Path.Combine(
                baseRoot,
                "media",
                "series",
                "Show",
                "Episode.mkv");

        CreateSourceFile(sourcePath);

        var result =
            mapper.MapAll(sourcePath);

        Assert.StartsWith(
            Path.Combine(baseRoot, "media"),
            result.SourcePath);

        Assert.StartsWith(
            Path.Combine(baseRoot, "repair-media"),
            result.RepairPath);

        Assert.StartsWith(
            Path.Combine(baseRoot, "repair-backups"),
            result.BackupPath);

        Assert.StartsWith(
            Path.Combine(baseRoot, "cache"),
            result.TemporaryPath);
    }

    private static void CreateSourceFile(
        string path)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        File.WriteAllBytes(
            path,
            Array.Empty<byte>());
    }
}
