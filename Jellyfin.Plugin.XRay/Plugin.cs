using System.Reflection;
using Jellyfin.Plugin.XRay.Configuration;
using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// X-Ray plugin. Constructor takes only the two parameters Jellyfin's
/// plugin loader always provides. Services are initialised separately
/// via InitializeServices() called by PluginServiceRegistrator.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(EncodingCachePath);
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "X-Ray";
    public override Guid Id => Guid.Parse("3f4a2b1c-8e7d-4f6a-9b5c-2d1e0f3a4b5c");
    public override string Description => "Shows actor information in the video player, similar to Amazon Prime X-Ray.";

    public string DataPath => Path.Combine(ApplicationPaths.DataPath, "xray");
    public string EncodingCachePath => Path.Combine(ApplicationPaths.CachePath, "xray-encodings");

    // Services — set by PluginServiceRegistrator after DI container is built
    public XRayStore? Store { get; internal set; }
    public SidecarClient? SidecarHttpClient { get; internal set; }
    public MetadataService? Metadata { get; internal set; }
    public XRayService? XRay { get; internal set; }
    public XRayFileLogger? FileLogger { get; internal set; }

    public IEnumerable<PluginPageInfo> GetPages() =>
        new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
            new PluginPageInfo
            {
                Name = "XRayOverlayInjector",
                DisplayName = "X-Ray",
                EmbeddedResourcePath = $"{GetType().Namespace}.ClientScript.overlay-injector.html",
                EnableInMainMenu = true,
                MenuIcon = "movie",
            },
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
