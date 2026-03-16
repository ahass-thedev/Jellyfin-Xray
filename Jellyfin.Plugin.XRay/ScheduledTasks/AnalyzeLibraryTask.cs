using MediaBrowser.Model.Tasks;
using Jellyfin.Plugin.XRay.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.ScheduledTasks;

/// <summary>
/// Scheduled task — appears in Jellyfin Dashboard → Scheduled Tasks as "X-Ray: Analyse Library".
/// </summary>
public class AnalyzeLibraryTask : IScheduledTask
{
    private readonly ILogger<AnalyzeLibraryTask> _logger;

    public AnalyzeLibraryTask(ILogger<AnalyzeLibraryTask> logger)
    {
        _logger = logger;
    }

    public string Name => "Analyse Library";
    public string Key => "XRayAnalyseLibrary";
    public string Description =>
        "Analyses trickplay images for all movies and episodes to generate X-Ray actor data. " +
        "Skips items that have already been analysed.";
    public string Category => "X-Ray";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogError("Plugin instance not available");
            return;
        }

        if (!await plugin.SidecarHttpClient.PingAsync(ct).ConfigureAwait(false))
        {
            _logger.LogError(
                "X-Ray sidecar is not reachable. Check that it is running and that " +
                "SidecarUrl is correct in plugin settings.");
            return;
        }

        var itemIds = plugin.Metadata.GetAllAnalysableItemIds();
        if (itemIds.Count == 0)
        {
            _logger.LogInformation("No analysable items found in library");
            return;
        }

        _logger.LogInformation("X-Ray library analysis starting — {Count} items", itemIds.Count);

        int completed = 0;
        foreach (var itemId in itemIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await plugin.XRay.AnalyzeAsync(itemId, force: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyse item {ItemId}", itemId);
            }

            completed++;
            progress.Report((double)completed / itemIds.Count * 100);
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
