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

    // ---- v1.2.1: packet evidence is a confirmation, never an authority ----

    [Fact]
    public void PartialTailWindow_MetadataFarAboveLimit_IsAmbiguousNotAudioEndsLate()
    {
        // Case A: metadata +10.8s (start in sync), sampled packets +1.897s, limit 2.0s.
        var evidence = Evidence(0, 0, startOffset: 0, endOffset: 1.897);
        var diagnosis = Classify(Video(0, 100), Audio(0, 110.8), evidence);
        Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
        Assert.Contains("conflicts", diagnosis.Reason);
        Assert.False(diagnosis.IsPotentiallyAutoRepairable);
        var plan = AvRepairPlanner.Plan(diagnosis, PlanningConfiguration());
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
        Assert.Null(plan.TrimSeconds);
    }

    [Fact]
    public void ConsistentBoundedLateEnd_IsAudioEndsLateWithMetadataMagnitude()
    {
        // Case B
        var evidence = Evidence(0, 0, startOffset: 0, endOffset: 1.05);
        var diagnosis = Classify(Video(0, 100), Audio(0, 101), evidence);
        Assert.Equal(AvRepairClassification.AudioEndsLate, diagnosis.Classification);
        var plan = AvRepairPlanner.Plan(diagnosis, PlanningConfiguration());
        Assert.Equal(AvRepairStrategy.AudioTrim, plan.Strategy);
        Assert.Equal(1.0, plan.TrimSeconds!.Value, 6);
        Assert.True(plan.IsAutoRepairEligible);
    }

    [Fact]
    public void ConsistentBoundedEarlyEnd_IsAudioEndsEarlyAndPads()
    {
        // Case C
        var evidence = Evidence(0, 0, startOffset: 0, endOffset: -1.05);
        var diagnosis = Classify(Video(0, 100), Audio(0, 99), evidence);
        Assert.Equal(AvRepairClassification.AudioEndsEarly, diagnosis.Classification);
        var plan = AvRepairPlanner.Plan(diagnosis, PlanningConfiguration());
        Assert.Equal(AvRepairStrategy.AudioPad, plan.Strategy);
        Assert.Equal(1.0, plan.PadSeconds!.Value, 6);
    }

    [Fact]
    public void MetadataAboveLimit_PacketBelowLimit_NeverAutoRepairs()
    {
        // Case D, both a straddle within tolerance and a gross divergence.
        foreach (var (metadataDelta, packetEnd) in new[] { (2.3, 1.9), (2.6, 1.0), (10.8, 1.5) })
        {
            var evidence = Evidence(0, 0, startOffset: 0, endOffset: packetEnd);
            var diagnosis = Classify(Video(0, 100), Audio(0, 100 + metadataDelta), evidence);
            Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
            Assert.Equal(AvRepairStrategy.ManualOnly, AvRepairPlanner.Plan(diagnosis, PlanningConfiguration()).Strategy);
        }
    }

    [Fact]
    public void PacketAboveLimit_MetadataBelowLimit_NeverAutoRepairs()
    {
        // Case E: never resolved in favor of the more favorable value.
        foreach (var (metadataDelta, packetEnd) in new[] { (1.9, 2.3), (1.0, 2.6), (0.6, 3.0) })
        {
            var evidence = Evidence(0, 0, startOffset: 0, endOffset: packetEnd);
            var diagnosis = Classify(Video(0, 100), Audio(0, 100 + metadataDelta), evidence);
            Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
            Assert.Equal(AvRepairStrategy.ManualOnly, AvRepairPlanner.Plan(diagnosis, PlanningConfiguration()).Strategy);
        }
    }

    [Fact]
    public void OppositeSignBetweenMetadataAndPackets_IsAmbiguous()
    {
        // Case F
        var evidence = Evidence(0, 0, startOffset: 0, endOffset: -1.0);
        var diagnosis = Classify(Video(0, 100), Audio(0, 101), evidence);
        Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
        Assert.Contains("opposite direction", diagnosis.Reason);
        Assert.Equal(AvRepairStrategy.ManualOnly, AvRepairPlanner.Plan(diagnosis, PlanningConfiguration()).Strategy);
    }

    [Fact]
    public void PacketDriftWithoutMetadataCorroboration_IsAmbiguous()
    {
        // Metadata says aligned (0.2s), packets say 0.6s: within tolerance, but
        // a sampled drift alone must not be enough to plan an end repair.
        var evidence = Evidence(0, 0, startOffset: 0, endOffset: 0.6);
        var diagnosis = Classify(Video(0, 100), Audio(0, 100.2), evidence);
        Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
    }

    [Fact]
    public void ConstantOffset_WhenMetadataShowsUnseenDurationDelta_IsNotConstantOffset()
    {
        var evidence = Evidence(0, 0, startOffset: 1.5, endOffset: 1.6);
        var diagnosis = Classify(Video(0, 100, videoStart: 0), Audio(0, 100.55, audioStart: 1.5), evidence);
        Assert.NotEqual(AvRepairClassification.ConstantOffset, diagnosis.Classification);
    }

    [Fact]
    public void StartOffsetDisagreement_IsAmbiguous()
    {
        var evidence = Evidence(0, 0, startOffset: 1.5, endOffset: 1.5);
        var diagnosis = Classify(Video(0, 100, videoStart: 0), Audio(0, 100, audioStart: 1.0), evidence);
        Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
    }

    [Theory]
    [InlineData(0.25, true)]
    [InlineData(0.26, false)]
    public void StartTolerance_BoundaryIsInclusive(double packetStart, bool consistent)
    {
        var diagnosis = new AvRepairDiagnosis
        {
            StartOffsetSeconds = 0,
            PacketEvidence = Evidence(0, 0, startOffset: packetStart, endOffset: packetStart)
        };
        Assert.Equal(consistent, AvEvidenceConsistency.Reconcile(diagnosis).IsConsistent);
    }

    [Theory]
    [InlineData(1.5, true)]
    [InlineData(1.51, false)]
    public void EndTolerance_BoundaryIsInclusive(double packetEnd, bool consistent)
    {
        var diagnosis = new AvRepairDiagnosis
        {
            DurationDeltaSeconds = 1.0,
            EndOffsetSeconds = 1.0,
            PacketEvidence = Evidence(0, 0, startOffset: 0, endOffset: packetEnd)
        };
        Assert.Equal(consistent, AvEvidenceConsistency.Reconcile(diagnosis).IsConsistent);
    }

    [Fact]
    public void ConsistencyTolerances_AreTheDocumentedSignalFloors()
    {
        Assert.Equal(AvRepairClassifier.OffsetSignalFloorSeconds, AvEvidenceConsistency.StartToleranceSeconds);
        Assert.Equal(AvRepairClassifier.DriftSignalFloorSeconds, AvEvidenceConsistency.EndToleranceSeconds);
    }

    [Fact]
    public void NoPacketEvidence_HasNoConflict()
    {
        var result = AvEvidenceConsistency.Reconcile(new AvRepairDiagnosis { DurationDeltaSeconds = 10.8 });
        Assert.True(result.IsConsistent);
        Assert.Equal(10.8, result.MetadataDriftSeconds);
    }

    [Fact]
    public void ConstantOffset_WhenMuxerReportsEndTimestampAsDuration_IsStillConstantOffset()
    {
        // Some muxers (e.g. ffmpeg Matroska DURATION tags) report the end
        // timestamp: audio starts at 1.5s and lasts 6s, tagged as 7.5s.
        var evidence = Evidence(0, 0, startOffset: 1.5, endOffset: 1.5);
        var diagnosis = Classify(Video(0, 6, videoStart: 0), Audio(0, 7.5, audioStart: 1.5), evidence);
        Assert.Equal(AvRepairClassification.ConstantOffset, diagnosis.Classification);
    }

    [Fact]
    public void ConstantOffset_WhenPacketsHideARealEndMismatchUnderBothDurationReadings_IsAmbiguous()
    {
        // Start offset 1.5s, audio tagged 3.0s longer than video: no reading of
        // the metadata is compatible with a packet-measured constant offset.
        var evidence = Evidence(0, 0, startOffset: 1.5, endOffset: 1.5);
        var diagnosis = Classify(Video(0, 6, videoStart: 0), Audio(0, 9, audioStart: 1.5), evidence);
        Assert.Equal(AvRepairClassification.Ambiguous, diagnosis.Classification);
    }

    [Fact]
    public void InSyncStart_HasSingleMetadataReading_SoPartialWindowCannotBeExplainedAway()
    {
        var diagnosis = new AvRepairDiagnosis
        {
            StartOffsetSeconds = 0,
            DurationDeltaSeconds = 10.8,
            EndOffsetSeconds = 10.8,
            PacketEvidence = Evidence(0, 0, startOffset: 0, endOffset: 1.897)
        };
        var result = AvEvidenceConsistency.Reconcile(diagnosis);
        Assert.False(result.IsConsistent);
        Assert.Equal(10.8, result.MetadataDriftSeconds);
    }

    private static PluginConfiguration PlanningConfiguration() =>
        new() { EnableAudioVideoRepair = true, AllowAudioReencode = true };

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
