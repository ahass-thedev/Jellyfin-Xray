using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// Minimal service registrator — services are instantiated in Plugin constructor.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Services are created directly in Plugin constructor.
        // Nothing to register here.
    }
}
