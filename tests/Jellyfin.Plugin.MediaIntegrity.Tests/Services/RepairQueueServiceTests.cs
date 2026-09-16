using System.Reflection;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class RepairQueueServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "media-integrity-queue-" + Guid.NewGuid().ToString("N"));

    private RepairQueueService CreateService()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, ScanStatisticsServiceTests.ApplicationPathsProxy>();
        ((ScanStatisticsServiceTests.ApplicationPathsProxy)(object)paths).DataPath = _root;
        return new RepairQueueService(paths, NullLogger<RepairQueueService>.Instance);
    }

    [Theory]
    [InlineData(RepairQueueItemStatus.Pending, 0, true)]
    [InlineData(RepairQueueItemStatus.Failed, 0, false)]
    [InlineData(RepairQueueItemStatus.Failed, 1, true)]
    [InlineData(RepairQueueItemStatus.Failed, 2, true)]
    [InlineData(RepairQueueItemStatus.Failed, 3, false)]
    [InlineData(RepairQueueItemStatus.Processing, 1, false)]
    [InlineData(RepairQueueItemStatus.Repaired, 1, false)]
    public void RetryPolicy_RespectsAttemptCapAndAmbiguousStates(RepairQueueItemStatus status, int attempts, bool expected)
    {
        Assert.Equal(expected, RepairRetryPolicy.IsCandidate(
            new RepairQueueItem { Status = status, Attempts = attempts }, new PluginConfiguration()));
    }

    [Fact]
    public async Task Queue_RetainsRetryHistoryAcrossReload()
    {
        var queue = RepairQueueService.CreateEmpty();
        queue.Files.Add(new RepairQueueItem
        {
            Path = "/media/fixture.mp4",
            Status = RepairQueueItemStatus.Failed,
            Attempts = 2,
            LastError = "Test timeout"
        });
        await CreateService().SaveAsync(queue, CancellationToken.None);
        var restored = await CreateService().LoadAsync(CancellationToken.None);
        Assert.Equal(2, restored.Files[0].Attempts);
        Assert.Equal("Test timeout", restored.Files[0].LastError);
        Assert.True(RepairRetryPolicy.IsCandidate(restored.Files[0], new PluginConfiguration()));
    }

    [Fact]
    public async Task ExecutionLease_SerializesTasksAndAllowsCancellationWhileWaiting()
    {
        var service = CreateService();
        var first = await service.AcquireExecutionAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = service.AcquireExecutionAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        first.Dispose(); // Lease disposal must not over-release the semaphore.
        using var second = await service.AcquireExecutionAsync(CancellationToken.None);
        using var thirdCancellation = new CancellationTokenSource();
        var third = service.AcquireExecutionAsync(thirdCancellation.Token);
        Assert.False(third.IsCompleted);
        thirdCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
    }

    [Fact]
    public async Task CancelledSave_KeepsPreviousQueueAndRemovesTemporary()
    {
        var service = CreateService();
        var queue = RepairQueueService.CreateEmpty();
        queue.Summary.Checked = 42;
        await service.SaveAsync(queue, CancellationToken.None);
        queue.Summary.Checked = 99;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(queue, new CancellationToken(true)));
        Assert.Equal(42, (await service.LoadAsync(CancellationToken.None)).Summary.Checked);
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
