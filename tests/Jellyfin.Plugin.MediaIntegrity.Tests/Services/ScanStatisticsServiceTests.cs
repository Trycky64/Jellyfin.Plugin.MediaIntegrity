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
        Assert.Equal(expected, await CreateService().LoadAsync(CancellationToken.None));
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
        Assert.Equal(expected, await service.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
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
