using System.Reflection;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Jellyfin.Plugin.MediaIntegrity.Tasks;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Tasks;

public sealed class AudioVideoScanTaskTests
{
    [Fact]
    public void AudioVideoWarning_UpdatesStatisticsWithoutQueueingRepair()
    {
        var queue = RepairQueueService.CreateEmpty();
        var counters = new AudioVideoScanCounters();
        var scan = new MediaScanResult
        {
            Status = MediaIntegrityStatus.Warning,
            Path = "/media/fixture.mp4",
            Issues = [new MediaIssue { Code = "AudioVideoDriftSuspected", Severity = MediaIssueSeverity.Warning }]
        };
        Process(scan, queue, counters);
        Assert.Equal(1, queue.Summary.Warning);
        Assert.Empty(queue.Files);
        Assert.Equal(1, counters.AffectedMedia);
    }

    [Fact]
    public void RepairableStructuralIssue_PreservesAudioVideoContextInQueue()
    {
        var queue = RepairQueueService.CreateEmpty();
        var issue = new MediaIssue
        {
            Code = "AudioVideoDurationMismatch",
            Severity = MediaIssueSeverity.Warning,
            VideoStreamIndex = 0,
            AudioStreamIndex = 2,
            AudioLanguage = "fra",
            AudioVideoDurationRatio = .999
        };
        Process(new MediaScanResult
        {
            Status = MediaIntegrityStatus.RemuxRecommended,
            Issues = [new MediaIssue { Code = "MP4_WRONG_SAMPLE_COUNT", Severity = MediaIssueSeverity.Repairable }, issue]
        }, queue, new AudioVideoScanCounters());
        var queued = Assert.Single(queue.Files);
        var retained = Assert.Single(queued.Issues, static item => item.Code == "AudioVideoDurationMismatch");
        Assert.Equal(2, retained.AudioStreamIndex);
        Assert.Equal("fra", retained.AudioLanguage);
        Assert.Equal(.999, retained.AudioVideoDurationRatio);
    }

    private static void Process(MediaScanResult scan, RepairQueue queue, AudioVideoScanCounters counters)
    {
        var method = typeof(MediaIntegrityScanTask).GetMethod("ProcessScanResult", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [scan, queue.Summary, queue, counters]);
    }
}
