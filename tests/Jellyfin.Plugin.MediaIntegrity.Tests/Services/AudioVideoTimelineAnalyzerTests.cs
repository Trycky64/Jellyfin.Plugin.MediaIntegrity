using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class AudioVideoTimelineAnalyzerTests
{
    [Fact]
    public void AlignedStreams_HaveNoIssues()
    {
        Assert.Empty(AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, 0, 100), Audio(1, 0, 100))));
    }

    [Fact]
    public void SmallCodecDelay_IsNotReported()
    {
        Assert.Empty(AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, .02, 100), Audio(1, 0, 100))));
    }

    [Fact]
    public void RegressionDuration_ReportsDurationEndAndDrift()
    {
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, .083411, 1428.428867), Audio(1, 0, 1427)));
        Assert.Contains(issues, static issue => issue.Code == "AudioVideoDurationMismatch");
        Assert.Contains(issues, static issue => issue.Code == "AudioVideoEndMismatch");
        Assert.Contains(issues, static issue => issue.Code == "AudioVideoDriftSuspected");
    }

    [Fact]
    public void SignificantOffset_IsTargetedToTheAudioTrack()
    {
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, .8, 100), Audio(1, 0, 100), Audio(2, .8, 100)));
        var issue = Assert.Single(issues, static issue => issue.Code == "AudioVideoStartOffset");
        Assert.Equal(0, issue.VideoStreamIndex);
        Assert.Equal(1, issue.AudioStreamIndex);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("video")]
    public void MissingAudioOrVideo_HasNoFalseIssue(string type)
    {
        var stream = type == "audio" ? Audio(0, 0, 100) : Video(0, 0, 100);
        Assert.Empty(AudioVideoTimelineAnalyzer.Analyze(Scan(stream, Other(1, "subtitle"))));
    }

    [Fact]
    public void MissingTimeline_IsReportedAsInformational()
    {
        var issue = Assert.Single(AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, null, 100), Audio(1, 0, 100))));
        Assert.Equal("TimelineDataIncomplete", issue.Code);
        Assert.Equal(MediaIssueSeverity.Info, issue.Severity);
    }

    [Fact]
    public void CorruptedStructuralStatus_IsNotReclassifiedByAudioVideoWarning()
    {
        var scan = Scan(Video(0, 0, 100), Audio(1, 0, 101));
        scan.Status = MediaIntegrityStatus.Corrupted;
        scan.Issues.Add(new MediaIssue { Code = "CORRUPT_INPUT_PACKET", Severity = MediaIssueSeverity.Critical });
        scan.Issues.AddRange(AudioVideoTimelineAnalyzer.Analyze(scan));
        Assert.Equal(MediaIntegrityStatus.Corrupted, scan.Status);
        Assert.Contains(scan.Issues, static issue => issue.Code == "AudioVideoDriftSuspected");
        Assert.Contains(scan.Issues, static issue => issue.Code == "CORRUPT_INPUT_PACKET");
    }

    [Theory]
    [InlineData(100, 100, 1)]
    [InlineData(100, 99, .99)]
    [InlineData(100, 101, 1.01)]
    [InlineData(.000001, .000002, 2)]
    [InlineData(1000000, 999000, .999)]
    public void DurationRatio_IsFiniteAndUsesAudioDividedByVideo(double videoDuration, double audioDuration, double expected)
    {
        var issue = Assert.Single(AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, 0, videoDuration), Audio(1, .3, audioDuration))),
            static item => item.Code == "AudioVideoStartOffset");
        Assert.Equal(expected, issue.AudioVideoDurationRatio!.Value, 8);
        Assert.True(double.IsFinite(issue.AudioVideoDurationRatio.Value));
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(null, 1.0)]
    [InlineData(1.0, null)]
    [InlineData(-1.0, 1.0)]
    public void InvalidDuration_NeverProducesNonFiniteRatio(double? videoDuration, double? audioDuration)
    {
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, 0, videoDuration), Audio(1, 0, audioDuration)));
        Assert.All(issues, issue => Assert.True(issue.AudioVideoDurationRatio is null || double.IsFinite(issue.AudioVideoDurationRatio.Value)));
        if (videoDuration is null || audioDuration is null || videoDuration < 0)
        {
            Assert.Contains(issues, static issue => issue.Code == "TimelineDataIncomplete");
        }
    }

    [Theory]
    [InlineData(.249999, false)]
    [InlineData(.250000, false)]
    [InlineData(.250001, true)]
    [InlineData(-.249999, false)]
    [InlineData(-.250000, false)]
    [InlineData(-.250001, true)]
    public void StartOffsetBoundary_IsStrict(double offset, bool expected)
    {
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, -2, 100), Audio(1, -2 + offset, 100)));
        Assert.Equal(expected, issues.Any(static issue => issue.Code == "AudioVideoStartOffset"));
    }

    [Theory]
    [InlineData(.499999, false)]
    [InlineData(.500000, false)]
    [InlineData(.500001, true)]
    [InlineData(-.499999, false)]
    [InlineData(-.500000, false)]
    [InlineData(-.500001, true)]
    public void DurationBoundary_IsStrict(double delta, bool expected)
    {
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, 0, 100), Audio(1, 0, 100 + delta)));
        Assert.Equal(expected, issues.Any(static issue => issue.Code == "AudioVideoDurationMismatch"));
        Assert.Equal(expected, issues.Any(static issue => issue.Code == "AudioVideoEndMismatch"));
    }

    [Fact]
    public void MultiAudio_OnlyProblemTrackCarriesItsMetadata()
    {
        var good = Audio(1, 0, 100);
        good.Language = "fra";
        good.IsDefault = true;
        var commentary = Audio(2, 0, 101);
        commentary.Language = "eng";
        commentary.Title = "Commentary";
        commentary.IsCommentary = true;
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, 0, 100), good, commentary));
        Assert.All(issues, issue =>
        {
            Assert.Equal(0, issue.VideoStreamIndex);
            Assert.Equal(2, issue.AudioStreamIndex);
            Assert.Equal("eng", issue.AudioLanguage);
            Assert.Equal("Commentary", issue.AudioTitle);
            Assert.True(issue.AudioIsCommentary);
            Assert.False(issue.AudioIsDefault);
        });
    }

    [Fact]
    public void IncompleteTimeline_EmitsOneIssuePerAffectedPair()
    {
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(
            Video(0, 0, 100), Audio(1, 0, 100), Audio(2, 0, null), Audio(3, 0, 100)));
        var issue = Assert.Single(issues);
        Assert.Equal("TimelineDataIncomplete", issue.Code);
        Assert.Equal(2, issue.AudioStreamIndex);
    }

    [Fact]
    public void IncompleteTimeline_MultipleVideosAndAudiosRemainDistinctWithoutDuplicates()
    {
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(
            Video(0, 0, 100), Video(4, 0, null),
            Audio(1, 0, null), Audio(2, null, 100), Audio(3, 0, null)));
        Assert.Equal(6, issues.Count);
        Assert.All(issues, static issue => Assert.Equal("TimelineDataIncomplete", issue.Code));
        Assert.Equal(6, issues.Select(static issue => (issue.VideoStreamIndex, issue.AudioStreamIndex)).Distinct().Count());
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(-1, -2, true)]
    [InlineData(-1, 1, true)]
    [InlineData(1, 0, true)]
    public void Drift_IsEvolutionOfOffset(double startOffset, double endOffset, bool expected)
    {
        var durationDelta = endOffset - startOffset;
        var issues = AudioVideoTimelineAnalyzer.Analyze(Scan(
            Video(0, 0, 100), Audio(1, startOffset, 100 + durationDelta)));
        Assert.Equal(expected, issues.Any(static issue => issue.Code == "AudioVideoDriftSuspected"));
    }

    [Fact]
    public void ExactDamagedRemuxRatio_IsReportedAndCorrectedFixtureIsQuiet()
    {
        var damaged = AudioVideoTimelineAnalyzer.Analyze(Scan(
            Video(0, .083, 1428.428867), Audio(1, 0, 1427)));
        var issue = Assert.Single(damaged, static item => item.Code == "AudioVideoDriftSuspected");
        Assert.Equal(1427d / 1428.428867d, issue.AudioVideoDurationRatio!.Value, 9);
        Assert.InRange(issue.AudioVideoDurationRatio.Value, .998999, .999001);

        var corrected = AudioVideoTimelineAnalyzer.Analyze(Scan(
            Video(0, .083, 1428.428867), Audio(1, 0, 1428.392)));
        Assert.Empty(corrected);
    }

    [Fact]
    public void LongMedia_RequiresBothAbsoluteAndRelativeDifference()
    {
        var small = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, 0, 7200), Audio(1, 0, 7201)));
        Assert.DoesNotContain(small, static issue => issue.Code == "AudioVideoDurationMismatch");
        var large = AudioVideoTimelineAnalyzer.Analyze(Scan(Video(0, 0, 7200), Audio(1, 0, 7204)));
        Assert.Contains(large, static issue => issue.Code == "AudioVideoDurationMismatch");
    }

    private static MediaScanResult Scan(params MediaStreamInfo[] streams) => new() { Streams = streams.ToList() };
    private static MediaStreamInfo Video(int index, double? start, double? duration) => new() { Index = index, CodecType = "video", CodecName = "h264", StartTimeSeconds = start, DurationSeconds = duration };
    private static MediaStreamInfo Audio(int index, double? start, double? duration) => new() { Index = index, CodecType = "audio", CodecName = "aac", StartTimeSeconds = start, DurationSeconds = duration };
    private static MediaStreamInfo Other(int index, string type) => new() { Index = index, CodecType = type, CodecName = "subrip" };
}
