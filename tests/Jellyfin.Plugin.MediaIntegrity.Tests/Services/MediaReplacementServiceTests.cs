using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class MediaReplacementServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "media-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _configuration;
    private readonly MediaReplacementService _service;
    private readonly string _source;
    private readonly string _temporary;

    public MediaReplacementServiceTests()
    {
        _configuration = new PluginConfiguration
        {
            DryRun = false,
            KeepBackups = false,
            SourceRoot = Path.Combine(_root, "media"),
            RepairRoot = Path.Combine(_root, "repair-media"),
            BackupRoot = Path.Combine(_root, "backups"),
            TempRoot = Path.Combine(_root, "cache")
        };
        foreach (var path in new[] { _configuration.SourceRoot, _configuration.RepairRoot, _configuration.BackupRoot, _configuration.TempRoot })
        {
            Directory.CreateDirectory(path);
        }

        _source = Path.Combine(_configuration.SourceRoot, "series", "Show", "episode.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        File.WriteAllText(_source, "original media fixture");
        var repair = Path.Combine(_configuration.RepairRoot, "series", "Show", "episode.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(repair)!);
        File.Copy(_source, repair);
        _temporary = Path.Combine(_configuration.TempRoot, "episode.repairing.mp4");
        File.WriteAllText(_temporary, "validated remux fixture");
        var configService = new PluginConfigurationService(NullLogger<PluginConfigurationService>.Instance, _configuration);
        var security = new PathSecurityService(NullLogger<PathSecurityService>.Instance);
        var mapper = new PathMapper(configService, security, NullLogger<PathMapper>.Instance);
        _service = new MediaReplacementService(configService, mapper, security, NullLogger<MediaReplacementService>.Instance);
    }

    [Fact]
    public async Task Replace_PreservesBackupAndManifestEvenWhenKeepBackupsFalse()
    {
        var result = await _service.ReplaceAsync(_source, _temporary, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True(result.BackupCreated);
        Assert.Equal(Path.Combine(_configuration.BackupRoot, "series", "Show", "episode.mp4"), result.BackupPath);
        Assert.Equal(File.ReadAllBytes(_source), File.ReadAllBytes(result.BackupPath));
        Assert.Equal(File.ReadAllBytes(_temporary), File.ReadAllBytes(result.RepairPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(result.BackupPath + ".metadata.json"));
        var data = manifest.RootElement;
        Assert.Equal(_source, data.GetProperty("sourcePath").GetString());
        Assert.Equal(result.BackupPath, data.GetProperty("backupPath").GetString());
        Assert.Equal(result.RepairPath, data.GetProperty("repairedPath").GetString());
        Assert.Equal(Hash(_source), data.GetProperty("sourceHash").GetString());
        Assert.Equal(Hash(result.BackupPath), data.GetProperty("backupHash").GetString());
        Assert.Equal(Hash(result.RepairPath), data.GetProperty("repairedHash").GetString());
        Assert.Equal("SHA-256", data.GetProperty("algorithm").GetString());
        Assert.NotEmpty(data.GetProperty("pluginVersion").GetString()!);
        Assert.True(data.GetProperty("timestampUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Replace_CollisionPreservesBothBackups()
    {
        var first = await _service.ReplaceAsync(_source, _temporary, CancellationToken.None);
        File.Copy(_source, first.RepairPath, overwrite: true);
        var second = await _service.ReplaceAsync(_source, _temporary, CancellationToken.None);
        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Contains(".original-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd"), second.BackupPath);
        Assert.True(File.Exists(first.BackupPath + ".metadata.json"));
        Assert.True(File.Exists(second.BackupPath + ".metadata.json"));
    }

    [Fact]
    public async Task Replace_DryRunDoesNotWriteBackupOrReplacement()
    {
        _configuration.DryRun = true;
        var result = await _service.ReplaceAsync(_source, _temporary, CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(result.ReplacementCompleted);
        Assert.Empty(Directory.GetFiles(_configuration.BackupRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(Hash(_source), Hash(result.RepairPath));
    }

    [Fact]
    public async Task Replace_CancelledBeforeStartLeavesOriginalUntouched()
    {
        var expected = Hash(_source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.ReplaceAsync(_source, _temporary, new CancellationToken(true)));
        Assert.Equal(expected, Hash(_source));
        Assert.Empty(Directory.GetFiles(_configuration.BackupRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Replace_ManifestFailurePreventsReplacementAndRetainsBackup()
    {
        var backup = Path.Combine(_configuration.BackupRoot, "series", "Show", "episode.mp4");
        Directory.CreateDirectory(backup + ".metadata.json");
        var result = await _service.ReplaceAsync(_source, _temporary, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.BackupCreated);
        Assert.False(result.ReplacementCompleted);
        Assert.Equal(Hash(_source), Hash(backup));
        Assert.Equal(Hash(_source), Hash(result.RepairPath));
    }

    [Fact]
    public async Task Replace_PostSwapFailureRollsBackAndRetainsRecoveryManifest()
    {
        var configurationService = new PluginConfigurationService(NullLogger<PluginConfigurationService>.Instance, _configuration);
        var security = new PathSecurityService(NullLogger<PathSecurityService>.Instance);
        var mapper = new PathMapper(configurationService, security, NullLogger<PathMapper>.Instance);
        var service = new MediaReplacementService(configurationService, mapper, security, new PostSwapFailureLogger());
        var result = await service.ReplaceAsync(_source, _temporary, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(Hash(_source), Hash(result.RepairPath));
        Assert.True(File.Exists(result.BackupPath + ".metadata.json"));
        Assert.Empty(Directory.GetFiles(_configuration.RepairRoot, "*.replacing", SearchOption.AllDirectories));
    }

    private sealed class PostSwapFailureLogger : ILogger<MediaReplacementService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Transactional replacement completed", StringComparison.Ordinal))
            {
                throw new IOException("Injected post-swap failure to exercise rollback.");
            }
        }
    }

    [Fact]
    public async Task Replace_RejectsMismatchedWritableMirror()
    {
        var repair = Path.Combine(_configuration.RepairRoot, "series", "Show", "episode.mp4");
        File.WriteAllText(repair, "unrelated media that must not be overwritten");
        var expected = Hash(repair);
        var result = await _service.ReplaceAsync(_source, _temporary, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.ReplacementCompleted);
        Assert.Contains("mirror SHA-256", result.Error);
        Assert.Equal(expected, Hash(repair));
        Assert.True(result.BackupCreated);
    }

    [Fact]
    public async Task Replace_PlaybackStartingDuringPreparationPreventsSwap()
    {
        var checks = 0;
        var result = await _service.ReplaceAsync(_source, _temporary, CancellationToken.None, () =>
        {
            checks++;
            Assert.True(File.Exists(Path.Combine(_configuration.BackupRoot, "series", "Show", "episode.mp4.metadata.json")));
            return true;
        });
        Assert.Equal(1, checks);
        Assert.False(result.Success);
        Assert.False(result.ReplacementCompleted);
        Assert.Equal(Hash(_source), Hash(result.RepairPath));
        Assert.Empty(Directory.GetFiles(_configuration.RepairRoot, "*.replacing", SearchOption.AllDirectories));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
