using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class AvRepairPlannerTests
{
    [Fact]
    public void Disabled_AlwaysProducesManualOnly()
    {
        var configuration = new PluginConfiguration { EnableAudioVideoRepair = false };
        var diagnosis = ConstantOffsetDiagnosis(1.5, confidence: 0.95);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void ConstantOffset_WithinLimits_PlansTimestampShift()
    {
        var configuration = Enabled();
        var diagnosis = ConstantOffsetDiagnosis(1.5, confidence: 0.95);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.TimestampShift, plan.Strategy);
        Assert.Equal(-1.5, plan.TimestampShiftSeconds);
        Assert.True(plan.IsStreamCopySafe);
        Assert.False(plan.RequiresAudioReencode);
    }

    [Fact]
    public void ConstantOffset_NegativeOffset_ShiftsOppositeDirection()
    {
        var configuration = Enabled();
        var diagnosis = ConstantOffsetDiagnosis(-2.0, confidence: 0.95);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.TimestampShift, plan.Strategy);
        Assert.Equal(2.0, plan.TimestampShiftSeconds);
    }

    [Fact]
    public void ConstantOffset_ExceedingPolicyLimit_IsManualOnly()
    {
        var configuration = Enabled();
        configuration.MaxAutoRepairOffsetSeconds = 1.0;
        var diagnosis = ConstantOffsetDiagnosis(1.5, confidence: 0.95);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void LowConfidence_IsManualOnly()
    {
        var configuration = Enabled();
        var diagnosis = ConstantOffsetDiagnosis(1.5, confidence: 0.5);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void ProgressiveDrift_WithReencodeAllowed_PlansAudioTimeStretch()
    {
        var configuration = Enabled();
        configuration.AllowAudioReencode = true;
        var diagnosis = ProgressiveDriftDiagnosis(videoDuration: 5000, drift: 3.0, confidence: 0.9);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.AudioTimeStretch, plan.Strategy);
        Assert.True(plan.RequiresAudioReencode);
        Assert.False(plan.IsStreamCopySafe);
        Assert.NotNull(plan.AtempoFactor);
    }

    [Fact]
    public void ProgressiveDrift_WithoutReencodeAllowed_IsManualOnly()
    {
        var configuration = Enabled();
        configuration.AllowAudioReencode = false;
        var diagnosis = ProgressiveDriftDiagnosis(videoDuration: 5000, drift: 3.0, confidence: 0.9);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void ProgressiveDrift_ExceedingRatioLimit_IsManualOnly()
    {
        var configuration = Enabled();
        configuration.AllowAudioReencode = true;
        configuration.MaxAutoRepairDriftRatio = 0.0001;
        var diagnosis = ProgressiveDriftDiagnosis(videoDuration: 5000, drift: 3.0, confidence: 0.9);
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void GrossMismatch_AlwaysManualOnly()
    {
        var configuration = Enabled();
        configuration.AllowAudioReencode = true;
        var diagnosis = new AvRepairDiagnosis
        {
            Classification = AvRepairClassification.UnsafeToAutoRepair,
            Confidence = 1.0
        };
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void MissingAudio_AmbiguousIsManualOnly()
    {
        var configuration = Enabled();
        var diagnosis = new AvRepairDiagnosis { Classification = AvRepairClassification.Ambiguous, Confidence = 0.9 };
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void AudioEndsEarly_WithinBound_PlansAudioPad()
    {
        var configuration = Enabled();
        configuration.AllowAudioReencode = true;
        var diagnosis = new AvRepairDiagnosis
        {
            Classification = AvRepairClassification.AudioEndsEarly,
            Confidence = 0.9,
            DurationDeltaSeconds = -1.0
        };
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.AudioPad, plan.Strategy);
        Assert.Equal(1.0, plan.PadSeconds);
    }

    [Fact]
    public void AudioEndsLate_WithinBound_PlansAudioTrim()
    {
        var configuration = Enabled();
        configuration.AllowAudioReencode = true;
        var diagnosis = new AvRepairDiagnosis
        {
            Classification = AvRepairClassification.AudioEndsLate,
            Confidence = 0.9,
            DurationDeltaSeconds = 1.0
        };
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.AudioTrim, plan.Strategy);
        Assert.Equal(1.0, plan.TrimSeconds);
    }

    [Fact]
    public void AudioEndsEarly_ExceedingBound_IsManualOnly()
    {
        var configuration = Enabled();
        configuration.AllowAudioReencode = true;
        configuration.MaxAutoRepairDurationDeltaSeconds = 0.5;
        var diagnosis = new AvRepairDiagnosis
        {
            Classification = AvRepairClassification.AudioEndsEarly,
            Confidence = 0.9,
            DurationDeltaSeconds = -1.0
        };
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void AudioEndsEarly_WithoutReencodeAllowed_IsManualOnly()
    {
        var configuration = Enabled();
        var diagnosis = new AvRepairDiagnosis
        {
            Classification = AvRepairClassification.AudioEndsEarly,
            Confidence = 0.9,
            DurationDeltaSeconds = -1.0
        };
        var plan = AvRepairPlanner.Plan(diagnosis, configuration);
        Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy);
    }

    [Fact]
    public void PlanMedia_ConflictingPlansForSameAudioStreamBecomeManualOnly()
    {
        var configuration = Enabled();
        var first = ConstantOffsetDiagnosis(1.5, confidence: 0.95);
        first.AudioStreamIndex = 1;
        first.VideoStreamIndex = 0;
        var second = ConstantOffsetDiagnosis(-1.5, confidence: 0.95);
        second.AudioStreamIndex = 1;
        second.VideoStreamIndex = 2;

        var plans = AvRepairPlanner.PlanMedia([first, second], configuration);

        Assert.All(plans, plan => Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy));
    }

    [Fact]
    public void PlanMedia_MultipleEligibleTracksOnOneFile_AllBecomeManualOnly()
    {
        // End-to-end validation found that chaining a stream-copy retime with a
        // following per-stream re-encode can introduce a small collateral
        // timestamp shift on untouched streams (including video); automatic
        // multi-track chained execution is withheld until that is resolved.
        var configuration = Enabled();
        var track1 = ConstantOffsetDiagnosis(1.5, confidence: 0.95);
        track1.AudioStreamIndex = 1;
        var track2 = ConstantOffsetDiagnosis(-1.5, confidence: 0.95);
        track2.AudioStreamIndex = 2;

        var plans = AvRepairPlanner.PlanMedia([track1, track2], configuration);

        Assert.All(plans, plan => Assert.Equal(AvRepairStrategy.ManualOnly, plan.Strategy));
    }

    [Fact]
    public void PlanMedia_SingleEligibleTrackAmongManualOnlyDiagnoses_StillAutoRepairs()
    {
        var configuration = Enabled();
        var eligible = ConstantOffsetDiagnosis(1.5, confidence: 0.95);
        eligible.AudioStreamIndex = 1;
        var manualOnly = new AvRepairDiagnosis
        {
            Classification = AvRepairClassification.Ambiguous,
            Confidence = 0.9,
            AudioStreamIndex = 2
        };

        var plans = AvRepairPlanner.PlanMedia([eligible, manualOnly], configuration);

        Assert.Equal(AvRepairStrategy.TimestampShift, plans.Single(p => p.AudioStreamIndex == 1).Strategy);
        Assert.Equal(AvRepairStrategy.ManualOnly, plans.Single(p => p.AudioStreamIndex == 2).Strategy);
    }

    private static PluginConfiguration Enabled() => new() { EnableAudioVideoRepair = true };

    private static AvRepairDiagnosis ConstantOffsetDiagnosis(double offset, double confidence) => new()
    {
        Classification = AvRepairClassification.ConstantOffset,
        Confidence = confidence,
        StartOffsetSeconds = offset,
        PacketEvidence = new AvPacketEvidence
        {
            VideoStreamIndex = 0,
            AudioStreamIndex = 0,
            StartOffsetSeconds = offset,
            EndOffsetSeconds = offset
        }
    };

    private static AvRepairDiagnosis ProgressiveDriftDiagnosis(double videoDuration, double drift, double confidence) => new()
    {
        Classification = AvRepairClassification.ProgressiveDrift,
        Confidence = confidence,
        VideoDurationSeconds = videoDuration,
        AudioDurationSeconds = videoDuration + drift,
        DurationDeltaSeconds = drift,
        DurationRatio = (videoDuration + drift) / videoDuration,
        PacketEvidence = new AvPacketEvidence
        {
            VideoStreamIndex = 0,
            AudioStreamIndex = 0,
            StartOffsetSeconds = 0,
            EndOffsetSeconds = drift
        }
    };
}
