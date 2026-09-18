using Jellyfin.Plugin.MediaIntegrity.Services;
using Jellyfin.Plugin.MediaIntegrity.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaIntegrity;

/// <summary>
/// Registers plugin services in Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator :
    IPluginServiceRegistrator
{
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginConfigurationService>();

        serviceCollection.AddSingleton<PathSecurityService>();
        serviceCollection.AddSingleton<PathMapper>();

        serviceCollection.AddSingleton<LibraryMediaProvider>();

        serviceCollection.AddSingleton<MediaIssueDetector>();
        serviceCollection.AddSingleton<MediaProbeService>();

        serviceCollection.AddSingleton<RepairQueueService>();
        serviceCollection.AddSingleton<ScanStatisticsService>();
        serviceCollection.AddSingleton<AvRepairStatisticsService>();

        serviceCollection.AddSingleton<MediaUseService>();
        serviceCollection.AddSingleton<MediaRemuxService>();
        serviceCollection.AddSingleton<MediaValidationService>();
        serviceCollection.AddSingleton<IMediaValidationService>(
            static provider =>
                provider.GetRequiredService<MediaValidationService>());
        serviceCollection.AddSingleton<MediaReplacementService>();
        serviceCollection.AddSingleton<AvRepairExecutionService>();

        serviceCollection.AddSingleton<MediaIntegrityScanTask>();
        serviceCollection.AddSingleton<MediaRemuxRepairTask>();
        serviceCollection.AddSingleton<AvRepairTask>();
    }
}
