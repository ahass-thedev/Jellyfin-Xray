using Jellyfin.Plugin.XRay.Api;
using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// Registers all plugin services with Jellyfin's dependency injection container.
/// Jellyfin discovers this class automatically via <see cref="IPluginServiceRegistrator"/>.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServiceProvider applicationServiceProvider)
    {
        serviceCollection.AddSingleton<XRayStore>();
        serviceCollection.AddSingleton<MetadataService>();
        serviceCollection.AddSingleton<SidecarClient>();
        serviceCollection.AddSingleton<XRayService>();
    }
}
