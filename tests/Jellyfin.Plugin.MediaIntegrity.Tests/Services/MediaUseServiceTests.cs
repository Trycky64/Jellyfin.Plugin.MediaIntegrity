using System.Reflection;
using Jellyfin.Plugin.MediaIntegrity.Services;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class MediaUseServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlaybackSession_IdentifiesOnlyActivePath(bool active)
    {
        var path = Path.GetFullPath("playing.mp4");
        var manager = DispatchProxy.Create<ISessionManager, SessionManagerProxy>();
        ((SessionManagerProxy)(object)manager).Sessions = active
            ? [new SessionInfo(manager, NullLogger.Instance) { NowPlayingItem = new BaseItemDto { Path = path } }]
            : [];
        var service = new MediaUseService(manager, NullLogger<MediaUseService>.Instance);
        Assert.Equal(active, service.IsMediaInUse(path));
        Assert.False(service.IsMediaInUse(Path.GetFullPath("other.mp4")));
    }

    public class SessionManagerProxy : DispatchProxy
    {
        public IReadOnlyList<SessionInfo> Sessions { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_Sessions" ? Sessions : throw new NotSupportedException();
    }
}
