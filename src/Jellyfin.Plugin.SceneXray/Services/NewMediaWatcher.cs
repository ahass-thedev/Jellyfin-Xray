using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.Services;

/// <summary>
/// Queues titles as they are added to the library, so new media has X-Ray before anyone watches
/// it instead of waiting its turn in the nightly backlog. A new title first waits for its
/// metadata, because the credited cast is what names the faces: it is queued on its first
/// metadata refresh, or after <see cref="SettleTime"/> if none comes.
/// </summary>
public sealed class NewMediaWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(15);
    private readonly ILibraryManager _library;
    private readonly AnalysisQueue _queue;
    private readonly AnalysisService _service;
    private readonly ILogger<NewMediaWatcher> _log;
    private readonly ConcurrentDictionary<Guid, DateTime> _waiting = new();
    private Timer? _timer;

    public NewMediaWatcher(ILibraryManager library, AnalysisQueue queue, AnalysisService service, ILogger<NewMediaWatcher> log)
    {
        _library = library;
        _queue = queue;
        _service = service;
        _log = log;
    }

    private static bool Enabled => Plugin.Instance?.Configuration.AnalyzeNewMedia == true && Vision.NativeLibraries.IsSupported;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _library.ItemUpdated += OnItemUpdated;
        _timer = new Timer(_ => QueueSettled(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemUpdated -= OnItemUpdated;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (Enabled && AnalysisService.IsEligible(e.Item) && _service.InScope(e.Item))
        {
            _waiting[e.Item.Id] = DateTime.UtcNow;
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if ((e.UpdateReason & (ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataImport)) != 0
            && _waiting.TryRemove(e.Item.Id, out _))
        {
            Queue(e.Item.Id);
        }
    }

    private void QueueSettled()
    {
        var cutoff = DateTime.UtcNow - SettleTime;
        foreach (var (id, added) in _waiting)
        {
            if (added < cutoff && _waiting.TryRemove(id, out _))
            {
                Queue(id);
            }
        }
    }

    private void Queue(Guid id)
    {
        // Settings can change while a title waits, so check again.
        if (!Enabled || _library.GetItemById(id) is not { } item || !AnalysisService.IsEligible(item) || _service.IsCurrent(item))
        {
            return;
        }

        _log.LogInformation("X-Ray: {Name} was added to the library; queued for analysis", item.Name);
        _ = _queue.Enqueue(id, QueuePriority.NewMedia);
    }
}
