using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class TemporalVideoStreamPolicyTests
{
    [Theory]
    [InlineData("h264", 7223, false, true)]
    [InlineData("mjpeg", 7223, false, true)]
    [InlineData("mjpeg", 0, false, false)]
    [InlineData("h264", 0, false, false)]
    [InlineData("mjpeg", 7223, true, false)]
    public void Eligibility_DoesNotDependOnCodecAndRequiresPositiveDuration(
        string codec, double duration, bool attachedPicture, bool expected)
    {
        var stream = new MediaStreamInfo
        {
            CodecType = "video",
            CodecName = codec,
            DurationSeconds = duration,
            IsAttachedPicture = attachedPicture
        };
        Assert.Equal(expected, TemporalVideoStreamPolicy.IsEligible(stream));
    }

    [Fact]
    public void MissingDuration_IsNotATemporalVideoTimeline()
    {
        Assert.False(TemporalVideoStreamPolicy.IsEligible(new MediaStreamInfo
        {
            CodecType = "video",
            CodecName = "h264",
            DurationSeconds = null
        }));
    }
}
