using System.Reflection;
using Jellyfin.Plugin.XRay.Configuration;
using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// The X-Ray plugin entry point.
/// Follows the minimal-constructor pattern required by Jellyfin's plugin system.
/// Services are registered via DI through PluginServiceRegistrator.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = loggerFactory.CreateLogger<Plugin>();
        Instance = this;

        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(EncodingCachePath);
    }

    /// <summary>Gets the singleton plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "X-Ray";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("3f4a2b1c-8e7d-4f6a-9b5c-2d1e0f3a4b5c");

    /// <inheritdoc />
    public override string Description =>
        "Shows actor information in the video player, similar to Amazon Prime X-Ray.";

    /// <summary>Gets the plugin data directory path.</summary>
    public string DataPath => Path.Combine(ApplicationPaths.DataPath, "xray");

    /// <summary>Gets the encoding cache directory path.</summary>
    public string EncodingCachePath => Path.Combine(ApplicationPaths.CachePath, "xray-encodings");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            }
        };
    }

    /// <summary>Returns the embedded JS overlay script as a string.</summary>
    public string GetOverlayScript()
    {
        var resourceName = $"{GetType().Namespace}.ClientScript.xray-overlay.js";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogError("Could not find embedded resource: {Name}", resourceName);
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
