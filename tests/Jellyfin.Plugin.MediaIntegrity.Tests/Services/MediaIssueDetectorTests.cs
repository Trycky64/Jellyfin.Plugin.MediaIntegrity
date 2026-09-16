using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class MediaIssueDetectorTests
{
    private readonly MediaIssueDetector _detector = new();

    public static TheoryData<
        string,
        string,
        MediaIssueSeverity,
        MediaIntegrityStatus> KnownPatterns =>
        new()
        {
            {
                "wrong sample count",
                "MP4_WRONG_SAMPLE_COUNT",
                MediaIssueSeverity.Repairable,
                MediaIntegrityStatus.RemuxRecommended
            },
            {
                "non-monotonous DTS",
                "NON_MONOTONOUS_DTS",
                MediaIssueSeverity.Repairable,
                MediaIntegrityStatus.RemuxRecommended
            },
            {
                "non monotonically increasing dts",
                "NON_MONOTONIC_DTS",
                MediaIssueSeverity.Repairable,
                MediaIntegrityStatus.RemuxRecommended
            },
            {
                "invalid edit list",
                "INVALID_EDIT_LIST",
                MediaIssueSeverity.Repairable,
                MediaIntegrityStatus.RemuxRecommended
            },
            {
                "missing picture in access unit",
                "MISSING_PICTURE_ACCESS_UNIT",
                MediaIssueSeverity.Warning,
                MediaIntegrityStatus.Warning
            },
            {
                "corrupt input packet",
                "CORRUPT_INPUT_PACKET",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Corrupted
            },
            {
                "invalid NAL unit",
                "INVALID_NAL_UNIT",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Corrupted
            },
            {
                "error reading header",
                "ERROR_READING_HEADER",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Corrupted
            },
            {
                "invalid data found when processing input",
                "INVALID_DATA",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Corrupted
            },
            {
                "truncated",
                "TRUNCATED_MEDIA",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Corrupted
            },
            {
                "moov atom not found",
                "MOOV_ATOM_NOT_FOUND",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Unreadable
            },
            {
                "permission denied",
                "PERMISSION_DENIED",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Unreadable
            },
            {
                "no such file or directory",
                "FILE_NOT_FOUND",
                MediaIssueSeverity.Critical,
                MediaIntegrityStatus.Unreadable
            }
        };

    [Theory]
    [MemberData(nameof(KnownPatterns))]
    public void Analyze_DetectsKnownPattern(
        string diagnostic,
        string expectedCode,
        MediaIssueSeverity expectedSeverity,
        MediaIntegrityStatus expectedStatus)
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(expectedCode, issue.Code);
        Assert.Equal(expectedSeverity, issue.Severity);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(diagnostic, issue.RawMessage);
    }

    [Theory]
    [InlineData("WRONG SAMPLE COUNT")]
    [InlineData("Wrong Sample Count")]
    [InlineData("wrong sample COUNT")]
    public void Analyze_IsCaseInsensitive(
        string diagnostic)
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "MP4_WRONG_SAMPLE_COUNT",
            issue.Code);

        Assert.Equal(
            MediaIntegrityStatus.RemuxRecommended,
            result.Status);
    }

    [Fact]
    public void Analyze_HealthyInput_IsOk()
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            string.Empty,
            exitCode: 0);

        Assert.Equal(
            MediaIntegrityStatus.Ok,
            result.Status);

        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(
        "Estimating duration from bitrate, this may be inaccurate")]
    [InlineData(
        "deprecated pixel format used, make sure you did set range correctly")]
    public void Analyze_BenignWarning_IsIgnored(
        string diagnostic)
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        Assert.Equal(
            MediaIntegrityStatus.Ok,
            result.Status);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Analyze_UnknownWarning_DoesNotCreateIssue()
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            "Some unknown ffmpeg warning that we do not classify.",
            exitCode: 0);

        Assert.Equal(
            MediaIntegrityStatus.Ok,
            result.Status);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Analyze_FailedProbeWithoutKnownIssue_CreatesGenericFailure()
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            "Unexpected ffprobe failure.",
            exitCode: 1);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "FFPROBE_FAILED",
            issue.Code);

        Assert.Equal(
            MediaIssueSeverity.Critical,
            issue.Severity);

        Assert.Equal(
            MediaIntegrityStatus.Unreadable,
            result.Status);

        Assert.Equal(
            "Unexpected ffprobe failure.",
            issue.RawMessage);
    }

    [Fact]
    public void Analyze_FailedProbeWithKnownIssue_UsesKnownIssue()
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            "moov atom not found",
            exitCode: 1);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "MOOV_ATOM_NOT_FOUND",
            issue.Code);

        Assert.Equal(
            MediaIntegrityStatus.Unreadable,
            result.Status);
    }

    [Fact]
    public void Analyze_MultipleIssues_UsesMostSevereStatus()
    {
        var result = CreateScanResult();

        var diagnostic = string.Join(
            Environment.NewLine,
            "wrong sample count",
            "corrupt input packet",
            "moov atom not found");

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        Assert.Equal(
            3,
            result.Issues.Count);

        Assert.Equal(
            MediaIntegrityStatus.Unreadable,
            result.Status);
    }

    [Fact]
    public void Analyze_DuplicatePattern_IsReportedOnlyOnce()
    {
        var result = CreateScanResult();

        var diagnostic = string.Join(
            Environment.NewLine,
            "wrong sample count",
            "wrong sample count",
            "wrong sample count");

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "MP4_WRONG_SAMPLE_COUNT",
            issue.Code);
    }

    [Fact]
    public void Analyze_PreservesOriginalDiagnosticLine()
    {
        var result = CreateScanResult();

        const string diagnostic =
            "[mov,mp4,m4a,3gp,3g2,mj2 @ 0x1234] wrong sample count";

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            diagnostic,
            issue.RawMessage);
    }

    [Fact]
    public void Analyze_ClearsPreviousIssuesBeforeNewAnalysis()
    {
        var result = CreateScanResult();

        result.Issues.Add(
            new MediaIssue
            {
                Code = "OLD",
                Message = "Old issue",
                Severity = MediaIssueSeverity.Warning,
                RawMessage = "old"
            });

        _detector.Analyze(
            result,
            string.Empty,
            exitCode: 0);

        Assert.Empty(result.Issues);

        Assert.Equal(
            MediaIntegrityStatus.Ok,
            result.Status);
    }

    [Fact]
    public void Analyze_BenignWarningDoesNotHideRealIssue()
    {
        var result = CreateScanResult();

        var diagnostic = string.Join(
            Environment.NewLine,
            "Estimating duration from bitrate, this may be inaccurate",
            "wrong sample count");

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "MP4_WRONG_SAMPLE_COUNT",
            issue.Code);

        Assert.Equal(
            MediaIntegrityStatus.RemuxRecommended,
            result.Status);
    }

    [Fact]
    public void Analyze_CodecParametersWarningWithUsableVideo_IsWarning()
    {
        var result = CreateScanResult();

        result.Streams.Add(
            new MediaStreamInfo
            {
                Index = 0,
                CodecType = "video",
                CodecName = "hevc"
            });

        const string diagnostic =
            "[matroska,webm @ 0x1234] "
            + "Could not find codec parameters for stream 2 "
            + "(Subtitle: hdmv_pgs_subtitle (pgssub)): unspecified size";

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "CODEC_PARAMETERS_NOT_FOUND",
            issue.Code);

        Assert.Equal(
            MediaIssueSeverity.Warning,
            issue.Severity);

        Assert.Equal(
            MediaIntegrityStatus.Warning,
            result.Status);

        Assert.Equal(
            diagnostic,
            issue.RawMessage);
    }

    [Fact]
    public void Analyze_CodecParametersWarningWithUsableAudio_IsWarning()
    {
        var result = CreateScanResult();

        result.Streams.Add(
            new MediaStreamInfo
            {
                Index = 0,
                CodecType = "audio",
                CodecName = "flac"
            });

        _detector.Analyze(
            result,
            "Could not find codec parameters for stream 1",
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "CODEC_PARAMETERS_NOT_FOUND",
            issue.Code);

        Assert.Equal(
            MediaIssueSeverity.Warning,
            issue.Severity);

        Assert.Equal(
            MediaIntegrityStatus.Warning,
            result.Status);
    }

    [Fact]
    public void Analyze_CodecParametersWithoutUsableStreams_IsUnreadable()
    {
        var result = CreateScanResult();

        _detector.Analyze(
            result,
            "Could not find codec parameters",
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "CODEC_PARAMETERS_NOT_FOUND",
            issue.Code);

        Assert.Equal(
            MediaIssueSeverity.Critical,
            issue.Severity);

        Assert.Equal(
            MediaIntegrityStatus.Unreadable,
            result.Status);
    }

    [Fact]
    public void Analyze_CodecParametersWithFailedProbe_IsUnreadable()
    {
        var result = CreateScanResult();

        result.Streams.Add(
            new MediaStreamInfo
            {
                Index = 0,
                CodecType = "video",
                CodecName = "hevc"
            });

        _detector.Analyze(
            result,
            "Could not find codec parameters",
            exitCode: 1);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "CODEC_PARAMETERS_NOT_FOUND",
            issue.Code);

        Assert.Equal(
            MediaIssueSeverity.Critical,
            issue.Severity);

        Assert.Equal(
            MediaIntegrityStatus.Unreadable,
            result.Status);
    }

    [Fact]
    public void Analyze_DoctorStrangePgsDiagnostic_IsWarning()
    {
        var result = CreateScanResult();

        result.Container = "matroska,webm";

        result.Streams.Add(
            new MediaStreamInfo
            {
                Index = 0,
                CodecType = "video",
                CodecName = "hevc"
            });

        result.Streams.Add(
            new MediaStreamInfo
            {
                Index = 1,
                CodecType = "audio",
                CodecName = "eac3"
            });

        result.Streams.Add(
            new MediaStreamInfo
            {
                Index = 2,
                CodecType = "subtitle",
                CodecName = "hdmv_pgs_subtitle"
            });

        var diagnostic = string.Join(
            Environment.NewLine,
            "[matroska,webm @ 0x7f91800000] "
            + "Could not find codec parameters for stream 2 "
            + "(Subtitle: hdmv_pgs_subtitle (pgssub)): unspecified size",
            "Consider increasing the value for the 'analyzeduration' (0) "
            + "and 'probesize' (5000000) options");

        _detector.Analyze(
            result,
            diagnostic,
            exitCode: 0);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(
            "CODEC_PARAMETERS_NOT_FOUND",
            issue.Code);

        Assert.Equal(
            MediaIssueSeverity.Warning,
            issue.Severity);

        Assert.Equal(
            MediaIntegrityStatus.Warning,
            result.Status);
    }

    private static MediaScanResult CreateScanResult()
    {
        return new MediaScanResult
        {
            Path = "/media/test.mp4",
            Extension = ".mp4",
            Container = "mov,mp4,m4a,3gp,3g2,mj2",
            Status = MediaIntegrityStatus.Ok
        };
    }
}
