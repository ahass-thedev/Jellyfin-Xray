using Docker.DotNet;
using Docker.DotNet.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Manages the X-Ray sidecar Docker container lifecycle.
/// Registered as IHostedService so it starts/stops with Jellyfin.
/// </summary>
public class SidecarManager : IHostedService, IDisposable
{
    private const string ImageName = "ghcr.io/ahass-thedev/jellyfin-xray-sidecar";
    private const string ImageTag = "latest";
    private const string ContainerName = "jellyfin-xray-sidecar";
    private const int SidecarPort = 8756;

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<SidecarManager> _logger;
    private DockerClient? _docker;
    private string? _containerId;
    private bool _disposed;

    public SidecarManager(IApplicationPaths appPaths, ILogger<SidecarManager> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoStartSidecar)
        {
            _logger.LogInformation("X-Ray sidecar auto-start is disabled");
            return;
        }

        try
        {
            _docker = CreateDockerClient(config.DockerSocketPath);
            await RemoveExistingContainerAsync().ConfigureAwait(false);
            await EnsureImageAsync().ConfigureAwait(false);
            _containerId = await CreateAndStartContainerAsync().ConfigureAwait(false);
            _logger.LogInformation("X-Ray sidecar started (id={Id})", _containerId[..12]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to start X-Ray sidecar. " +
                "If using Docker Compose, disable Auto-start in plugin settings. " +
                "Otherwise ensure the Docker socket is mounted: -v /var/run/docker.sock:/var/run/docker.sock");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_docker is null || string.IsNullOrEmpty(_containerId)) return;
        _logger.LogInformation("Stopping X-Ray sidecar {Id}", _containerId[..12]);
        try
        {
            await _docker.Containers.StopContainerAsync(_containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, cancellationToken).ConfigureAwait(false);
            await _docker.Containers.RemoveContainerAsync(_containerId,
                new ContainerRemoveParameters { Force = true }, cancellationToken).ConfigureAwait(false);
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

        if (images.Count > 0) { _logger.LogDebug("Sidecar image already present"); return; }

        _logger.LogInformation("Pulling sidecar image {Image} — first run may take a few minutes", fullImage);
        await _docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = ImageName, Tag = ImageTag },
            authConfig: null,
            new Progress<JSONMessage>(msg =>
            {
                if (!string.IsNullOrEmpty(msg.Status))
                    _logger.LogDebug("[docker pull] {Status}", msg.Status);
            })).ConfigureAwait(false);
    }

    private async Task<string> CreateAndStartContainerAsync()
    {
        var cacheDir = Path.Combine(_appPaths.CachePath, "xray-encodings");
        Directory.CreateDirectory(cacheDir);

        var response = await _docker!.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{ImageName}:{ImageTag}",
            Name = ContainerName,
            ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{SidecarPort}/tcp"] = default },
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{SidecarPort}/tcp"] = new List<PortBinding>
                    {
                        new PortBinding { HostIP = "127.0.0.1", HostPort = SidecarPort.ToString() }
                    }
                },
                Binds = new List<string> { $"{cacheDir}:/cache" },
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

    private static DockerClient CreateDockerClient(string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
            socketPath = OperatingSystem.IsWindows()
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock";
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
        if (disposing) _docker?.Dispose();
        _disposed = true;
    }
}
