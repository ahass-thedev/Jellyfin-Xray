using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// Registers all plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServiceProvider applicationServiceProvider)
    {
        serviceCollection.AddSingleton<XRayStore>();
        serviceCollection.AddSingleton<SidecarClient>();
        serviceCollection.AddSingleton<MetadataService>();
        serviceCollection.AddSingleton<XRayService>();
        serviceCollection.AddHostedService<SidecarManager>();
    }
}
