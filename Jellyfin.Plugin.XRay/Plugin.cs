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
/// The X-Ray plugin entry point.
/// Services are created here and exposed as static properties so the controller
/// and scheduled tasks can access them without requiring DI registration.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
{
    private readonly ILogger<Plugin> _logger;
    private SidecarManager? _sidecarManager;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// Jellyfin passes ILibraryManager via constructor injection automatically.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = loggerFactory.CreateLogger<Plugin>();
        Instance = this;

        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(EncodingCachePath);

        // Instantiate services — exposed statically for controller/task access
        Store = new XRayStore(loggerFactory.CreateLogger<XRayStore>());
        SidecarHttpClient = new SidecarClient(loggerFactory.CreateLogger<SidecarClient>());
        Metadata = new MetadataService(libraryManager, loggerFactory.CreateLogger<MetadataService>());
        XRay = new XRayService(Metadata, SidecarHttpClient, Store, loggerFactory.CreateLogger<XRayService>());

        if (Configuration.AutoStartSidecar)
        {
            _sidecarManager = new SidecarManager(
                applicationPaths,
                Configuration,
                loggerFactory.CreateLogger<SidecarManager>());
            _sidecarManager.Start();
        }
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

    /// <summary>Gets the plugin data directory (xray JSON files).</summary>
    public string DataPath => Path.Combine(ApplicationPaths.DataPath, "xray");

    /// <summary>Gets the encoding cache directory (Python .pkl files).</summary>
    public string EncodingCachePath => Path.Combine(ApplicationPaths.CachePath, "xray-encodings");

    // Services exposed for use by controllers and scheduled tasks
    public XRayStore Store { get; private set; } = null!;
    public SidecarClient SidecarHttpClient { get; private set; } = null!;
    public MetadataService Metadata { get; private set; } = null!;
    public XRayService XRay { get; private set; } = null!;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
        new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            }
        };

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

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases resources.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _sidecarManager?.Stop();
            _sidecarManager = null;
            SidecarHttpClient?.Dispose();
        }
        _disposed = true;
    }
}
