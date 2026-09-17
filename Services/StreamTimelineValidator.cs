using System.Globalization;
using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Compares source and candidate stream clocks before a candidate can replace
/// media. Unknown audio/video timing fails closed because it cannot prove that
/// the remux preserved the timeline.
/// </summary>
public static class StreamTimelineValidator
{
    public static IReadOnlyList<TimelineValidationIssue> Validate(
        MediaScanResult source,
        MediaScanResult candidate,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<TimelineValidationIssue>();
        var sourceByIndex = source.Streams
            .GroupBy(static stream => stream.Index)
            .ToDictionary(static group => group.Key, static group => group.First());
        var candidateByIndex = candidate.Streams
            .GroupBy(static stream => stream.Index)
            .ToDictionary(static group => group.Key, static group => group.First());

        foreach (var sourceStream in source.Streams)
        {
            if (!candidateByIndex.TryGetValue(sourceStream.Index, out var candidateStream))
            {
                issues.Add(Create(
                    "StreamMissing",
                    $"Stream timeline validation failed: stream={sourceStream.Index} "
                    + $"type={sourceStream.CodecType} is missing from candidate."));
                continue;
            }

            ValidateStream(sourceStream, candidateStream, configuration, issues);
        }

        foreach (var candidateStream in candidate.Streams)
        {
            if (!sourceByIndex.ContainsKey(candidateStream.Index))
            {
                issues.Add(Create(
                    "StreamUnexpected",
                    $"Stream timeline validation failed: stream={candidateStream.Index} "
                    + $"type={candidateStream.CodecType} is unexpected in candidate."));
            }
        }

        AddDuplicateIndexIssues(source.Streams, "source", issues);
        AddDuplicateIndexIssues(candidate.Streams, "candidate", issues);

        ValidateAudioVideoRelations(source, candidate, configuration, issues);
        return issues;
    }

    private static void ValidateStream(
        MediaStreamInfo source,
        MediaStreamInfo candidate,
        PluginConfiguration configuration,
        ICollection<TimelineValidationIssue> issues)
    {
        if (!string.Equals(
                source.CodecType,
                candidate.CodecType,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                source.CodecName,
                candidate.CodecName,
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Create(
                "StreamCodecChanged",
                $"Stream timeline validation failed: stream={source.Index} "
                + $"sourceType={source.CodecType} sourceCodec={source.CodecName} "
                + $"repairedType={candidate.CodecType} repairedCodec={candidate.CodecName}."));
        }

        if (!string.Equals(
                source.TimeBase,
                candidate.TimeBase,
                StringComparison.Ordinal))
        {
            issues.Add(Create(
                "StreamTimeBaseChanged",
                $"Stream timeline validation failed: stream={source.Index} "
                + $"type={source.CodecType} sourceTimeBase={source.TimeBase} "
                + $"repairedTimeBase={candidate.TimeBase}."));
        }

        if (!IsTimedMediaStream(source.CodecType))
        {
            return;
        }

        if (source.DurationSeconds is null || candidate.DurationSeconds is null)
        {
            issues.Add(Create(
                "StreamDurationUnknown",
                $"Stream timeline validation failed: stream={source.Index} "
                + $"type={source.CodecType} has an unknown duration; "
                + "timeline preservation cannot be verified."));
        }
        else
        {
            var delta = candidate.DurationSeconds.Value - source.DurationSeconds.Value;
            if (Math.Abs(delta) > configuration.StreamDurationToleranceSeconds)
            {
                issues.Add(Create(
                    "StreamDurationChanged",
                    $"Stream timeline validation failed: stream={source.Index} "
                    + $"type={source.CodecType} "
                    + $"sourceDuration={Format(source.DurationSeconds.Value)} "
                    + $"repairedDuration={Format(candidate.DurationSeconds.Value)} "
                    + $"delta={FormatSigned(delta)}."));
            }
        }

        if (source.StartTimeSeconds is null || candidate.StartTimeSeconds is null)
        {
            issues.Add(Create(
                "StreamStartTimeUnknown",
                $"Stream timeline validation failed: stream={source.Index} "
                + $"type={source.CodecType} has an unknown start time; "
                + "timeline preservation cannot be verified."));
        }
        else
        {
            var delta = candidate.StartTimeSeconds.Value - source.StartTimeSeconds.Value;
            if (Math.Abs(delta) > configuration.StreamStartTimeToleranceSeconds)
            {
                issues.Add(Create(
                    "StreamStartTimeChanged",
                    $"Stream timeline validation failed: stream={source.Index} "
                    + $"type={source.CodecType} "
                    + $"sourceStartTime={Format(source.StartTimeSeconds.Value)} "
                    + $"repairedStartTime={Format(candidate.StartTimeSeconds.Value)} "
                    + $"delta={FormatSigned(delta)}."));
            }
        }
    }

    private static void ValidateAudioVideoRelations(
        MediaScanResult source,
        MediaScanResult candidate,
        PluginConfiguration configuration,
        ICollection<TimelineValidationIssue> issues)
    {
        var sourceVideo = source.Streams.Where(static stream => IsType(stream, "video"));
        var sourceAudio = source.Streams.Where(static stream => IsType(stream, "audio"));
        var candidateByIndex = candidate.Streams
            .GroupBy(static stream => stream.Index)
            .ToDictionary(static group => group.Key, static group => group.First());

        foreach (var video in sourceVideo)
        {
            foreach (var audio in sourceAudio)
            {
                if (video.DurationSeconds is null || audio.DurationSeconds is null
                    || !candidateByIndex.TryGetValue(video.Index, out var candidateVideo)
                    || !candidateByIndex.TryGetValue(audio.Index, out var candidateAudio)
                    || candidateVideo.DurationSeconds is null || candidateAudio.DurationSeconds is null)
                {
                    continue;
                }

                var sourceDelta = video.DurationSeconds.Value - audio.DurationSeconds.Value;
                var candidateDelta = candidateVideo.DurationSeconds.Value - candidateAudio.DurationSeconds.Value;
                var introducedDrift = candidateDelta - sourceDelta;
                if (Math.Abs(introducedDrift) > configuration.StreamDurationToleranceSeconds)
                {
                    issues.Add(Create(
                        "AudioVideoDriftIntroduced",
                        $"Audio/video timeline drift introduced: videoStream={video.Index} "
                        + $"audioStream={audio.Index} "
                        + $"sourceAvDurationDelta={FormatSigned(sourceDelta)} "
                        + $"repairedAvDurationDelta={FormatSigned(candidateDelta)} "
                        + $"introducedAvDrift={FormatSigned(introducedDrift)}."));
                }
            }
        }
    }

    private static bool IsTimedMediaStream(string codecType) =>
        IsType(codecType, "video") || IsType(codecType, "audio");

    private static bool IsType(MediaStreamInfo stream, string type) =>
        IsType(stream.CodecType, type);

    private static bool IsType(string value, string type) =>
        string.Equals(value, type, StringComparison.OrdinalIgnoreCase);

    private static void AddDuplicateIndexIssues(
        IEnumerable<MediaStreamInfo> streams,
        string side,
        ICollection<TimelineValidationIssue> issues)
    {
        foreach (var duplicate in streams.GroupBy(static stream => stream.Index).Where(static group => group.Count() > 1))
        {
            issues.Add(Create(
                "StreamIndexDuplicate",
                $"Stream timeline validation failed: stream={duplicate.Key} occurs more than once in {side}."));
        }
    }

    private static TimelineValidationIssue Create(string code, string message) =>
        new() { Code = code, Message = message };

    private static string Format(double value) =>
        value.ToString("F6", CultureInfo.InvariantCulture);

    private static string FormatSigned(double value) =>
        value.ToString("+0.000000;-0.000000;0.000000", CultureInfo.InvariantCulture);
}


