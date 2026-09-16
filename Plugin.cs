using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaIntegrity;

public sealed class Plugin :
    BasePlugin<PluginConfiguration>,
    IHasWebPages
{
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Media Integrity";

    public override Guid Id =>
        Guid.Parse("b8dc8a71-3d33-4e51-b4d6-8ea09f8db491");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "MediaIntegrityConfiguration",
                EmbeddedResourcePath =
                    $"{GetType().Namespace}.Configuration.configPage.html"
            }
        ];
    }
}
