using Jellyfin.Plugin.MediaIntegrity.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Models;

public sealed class AudioVideoScanCountersTests
{
    [Fact]
    public void NoIssue_DoesNotAffectCounters()
    {
        var counters = new AudioVideoScanCounters();
        counters.Add(new MediaScanResult());
        Assert.Equal(0, counters.AffectedMedia);
    }

    [Fact]
    public void MultipleIssuesInOneMedia_CountsAffectedMediaOnce()
    {
        var counters = new AudioVideoScanCounters();
        counters.Add(Result("AudioVideoStartOffset", "AudioVideoDurationMismatch", "AudioVideoEndMismatch", "AudioVideoDriftSuspected"));
        Assert.Equal(1, counters.AffectedMedia);
        Assert.Equal(1, counters.StartOffsets);
        Assert.Equal(1, counters.SuspectedDrifts);
    }

    [Fact]
    public void MultipleMedia_AccumulatesIncompleteTimeline()
    {
        var counters = new AudioVideoScanCounters();
        counters.Add(Result("TimelineDataIncomplete"));
        counters.Add(Result("AudioVideoStartOffset"));
        Assert.Equal(2, counters.AffectedMedia);
        Assert.Equal(1, counters.IncompleteTimelineData);
    }

    private static MediaScanResult Result(params string[] codes) => new()
    {
        Issues = codes.Select(code => new MediaIssue { Code = code }).ToList()
    };
}
