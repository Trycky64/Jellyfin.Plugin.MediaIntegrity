using System.Globalization;
using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Reports potentially perceptible source audio/video timeline differences.
/// It is diagnostic-only and never makes media eligible for a repair.
/// </summary>
public static class AudioVideoTimelineAnalyzer
{
    // Conservative diagnostic thresholds. They are intentionally looser than
    // remux-validation tolerances to avoid codec delay and container rounding
    // false positives.
    public const double StartOffsetThresholdSeconds = 0.250;
    public const double DurationMismatchThresholdSeconds = 0.500;
    public const double EndMismatchThresholdSeconds = 0.500;
    public const double RelativeDurationThreshold = 0.0005;

    public static IReadOnlyList<MediaIssue> Analyze(MediaScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var issues = new List<MediaIssue>();
        var videos = scan.Streams.Where(TemporalVideoStreamPolicy.IsEligible);
        var audios = scan.Streams.Where(static stream =>
            string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase));

        foreach (var video in videos)
        {
            foreach (var audio in audios)
            {
                AnalyzePair(video, audio, issues);
            }
        }

        return issues;
    }

    private static void AnalyzePair(
        MediaStreamInfo video,
        MediaStreamInfo audio,
        ICollection<MediaIssue> issues)
    {
        if (video.StartTimeSeconds is null || audio.StartTimeSeconds is null
            || video.DurationSeconds is null || audio.DurationSeconds is null)
        {
            issues.Add(Create("TimelineDataIncomplete", video, audio,
                "Audio/video timeline data is incomplete; no desynchronization conclusion was made."));
            return;
        }

        var videoStart = video.StartTimeSeconds.Value;
        var audioStart = audio.StartTimeSeconds.Value;
        var videoDuration = video.DurationSeconds.Value;
        var audioDuration = audio.DurationSeconds.Value;
        if (!double.IsFinite(videoStart) || !double.IsFinite(audioStart)
            || !double.IsFinite(videoDuration) || !double.IsFinite(audioDuration)
            || videoDuration < 0 || audioDuration < 0)
        {
            issues.Add(Create("TimelineDataIncomplete", video, audio,
                "Audio/video timeline data is invalid; no desynchronization conclusion was made."));
            return;
        }

        var startDelta = audioStart - videoStart;
        var durationDelta = audioDuration - videoDuration;
        var endDelta = (audioStart + audioDuration) - (videoStart + videoDuration);
        var drift = endDelta - startDelta;
        if (!double.IsFinite(startDelta) || !double.IsFinite(durationDelta)
            || !double.IsFinite(endDelta) || !double.IsFinite(drift))
        {
            issues.Add(Create("TimelineDataIncomplete", video, audio,
                "Audio/video timeline arithmetic exceeded the usable range."));
            return;
        }
        double? ratio = videoDuration > 0 ? audioDuration / videoDuration : null;
        if (ratio is not null && !double.IsFinite(ratio.Value))
        {
            ratio = null;
        }
        var durationThreshold = Math.Max(DurationMismatchThresholdSeconds,
            Math.Max(videoDuration, audioDuration) * RelativeDurationThreshold);
        var endThreshold = Math.Max(EndMismatchThresholdSeconds,
            Math.Max(videoDuration, audioDuration) * RelativeDurationThreshold);

        if (Math.Abs(startDelta) > StartOffsetThresholdSeconds)
        {
            issues.Add(Create("AudioVideoStartOffset", video, audio,
                $"Audio/video start offset: video {video.Index} / audio {audio.Index} differ by {Format(startDelta)}s.",
                startDelta, durationDelta, endDelta, ratio));
        }

        if (Math.Abs(durationDelta) > durationThreshold)
        {
            issues.Add(Create("AudioVideoDurationMismatch", video, audio,
                $"Audio/video duration mismatch: video {video.Index} / audio {audio.Index} differ by {Format(durationDelta)}s.",
                startDelta, durationDelta, endDelta, ratio));
        }

        if (Math.Abs(endDelta) > endThreshold)
        {
            issues.Add(Create("AudioVideoEndMismatch", video, audio,
                $"Audio/video end mismatch: video {video.Index} / audio {audio.Index} differ by {Format(endDelta)}s.",
                startDelta, durationDelta, endDelta, ratio));
        }

        if (Math.Abs(drift) > durationThreshold)
        {
            issues.Add(Create("AudioVideoDriftSuspected", video, audio,
                $"Audio/video drift suspected: video {video.Index} / audio {audio.Index} offset changes by {Format(drift)}s.",
                startDelta, durationDelta, endDelta, ratio));
        }
    }

    private static MediaIssue Create(
        string code,
        MediaStreamInfo video,
        MediaStreamInfo audio,
        string message,
        double? startDelta = null,
        double? durationDelta = null,
        double? endDelta = null,
        double? ratio = null) => new()
        {
            Code = code,
            Message = message,
            Severity = code == "TimelineDataIncomplete" ? MediaIssueSeverity.Info : MediaIssueSeverity.Warning,
            VideoStreamIndex = video.Index,
            AudioStreamIndex = audio.Index,
            StartDeltaSeconds = startDelta,
            DurationDeltaSeconds = durationDelta,
            EndDeltaSeconds = endDelta,
            AudioVideoDurationRatio = ratio,
            AudioLanguage = audio.Language,
            AudioTitle = audio.Title,
            AudioIsDefault = audio.IsDefault,
            AudioIsForced = audio.IsForced,
            AudioIsCommentary = audio.IsCommentary,
            AudioIsDescription = audio.IsAudioDescription,
            VideoStartTimeSeconds = video.StartTimeSeconds,
            AudioStartTimeSeconds = audio.StartTimeSeconds,
            VideoDurationSeconds = video.DurationSeconds,
            AudioDurationSeconds = audio.DurationSeconds,
            OffsetEvolutionSeconds = endDelta is null || startDelta is null ? null : endDelta - startDelta
        };

    private static string Format(double value) =>
        value.ToString("+0.000000;-0.000000;0.000000", CultureInfo.InvariantCulture);
}
