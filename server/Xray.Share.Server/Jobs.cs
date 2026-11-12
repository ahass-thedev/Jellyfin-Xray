using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>
/// The work queue in the <c>jobs</c> table (design §6): consensus is recomputed by a queued job,
/// never inside a request. One row per (kind, ref), so a burst of submissions to one variant
/// costs one recomputation. Taken with <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
public sealed class JobQueue
{
    public const string Consensus = "consensus";
    public const string Maintenance = "maintenance";
    public const string Unknowns = "unknowns";
    public const string MusicBrainz = "musicbrainz";

    /// <summary>The ref of a job that runs over everything.</summary>
    public const string AllRef = "all";

    private readonly Db _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<JobQueue> _log;

    public JobQueue(Db db, IServiceProvider services, ILogger<JobQueue> log)
    {
        _db = db;
        _services = services;
        _log = log;
    }

    public static Task EnqueueAsync(IDbConnection conn, IDbTransaction? tx, string kind, string reference, TimeSpan? delay = null) =>
        conn.ExecuteAsync(
            """
            insert into jobs (kind, ref, run_after) values (@kind, @reference, now() + @delay)
            on conflict (kind, ref) do update set run_after = least(jobs.run_after, excluded.run_after)
            """,
            new { kind, reference, delay = delay ?? TimeSpan.Zero },
            tx);

    /// <summary>A taken job is leased for this long; a crashed worker's job runs again after it.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs one due job. False when none is due. The job is leased (its run_after pushed out) in a
    /// short transaction rather than kept locked, so the job itself may enqueue work, even its own
    /// kind and ref: that moves run_after back, and the job then stays queued for another run.
    /// </summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        var job = await conn.QueryFirstOrDefaultAsync<(long Id, string Kind, string Ref, int Attempts, DateTime Leased)>(
            """
            update jobs set run_after = now() + @Lease, attempts = attempts + 1
            where id = (select id from jobs where run_after <= now() order by run_after, id limit 1 for update skip locked)
            returning id, kind, ref, attempts, run_after
            """,
            new { Lease }).ConfigureAwait(false);
        if (job.Kind is null)
        {
            return false;
        }

        try
        {
            await DispatchAsync(job.Kind, job.Ref, ct).ConfigureAwait(false);
            await conn.ExecuteAsync(
                job.Kind == Maintenance
                    ? "update jobs set run_after = now() + @every, attempts = 0, last_error = null where id = @Id and run_after = @Leased"
                    : "delete from jobs where id = @Id and run_after = @Leased",
                new { job.Id, job.Leased, every = MaintenanceService.Every }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Job {Kind} {Ref} failed", job.Kind, job.Ref);
            var backoff = TimeSpan.FromSeconds(Math.Min(3600, 10 * Math.Pow(2, job.Attempts)));
            await conn.ExecuteAsync(
                "update jobs set last_error = @error, run_after = now() + @backoff where id = @Id and run_after = @Leased",
                new { job.Id, job.Leased, error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, backoff }).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Runs every due job (tests, and the CLI).</summary>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        int n = 0;
        while (n < 10_000 && await RunOnceAsync(ct).ConfigureAwait(false))
        {
            n++;
        }

        return n;
    }

    private Task DispatchAsync(string kind, string reference, CancellationToken ct)
    {
        switch (kind)
        {
            case Consensus:
                var parts = reference.Split(':');
                return _services.GetRequiredService<ConsensusService>().RecomputeAsync(Guid.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), ct);
            case MusicBrainz:
                return _services.GetRequiredService<MusicBrainzService>().RunAsync(ct);
            case Unknowns:
                return _services.GetRequiredService<UnknownFaceService>().RunAsync(ct);
            case Maintenance:
                return _services.GetRequiredService<MaintenanceService>().RunAsync(ct);
            default:
                throw new InvalidOperationException("Unknown job kind " + kind);
        }
    }

    public static string ConsensusRef(Guid variantId, int version) => FormattableString.Invariant($"{variantId}:{version}");
}

/// <summary>Runs the queue in the background.</summary>
public sealed class JobWorker : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly Db _db;
    private readonly ShareOptions _o;
    private readonly ILogger<JobWorker> _log;

    public JobWorker(JobQueue queue, Db db, IOptions<ShareOptions> o, ILogger<JobWorker> log)
    {
        _queue = queue;
        _db = db;
        _o = o.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_o.Worker)
        {
            return;
        }

        await using (var conn = await _db.OpenAsync(stoppingToken).ConfigureAwait(false))
        {
            await JobQueue.EnqueueAsync(conn, null, JobQueue.Maintenance, "hourly").ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queue.DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Job queue failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Applies migrations before anything else starts.</summary>
public sealed class Migrator(Db db) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => db.MigrateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
