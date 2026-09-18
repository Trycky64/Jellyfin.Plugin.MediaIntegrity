using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class StreamTimelineValidatorTests
{
    private static readonly PluginConfiguration Configuration = new()
    {
        StreamDurationToleranceSeconds = 0.05,
        StreamStartTimeToleranceSeconds = 0.01
    };

    [Fact]
    public void ExactRegression_IntroducedAudioVideoDriftIsRejected()
    {
        var source = CreateScan(
            Video(0, 0.083411, 1427.000000),
            Audio(1, 0, 1427.000000));
        var candidate = CreateScan(
            Video(0, 0.083000, 1428.428867),
            Audio(1, 0, 1427.000000));

        var issues = StreamTimelineValidator.Validate(source, candidate, Configuration);

        Assert.Contains(issues, static issue => issue.Code == "StreamDurationChanged");
        var drift = Assert.Single(issues, static issue => issue.Code == "AudioVideoDriftIntroduced");
        Assert.Contains("sourceAvDurationDelta=0.000000", drift.Message);
        Assert.Contains("repairedAvDurationDelta=+1.428867", drift.Message);
        Assert.Contains("introducedAvDrift=+1.428867", drift.Message);

        // v1.0.0 only compared the global container duration with a 2s tolerance.
        Assert.True(Math.Abs(1428.428867 - 1427.000000) < 2.0);
    }

    [Fact]
    public void IdenticalStreams_AreAccepted()
    {
        var source = CreateScan(Video(0, 0.083411, 1427), Audio(1, 0, 1427));
        Assert.Empty(StreamTimelineValidator.Validate(source, Clone(source), Configuration));
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public void SmallDurationRounding_IsAccepted(string streamType)
    {
        var source = CreateScan(Video(0, 0, 100), Audio(1, 0, 100));
        var candidate = Clone(source);
        candidate.Streams.Single(stream => stream.CodecType == streamType).DurationSeconds = 100.04;
        Assert.Empty(StreamTimelineValidator.Validate(source, candidate, Configuration));
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public void SignificantDurationChange_IsRejected(string streamType)
    {
        var source = CreateScan(Video(0, 0, 100), Audio(1, 0, 100));
        var candidate = Clone(source);
        candidate.Streams.Single(stream => stream.CodecType == streamType).DurationSeconds = 100.06;
        Assert.Contains(StreamTimelineValidator.Validate(source, candidate, Configuration),
            static issue => issue.Code == "StreamDurationChanged");
    }

    [Fact]
    public void ExistingAudioVideoOffsetThatIsNotWorsened_IsAccepted()
    {
        var source = CreateScan(Video(0, 0.1, 100.2), Audio(1, 0, 100));
        var candidate = CreateScan(Video(0, 0.101, 100.21), Audio(1, 0.001, 100.01));
        Assert.Empty(StreamTimelineValidator.Validate(source, candidate, Configuration));
    }

    [Theory]
    [InlineData("video", 0.009, false)]
    [InlineData("audio", 0.009, false)]
    [InlineData("video", 0.011, true)]
    [InlineData("audio", 0.011, true)]
    public void StartTimeChangesRespectTolerance(string streamType, double delta, bool invalid)
    {
        var source = CreateScan(Video(0, 0, 100), Audio(1, 0, 100));
        var candidate = Clone(source);
        candidate.Streams.Single(stream => stream.CodecType == streamType).StartTimeSeconds = delta;
        var issues = StreamTimelineValidator.Validate(source, candidate, Configuration);
        Assert.Equal(invalid, issues.Any(static issue => issue.Code == "StreamStartTimeChanged"));
    }

    [Fact]
    public void UnknownTimedStreamDuration_IsRejectedWithoutThrowing()
    {
        var source = CreateScan(Video(0, 0, null), Audio(1, 0, 100));
        var candidate = Clone(source);
        Assert.Contains(StreamTimelineValidator.Validate(source, candidate, Configuration),
            static issue => issue.Code == "StreamDurationUnknown");
    }

    [Fact]
    public void UnknownTimedStreamStartTime_IsRejectedWithoutThrowing()
    {
        var source = CreateScan(Video(0, null, 100), Audio(1, 0, 100));
        var candidate = Clone(source);
        Assert.Contains(StreamTimelineValidator.Validate(source, candidate, Configuration),
            static issue => issue.Code == "StreamStartTimeUnknown");
    }

    [Fact]
    public void MultiAudioAndMultiVideo_AreEachCompared()
    {
        var source = CreateScan(Video(0, 0, 100), Video(1, 0, 100), Audio(2, 0, 100), Audio(3, 0, 100));
        var candidate = Clone(source);
        candidate.Streams.Single(stream => stream.Index == 3).DurationSeconds = 100.2;
        var issues = StreamTimelineValidator.Validate(source, candidate, Configuration);
        Assert.Contains(issues, static issue => issue.Code == "StreamDurationChanged");
        Assert.Contains(issues, static issue => issue.Code == "AudioVideoDriftIntroduced");
    }

    [Fact]
    public void SubtitleAndAttachmentAreRetainedWithoutTimelineRequirement()
    {
        var source = CreateScan(
            Video(0, 0, 100),
            Audio(1, 0, 100),
            Other(2, "subtitle", "subrip"),
            Other(3, "attachment", "ttf"),
            Other(4, "data", "bin_data"));
        Assert.Empty(StreamTimelineValidator.Validate(source, Clone(source), Configuration));
    }

    [Fact]
    public void MissingAndUnexpectedStreams_AreRejected()
    {
        var source = CreateScan(Video(0, 0, 100), Audio(1, 0, 100));
        var candidate = CreateScan(Video(0, 0, 100), Other(2, "subtitle", "subrip"));
        var issues = StreamTimelineValidator.Validate(source, candidate, Configuration);
        Assert.Contains(issues, static issue => issue.Code == "StreamMissing");
        Assert.Contains(issues, static issue => issue.Code == "StreamUnexpected");
    }

    [Fact]
    public void CodecAndTimeBaseChanges_AreRejected()
    {
        var source = CreateScan(Video(0, 0, 100), Audio(1, 0, 100));
        var candidate = Clone(source);
        candidate.Streams[0].CodecName = "hevc";
        candidate.Streams[1].TimeBase = "1/48000";

        var issues = StreamTimelineValidator.Validate(source, candidate, Configuration);

        Assert.Contains(issues, static issue => issue.Code == "StreamCodecChanged");
        Assert.Contains(issues, static issue => issue.Code == "StreamTimeBaseChanged");
    }

    [Fact]
    public void WrongSampleCountWithoutTimelineChange_RemainsEligibleForValidation()
    {
        var source = CreateScan(Video(0, 0, 100), Audio(1, 0, 100));
        source.Issues.Add(new MediaIssue { Code = "MP4_WRONG_SAMPLE_COUNT", Message = "test", RawMessage = "wrong sample count" });
        Assert.Empty(StreamTimelineValidator.Validate(source, Clone(source), Configuration));
    }

    [Fact]
    public void DeterministicTimelineFailures_AreNotRetryable()
    {
        Assert.True(RepairRetryPolicy.IsDeterministicTimelineFailure(
            "Remux validation failed: StreamDurationChanged: timeline changed."));
        Assert.False(RepairRetryPolicy.IsDeterministicTimelineFailure("FFmpeg exited with code 1."));
    }

    private static MediaScanResult CreateScan(params MediaStreamInfo[] streams) =>
        new() { Streams = streams.ToList() };

    private static MediaScanResult Clone(MediaScanResult source) =>
        new()
        {
            Streams = source.Streams.Select(stream => new MediaStreamInfo
            {
                Index = stream.Index,
                CodecType = stream.CodecType,
                CodecName = stream.CodecName,
                TimeBase = stream.TimeBase,
                StartTimeSeconds = stream.StartTimeSeconds,
                DurationSeconds = stream.DurationSeconds
            }).ToList()
        };

    private static MediaStreamInfo Video(int index, double? start, double? duration) =>
        new()
        {
            Index = index,
            CodecType = "video",
            CodecName = "h264",
            TimeBase = "1/90000",
            StartTimeSeconds = start,
            DurationSeconds = duration
        };

    private static MediaStreamInfo Audio(int index, double? start, double? duration) =>
        new()
        {
            Index = index,
            CodecType = "audio",
            CodecName = "aac",
            TimeBase = "1/32000",
            StartTimeSeconds = start,
            DurationSeconds = duration
        };

    private static MediaStreamInfo Other(int index, string type, string codec) =>
        new() { Index = index, CodecType = type, CodecName = codec };
}
