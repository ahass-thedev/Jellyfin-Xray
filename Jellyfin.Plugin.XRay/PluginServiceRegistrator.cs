using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// Registers all plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<XRayStore>();
        serviceCollection.AddSingleton<SidecarClient>();
        serviceCollection.AddSingleton<MetadataService>();
        serviceCollection.AddSingleton<XRayService>();
        serviceCollection.AddHostedService<SidecarManager>();
    }
}
