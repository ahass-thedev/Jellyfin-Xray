using Docker.DotNet;
using Docker.DotNet.Models;
using Jellyfin.Plugin.XRay.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Manages the lifecycle of the face recognition sidecar Docker container.
/// Connects to the Docker socket, pulls the image if needed, and starts/stops
/// the container alongside the plugin.
/// </summary>
public class SidecarManager : IDisposable
{
    private const string ImageName = "ghcr.io/ahass-thedev/jellyfin-xray-sidecar";
    private const string ImageTag = "latest";
    private const string ContainerName = "jellyfin-xray-sidecar";
    private const int SidecarPort = 8756;

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private readonly string _cacheDir;
    private DockerClient? _docker;
    private string? _containerId;
    private bool _disposed;

    public SidecarManager(
        IApplicationPaths appPaths,
        PluginConfiguration config,
        ILogger logger)
    {
        _config = config;
        _logger = logger;
        _cacheDir = Path.Combine(appPaths.CachePath, "xray-encodings");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try { await StartAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to start X-Ray sidecar container. " +
                    "Ensure Docker socket is mounted: -v /var/run/docker.sock:/var/run/docker.sock");
            }
        });
    }

    public void Stop()
    {
        _ = Task.Run(async () =>
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping sidecar container"); }
        });
    }

    private async Task StartAsync()
    {
        _docker = CreateDockerClient();
        await RemoveExistingContainerAsync().ConfigureAwait(false);
        await EnsureImageAsync().ConfigureAwait(false);
        _containerId = await CreateAndStartContainerAsync().ConfigureAwait(false);
        _logger.LogInformation("X-Ray sidecar started (id={Id}) on port {Port}",
            _containerId[..12], SidecarPort);
    }

    private async Task StopAsync()
    {
        if (_docker is null || string.IsNullOrEmpty(_containerId)) return;
        _logger.LogInformation("Stopping X-Ray sidecar {Id}", _containerId[..12]);
        try
        {
            await _docker.Containers.StopContainerAsync(_containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 5 }).ConfigureAwait(false);
            await _docker.Containers.RemoveContainerAsync(_containerId,
                new ContainerRemoveParameters { Force = true }).ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException) { }
        _containerId = null;
    }

    private async Task EnsureImageAsync()
    {
        var fullImage = $"{ImageName}:{ImageTag}";
        var images = await _docker!.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [fullImage] = true }
            }
        }).ConfigureAwait(false);

        if (images.Count > 0)
        {
            _logger.LogDebug("Sidecar image {Image} already present", fullImage);
            return;
        }

        _logger.LogInformation("Pulling sidecar image {Image} — first run may take a few minutes", fullImage);
        await _docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = ImageName, Tag = ImageTag },
            authConfig: null,
            new Progress<JSONMessage>(msg =>
            {
                if (!string.IsNullOrEmpty(msg.Status))
                    _logger.LogDebug("[docker pull] {Status}", msg.Status);
            })).ConfigureAwait(false);
        _logger.LogInformation("Sidecar image pulled successfully");
    }

    private async Task<string> CreateAndStartContainerAsync()
    {
        var response = await _docker!.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{ImageName}:{ImageTag}",
            Name = ContainerName,
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                [$"{SidecarPort}/tcp"] = default
            },
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{SidecarPort}/tcp"] = new List<PortBinding>
                    {
                        new PortBinding { HostIP = "127.0.0.1", HostPort = SidecarPort.ToString() }
                    }
                },
                Binds = new List<string> { $"{_cacheDir}:/cache" },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
                NetworkMode = "bridge",
            },
        }).ConfigureAwait(false);

        await _docker.Containers.StartContainerAsync(response.ID, new ContainerStartParameters())
            .ConfigureAwait(false);
        return response.ID;
    }

    private async Task RemoveExistingContainerAsync()
    {
        try
        {
            var containers = await _docker!.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }).ConfigureAwait(false);
            var existing = containers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == ContainerName));
            if (existing is null) return;
            await _docker.Containers.StopContainerAsync(existing.ID,
                new ContainerStopParameters { WaitBeforeKillSeconds = 3 }).ConfigureAwait(false);
            await _docker.Containers.RemoveContainerAsync(existing.ID,
                new ContainerRemoveParameters { Force = true }).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not remove existing sidecar container"); }
    }

    private DockerClient CreateDockerClient()
    {
        var socketPath = _config.DockerSocketPath;
        if (string.IsNullOrWhiteSpace(socketPath))
            socketPath = OperatingSystem.IsWindows()
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock";
        _logger.LogDebug("Connecting to Docker at {Socket}", socketPath);
        return new DockerClientConfiguration(new Uri(socketPath)).CreateClient();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) { Stop(); _docker?.Dispose(); }
        _disposed = true;
    }
}
