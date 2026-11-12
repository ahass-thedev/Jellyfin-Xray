using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.Share;

/// <summary>
/// Sends queued contributions in the background (design §5 step 3), retrying with backoff when
/// the service is unreachable. The queue lives on disk, so it survives restarts; turning sharing
/// off empties it.
/// </summary>
public sealed class ShareOutbox : BackgroundService
{
    private const int MaxAttempts = 12;
    private readonly SharingService _sharing;
    private readonly ILogger<ShareOutbox> _log;

    public ShareOutbox(SharingService sharing, ILogger<ShareOutbox> log)
    {
        _sharing = sharing;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextNames = DateTime.UtcNow.AddMinutes(5);
        var nextRefresh = DateTime.UtcNow.AddMinutes(10);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            if (!_sharing.Active)
            {
                continue;
            }

            // Newer consensus for received titles, once a day.
            if (DateTime.UtcNow >= nextRefresh)
            {
                nextRefresh = DateTime.UtcNow.AddHours(24);
                try
                {
                    await _sharing.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "X-Ray sharing: refresh failed");
                }
            }

            // Names for this server's unknown faces, a few times a day.
            if (DateTime.UtcNow >= nextNames)
            {
                nextNames = DateTime.UtcNow.AddHours(6);
                try
                {
                    await _sharing.SyncUnknownsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "X-Ray sharing: applying names for unknown faces failed");
                }
            }

            foreach (var e in _sharing.State.Outbox().Where(e => e.Due <= DateTime.UtcNow).OrderBy(e => e.Due))
            {
                if (!_sharing.Active || stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                SharingService.SubmitOutcome outcome;
                try
                {
                    outcome = await _sharing.SubmitAsync(e.ItemId, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "X-Ray sharing: submission of {Id} failed", e.ItemId);
                    outcome = SharingService.SubmitOutcome.Retry;
                }

                if (outcome == SharingService.SubmitOutcome.Done || e.Attempts + 1 >= MaxAttempts)
                {
                    _sharing.State.Dequeue(e.ItemId);
                }
                else
                {
                    var wait = TimeSpan.FromMinutes(Math.Min(24 * 60, 5 * Math.Pow(2, e.Attempts)));
                    _sharing.State.Enqueue(e.ItemId, DateTime.UtcNow + wait, e.Attempts + 1);
                }
            }
        }
    }
}
