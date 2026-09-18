using System.Globalization;
using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Turns an <see cref="AvRepairDiagnosis"/> into a deterministic
/// <see cref="AvRepairPlan"/>, applying every configured safety policy. This
/// class never performs I/O or runs ffmpeg; it only decides what should be
/// attempted, and rejects (ManualOnly) whenever a policy limit is exceeded.
/// </summary>
public static class AvRepairPlanner
{
    /// <summary>
    /// Maximum allowed deviation of an atempo factor from 1.0. A real clock
    /// or resampling drift on ordinary media is a tiny fraction of this; a
    /// factor further from 1.0 indicates the classification is unreliable,
    /// not that a larger correction is safe to automate.
    /// </summary>
    public const double MaxAtempoFactorDeviation = 0.10;

    public static AvRepairPlan Plan(AvRepairDiagnosis diagnosis, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        ArgumentNullException.ThrowIfNull(configuration);

        var plan = new AvRepairPlan
        {
            VideoStreamIndex = diagnosis.VideoStreamIndex,
            AudioStreamIndex = diagnosis.AudioStreamIndex,
            Classification = diagnosis.Classification,
            Confidence = diagnosis.Confidence
        };

        if (!configuration.EnableAudioVideoRepair)
        {
            return ManualOnly(plan, "Audio/video repair is disabled (EnableAudioVideoRepair=false).");
        }

        if (diagnosis.Confidence < configuration.MinRepairConfidence)
        {
            return ManualOnly(plan,
                $"Classification confidence {Format(diagnosis.Confidence)} is below the configured minimum " +
                $"{Format(configuration.MinRepairConfidence)}.");
        }

        return diagnosis.Classification switch
        {
            AvRepairClassification.None => ManualOnly(plan, "No anomaly was classified."),

            AvRepairClassification.ConstantOffset => PlanConstantOffset(plan, diagnosis, configuration),

            AvRepairClassification.ProgressiveDrift => PlanProgressiveDrift(plan, diagnosis, configuration),

            AvRepairClassification.AudioEndsEarly => PlanBoundedEndMismatch(
                plan, diagnosis, configuration, isEarly: true),

            AvRepairClassification.AudioEndsLate => PlanBoundedEndMismatch(
                plan, diagnosis, configuration, isEarly: false),

            AvRepairClassification.DurationMismatch => ManualOnly(plan,
                "Duration mismatch magnitude or evidence quality requires manual review."),

            AvRepairClassification.TimelineMetadataOnly => ManualOnly(plan,
                "No independent packet evidence confirms this anomaly; metadata alone is not sufficient " +
                "for an automatic repair."),

            AvRepairClassification.Ambiguous => ManualOnly(plan,
                "Evidence is ambiguous or conflicting; requires manual review."),

            AvRepairClassification.UnsafeToAutoRepair => ManualOnly(plan,
                "Classified unsafe to auto-repair (magnitude, discontinuity, or explicit policy)."),

            _ => ManualOnly(plan, "Unrecognized classification.")
        };
    }

    /// <summary>
    /// Plans every diagnosis for one media file, then rejects any plan that
    /// conflicts with another plan for the same audio stream (for example
    /// when a stream is paired with more than one eligible video stream and
    /// each pairing implies a different repair).
    ///
    /// A file whose independent tracks each need a *different* automatic
    /// repair also falls back to ManualOnly for every track, even without a
    /// direct conflict: executing more than one plan for the same file
    /// requires chaining sequential FFmpeg passes, and end-to-end validation
    /// found that chaining a stream-copy retime with a following per-stream
    /// re-encode can introduce a small collateral timestamp shift on
    /// completely untouched streams (including video) that this release
    /// cannot yet prove safe. Each track is still classified and its
    /// individual plan is still computed and visible in statistics; only
    /// unattended multi-track chained execution is withheld.
    /// </summary>
    public static IReadOnlyList<AvRepairPlan> PlanMedia(
        IReadOnlyList<AvRepairDiagnosis> diagnoses, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(diagnoses);
        ArgumentNullException.ThrowIfNull(configuration);

        var plans = diagnoses.Select(diagnosis => Plan(diagnosis, configuration)).ToList();

        if (plans.Count(static plan => plan.IsAutoRepairEligible) > 1)
        {
            foreach (var plan in plans.Where(static plan => plan.IsAutoRepairEligible))
            {
                ManualOnly(plan,
                    "Multiple tracks on this file each need an automatic repair; chained multi-track " +
                    "execution is not yet auto-repaired in this release. Each track remains individually " +
                    "classified for manual review.");
            }
        }

        return plans;
    }

    private static AvRepairPlan PlanConstantOffset(
        AvRepairPlan plan, AvRepairDiagnosis diagnosis, PluginConfiguration configuration)
    {
        var offset = diagnosis.PacketEvidence?.StartOffsetSeconds ?? diagnosis.StartOffsetSeconds;

        if (Math.Abs(offset) > configuration.MaxAutoRepairOffsetSeconds)
        {
            return ManualOnly(plan,
                $"Offset {Format(offset)}s exceeds the configured MaxAutoRepairOffsetSeconds " +
                $"({Format(configuration.MaxAutoRepairOffsetSeconds)}s).");
        }

        plan.Strategy = AvRepairStrategy.TimestampShift;
        plan.TimestampShiftSeconds = -offset;
        plan.IsStreamCopySafe = true;
        plan.RequiresAudioReencode = false;
        plan.Reason =
            $"Constant {Format(offset)}s offset; shifting the audio track's timestamps by " +
            $"{Format(-offset)}s with stream copy only.";
        return plan;
    }

    private static AvRepairPlan PlanProgressiveDrift(
        AvRepairPlan plan, AvRepairDiagnosis diagnosis, PluginConfiguration configuration)
    {
        if (!configuration.AllowAudioReencode)
        {
            return ManualOnly(plan,
                "Progressive drift requires an audio re-encode (AudioTimeStretch), " +
                "and AllowAudioReencode=false.");
        }

        if (diagnosis.VideoDurationSeconds <= 0)
        {
            return ManualOnly(plan, "Video duration is unknown or non-positive; cannot compute a safe atempo factor.");
        }

        var drift = diagnosis.PacketEvidence?.Drift ?? diagnosis.DurationDeltaSeconds;
        var driftRatio = Math.Abs(drift) / diagnosis.VideoDurationSeconds;

        if (driftRatio > configuration.MaxAutoRepairDriftRatio)
        {
            return ManualOnly(plan,
                $"Drift ratio {Format(driftRatio)} exceeds the configured MaxAutoRepairDriftRatio " +
                $"({Format(configuration.MaxAutoRepairDriftRatio)}).");
        }

        var factor = diagnosis.DurationRatio ?? (diagnosis.VideoDurationSeconds + drift) / diagnosis.VideoDurationSeconds;

        if (!double.IsFinite(factor) || Math.Abs(factor - 1.0) > MaxAtempoFactorDeviation)
        {
            return ManualOnly(plan,
                $"Computed atempo factor {Format(factor)} deviates from 1.0 by more than the safe limit " +
                $"({Format(MaxAtempoFactorDeviation)}); refusing an automatic correction.");
        }

        plan.Strategy = AvRepairStrategy.AudioTimeStretch;
        plan.AtempoFactor = factor;
        plan.IsStreamCopySafe = false;
        plan.RequiresAudioReencode = true;
        plan.Reason =
            $"Progressive drift of {Format(drift)}s across the file; re-encoding the audio track with " +
            $"atempo={Format(factor)} while stream-copying video.";
        return plan;
    }

    private static AvRepairPlan PlanBoundedEndMismatch(
        AvRepairPlan plan, AvRepairDiagnosis diagnosis, PluginConfiguration configuration, bool isEarly)
    {
        // apad/atrim are filter-graph operations: the target audio stream is
        // decoded and re-encoded even though the edit itself is a clean,
        // silence-only pad or an edge trim. AllowAudioReencode therefore
        // gates this strategy too, even though video is always stream-copied.
        if (!configuration.AllowAudioReencode)
        {
            return ManualOnly(plan,
                $"{(isEarly ? "Padding" : "Trimming")} the audio track requires a bounded audio re-encode, " +
                "and AllowAudioReencode=false.");
        }

        var magnitude = Math.Abs(diagnosis.PacketEvidence?.Drift ?? diagnosis.DurationDeltaSeconds);

        if (magnitude > configuration.MaxAutoRepairDurationDeltaSeconds)
        {
            return ManualOnly(plan,
                $"Magnitude {Format(magnitude)}s exceeds the configured MaxAutoRepairDurationDeltaSeconds " +
                $"({Format(configuration.MaxAutoRepairDurationDeltaSeconds)}s).");
        }

        // AudioPad/AudioTrim must preserve the source audio codec exactly (they
        // are meant to be minimal edits, not transcodes). If FFmpeg has no known
        // safe encoder matching the source codec, refuse rather than silently
        // switching codec (which the existing stream-identity validation would
        // reject anyway, after an expensive wasted FFmpeg pass).
        if (!AvRepairExecutionService.CanPreserveCodec(diagnosis.AudioCodecName))
        {
            return ManualOnly(plan,
                $"No safe codec-preserving encoder is known for source audio codec " +
                $"'{diagnosis.AudioCodecName}'; refusing to transcode to a different codec.");
        }

        plan.IsStreamCopySafe = false;
        plan.RequiresAudioReencode = true;

        if (isEarly)
        {
            plan.Strategy = AvRepairStrategy.AudioPad;
            plan.PadSeconds = magnitude;
            plan.Reason = $"Audio ends {Format(magnitude)}s early while in sync at the start; " +
                "padding the end of the audio track with silence.";
        }
        else
        {
            plan.Strategy = AvRepairStrategy.AudioTrim;
            plan.TrimSeconds = magnitude;
            plan.Reason = $"Audio runs {Format(magnitude)}s longer while in sync at the start; " +
                "trimming the excess from the end of the audio track.";
        }

        return plan;
    }

    private static AvRepairPlan ManualOnly(AvRepairPlan plan, string reason)
    {
        plan.Strategy = AvRepairStrategy.ManualOnly;
        plan.RequiresAudioReencode = false;
        plan.IsStreamCopySafe = false;
        plan.Reason = reason;
        return plan;
    }

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
