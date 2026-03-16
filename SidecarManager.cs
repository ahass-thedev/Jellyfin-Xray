using System.Diagnostics;
using Jellyfin.Plugin.XRay.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Manages the lifecycle of the Python face recognition sidecar process.
///
/// The sidecar lives in {PluginDataPath}/sidecar/main.py.
/// On first run (or after an update), we copy the bundled sidecar files there.
/// </summary>
public class SidecarManager
{
    private readonly IApplicationPaths _appPaths;
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private Process? _process;

    private string SidecarDir => Path.Combine(
        _appPaths.DataPath, "xray-sidecar");

    private string SidecarScript => Path.Combine(SidecarDir, "main.py");

    public SidecarManager(
        IApplicationPaths appPaths,
        PluginConfiguration config,
        ILogger logger)
    {
        _appPaths = appPaths;
        _config = config;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void Start()
    {
        if (!File.Exists(SidecarScript))
        {
            _logger.LogWarning(
                "Sidecar script not found at {Path}. " +
                "Copy the sidecar/ folder from the plugin repo to {Dir} and run: pip install -r requirements.txt",
                SidecarScript, SidecarDir);
            return;
        }

        var python = string.IsNullOrWhiteSpace(_config.PythonPath)
            ? FindPython()
            : _config.PythonPath;

        if (python is null)
        {
            _logger.LogError(
                "Python executable not found. Set PythonPath in plugin config or ensure python3 is on PATH.");
            return;
        }

        var encodingCacheDir = Plugin.Instance?.EncodingCachePath
            ?? Path.Combine(_appPaths.CachePath, "xray-encodings");
        Directory.CreateDirectory(encodingCacheDir);

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{SidecarScript}\" --cache-dir \"{encodingCacheDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Starting X-Ray sidecar: {Python} {Args}", python, psi.Arguments);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogDebug("[sidecar] {Line}", e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogWarning("[sidecar] {Line}", e.Data);
        };
        _process.Exited += (_, _) =>
            _logger.LogWarning("X-Ray sidecar process exited unexpectedly");

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.LogInformation("X-Ray sidecar started (PID {Pid})", _process.Id);
    }

    public void Stop()
    {
        if (_process is null || _process.HasExited)
            return;

        _logger.LogInformation("Stopping X-Ray sidecar (PID {Pid})", _process.Id);
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping sidecar process");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                probe?.WaitForExit(2000);
                if (probe?.ExitCode == 0)
                    return candidate;
            }
            catch
            {
                // not found, try next
            }
        }

        return null;
    }
}
