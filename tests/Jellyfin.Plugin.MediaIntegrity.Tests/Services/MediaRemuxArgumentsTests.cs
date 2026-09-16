using System.Diagnostics;
using System.Reflection;
using Jellyfin.Plugin.MediaIntegrity.Services;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class MediaRemuxArgumentsTests
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("m4a")]
    public void Remux_AlwaysCopiesAllStreamsWithoutShellOrEncoderOptions(string extension)
    {
        var method = typeof(MediaRemuxService).GetMethod("BuildStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
        var source = "/media/name with spaces; -c libx264." + extension;
        var output = "/cache/media-integrity/test." + extension;
        var start = (ProcessStartInfo)method.Invoke(null, ["ffmpeg", source, output])!;
        var arguments = start.ArgumentList.ToList();
        Assert.False(start.UseShellExecute);
        Assert.Equal("copy", arguments[arguments.IndexOf("-c") + 1]);
        Assert.Equal("0", arguments[arguments.IndexOf("-map") + 1]);
        Assert.Equal("0", arguments[arguments.IndexOf("-map_metadata") + 1]);
        Assert.Equal("0", arguments[arguments.IndexOf("-map_chapters") + 1]);
        Assert.Equal(source, arguments[arguments.IndexOf("-i") + 1]);
        Assert.Equal(output, arguments[^1]);
        Assert.Single(arguments, argument => argument == "-c");
        Assert.DoesNotContain("-c:v", arguments);
        Assert.DoesNotContain("-c:a", arguments);
        Assert.DoesNotContain("-vf", arguments);
        Assert.DoesNotContain("-af", arguments);
    }
}
