using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Share;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.Services;

/// <summary>Why a title was queued; lower runs first.</summary>
public enum QueuePriority
{
    /// <summary>Someone just pressed play on it.</summary>
    Playback = 0,

    /// <summary>An admin asked for it.</summary>
    Manual = 1,

    /// <summary>Just added to the library.</summary>
    NewMedia = 2,

    /// <summary>The nightly library task.</summary>
    Library = 3,
}

/// <summary>State of one title, for the API.</summary>
public sealed record QueueStatus(string State, double Progress, int Position, string? Error);

/// <summary>
/// Priority queue with up to <see cref="PluginConfiguration.MaxConcurrentAnalyses"/> workers: a
/// title someone starts watching jumps ahead of new media, and new media ahead of the nightly
/// backlog. The library task enqueues its batch and waits on it, so cancelling the task (or
/// hitting its time limit) drops the batch without touching on-demand work.
/// </summary>
public sealed class AnalysisQueue : BackgroundService
{
    private readonly AnalysisService _service;
    private readonly SharingService _sharing;
    private readonly ILogger<AnalysisQueue> _log;
    private readonly object _lock = new();
    private readonly PriorityQueue<Guid, (QueuePriority, long)> _queue = new();
    private readonly Dictionary<Guid, QueuePriority> _queued = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _waiters = [];
    private readonly Dictionary<Guid, string> _failed = [];
    private readonly Dictionary<Guid, Run> _running = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _slotFreed = new(0);
    private long _seq;

    public AnalysisQueue(AnalysisService service, SharingService sharing, ILogger<AnalysisQueue> log)
    {
        _service = service;
        _sharing = sharing;
        _log = log;
    }

    /// <summary>How many titles may run at once: the setting, kept to 1-4.</summary>
    public static int Slots(PluginConfiguration cfg) => Math.Clamp(cfg.MaxConcurrentAnalyses, 1, 4);

    private static int SlotCount => Slots(Plugin.Instance!.Configuration);

    /// <summary>
    /// The run to pause for a title being watched: none while a slot is free, otherwise the
    /// lowest-priority new-media or backlog run, the most recently started first (it has done least).
    /// </summary>
    public static T? Victim<T>(IReadOnlyCollection<T> running, int slots, Func<T, QueuePriority> priority, Func<T, long> started)
        where T : class =>
        running.Count < slots ? null : running
            .Where(r => priority(r) >= QueuePriority.NewMedia)
            .OrderByDescending(priority)
            .ThenByDescending(started)
            .FirstOrDefault();

    /// <summary>Queues a title (or raises its priority). The task completes when it is analysed (true) or fails/drops (false).</summary>
    public Task<bool> Enqueue(Guid itemId, QueuePriority priority)
    {
        lock (_lock)
        {
            if (!_waiters.TryGetValue(itemId, out var tcs))
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[itemId] = tcs;
            }

            if (_running.ContainsKey(itemId))
            {
                return tcs.Task;
            }

            if (!_queued.TryGetValue(itemId, out var existing) || priority < existing)
            {
                _queued[itemId] = priority;
                _queue.Enqueue(itemId, (priority, _seq++)); // a stale lower-priority copy is skipped later
                _failed.Remove(itemId);
                _signal.Release();
            }

            // Someone is waiting to watch this and every slot is busy: pause new media or the
            // nightly backlog (the interrupted title goes back in the queue and is redone later).
            if (priority == QueuePriority.Playback && Victim(_running.Values, SlotCount, r => r.Priority, r => r.Started) is { } victim)
            {
                victim.Preempted = true;
                victim.Cts.Cancel();
            }

            return tcs.Task;
        }
    }

    /// <summary>Drops every library-priority title, including the ones running.</summary>
    public void CancelLibraryBatch()
    {
        lock (_lock)
        {
            foreach (var id in _queued.Where(kv => kv.Value == QueuePriority.Library).Select(kv => kv.Key).ToList())
            {
                _queued.Remove(id);
                Complete(id, false);
            }

            foreach (var run in _running.Values.Where(r => r.Priority == QueuePriority.Library))
            {
                run.Cts.Cancel();
            }
        }
    }

    public QueueStatus Status(Guid itemId) =>
        Active(itemId) ?? new QueueStatus(_service.Store.Head(itemId) is null ? "none" : "ready", 100, 0, null);

    /// <summary>Where the title is in the queue: running, queued or failed; null when it is none of them.</summary>
    public QueueStatus? Active(Guid itemId)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(itemId, out var run))
            {
                return new QueueStatus("running", Math.Round(run.Progress, 1), 0, null);
            }

            if (_queued.TryGetValue(itemId, out var p))
            {
                int ahead = _queued.Count(kv => kv.Value < p) + _running.Count;
                return new QueueStatus("queued", 0, ahead, null);
            }

            if (_failed.TryGetValue(itemId, out var err))
            {
                return new QueueStatus("failed", 0, 0, err);
            }
        }

        return null;
    }

    /// <summary>Gets the titles being analysed and how far each is (0-100).</summary>
    public IReadOnlyList<(Guid Id, double Progress)> Running
    {
        get
        {
            lock (_lock)
            {
                return _running.Select(kv => (kv.Key, kv.Value.Progress)).ToList();
            }
        }
    }

    public int Length
    {
        get
        {
            lock (_lock)
            {
                return _queued.Count;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runs = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);

                // Wait for a free slot before taking a title, so the best one waiting is taken then
                // (and look again now and then, in case the setting was raised).
                while (RunningCount >= SlotCount)
                {
                    await _slotFreed.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                }

                Guid id;
                Run run;
                lock (_lock)
                {
                    if (!_queue.TryDequeue(out id, out var key) || !_queued.TryGetValue(id, out var priority) || priority != key.Item1)
                    {
                        continue; // stale entry (re-prioritised or cancelled)
                    }

                    _queued.Remove(id);
                    run = new Run(priority, _seq++, CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
                    _running[id] = run;
                }

                runs.RemoveAll(t => t.IsCompleted);
                runs.Add(RunAsync(id, run, stoppingToken));
            }
        }
        finally
        {
            await Task.WhenAll(runs).ConfigureAwait(false);
        }
    }

    private int RunningCount
    {
        get
        {
            lock (_lock)
            {
                return _running.Count;
            }
        }
    }

    private async Task RunAsync(Guid id, Run run, CancellationToken stoppingToken)
    {
        await Task.Yield(); // the dispatcher goes back to the queue straight away
        bool ok = false, requeue = false;
        try
        {
            // With sharing on, another server's result may make the analysis unnecessary; any
            // failure there falls through to local analysis, whose result is then offered.
            if (!await _sharing.TryReceiveAsync(id, run.Priority, run.Cts.Token).ConfigureAwait(false))
            {
                await _service.AnalyzeAsync(id, new Progress<double>(p => run.Progress = p), run.Cts.Token).ConfigureAwait(false);
                _sharing.Offer(id);
            }

            ok = true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                requeue = run.Preempted;
            }

            _log.LogInformation(requeue ? "X-Ray: paused {Id} for a title being watched" : "X-Ray: analysis of {Id} cancelled", id);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "X-Ray: analysis of {Id} failed", id);
            lock (_lock)
            {
                _failed[id] = ex.Message;
            }
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(id);
                if (requeue && !_queued.ContainsKey(id))
                {
                    _queued[id] = run.Priority;
                    _queue.Enqueue(id, (run.Priority, _seq++));
                    _signal.Release();
                }
                else
                {
                    Complete(id, ok);
                }
            }

            run.Cts.Dispose();
            _slotFreed.Release();
        }
    }

    private void Complete(Guid id, bool ok)
    {
        if (_waiters.Remove(id, out var tcs))
        {
            tcs.TrySetResult(ok);
        }
    }

    /// <summary>One title being analysed.</summary>
    private sealed class Run(QueuePriority priority, long started, CancellationTokenSource cts)
    {
        public QueuePriority Priority { get; } = priority;

        public long Started { get; } = started;

        public CancellationTokenSource Cts { get; } = cts;

        public double Progress { get; set; }

        public bool Preempted { get; set; }
    }
}
