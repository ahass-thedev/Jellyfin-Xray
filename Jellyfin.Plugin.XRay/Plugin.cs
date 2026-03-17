using System.Reflection;
using Jellyfin.Plugin.XRay.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.XRay;

/// <summary>
/// The X-Ray plugin entry point.
/// Constructor must only take IApplicationPaths and IXmlSerializer —
/// these are the only two parameters Jellyfin's plugin loader injects.
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

    /// <summary>Lists all embedded resource names in the assembly — for diagnostics.</summary>
    public IEnumerable<string> GetEmbeddedResourceNames()
        => System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames();

    /// <summary>Returns the embedded JS overlay script as a string.</summary>
    public string GetOverlayScript()
    {
        var resourceName = $"{GetType().Namespace}.ClientScript.xray-overlay.js";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
