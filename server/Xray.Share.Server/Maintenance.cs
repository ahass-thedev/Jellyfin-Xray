using Dapper;
using Microsoft.Extensions.Options;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>
/// The hourly job: things that change with time rather than with a submission.
/// </summary>
public sealed class MaintenanceService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly Db _db;
    private readonly LifecycleService _lifecycle;
    private readonly ShareOptions _o;

    public MaintenanceService(Db db, LifecycleService lifecycle, IOptions<ShareOptions> o)
    {
        _db = db;
        _lifecycle = lifecycle;
        _o = o.Value;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Installs that have become old enough to vote: their variants' consensus may move.
        var aged = await conn.QueryAsync<(Guid VariantId, int Version)>(
            """
            with newly as (
              update installs set voting_since = now()
              where voting_since is null and created_at <= now() - make_interval(days => @days)
              returning id
            )
            select distinct s.variant_id, s.version from submissions s join newly n on n.id = s.install_id
            where s.status in ('live', 'quarantined')
            """,
            new { days = _o.VotingAgeDays },
            tx).ConfigureAwait(false);
        foreach (var (v, ver) in aged)
        {
            await JobQueue.EnqueueAsync(conn, tx, JobQueue.Consensus, JobQueue.ConsensusRef(v, ver)).ConfigureAwait(false);
        }

        // Older analyser versions go 90 days after a newer one is confirmed (design §4).
        await conn.ExecuteAsync(
            """
            with newer as (
              select variant_id, min(version) as version from consensus
              where tier = 'confirmed' and confirmed_at < now() - make_interval(days => @days)
              group by variant_id
            ), gone as (
              delete from submissions s using newer n
              where s.variant_id = n.variant_id and s.version < n.version
              returning s.variant_id
            )
            delete from consensus c using newer n where c.variant_id = n.variant_id and c.version < n.version
            """,
            new { days = _o.SupersededRetentionDays },
            tx).ConfigureAwait(false);

        await RunMoreAsync(conn, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        // Credit expiry and inactive installs (design §1).
        await _lifecycle.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Retention rules.</summary>
    private static async Task RunMoreAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, CancellationToken ct)
    {
        // Recording ids MusicBrainz could not be asked about yet.
        await JobQueue.EnqueueAsync(conn, tx, JobQueue.MusicBrainz, JobQueue.AllRef).ConfigureAwait(false);

        // Unresolved unknown-face embeddings are deleted after 12 months (design D13).
        await conn.ExecuteAsync(
            "update unknown_faces set embedding = null where embedding is not null and person is null and created_at < now() - interval '12 months'",
            transaction: tx).ConfigureAwait(false);
    }
}
