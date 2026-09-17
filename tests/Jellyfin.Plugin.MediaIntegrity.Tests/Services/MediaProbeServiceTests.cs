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

    [Fact]
    public void ProbeOutput_ParsesAudioTagsAndDispositions()
    {
        var result = Parse("""
            {
              "streams": [{
                "index": 2, "codec_type": "audio", "codec_name": "aac",
                "tags": { "language": "fra", "title": "Commentary" },
                "disposition": {
                  "default": 1, "forced": 1, "comment": 1,
                  "visual_impaired": 1, "descriptions": 0
                }
              }]
            }
            """);
        var stream = Assert.Single(result.Streams);
        Assert.Equal("fra", stream.Language);
        Assert.Equal("Commentary", stream.Title);
        Assert.True(stream.IsDefault);
        Assert.True(stream.IsForced);
        Assert.True(stream.IsCommentary);
        Assert.True(stream.IsAudioDescription);
    }

    [Fact]
    public void ProbeOutput_HandlesAbsentAndFalseDispositions()
    {
        var result = Parse("""
            {
              "streams": [
                {"index":0,"codec_type":"audio","codec_name":"aac"},
                {"index":1,"codec_type":"audio","codec_name":"aac",
                 "tags":{"language":"N/A"},
                 "disposition":{"default":0,"forced":0,"comment":0,"descriptions":0}}
              ]
            }
            """);
        Assert.All(result.Streams, stream =>
        {
            Assert.False(stream.IsDefault);
            Assert.False(stream.IsForced);
            Assert.False(stream.IsCommentary);
            Assert.False(stream.IsAudioDescription);
            Assert.Empty(stream.Title);
        });
        Assert.Empty(result.Streams[1].Language);
    }

    [Fact]
    public void ProbeOutput_ParsesMixedCaseTagsAndNumericStringDisposition()
    {
        var result = Parse("""
            {"streams":[{"index":0,"codec_type":"audio","codec_name":"aac",
              "tags":{"LANGUAGE":"enG","TITLE":"Narration","duration":"00:00:01.500000000"},
              "disposition":{"default":"1","forced":"0","comment":"N/A"}}]}
            """);
        var stream = Assert.Single(result.Streams);
        Assert.Equal("enG", stream.Language);
        Assert.Equal("Narration", stream.Title);
        Assert.Equal(1.5, stream.DurationSeconds);
        Assert.True(stream.IsDefault);
        Assert.False(stream.IsForced);
        Assert.False(stream.IsCommentary);
    }

    [Fact]
    public void ProbeOutput_ParsesAttachedPictureAndZeroDurationTag()
    {
        var result = Parse("""
            {"streams":[
              {"index":3,"codec_type":"video","codec_name":"mjpeg","duration":"N/A",
               "tags":{"DURATION":"00:00:00.000000000"},"disposition":{"attached_pic":1}},
              {"index":4,"codec_type":"video","codec_name":"mjpeg","duration":"0",
               "disposition":{"attached_pic":0}},
              {"index":5,"codec_type":"video","codec_name":"mjpeg"}
            ]}
            """);

        Assert.True(result.Streams[0].IsAttachedPicture);
        Assert.Equal(0, result.Streams[0].DurationSeconds);
        Assert.False(result.Streams[1].IsAttachedPicture);
        Assert.Equal(0, result.Streams[1].DurationSeconds);
        Assert.False(result.Streams[2].IsAttachedPicture);
        Assert.Null(result.Streams[2].DurationSeconds);
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
