using System.Reflection;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using MediaBrowser.Common.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class ScanStatisticsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "media-integrity-stats-" + Guid.NewGuid().ToString("N"));

    private ScanStatisticsService CreateService()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, ApplicationPathsProxy>();
        ((ApplicationPathsProxy)(object)paths).DataPath = _root;
        return new ScanStatisticsService(paths);
    }

    [Fact]
    public async Task MissingStats_ReturnNull()
    {
        Assert.Null(await CreateService().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stats_SurviveNewServiceAndRemainIndependentOfQueue()
    {
        var queue = RepairQueueService.CreateEmpty();
        queue.Summary.Checked = 4;
        queue.Summary.Ok = 1;
        queue.Summary.Warning = 1;
        queue.Summary.Unreadable = 1;
        queue.Files.Add(new RepairQueueItem { Path = "/media/test.mp4" });
        var expected = LastScanStats.FromQueue(queue);
        await CreateService().SaveAsync(expected, CancellationToken.None);
        queue.Files.Clear();
        queue.Summary.Checked = 0;
        Assert.Equivalent(expected, await CreateService().LoadAsync(CancellationToken.None));
        Assert.Equal(1, expected.Queued);
        Assert.Equal(4, expected.Checked);
    }

    [Fact]
    public async Task CancelledSave_PreservesPreviousStatsAndCleansTemporary()
    {
        var service = CreateService();
        var expected = new LastScanStats { Checked = 12 };
        await service.SaveAsync(expected, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SaveAsync(new LastScanStats { Checked = 99 }, new CancellationToken(true)));
        Assert.Equivalent(expected, await service.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LegacyJson_LoadsWithZeroAudioVideoStatistics()
    {
        var path = Path.Combine(_root, "media-integrity", "last-scan.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"checked\":3,\"healthy\":2}");
        var stats = (await CreateService().LoadAsync(CancellationToken.None))!;
        Assert.Equal(3, stats.Checked);
        Assert.Equal(0, stats.AudioVideoAffectedMedia);
        Assert.Empty(stats.AudioVideoDiagnostics);
    }

    [Fact]
    public async Task AudioVideoDiagnostics_RoundTripWithStatistics()
    {
        var counters = new AudioVideoScanCounters();
        counters.Add(new MediaScanResult
        {
            Path = "/media/fixture.mp4",
            Issues = [new MediaIssue
            {
                Code = "AudioVideoDurationMismatch",
                Severity = MediaIssueSeverity.Warning,
                VideoStreamIndex = 0,
                AudioStreamIndex = 2,
                DurationDeltaSeconds = -1.428867
            }]
        });
        var expected = LastScanStats.FromQueue(RepairQueueService.CreateEmpty(), counters);
        await CreateService().SaveAsync(expected, CancellationToken.None);
        var loaded = (await CreateService().LoadAsync(CancellationToken.None))!;
        Assert.Equal(1, loaded.AudioVideoAffectedMedia);
        Assert.Equal(1, loaded.AudioVideoDurationMismatches);
        var diagnostic = Assert.Single(loaded.AudioVideoDiagnostics);
        Assert.Equal("fixture.mp4", diagnostic.FileName);
        Assert.Equal(2, diagnostic.AudioStreamIndex);
        Assert.Equal(-1.428867, diagnostic.DurationDeltaSeconds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public class ApplicationPathsProxy : DispatchProxy
    {
        public string DataPath { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_DataPath" ? DataPath : throw new NotSupportedException();
    }
}
