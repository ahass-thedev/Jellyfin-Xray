using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.ScheduledTasks;

public class AnalyzeLibraryTask : IScheduledTask
{
    private readonly MetadataService _metadata;
    private readonly XRayService _xrayService;
    private readonly SidecarClient _sidecarClient;
    private readonly ILogger<AnalyzeLibraryTask> _logger;

    public AnalyzeLibraryTask(
        MetadataService metadata,
        XRayService xrayService,
        SidecarClient sidecarClient,
        ILogger<AnalyzeLibraryTask> logger)
    {
        _metadata = metadata;
        _xrayService = xrayService;
        _sidecarClient = sidecarClient;
        _logger = logger;
    }

    public string Name => "Analyse Library";
    public string Key => "XRayAnalyseLibrary";
    public string Description => "Analyses trickplay images for all movies and episodes to generate X-Ray actor data.";
    public string Category => "X-Ray";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (!await _sidecarClient.PingAsync(ct).ConfigureAwait(false))
        {
            _logger.LogError("X-Ray sidecar is not reachable at {Url}", Plugin.Instance?.Configuration.SidecarUrl);
            return;
        }

        var itemIds = _metadata.GetAllAnalysableItemIds();
        if (itemIds.Count == 0)
        {
            _logger.LogInformation("No analysable items found");
            return;
        }

        _logger.LogInformation("X-Ray analysis starting — {Count} items", itemIds.Count);

        int completed = 0;
        foreach (var itemId in itemIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _xrayService.AnalyzeAsync(itemId, force: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Failed to analyse {ItemId}", itemId); }

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
