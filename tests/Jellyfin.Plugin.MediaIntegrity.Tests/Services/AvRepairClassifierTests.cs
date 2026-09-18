using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class AvRepairClassifierTests
{
    private static readonly PluginConfiguration Configuration = new();

    [Fact]
    public void AlignedStreams_ClassifyAsNone()
    {
        var diagnosis = Classify(Video(0, 100), Audio(0, 100), evidence: null);
        Assert.Equal(AvRepairClassification.None, diagnosis.Classification);
        Assert.Equal(1.0, diagnosis.Confidence);
    }

    [Fact]
    public void ConstantOffsetPositive_WithPacketConfirmation_IsHighConfidence()
    {
        var evidence = Evidence(0, 0, startOffset: 1.5, endOffset: 1.5);
        var diagnosis = Classify(Video(0, 100, videoStart: 0), Audio(0, 100, audioStart: 1.5), evidence);
        Assert.Equal(AvRepairClassification.ConstantOffset, diagnosis.Classification);
        Assert.True(diagnosis.Confidence >= 0.9);
    }

    [Fact]
    public void ConstantOffsetNegative_WithPacketConfirmation_IsHighConfidence()
    {
        var evidence = Evidence(0, 0, startOffset: -1.5, endOffset: -1.5);
        var diagnosis = Classify(Video(0, 100, videoStart: 0), Audio(0, 100, audioStart: -1.5), evidence);
        Assert.Equal(AvRepairClassification.ConstantOffset, diagnosis.Classification);
        Assert.True(diagnosis.Confidence >= 0.9);
    }

    [Fact]
    public void ConstantOffset_NearZero_IsNone()
    {
        var diagnosis = Classify(Video(0, 100, videoStart: 0), Audio(0, 100, audioStart: 0.05), evidence: null);
        Assert.Equal(AvRepairClassification.None, diagnosis.Classification);
    }

    [Fact]
    public void ConstantOffset_WithoutPacketEvidence_IsTimelineMetadataOnly()
    {
        var diagnosis = Classify(Video(0, 100, videoStart: 0), Audio(0, 100, audioStart: 1.5), evidence: null);
        Assert.Equal(AvRepairClassification.TimelineMetadataOnly, diagnosis.Classification);
        Assert.True(diagnosis.Confidence < 0.75);
    }

    [Fact]
    public void SmallDurationMismatch_WithoutPacketEvidence_IsBoundedAudioEndsEarly()
    {
        var diagnosis = Classify(Video(0, 100), Audio(0, 99), evidence: null);
        Assert.Equal(AvRepairClassification.AudioEndsEarly, diagnosis.Classification);
    }

    [Fact]
    public void LargeDurationMismatch_WithoutPacketEvidence_IsDurationMismatch()
    {
        var diagnosis = Classify(Video(0, 100), Audio(0, 90), evidence: null);
        Assert.Equal(AvRepairClassification.DurationMismatch, diagnosis.Classification);
    }

    [Fact]
    public void GrossMismatch_IsAlwaysUnsafe()
    {
        var diagnosis = Classify(Video(0, 9000), Audio(0, 8769), evidence: null);
        Assert.Equal(AvRepairClassification.UnsafeToAutoRepair, diagnosis.Classification);
        Assert.Equal(1.0, diagnosis.Confidence);
    }

    [Fact]
    public void GrossMismatch_231Seconds_IsUnsafeNotAutoRepaired()
    {
        // Documented real-world case: diagnose only, never auto-repair.
        var diagnosis = Classify(Video(0, 5400), Audio(0, 5400 - 231), evidence: null);
        Assert.Equal(AvRepairClassification.UnsafeToAutoRepair, diagnosis.Classification);
    }

    [Fact]
    public void ProgressiveDriftPositive_WithPacketEvidence_IsDetected()
    {
        var evidence = Evidence(0, 0, startOffset: 0.0, endOffset: 3.0);
        var diagnosis = Classify(Video(0, 5000), Audio(0, 5003), evidence);
        Assert.Equal(AvRepairClassification.ProgressiveDrift, diagnosis.Classification);
        Assert.True(diagnosis.Confidence >= 0.8);
    }

    [Fact]
    public void ProgressiveDriftNegative_WithPacketEvidence_IsDetected()
    {
        var evidence = Evidence(0, 0, startOffset: 0.0, endOffset: -3.0);
        var diagnosis = Classify(Video(0, 5000), Audio(0, 4997), evidence);
        Assert.Equal(AvRepairClassification.ProgressiveDrift, diagnosis.Classification);
    }

    [Fact]
    public void ProgressiveDrift_WithNonZeroStartOffsetAndEvolution_IsDetected()
    {
        // Offset compounds from 1.0s to 4.0s: this is not a single-sided bounded mismatch.
        var evidence = Evidence(0, 0, startOffset: 1.0, endOffset: 4.0);
        var diagnosis = Classify(Video(0, 5000, videoStart: 0), Audio(0, 5003, audioStart: 1.0), evidence);
        Assert.Equal(AvRepairClassification.ProgressiveDrift, diagnosis.Classification);
    }

    [Fact]
    public void InsufficientPacketData_DoesNotProduceProgressiveDrift()
    {
        var diagnosis = Classify(Video(0, 5000), Audio(0, 5003), evidence: null);
        Assert.NotEqual(AvRepairClassification.ProgressiveDrift, diagnosis.Classification);
    }

    [Fact]
    public void TimelineDiscontinuity_IsUnsafe()
    {
        var evidence = Evidence(0, 0, startOffset: 1.0, endOffset: 1.0, hasDiscontinuity: true);
        var diagnosis = Classify(Video(0, 100, videoStart: 0), Audio(0, 100, audioStart: 1.0), evidence);
        Assert.Equal(AvRepairClassification.UnsafeToAutoRepair, diagnosis.Classification);
    }

    [Fact]
    public void AudioEndsEarly_WithPacketConfirmation_IsHighConfidence()
    {
        var evidence = Evidence(0, 0, startOffset: 0.0, endOffset: -1.0);
        var diagnosis = Classify(Video(0, 100), Audio(0, 99), evidence);
        Assert.Equal(AvRepairClassification.AudioEndsEarly, diagnosis.Classification);
        Assert.True(diagnosis.Confidence >= 0.8);
    }

    [Fact]
    public void AudioEndsLate_WithPacketConfirmation_IsHighConfidence()
    {
        var evidence = Evidence(0, 0, startOffset: 0.0, endOffset: 1.0);
        var diagnosis = Classify(Video(0, 100), Audio(0, 101), evidence);
        Assert.Equal(AvRepairClassification.AudioEndsLate, diagnosis.Classification);
        Assert.True(diagnosis.Confidence >= 0.8);
    }

    [Fact]
    public void MultiAudio_HealthyAndDefectiveTracksClassifyIndependently()
    {
        var video = Video(0, 100);
        var healthy = Audio(0, 100);
        var defective = Audio(1, 90);
        var diagnoses = AvRepairClassifier.ClassifyAll(Scan(video, healthy, defective), null, Configuration);
        var single = Assert.Single(diagnoses);
        Assert.Equal(1, single.AudioStreamIndex);
    }

    [Fact]
    public void Vfr_MetadataOnlyDoesNotFalselyConfirmDrift()
    {
        // VFR files often carry slightly imprecise container duration; without
        // packet confirmation this must stay in the conservative, low-confidence bucket.
        var diagnosis = Classify(Video(0, 3600.2), Audio(0, 3599.1), evidence: null);
        Assert.NotEqual(AvRepairClassification.ProgressiveDrift, diagnosis.Classification);
    }

    [Fact]
    public void MjpegTemporalVideo_IsStillClassified()
    {
        var video = new MediaStreamInfo
        {
            Index = 8,
            CodecType = "video",
            CodecName = "mjpeg",
            StartTimeSeconds = 0,
            DurationSeconds = 100
        };
        var audio = Audio(9, 101);
        var diagnoses = AvRepairClassifier.ClassifyAll(Scan(video, audio), null, Configuration);
        Assert.Single(diagnoses);
    }

    [Fact]
    public void AttachedPicture_IsNeverClassified()
    {
        var picture = new MediaStreamInfo
        {
            Index = 3,
            CodecType = "video",
            CodecName = "mjpeg",
            StartTimeSeconds = 0,
            DurationSeconds = 100,
            IsAttachedPicture = true
        };
        var audio = Audio(1, 50);
        var diagnoses = AvRepairClassifier.ClassifyAll(Scan(picture, audio), null, Configuration);
        Assert.Empty(diagnoses);
    }

    [Fact]
    public void StillImageStream_WithoutDuration_IsNeverClassified()
    {
        var still = new MediaStreamInfo
        {
            Index = 3,
            CodecType = "video",
            CodecName = "png",
            StartTimeSeconds = 0,
            DurationSeconds = 0
        };
        var audio = Audio(1, 100);
        var diagnoses = AvRepairClassifier.ClassifyAll(Scan(still, audio), null, Configuration);
        Assert.Empty(diagnoses);
    }

    [Fact]
    public void IncompleteTimeline_IsAmbiguousWithZeroConfidence()
    {
        var diagnosis = AvRepairClassifier.Classify(
            Video(0, null), Audio(0, 100), null, Configuration);
        Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
        Assert.Equal(0.0, diagnosis.Confidence);
    }

    private static AvRepairDiagnosis Classify(MediaStreamInfo video, MediaStreamInfo audio, AvPacketEvidence? evidence) =>
        AvRepairClassifier.Classify(video, audio, evidence, Configuration);

    private static MediaScanResult Scan(params MediaStreamInfo[] streams) => new() { Streams = streams.ToList() };

    private static MediaStreamInfo Video(int index, double? duration, double videoStart = 0, string codec = "h264") =>
        new() { Index = index, CodecType = "video", CodecName = codec, StartTimeSeconds = videoStart, DurationSeconds = duration };

    private static MediaStreamInfo Audio(int index, double? duration, double audioStart = 0) =>
        new() { Index = index, CodecType = "audio", CodecName = "aac", StartTimeSeconds = audioStart, DurationSeconds = duration };

    private static AvPacketEvidence Evidence(
        int video, int audio, double startOffset, double endOffset, bool hasDiscontinuity = false) =>
        new()
        {
            VideoStreamIndex = video,
            AudioStreamIndex = audio,
            StartOffsetSeconds = startOffset,
            EndOffsetSeconds = endOffset,
            HasDiscontinuity = hasDiscontinuity
        };
}
