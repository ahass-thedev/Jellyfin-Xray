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
/// Registers configuration, web resources (the overlay JS), and manages
/// the Python sidecar process lifecycle.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;
    private SidecarManager? _sidecarManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;

        if (Configuration.AutoStartSidecar)
        {
            _sidecarManager = new SidecarManager(
                applicationPaths,
                Configuration,
                logger);

            _sidecarManager.Start();
        }
    }

    /// <summary>Gets the singleton plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "X-Ray";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("0344d9cc-bd3d-4f28-b6a0-314671628f84");

    /// <inheritdoc />
    public override string Description => "Shows actor information in the video player, similar to Amazon Prime X-Ray.";

    /// <summary>
    /// Gets the path to this plugin's data directory (for xray JSON files).
    /// </summary>
    public string DataPath => Path.Combine(
        ApplicationPaths.DataPath,
        "xray");

    /// <summary>
    /// Gets the path to the encoding cache directory (for Python sidecar .pkl files).
    /// </summary>
    public string EncodingCachePath => Path.Combine(
        ApplicationPaths.CachePath,
        "xray-encodings");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = false,
            }
        };
    }

    /// <summary>
    /// Returns the embedded JS overlay script as a string so the API controller can serve it.
    /// </summary>
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

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sidecarManager?.Stop();
            _sidecarManager = null;
        }

        base.Dispose(disposing);
    }
}
