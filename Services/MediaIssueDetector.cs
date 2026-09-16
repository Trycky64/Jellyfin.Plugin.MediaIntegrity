using Jellyfin.Plugin.MediaIntegrity.Models;

namespace Jellyfin.Plugin.MediaIntegrity.Services;

/// <summary>
/// Detects known integrity issues from ffprobe/ffmpeg diagnostic output.
/// </summary>
public sealed class MediaIssueDetector
{
    private const string CodecParametersIssueCode =
        "CODEC_PARAMETERS_NOT_FOUND";

    private static readonly string[] BenignWarningPatterns =
    [
        "estimating duration from bitrate, this may be inaccurate",
        "deprecated pixel format used, make sure you did set range correctly"
    ];

    private static readonly IssuePattern[] Patterns =
    [
        new(
            "MP4_WRONG_SAMPLE_COUNT",
            "wrong sample count",
            "Invalid or inconsistent MP4 sample table detected.",
            MediaIssueSeverity.Repairable,
            MediaIntegrityStatus.RemuxRecommended),

        new(
            "NON_MONOTONOUS_DTS",
            "non-monotonous dts",
            "Non-monotonous DTS timestamps detected.",
            MediaIssueSeverity.Repairable,
            MediaIntegrityStatus.RemuxRecommended),

        new(
            "NON_MONOTONIC_DTS",
            "non monotonically increasing dts",
            "Non-monotonically increasing DTS timestamps detected.",
            MediaIssueSeverity.Repairable,
            MediaIntegrityStatus.RemuxRecommended),

        new(
            "INVALID_EDIT_LIST",
            "invalid edit list",
            "Invalid edit list detected.",
            MediaIssueSeverity.Repairable,
            MediaIntegrityStatus.RemuxRecommended),

        new(
            "MISSING_PICTURE_ACCESS_UNIT",
            "missing picture in access unit",
            "Missing picture in access unit.",
            MediaIssueSeverity.Warning,
            MediaIntegrityStatus.Warning),

        new(
            "CORRUPT_INPUT_PACKET",
            "corrupt input packet",
            "Corrupt input packet detected.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Corrupted),

        new(
            "INVALID_NAL_UNIT",
            "invalid nal unit",
            "Invalid NAL unit detected.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Corrupted),

        new(
            "ERROR_READING_HEADER",
            "error reading header",
            "Unable to correctly read media header.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Corrupted),

        new(
            "INVALID_DATA",
            "invalid data found when processing input",
            "Invalid media data detected.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Corrupted),

        new(
            "TRUNCATED_MEDIA",
            "truncated",
            "Media appears to be truncated.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Corrupted),

        new(
            "MOOV_ATOM_NOT_FOUND",
            "moov atom not found",
            "MP4 metadata atom could not be found.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Unreadable),

        new(
            CodecParametersIssueCode,
            "could not find codec parameters",
            "Codec parameters could not be fully determined.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Unreadable),

        new(
            "PERMISSION_DENIED",
            "permission denied",
            "Media file could not be accessed due to filesystem permissions.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Unreadable),

        new(
            "FILE_NOT_FOUND",
            "no such file or directory",
            "Media file no longer exists.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Unreadable)
    ];

    /// <summary>
    /// Detects known issues and updates the scan result.
    /// </summary>
    public void Analyze(
        MediaScanResult scanResult,
        string standardError,
        int exitCode)
    {
        ArgumentNullException.ThrowIfNull(scanResult);

        scanResult.Issues.Clear();

        var detectedStatus = MediaIntegrityStatus.Ok;

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            foreach (var line in EnumerateLines(standardError))
            {
                if (IsBenignWarning(line))
                {
                    continue;
                }

                foreach (var pattern in Patterns)
                {
                    if (!line.Contains(
                            pattern.SearchText,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (scanResult.Issues.Any(
                            issue => string.Equals(
                                issue.Code,
                                pattern.Code,
                                StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var classification = ResolveClassification(
                        pattern,
                        scanResult,
                        exitCode);

                    scanResult.Issues.Add(
                        new MediaIssue
                        {
                            Code = pattern.Code,
                            Message = classification.Message,
                            Severity = classification.Severity,
                            RawMessage = line.Trim()
                        });

                    detectedStatus = GetMostSevereStatus(
                        detectedStatus,
                        classification.Status);
                }
            }
        }

        if (exitCode != 0)
        {
            detectedStatus = GetMostSevereStatus(
                detectedStatus,
                MediaIntegrityStatus.Unreadable);

            if (scanResult.Issues.Count == 0)
            {
                scanResult.Issues.Add(
                    new MediaIssue
                    {
                        Code = "FFPROBE_FAILED",
                        Message = "ffprobe failed to analyze the media file.",
                        Severity = MediaIssueSeverity.Critical,
                        RawMessage = standardError.Trim()
                    });
            }
        }

        scanResult.Status = detectedStatus;
    }

    private static IssueClassification ResolveClassification(
        IssuePattern pattern,
        MediaScanResult scanResult,
        int exitCode)
    {
        if (!string.Equals(
                pattern.Code,
                CodecParametersIssueCode,
                StringComparison.Ordinal))
        {
            return new IssueClassification(
                pattern.Message,
                pattern.Severity,
                pattern.Status);
        }

        if (exitCode == 0 && HasUsableMediaStream(scanResult))
        {
            return new IssueClassification(
                "Some stream parameters could not be fully determined, "
                + "but the media contains usable audio or video streams.",
                MediaIssueSeverity.Warning,
                MediaIntegrityStatus.Warning);
        }

        return new IssueClassification(
            "Codec parameters could not be determined sufficiently "
            + "to consider the media readable.",
            MediaIssueSeverity.Critical,
            MediaIntegrityStatus.Unreadable);
    }

    private static bool HasUsableMediaStream(
        MediaScanResult scanResult)
    {
        return scanResult.Streams.Any(
            static stream =>
                (string.Equals(
                     stream.CodecType,
                     "video",
                     StringComparison.OrdinalIgnoreCase)
                 || string.Equals(
                     stream.CodecType,
                     "audio",
                     StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(stream.CodecName));
    }

    private static bool IsBenignWarning(
        string line)
    {
        foreach (var pattern in BenignWarningPatterns)
        {
            if (line.Contains(
                    pattern,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateLines(
        string value)
    {
        using var reader = new StringReader(value);

        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static MediaIntegrityStatus GetMostSevereStatus(
        MediaIntegrityStatus current,
        MediaIntegrityStatus candidate)
    {
        return GetRank(candidate) > GetRank(current)
            ? candidate
            : current;
    }

    private static int GetRank(
        MediaIntegrityStatus status)
    {
        return status switch
        {
            MediaIntegrityStatus.Ok => 0,
            MediaIntegrityStatus.Warning => 1,
            MediaIntegrityStatus.RemuxRecommended => 2,
            MediaIntegrityStatus.Corrupted => 3,
            MediaIntegrityStatus.Unreadable => 4,
            _ => 0
        };
    }

    private sealed record IssuePattern(
        string Code,
        string SearchText,
        string Message,
        MediaIssueSeverity Severity,
        MediaIntegrityStatus Status);

    private sealed record IssueClassification(
        string Message,
        MediaIssueSeverity Severity,
        MediaIntegrityStatus Status);
}
