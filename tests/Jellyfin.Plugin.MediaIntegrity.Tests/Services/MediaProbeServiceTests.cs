using System.Reflection;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class MediaProbeServiceTests
{
    [Fact]
    public void ProbeOutput_CapturesTimelineDescriptorsWithoutLosingPrecision()
    {
        const string output = """
            {
              "streams": [
                {
                  "index": 0,
                  "codec_type": "video",
                  "codec_name": "h264",
                  "time_base": "1/90000",
                  "start_time": "0.083411",
                  "duration": "1427.000000"
                },
                {
                  "index": 1,
                  "codec_type": "audio",
                  "codec_name": "aac",
                  "time_base": "1/32000",
                  "start_time": "0.000000",
                  "duration": "1427.000000"
                }
              ]
            }
            """;

        var result = Parse(output);

        Assert.Collection(
            result.Streams,
            stream =>
            {
                Assert.Equal(0, stream.Index);
                Assert.Equal("video", stream.CodecType);
                Assert.Equal("h264", stream.CodecName);
                Assert.Equal("1/90000", stream.TimeBase);
                Assert.Equal(0.083411, stream.StartTimeSeconds);
                Assert.Equal(1427.000000, stream.DurationSeconds);
            },
            stream =>
            {
                Assert.Equal(1, stream.Index);
                Assert.Equal("audio", stream.CodecType);
                Assert.Equal("aac", stream.CodecName);
                Assert.Equal("1/32000", stream.TimeBase);
                Assert.Equal(0, stream.StartTimeSeconds);
                Assert.Equal(1427.000000, stream.DurationSeconds);
            });
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    public void ProbeOutput_UnknownTimelineValuesRemainNull(string value)
    {
        var result = Parse(
            "{ \"streams\": [{ \"index\": 0, \"codec_type\": \"audio\", "
            + "\"codec_name\": \"aac\", \"start_time\": \""
            + value
            + "\", \"duration\": \""
            + value
            + "\" }] }");

        var stream = Assert.Single(result.Streams);
        Assert.Null(stream.StartTimeSeconds);
        Assert.Null(stream.DurationSeconds);
    }

    [Fact]
    public void ProbeOutput_UsesMatroskaStreamDurationTagWithoutUsingContainerDuration()
    {
        var result = Parse(
            """
            {
              "format": { "duration": "99.000000" },
              "streams": [{
                "index": 0,
                "codec_type": "video",
                "codec_name": "h264",
                "duration": "N/A",
                "tags": { "DURATION": "00:00:02.021000000" }
              }]
            }
            """);

        Assert.Equal(99, result.DurationSeconds);
        Assert.Equal(2.021, Assert.Single(result.Streams).DurationSeconds);
    }

    private static MediaScanResult Parse(string output)
    {
        var method = typeof(MediaProbeService).GetMethod(
            "ParseProbeOutput",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        return (MediaScanResult)method.Invoke(
            null,
            ["/media/test.mp4", output, 0])!;
    }
}


