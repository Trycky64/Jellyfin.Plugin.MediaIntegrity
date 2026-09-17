using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class PacketTimelineAnalyzerTests
{
    [Fact]
    public async Task AlreadyCancelled_DoesNotStartProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PacketTimelineAnalyzer.AnalyzeAsync("nonexistent.mp4", new MediaScanResult { DurationSeconds = 4 }, 5, cancellation.Token));
    }

    [Fact]
    public void AlignedPacketEnds_HaveNoDrift()
    {
        var scan = Scan();
        Assert.Empty(PacketTimelineAnalyzer.AnalyzeSamples(scan,
            [new(0, 0, .04), new(1, 0, .02), new(0, 99.96, .04), new(1, 99.98, .02)]));
    }

    [Fact]
    public void PacketEnds_ExposeStartAndEndOffsetEvolution()
    {
        var issue = Assert.Single(PacketTimelineAnalyzer.AnalyzeSamples(Scan(),
            [new(0, 0, .04), new(1, .1, .02), new(0, 99.96, .04), new(1, 101.08, .02)]));
        Assert.Equal("AudioVideoPacketDriftSuspected", issue.Code);
        Assert.Equal(0, issue.VideoStreamIndex);
        Assert.Equal(1, issue.AudioStreamIndex);
        Assert.Equal(.1, issue.StartDeltaSeconds!.Value, 6);
        Assert.Equal(1.1, issue.EndDeltaSeconds!.Value, 6);
        Assert.Equal(1, issue.OffsetEvolutionSeconds!.Value, 6);
    }

    [Fact]
    public void MissingPacketStream_DoesNotInventDrift()
    {
        Assert.Empty(PacketTimelineAnalyzer.AnalyzeSamples(Scan(), [new(0, 0, .04), new(0, 99.96, .04)]));
    }

    [Fact]
    public void AttachedPictureAndZeroDurationMjpeg_AreIgnored()
    {
        var scan = Scan();
        scan.Streams.Add(new MediaStreamInfo { Index = 2, CodecType = "video", CodecName = "mjpeg", DurationSeconds = 0 });
        scan.Streams.Add(new MediaStreamInfo { Index = 3, CodecType = "video", CodecName = "mjpeg", DurationSeconds = 100, IsAttachedPicture = true });
        var issues = PacketTimelineAnalyzer.AnalyzeSamples(scan,
            [new(0, 0, .04), new(1, 0, .02), new(0, 99.96, .04), new(1, 99.98, .02),
                new(2, 0, 0), new(2, 99, 0), new(3, 0, .04), new(3, 99.96, .04)]);
        Assert.Empty(issues);
    }

    [Fact]
    public void TemporalMjpegAndRealLongAnomaly_AreStillAnalyzed()
    {
        var scan = Scan();
        scan.Streams[0].CodecName = "mjpeg";
        var issue = Assert.Single(PacketTimelineAnalyzer.AnalyzeSamples(scan,
            [new(0, 0, .04), new(1, 0, .02), new(0, 99.96, .04), new(1, 101.08, .02)]));
        Assert.Equal(0, issue.VideoStreamIndex);
    }

    private static MediaScanResult Scan() => new()
    {
        DurationSeconds = 100,
        Streams =
        [
            new() { Index = 0, CodecType = "video", DurationSeconds = 100 },
            new() { Index = 1, CodecType = "audio" }
        ]
    };
}
