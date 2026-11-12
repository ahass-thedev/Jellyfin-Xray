using Jellyfin.Plugin.SceneXray.Music;
using Jellyfin.Plugin.SceneXray.Services;
using Jellyfin.Plugin.SceneXray.Share;
using Jellyfin.Plugin.SceneXray.ThirdParty.FileTransformation;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SceneXray;

/// <summary>Registers X-Ray's services with the server.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CastResolver>();
        serviceCollection.AddSingleton<FingerprintCache>();
        serviceCollection.AddSingleton<MusicService>();
        serviceCollection.AddSingleton<AnalysisService>();
        serviceCollection.AddSingleton<SharingService>();
        serviceCollection.AddHostedService<ShareOutbox>();
        serviceCollection.AddSingleton<AnalysisQueue>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<AnalysisQueue>());
        serviceCollection.AddHostedService<StartupService>();
        serviceCollection.AddHostedService<NewMediaWatcher>();

        // The player overlay goes in through File Transformation. If that plugin is installed,
        // share it (several plugins edit index.html); otherwise use the bundled copy. Running
        // both would edit the page twice, so it is one or the other.
        if (!StartupService.FileTransformationInstalled)
        {
            serviceCollection.AddTransient<IStartupFilter, BundledTransformationStartupFilter>();
        }
    }
}
