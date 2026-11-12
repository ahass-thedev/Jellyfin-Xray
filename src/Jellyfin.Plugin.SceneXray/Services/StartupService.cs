using System.Runtime.Loader;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SceneXray.Services;

/// <summary>
/// Wires X-Ray into the running server: queues a title when someone starts watching it, and
/// adds the player overlay to jellyfin-web through the File Transformation plugin (no files
/// on disk are modified).
/// </summary>
public sealed class StartupService : IHostedService
{
    private static readonly Guid TransformationId = Guid.Parse("5b3f1e9a-7c42-4d1b-9a60-2f8e4c7d3a12");
    private readonly ISessionManager _sessions;
    private readonly AnalysisQueue _queue;
    private readonly AnalysisService _service;
    private readonly ILogger<StartupService> _log;
    private CancellationTokenSource? _cts;

    public StartupService(ISessionManager sessions, AnalysisQueue queue, AnalysisService service, ILogger<StartupService> log)
    {
        _sessions = sessions;
        _queue = queue;
        _service = service;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Vision.NativeLibraries.IsSupported)
        {
            _log.LogWarning("X-Ray: {Reason} Existing timelines and the player overlay still work.", Vision.NativeLibraries.UnsupportedReason);
        }

        _sessions.PlaybackStart += OnPlaybackStart;
        _cts = new CancellationTokenSource();
        _ = RegisterOverlayAsync(_cts.Token);
        _ = WarmAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (Plugin.Instance?.Configuration.AnalyzeOnPlayback != true || !Vision.NativeLibraries.IsSupported
            || !AnalysisService.IsEligible(e.Item) || _service.IsCurrent(e.Item))
        {
            return;
        }

        _log.LogInformation("X-Ray: {Name} started playing without a timeline; analysing it now", e.Item.Name);
        _ = _queue.Enqueue(e.Item.Id, QueuePriority.Playback);
    }

    /// <summary>Looks at every title's file once in the background, so the titles page and the counts open quickly.</summary>
    private async Task WarmAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = await Task.Run(() => _service.Titles().Count, ct).ConfigureAwait(false);
            _log.LogInformation("X-Ray: looked at {Count} titles' files in {Seconds:F0} s", n, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "X-Ray: could not look at the library's files");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Gets a value indicating whether the File Transformation plugin is loaded (plugins load before services are registered).</summary>
    public static bool FileTransformationInstalled =>
        AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
            .Any(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation");

    /// <summary>Called by File Transformation (or X-Ray's bundled copy) for index.html.</summary>
    public static string InjectOverlay(PatchPayload payload)
    {
        string tag = $"<script defer src=\"../SceneXray/overlay.js?v={typeof(StartupService).Assembly.GetName().Version}\"></script>";
        var html = payload.Contents ?? string.Empty;
        if (html.Contains("SceneXray/overlay.js", StringComparison.Ordinal))
        {
            return html;
        }

        // Only ever touch an HTML document; anything else passes through unchanged.
        int head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? html : html.Insert(head, tag);
    }

    private async Task RegisterOverlayAsync(CancellationToken ct)
    {
        if (!FileTransformationInstalled)
        {
            return; // the bundled copy is serving the overlay
        }

        // Plugins load in no particular order; give File Transformation a minute to appear.
        for (int attempt = 0; attempt < 12 && !ct.IsCancellationRequested; attempt++)
        {
            var ft = AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);
            var register = ft?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?.GetMethod("RegisterTransformation");
            if (register is not null)
            {
                var payload = new JObject
                {
                    ["id"] = TransformationId.ToString(),
                    // Must be exactly "index.html": File Transformation runs the exact-match pipeline
                    // (shared with other plugins) before any regex. As a regex the same string also
                    // matches chunks like "session-login-index-html.<hash>.chunk.js", so the callback
                    // only ever edits a real HTML document (see InjectOverlay).
                    ["fileNamePattern"] = "index.html",
                    ["callbackAssembly"] = typeof(StartupService).Assembly.FullName,
                    ["callbackClass"] = typeof(StartupService).FullName,
                    ["callbackMethod"] = nameof(InjectOverlay),
                };
                try
                {
                    register.Invoke(null, [payload]);
                    _log.LogInformation("X-Ray: player overlay registered with File Transformation");
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "X-Ray: File Transformation rejected the overlay");
                    return;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _log.LogWarning("X-Ray: File Transformation is installed but did not accept the overlay registration.");
    }

    /// <summary>Shape of File Transformation's callback argument.</summary>
    public sealed class PatchPayload
    {
        public string? Contents { get; set; }
    }
}
