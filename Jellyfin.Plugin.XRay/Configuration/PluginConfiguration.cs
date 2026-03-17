using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.XRay.Configuration;

/// <summary>
/// Plugin configuration — serialized to XML and surfaced in the Dashboard config page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the address of the Python sidecar process.
    /// Default assumes it is running on the same host.
    /// </summary>
    public string SidecarUrl { get; set; } = "http://localhost:8756";

    /// <summary>
    /// Gets or sets the maximum number of actors to display in the overlay at once.
    /// </summary>
    public int MaxActorsDisplayed { get; set; } = 4;

    /// <summary>
    /// Gets or sets the face match tolerance (0.0 = strict, 1.0 = loose).
    /// face_recognition recommends 0.6 as the default.
    /// Lower values reduce false positives at the cost of missing matches.
    /// </summary>
    public double FaceMatchTolerance { get; set; } = 0.55;

    /// <summary>
    /// Gets or sets the minimum confidence (0–1) required to report a match.
    /// </summary>
    public double FaceConfidenceThreshold { get; set; } = 0.60;

    /// <summary>
    /// Gets or sets the trickplay frame interval in seconds.
    /// Should match the interval Jellyfin was configured to generate trickplay at.
    /// </summary>
    public int TrickplayIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether the overlay is enabled in the player.
    /// </summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the sidecar process should be
    /// auto-started by the plugin on Jellyfin startup.
    /// Set to false if you manage the sidecar externally (e.g. systemd service).
    /// </summary>
    public bool AutoStartSidecar { get; set; } = true;
}
// append before closing brace — patch applied via sed below
    /// <summary>
    /// Gets or sets the Docker socket path.
    /// Leave blank for the platform default:
    ///   Linux/Mac: unix:///var/run/docker.sock
    ///   Windows:   npipe://./pipe/docker_engine
    /// </summary>
    public string DockerSocketPath { get; set; } = string.Empty;
}
