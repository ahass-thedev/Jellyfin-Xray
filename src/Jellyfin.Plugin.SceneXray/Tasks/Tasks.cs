using System.Collections.Concurrent;
using Jellyfin.Plugin.SceneXray.Music;
using Jellyfin.Plugin.SceneXray.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SceneXray.Tasks;

/// <summary>Analyses every movie and episode that has no up-to-date X-Ray timeline, newest first.</summary>
public sealed class AnalyzeLibraryTask : IScheduledTask
{
    private readonly AnalysisService _service;
    private readonly AnalysisQueue _queue;

    public AnalyzeLibraryTask(AnalysisService service, AnalysisQueue queue)
    {
        _service = service;
        _queue = queue;
    }

    public string Name => "Analyse library";

    public string Key => "SceneXrayAnalyzeLibrary";

    public string Description => "Finds who appears in each scene of movies and episodes, and identifies music. Works through the backlog a few titles per night; new titles, and titles you start watching, are analysed straight away.";

    public string Category => "X-Ray";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Vision.NativeLibraries.IsSupported)
        {
            throw new PlatformNotSupportedException(Vision.NativeLibraries.UnsupportedReason);
        }

        var pending = _service.Pending();
        if (pending.Count == 0)
        {
            progress.Report(100);
            return;
        }

        // Progress counts the running title's own progress too: with thousands of titles pending,
        // counting only finished ones keeps the bar at 0% for hours.
        var batch = pending.Select(i => i.Id).ToHashSet();
        var finished = new ConcurrentDictionary<Guid, bool>();
        var jobs = pending.Select(async item =>
        {
            await _queue.Enqueue(item.Id, QueuePriority.Library).ConfigureAwait(false);
            finished[item.Id] = true;
        }).ToList();

        using var reg = cancellationToken.Register(_queue.CancelLibraryBatch);
        var all = Task.WhenAll(jobs);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
            double running = _queue.Running.Where(r => batch.Contains(r.Id) && !finished.ContainsKey(r.Id)).Sum(r => r.Progress / 100);
            progress.Report(TaskProgress.Batch(finished.Count, running, batch.Count));
        }

        await all.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(5).Ticks, // stop before morning
        },
    ];
}

/// <summary>Fingerprints the music library so songs can be recognised inside films.</summary>
public sealed class IndexMusicTask : IScheduledTask
{
    private readonly MusicService _music;

    public IndexMusicTask(MusicService music) => _music = music;

    public string Name => "Index music library";

    public string Key => "SceneXrayIndexMusic";

    public string Description => "Fingerprints new or changed songs in your music libraries so X-Ray can recognise them in films and shows.";

    public string Category => "X-Ray";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        Plugin.Instance!.Configuration.MusicEnabled
            ? _music.IndexLibraryAsync(Plugin.Instance.Configuration, progress, cancellationToken)
            : Task.CompletedTask;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks,
        },
    ];
}
