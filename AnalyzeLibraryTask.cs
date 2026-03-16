using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.ScheduledTasks;

/// <summary>
/// Scheduled task that analyses the full library for X-Ray data.
///
/// Appears in Jellyfin Dashboard → Scheduled Tasks as "X-Ray: Analyse Library".
/// Can be run manually, on a schedule, or triggered after a library scan.
/// </summary>
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

    // ------------------------------------------------------------------
    // IScheduledTask
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public string Name => "Analyse Library";

    /// <inheritdoc />
    public string Key => "XRayAnalyseLibrary";

    /// <inheritdoc />
    public string Description =>
        "Analyses trickplay images for all movies and episodes to generate X-Ray actor data. " +
        "Skips items that have already been analysed.";

    /// <inheritdoc />
    public string Category => "X-Ray";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var itemIds = _metadata.GetAllAnalysableItemIds();
        if (itemIds.Count == 0)
        {
            _logger.LogInformation("No analysable items found in library");
            return;
        }

        _logger.LogInformation("X-Ray library analysis starting — {Count} items", itemIds.Count);

        // Verify sidecar is reachable before queueing all items
        if (!await _sidecarClient.PingAsync(ct).ConfigureAwait(false))
        {
            _logger.LogError(
                "X-Ray sidecar is not reachable. Check that it is running and that " +
                "SidecarUrl is correct in plugin settings.");
            return;
        }

        int completed = 0;
        foreach (var itemId in itemIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await _xrayService.AnalyzeAsync(itemId, force: false, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyse item {ItemId}", itemId);
            }

            completed++;
            progress.Report((double)completed / itemIds.Count * 100);
        }

        _logger.LogInformation("X-Ray library analysis complete — {Count} items processed", completed);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run once a day at 3am by default — after most library scans are likely done
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }
}
