using System.Reflection;
using Jellyfin.Plugin.XRay.Configuration;
using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// X-Ray plugin. Services are instantiated directly here since
/// IPluginServiceRegistrator is not reliable in Jellyfin 10.11.
/// Constructor parameters beyond IApplicationPaths + IXmlSerializer are
/// injected by Jellyfin's ActivatorUtilities.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(EncodingCachePath);

        var logger = loggerFactory.CreateLogger<Plugin>();

        Store = new XRayStore(loggerFactory.CreateLogger<XRayStore>());
        SidecarHttpClient = new SidecarClient(loggerFactory.CreateLogger<SidecarClient>());
        Metadata = new MetadataService(libraryManager, loggerFactory.CreateLogger<MetadataService>());
        XRay = new XRayService(Metadata, SidecarHttpClient, Store, loggerFactory.CreateLogger<XRayService>());

        if (Configuration.AutoStartSidecar)
        {
            var sidecarMgr = new SidecarManager(applicationPaths, loggerFactory.CreateLogger<SidecarManager>());
            _ = sidecarMgr.StartAsync(CancellationToken.None);
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "X-Ray";
    public override Guid Id => Guid.Parse("3f4a2b1c-8e7d-4f6a-9b5c-2d1e0f3a4b5c");
    public override string Description => "Shows actor information in the video player, similar to Amazon Prime X-Ray.";

    public string DataPath => Path.Combine(ApplicationPaths.DataPath, "xray");
    public string EncodingCachePath => Path.Combine(ApplicationPaths.CachePath, "xray-encodings");

    public XRayStore Store { get; }
    public SidecarClient SidecarHttpClient { get; }
    public MetadataService Metadata { get; }
    public XRayService XRay { get; }

    public IEnumerable<PluginPageInfo> GetPages() =>
        new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            }
        };

    public IEnumerable<string> GetEmbeddedResourceNames()
        => Assembly.GetExecutingAssembly().GetManifestResourceNames();

    public string GetOverlayScript()
    {
        var resourceName = $"{GetType().Namespace}.ClientScript.xray-overlay.js";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
