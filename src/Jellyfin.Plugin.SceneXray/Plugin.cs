using Jellyfin.Plugin.SceneXray.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SceneXray;

/// <summary>X-Ray: who is on screen and what is playing, for movies and episodes.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataDirectory = Path.Combine(applicationPaths.DataPath, "scenexray");
        Directory.CreateDirectory(DataDirectory);
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the folder holding models, timelines, the face gallery and music fingerprints.</summary>
    public string DataDirectory { get; }

    public override string Name => "X-Ray";

    public override Guid Id => Guid.Parse("5b3f1e9a-7c42-4d1b-9a60-2f8e4c7d3a11");

    public override string Description => "Amazon-style X-Ray: the cast in the current scene and the song that is playing.";

    /// <summary>Gets the analyser version; bumping it re-queues every title.</summary>
    public static string AnalyzerVersion => "2";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "SceneXray",
            DisplayName = "X-Ray",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            EnableInMainMenu = true, // a sidebar link in the dashboard, as in 10.x
            MenuSection = "server",
            MenuIcon = "theaters",
        },
        new PluginPageInfo
        {
            Name = "SceneXrayTitles",
            DisplayName = "X-Ray titles",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.titlesPage.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "fact_check",
        },
    ];
}
