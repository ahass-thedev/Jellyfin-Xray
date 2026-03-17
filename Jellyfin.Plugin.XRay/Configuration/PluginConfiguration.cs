using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.XRay.Configuration;

/// <summary>
/// Plugin configuration — serialized to XML and surfaced in the Dashboard config page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the address of the sidecar container.
    /// </summary>
    public string SidecarUrl { get; set; } = "http://localhost:8756";

    /// <summary>
    /// Gets or sets the maximum number of actors to display in the overlay at once.
    /// </summary>
    public int MaxActorsDisplayed { get; set; } = 4;

    /// <summary>
    /// Gets or sets the face match tolerance (0.0–1.0).
    /// Lower values reduce false positives. Recommended: 0.50–0.60.
    /// </summary>
    public double FaceMatchTolerance { get; set; } = 0.55;

    /// <summary>
    /// Gets or sets the minimum confidence (0–1) required to report a match.
    /// </summary>
    public double FaceConfidenceThreshold { get; set; } = 0.60;

    /// <summary>
    /// Gets or sets the trickplay frame interval in seconds.
    /// Should match Jellyfin's trickplay generation interval.
    /// </summary>
    public int TrickplayIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether the overlay is enabled in the player.
    /// </summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should auto-start
    /// the sidecar container on Jellyfin startup.
    /// </summary>
    public bool AutoStartSidecar { get; set; } = true;

    /// <summary>
    /// Gets or sets the Docker socket path.
    /// Leave blank for the platform default:
    ///   Linux: unix:///var/run/docker.sock
    ///   Windows: npipe://./pipe/docker_engine
    /// </summary>
    public string DockerSocketPath { get; set; } = string.Empty;
}
