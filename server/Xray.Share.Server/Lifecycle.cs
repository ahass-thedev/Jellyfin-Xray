using Dapper;
using Microsoft.Extensions.Options;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>Leaving, withdrawal, credit expiry and inactivity deletion (design §1, §5).</summary>
public sealed class LifecycleService
{
    private readonly Db _db;
    private readonly ShareOptions _o;

    public LifecycleService(Db db, IOptions<ShareOptions> o)
    {
        _db = db;
        _o = o.Value;
    }

    /// <summary>
    /// Deletes the install: its ledger, holdings, challenges and unknown faces go with it (the
    /// foreign keys cascade). Its submissions either stay as anonymous votes, keeping the trust
    /// they had (D7), or, with <paramref name="withdraw"/>, are deleted and their consensus
    /// recomputed.
    /// </summary>
    public async Task LeaveAsync(Guid installId, bool withdraw, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        double trust = await conn.ExecuteScalarAsync<double>("select trust from installs where id = @installId for update", new { installId }, tx).ConfigureAwait(false);
        var affected = (await conn.QueryAsync<(Guid VariantId, int Version)>(
            "select distinct variant_id, version from submissions where install_id = @installId and status <> 'replaced'",
            new { installId },
            tx).ConfigureAwait(false)).ToList();
        if (withdraw)
        {
            await conn.ExecuteAsync("delete from submissions where install_id = @installId", new { installId }, tx).ConfigureAwait(false);
        }
        else
        {
            await conn.ExecuteAsync("delete from submissions where install_id = @installId and status = 'replaced'", new { installId }, tx).ConfigureAwait(false);
            await conn.ExecuteAsync("update submissions set anon_trust = @trust where install_id = @installId", new { trust, installId }, tx).ConfigureAwait(false);
        }

        await conn.ExecuteAsync("delete from installs where id = @installId", new { installId }, tx).ConfigureAwait(false);
        foreach (var (v, ver) in affected)
        {
            await JobQueue.EnqueueAsync(conn, tx, JobQueue.Consensus, JobQueue.ConsensusRef(v, ver)).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The hourly retention pass: credit expiry and inactive installs.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await ExpireCreditAsync(ct).ConfigureAwait(false);

        // An install silent for 12 months is deleted, as if it had left (design §1).
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        var silent = await conn.QueryAsync<Guid>(
            "select id from installs where last_seen_at < now() - make_interval(days => @days)", new { days = _o.InactiveDays }).ConfigureAwait(false);
        foreach (var id in silent)
        {
            await LeaveAsync(id, withdraw: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Credit older than 12 months expires (design §1), oldest first: what was received a year
    /// ago and not spent since is written off. Held credit still undecided after a year is
    /// cancelled too.
    /// </summary>
    public async Task ExpireCreditAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            """
            with cutoff as (select now() - make_interval(days => @days) as at),
            per as (
              select l.install_id,
                     coalesce(sum(l.minutes) filter (where not l.held and l.minutes > 0 and l.kind <> 'expiry' and l.at < (select at from cutoff)
                                                    and (l.submission_id is null or s.credit_state = 'released')), 0) as old_credit,
                     coalesce(-sum(l.minutes) filter (where not l.held and l.kind = 'fetch'), 0) as spent,
                     coalesce(-sum(l.minutes) filter (where l.kind = 'expiry' and not l.held), 0) as expired
              from ledger l left join submissions s on s.id = l.submission_id
              group by l.install_id
            )
            insert into ledger (install_id, kind, minutes, held)
            select install_id, 'expiry', -(old_credit - spent - expired), false from per
            where old_credit - spent - expired > 0
            """,
            new { days = _o.CreditExpiryDays },
            tx).ConfigureAwait(false);

        var stale = await conn.QueryAsync<(Guid Id, Guid InstallId, int Held)>(
            """
            update submissions set credit_state = 'expired'
            where credit_state = 'pending' and held > 0 and install_id is not null and created_at < now() - make_interval(days => @days)
            returning id, install_id, held
            """,
            new { days = _o.CreditExpiryDays },
            tx).ConfigureAwait(false);
        foreach (var (id, install, held) in stale)
        {
            await Ledger.AddAsync(conn, tx, install, "expiry", -held, true, id).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
