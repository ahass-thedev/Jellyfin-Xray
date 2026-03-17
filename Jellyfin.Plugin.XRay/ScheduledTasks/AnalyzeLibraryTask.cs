using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.ScheduledTasks;

public class AnalyzeLibraryTask : IScheduledTask
{
    private readonly ILogger<AnalyzeLibraryTask> _logger;

    public AnalyzeLibraryTask(ILogger<AnalyzeLibraryTask> logger)
    {
        _logger = logger;
    }

    public string Name => "Analyse Library";
    public string Key => "XRayAnalyseLibrary";
    public string Description => "Analyses trickplay images for all movies and episodes to generate X-Ray actor data.";
    public string Category => "X-Ray";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) { _logger.LogError("Plugin not initialised"); return; }

        if (!await plugin.SidecarHttpClient.PingAsync(ct).ConfigureAwait(false))
        {
            _logger.LogError("X-Ray sidecar not reachable at {Url}", plugin.Configuration.SidecarUrl);
            return;
        }

        var itemIds = plugin.Metadata.GetAllAnalysableItemIds();
        _logger.LogInformation("X-Ray analysis starting — {Count} items", itemIds.Count);

        int completed = 0;
        foreach (var itemId in itemIds)
        {
            ct.ThrowIfCancellationRequested();
            try { await plugin.XRay.AnalyzeAsync(itemId, force: false, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Failed to analyse {ItemId}", itemId); }
            progress.Report((double)++completed / itemIds.Count * 100);
        }

        _logger.LogInformation("Analysis complete — {Count} items processed", completed);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }
}
