using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// Registers plugin services and wires them into the Plugin instance.
/// Called by Jellyfin after the DI container is built — safe to use
/// ILibraryManager and ILoggerFactory here.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<XRayInitializer>();
    }
}

/// <summary>
/// Hosted service that wires up plugin services once the DI container is ready.
/// </summary>
public class XRayInitializer : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _appPaths;

    public XRayInitializer(
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        IApplicationPaths appPaths)
    {
        _libraryManager = libraryManager;
        _loggerFactory = loggerFactory;
        _appPaths = appPaths;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return Task.CompletedTask;

        var fileLogger = new XRayFileLogger(plugin.DataPath);
        plugin.FileLogger = fileLogger;
        fileLogger.Write("INF", $"Plugin starting. PluginDataPath={plugin.DataPath} AppDataPath={_appPaths.DataPath}");

        plugin.Store = new XRayStore(new TeeLogger<XRayStore>(_loggerFactory.CreateLogger<XRayStore>(), fileLogger));
        plugin.SidecarHttpClient = new SidecarClient(new TeeLogger<SidecarClient>(_loggerFactory.CreateLogger<SidecarClient>(), fileLogger));
        plugin.Metadata = new MetadataService(_libraryManager, _appPaths, new TeeLogger<MetadataService>(_loggerFactory.CreateLogger<MetadataService>(), fileLogger));
        plugin.XRay = new XRayService(plugin.Metadata, plugin.SidecarHttpClient, plugin.Store, new TeeLogger<XRayService>(_loggerFactory.CreateLogger<XRayService>(), fileLogger));

        if (plugin.Configuration.AutoStartSidecar)
        {
            var sidecarMgr = new SidecarManager(_appPaths, _loggerFactory.CreateLogger<SidecarManager>());
            _ = sidecarMgr.StartAsync(CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
