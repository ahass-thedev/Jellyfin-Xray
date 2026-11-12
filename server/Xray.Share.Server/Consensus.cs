using System.Data;
using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Microsoft.Extensions.Options;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>
/// Recomputes the consensus of one (variant, analyser version) pair, as a queued job (design §4):
/// merges the votes, scores every submission, releases or claws back held credit, then
/// recomputes the trust of every install involved. The revision moves only when the served body
/// changes.
/// </summary>
public sealed class ConsensusService
{
    /// <summary>Quarantine below this trust (design §4)…</summary>
    public const double QuarantineBelow = 0.3;

    /// <summary>…and leave it again at this, so a borderline install does not flap.</summary>
    public const double RecoverAt = 0.4;

    private readonly Db _db;
    private readonly ShareOptions _o;

    public ConsensusService(Db db, IOptions<ShareOptions> o)
    {
        _db = db;
        _o = o.Value;
    }

    internal sealed class Row
    {
        public Guid Id { get; set; }

        public Guid? InstallId { get; set; }

        public Guid InviteId { get; set; }

        public string Status { get; set; } = string.Empty;

        public double ValidFrom { get; set; }

        public double ValidTo { get; set; }

        public byte[] Canonical { get; set; } = [];

        public string CreditState { get; set; } = string.Empty;

        public int Earned { get; set; }

        public int Held { get; set; }

        public double? Trust { get; set; }

        public DateTime? InstallCreated { get; set; }

        public double? AnonTrust { get; set; }

        public string ModelPack { get; set; } = string.Empty;
    }

    public async Task RecomputeAsync(Guid variantId, int version, CancellationToken ct)
    {
        List<Guid> installs;
        await using (var conn = await _db.OpenAsync(ct).ConfigureAwait(false))
        await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            double? runtime = await conn.QueryFirstOrDefaultAsync<double?>(
                "select runtime from variants where id = @variantId for update", new { variantId }, tx).ConfigureAwait(false);
            if (runtime is null)
            {
                return;
            }

            var (rows, ballots) = await LoadAsync(conn, tx, variantId, version).ConfigureAwait(false);
            var verified = await VerifiedMbidsAsync(conn, tx, ballots).ConfigureAwait(false);
            var result = Merge.Run(variantId, version, runtime.Value, ballots, new MergeOptions { ConfirmedVoices = _o.ConfirmedVoices }, m => verified.GetValueOrDefault(m));

            foreach (var r in rows)
            {
                if (result.Scores.TryGetValue(r.Id, out var s))
                {
                    await ApplyScoreAsync(conn, tx, r, s).ConfigureAwait(false);
                }
            }

            await WriteAsync(conn, tx, variantId, version, result.Consensus).ConfigureAwait(false);

            // A moved consensus can name unknown faces (design §2, rule 1).
            await JobQueue.EnqueueAsync(conn, tx, JobQueue.Unknowns, JobQueue.AllRef).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            installs = rows.Where(r => r.InstallId is not null).Select(r => r.InstallId!.Value).Distinct().ToList();
        }

        await RecomputeTrustAsync(installs, ct).ConfigureAwait(false);
    }

    /// <summary>The live and quarantined submissions of a (variant, version) as ballots.</summary>
    internal async Task<(List<Row> Rows, List<Ballot> Ballots)> LoadAsync(IDbConnection conn, IDbTransaction? tx, Guid variantId, int version)
    {
        var rows = (await conn.QueryAsync<Row>(
            """
            select s.id, s.install_id, s.invite_id, s.status, s.valid_from, s.valid_to, s.canonical, s.credit_state, s.earned, s.held,
                   s.anon_trust, s.model_pack, i.trust, i.created_at as install_created
            from submissions s left join installs i on i.id = s.install_id
            where s.variant_id = @variantId and s.version = @version and s.status in ('live', 'quarantined')
            order by s.created_at
            """,
            new { variantId, version },
            tx).ConfigureAwait(false)).ToList();

        var votingSince = DateTime.UtcNow - TimeSpan.FromDays(_o.VotingAgeDays);
        var ballots = rows.Select(r => new Ballot(
            r.Id,
            r.InstallId,
            r.InviteId,
            r.Status == SubmissionStatus.Live ? (r.InstallId is null ? r.AnonTrust ?? 0.5 : r.Trust ?? 0.5) * _o.ModelPackWeight(r.ModelPack) : 0,
            r.InstallId is null || r.InstallCreated?.ToUniversalTime() <= votingSince,
            r.ValidFrom,
            r.ValidTo,
            Gz.Unpack<CanonicalTimeline>(r.Canonical))).ToList();
        return (rows, ballots);
    }

    /// <summary>Recording ids among the ballots' cues that MusicBrainz confirmed.</summary>
    private static Task<Dictionary<string, MusicBrainzRecording>> VerifiedMbidsAsync(IDbConnection conn, IDbTransaction tx, IReadOnlyList<Ballot> ballots) =>
        MusicBrainzService.VerifiedAsync(conn, tx, ballots.SelectMany(b => b.Timeline.Music).Select(m => m.Mbid).OfType<string>());

    /// <summary>
    /// Stores the score and moves held credit, once: released when an independent voice agrees,
    /// cancelled and clawed back when the submission is outvoted (design §1, §4).
    /// </summary>
    private static async Task ApplyScoreAsync(IDbConnection conn, IDbTransaction tx, Row r, BallotScore s)
    {
        await conn.ExecuteAsync(
            "update submissions set agreement = @Agreement, corroborated = @Corroborated, outlier = @Outlier where id = @Id",
            new { s.Agreement, s.Corroborated, s.Outlier, r.Id },
            tx).ConfigureAwait(false);
        if (r.InstallId is not { } install)
        {
            return;
        }

        if (s.Outlier && r.CreditState != "clawed" && r.Earned + r.Held > 0)
        {
            if (r.CreditState == "pending")
            {
                await Ledger.AddAsync(conn, tx, install, Ledger.Cancel, -r.Held, true, r.Id).ConfigureAwait(false);
                await Ledger.AddAsync(conn, tx, install, Ledger.Clawback, -r.Earned, false, r.Id).ConfigureAwait(false);
            }
            else
            {
                await Ledger.AddAsync(conn, tx, install, Ledger.Clawback, -(r.Earned + r.Held), false, r.Id).ConfigureAwait(false);
            }

            await conn.ExecuteAsync("update submissions set credit_state = 'clawed' where id = @Id", new { r.Id }, tx).ConfigureAwait(false);
        }
        else if (s.Corroborated && !s.Outlier && r.CreditState == "pending" && r.Status == SubmissionStatus.Live && r.Held > 0)
        {
            await Ledger.AddAsync(conn, tx, install, Ledger.Release, -r.Held, true, r.Id).ConfigureAwait(false);
            await Ledger.AddAsync(conn, tx, install, Ledger.Release, r.Held, false, r.Id).ConfigureAwait(false);
            await conn.ExecuteAsync("update submissions set credit_state = 'released' where id = @Id", new { r.Id }, tx).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(IDbConnection conn, IDbTransaction tx, Guid variantId, int version, ConsensusTimeline? body)
    {
        if (body is null)
        {
            await conn.ExecuteAsync("delete from consensus where variant_id = @variantId and version = @version", new { variantId, version }, tx).ConfigureAwait(false);
            return;
        }

        await conn.ExecuteAsync(
            """
            insert into consensus (variant_id, version, revision, tier, voices, body, body_hash, confirmed_at)
            values (@variantId, @version, nextval('consensus_revision'), @Tier, @Voices, @packed, @hash,
                    case when @Tier = 'confirmed' then now() end)
            on conflict (variant_id, version) do update
              set revision = nextval('consensus_revision'), tier = excluded.tier, voices = excluded.voices,
                  body = excluded.body, body_hash = excluded.body_hash, updated_at = now(),
                  confirmed_at = coalesce(consensus.confirmed_at, excluded.confirmed_at)
              where consensus.body_hash <> excluded.body_hash
            """,
            new { variantId, version, body.Tier, body.Voices, packed = Gz.Pack(body), hash = Gz.Hash(body) },
            tx).ConfigureAwait(false);
    }

    /// <summary>
    /// Trust = (corroborated + 1) / (corroborated + outliers + 2): 0.5 to start (design §4).
    /// Below 0.3 an install is quarantined: its submissions keep being scored but carry no weight
    /// and earn nothing, and it cannot fetch. Recovery is automatic.
    /// </summary>
    public async Task RecomputeTrustAsync(IReadOnlyList<Guid> installs, CancellationToken ct)
    {
        if (installs.Count == 0)
        {
            return;
        }

        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var changed = await conn.QueryAsync<(Guid Id, string State)>(
            """
            with counts as (
              select i.id,
                     count(s.id) filter (where s.corroborated and not s.outlier) as good,
                     count(s.id) filter (where s.outlier) as bad
              from installs i left join submissions s on s.install_id = i.id and s.status <> 'replaced'
              where i.id = any(@ids)
              group by i.id
            ), next as (
              select id, (good + 1.0) / (good + bad + 2.0) as trust from counts
            )
            update installs i set trust = n.trust,
                   state = case when n.trust < @low then 'quarantined' when n.trust >= @high then 'member' else i.state end
            from next n where i.id = n.id
            returning i.id, i.state
            """,
            new { ids = installs.ToArray(), low = QuarantineBelow, high = RecoverAt },
            tx).ConfigureAwait(false);

        foreach (var (id, state) in changed)
        {
            string from = state == InstallState.Quarantined ? SubmissionStatus.Live : SubmissionStatus.Quarantined;
            var moved = await conn.QueryAsync<(Guid VariantId, int Version)>(
                "update submissions set status = @state2 where install_id = @id and status = @from returning variant_id, version",
                new { id, from, state2 = state == InstallState.Quarantined ? SubmissionStatus.Quarantined : SubmissionStatus.Live },
                tx).ConfigureAwait(false);
            foreach (var (v, ver) in moved.Distinct())
            {
                await JobQueue.EnqueueAsync(conn, tx, JobQueue.Consensus, JobQueue.ConsensusRef(v, ver)).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
