using System.Diagnostics;
using System.Reflection;
using Jellyfin.Plugin.MediaIntegrity.Models;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class AvRepairExecutionServiceTests
{
    [Fact]
    public void TimestampShift_UsesSecondInputOnlyForTargetAudioAndPreservesOrder()
    {
        var streams = Streams();
        var plan = new AvRepairPlan
        {
            Strategy = AvRepairStrategy.TimestampShift,
            AudioStreamIndex = 1,
            TimestampShiftSeconds = -1.5
        };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", streams, plan);
        var args = start.ArgumentList.ToList();

        Assert.False(start.UseShellExecute);
        AssertMapOrderPreserved(args, streams, shiftedAudioIndex: 1);
        Assert.Contains("-itsoffset", args);
        Assert.Equal("-1.500000", args[args.IndexOf("-itsoffset") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c") + 1]);
        Assert.DoesNotContain("-filter:a:0", args);
        Assert.DoesNotContain("-c:a:0", args);
    }

    [Fact]
    public void TimestampShift_PositiveShift_IsFormattedWithoutExplicitPlus()
    {
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.TimestampShift, AudioStreamIndex = 1, TimestampShiftSeconds = 2.25 };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", Streams(), plan);
        var args = start.ArgumentList.ToList();
        Assert.Equal("2.250000", args[args.IndexOf("-itsoffset") + 1]);
    }

    [Fact]
    public void AudioTimeStretch_SetsFilterAndCodecOnTargetAudioOnly()
    {
        var streams = Streams();
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioTimeStretch, AudioStreamIndex = 2, AtempoFactor = 1.02 };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", streams, plan);
        var args = start.ArgumentList.ToList();

        // audio stream 2 is the second audio track (ordinal 1)
        Assert.Equal("aac", args[args.IndexOf("-c:a:1") + 1]);
        Assert.Equal("atempo=1.020000", args[args.IndexOf("-filter:a:1") + 1]);
        Assert.DoesNotContain("-c:a:0", args);
        Assert.DoesNotContain("-itsoffset", args);
    }

    [Fact]
    public void AudioPad_UsesApadFilterWithConfiguredDuration()
    {
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioPad, AudioStreamIndex = 1, PadSeconds = 0.75 };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", Streams(), plan);
        var args = start.ArgumentList.ToList();
        Assert.Equal("apad=pad_dur=0.750000", args[args.IndexOf("-filter:a:0") + 1]);
    }

    [Fact]
    public void AudioPad_PreservesSourceCodecInsteadOfConfiguredDefault()
    {
        // Stream 1 is "aac" in Streams(); use a source with a different codec
        // to prove the configured "aac" default is ignored for pad/trim.
        var streams = Streams();
        streams[1].CodecName = "mp3";
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioPad, AudioStreamIndex = 1, PadSeconds = 0.5 };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", streams, plan);
        var args = start.ArgumentList.ToList();
        Assert.Equal("libmp3lame", args[args.IndexOf("-c:a:0") + 1]);
    }

    [Fact]
    public void AudioTrim_PreservesSourceCodecInsteadOfConfiguredDefault()
    {
        var streams = Streams();
        streams[1].CodecName = "ac3";
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioTrim, AudioStreamIndex = 1, TrimSeconds = 1.0 };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", streams, plan);
        var args = start.ArgumentList.ToList();
        Assert.Equal("ac3", args[args.IndexOf("-c:a:0") + 1]);
    }

    [Fact]
    public void AudioTimeStretch_UsesConfiguredCodecNotSourceCodec()
    {
        // Unlike pad/trim, time-stretch is an intentional re-encode and keeps
        // using the configurable AudioReencodeCodec regardless of source codec.
        var streams = Streams();
        streams[1].CodecName = "mp3";
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioTimeStretch, AudioStreamIndex = 1, AtempoFactor = 1.01 };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", streams, plan);
        var args = start.ArgumentList.ToList();
        Assert.Equal("aac", args[args.IndexOf("-c:a:0") + 1]);
    }

    [Fact]
    public void AudioPad_UnknownSourceCodec_Throws()
    {
        var streams = Streams();
        streams[1].CodecName = "truehd";
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioPad, AudioStreamIndex = 1, PadSeconds = 0.5 };
        Assert.Throws<InvalidOperationException>(() => BuildStartInfo("/media/f.mkv", "/cache/out.mkv", streams, plan));
    }

    [Theory]
    [InlineData("aac", true)]
    [InlineData("ac3", true)]
    [InlineData("mp3", true)]
    [InlineData("truehd", false)]
    [InlineData("dts", false)]
    [InlineData("", false)]
    public void CanPreserveCodec_MatchesKnownEncoderMap(string codec, bool expected)
    {
        Assert.Equal(expected, AvRepairExecutionService.CanPreserveCodec(codec));
    }

    [Fact]
    public void AudioTrim_ComputesEndFromSourceDurationMinusTrim()
    {
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioTrim, AudioStreamIndex = 1, TrimSeconds = 1.0 };
        var start = BuildStartInfo("/media/f.mkv", "/cache/out.mkv", Streams(), plan);
        var args = start.ArgumentList.ToList();
        Assert.Equal("atrim=end=99.000000", args[args.IndexOf("-filter:a:0") + 1]);
    }

    [Fact]
    public void AudioTrim_ExceedingSourceDuration_Throws()
    {
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.AudioTrim, AudioStreamIndex = 1, TrimSeconds = 1000.0 };
        Assert.Throws<InvalidOperationException>(() => BuildStartInfo("/media/f.mkv", "/cache/out.mkv", Streams(), plan));
    }

    [Fact]
    public void ManualOnlyOrNoneStrategy_ThrowsInsteadOfExecuting()
    {
        var manual = new AvRepairPlan { Strategy = AvRepairStrategy.ManualOnly, AudioStreamIndex = 1 };
        var none = new AvRepairPlan { Strategy = AvRepairStrategy.None, AudioStreamIndex = 1 };
        Assert.Throws<InvalidOperationException>(() => BuildStartInfo("/media/f.mkv", "/cache/out.mkv", Streams(), manual));
        Assert.Throws<InvalidOperationException>(() => BuildStartInfo("/media/f.mkv", "/cache/out.mkv", Streams(), none));
    }

    [Fact]
    public void UnknownAudioStreamIndex_Throws()
    {
        var plan = new AvRepairPlan { Strategy = AvRepairStrategy.TimestampShift, AudioStreamIndex = 99, TimestampShiftSeconds = 1 };
        Assert.Throws<InvalidOperationException>(() => BuildStartInfo("/media/f.mkv", "/cache/out.mkv", Streams(), plan));
    }

    [Theory]
    [InlineData(1.0, "atempo=1.000000")]
    [InlineData(1.5, "atempo=1.500000")]
    [InlineData(0.5, "atempo=0.500000")]
    [InlineData(3.0, "atempo=2.000000,atempo=1.500000")]
    [InlineData(0.25, "atempo=0.500000,atempo=0.500000")]
    public void BuildAtempoExpression_ChainsOutsideSingleFilterRange(double factor, string expected)
    {
        var method = typeof(AvRepairExecutionService).GetMethod(
            "BuildAtempoExpression", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = (string)method.Invoke(null, [factor])!;
        Assert.Equal(expected, result);
    }

    private static void AssertMapOrderPreserved(List<string> args, IReadOnlyList<MediaStreamInfo> streams, int shiftedAudioIndex)
    {
        var mapValues = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-map")
            {
                mapValues.Add(args[i + 1]);
            }
        }

        var ordered = streams.OrderBy(static s => s.Index).ToList();
        Assert.Equal(ordered.Count, mapValues.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var expected = ordered[i].Index == shiftedAudioIndex ? $"1:{ordered[i].Index}" : $"0:{ordered[i].Index}";
            Assert.Equal(expected, mapValues[i]);
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string source, string output, IReadOnlyList<MediaStreamInfo> streams, AvRepairPlan plan)
    {
        var method = typeof(AvRepairExecutionService).GetMethod(
            "BuildStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            return (ProcessStartInfo)method.Invoke(null, ["ffmpeg", source, output, streams, plan, "aac"])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static List<MediaStreamInfo> Streams() =>
    [
        new() { Index = 0, CodecType = "video", CodecName = "h264", DurationSeconds = 100 },
        new() { Index = 1, CodecType = "audio", CodecName = "aac", DurationSeconds = 100, SampleRate = 48000, Channels = 2 },
        new() { Index = 2, CodecType = "audio", CodecName = "ac3", DurationSeconds = 100, SampleRate = 48000, Channels = 6 },
        new() { Index = 3, CodecType = "subtitle", CodecName = "subrip" }
    ];
}
