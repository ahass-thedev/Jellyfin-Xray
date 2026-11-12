using System.Data;
using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Microsoft.Extensions.Options;
using Npgsql;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>Submission statuses.</summary>
public static class SubmissionStatus
{
    /// <summary>Votes and earns.</summary>
    public const string Live = "live";

    /// <summary>The install had fetched this title first (copy-back, design §1): stored, zero votes, zero credit.</summary>
    public const string Derived = "derived";

    /// <summary>A later submission from the same install for the same variant took its place.</summary>
    public const string Replaced = "replaced";

    /// <summary>From a quarantined install: stored, zero weight, no credit.</summary>
    public const string Quarantined = "quarantined";
}

/// <summary>A rejection the plugin can act on (design §4: "rejected with a reason").</summary>
public sealed class RejectedException(string reason) : Exception(reason);

/// <summary>POST /v1/submissions: checks, stores, credits and queues consensus for one contribution.</summary>
public sealed class SubmissionService
{
    private readonly Db _db;
    private readonly ICreditSource _credits;
    private readonly ShareOptions _o;

    public SubmissionService(Db db, ICreditSource credits, IOptions<ShareOptions> o)
    {
        _db = db;
        _credits = credits;
        _o = o.Value;
    }

    public async Task<SubmissionResult> SubmitAsync(Install me, SubmissionRequest req, CancellationToken ct)
    {
        var c = req.Contribution ?? throw new RejectedException("missing contribution");
        if (Validation.Check(c, _o.MinAnalyzerVersion) is { } problem)
        {
            throw new RejectedException(problem);
        }

        c = Validation.Sanitized(c);
        string key = TitleKey.Of(c.Title)!;
        int version = Validation.MajorVersion(c.Analyzer.Version)!.Value;

        // People must be TMDb ids from the title's own credits (series regulars count for episodes).
        var cast = await _credits.CastAsync(key, c.Title, ct).ConfigureAwait(false) ?? throw new RejectedException("title not found on TMDb");
        if (c.People.Concat(c.VoicePeople).FirstOrDefault(p => !cast.Allows(p.TmdbPerson)) is { } stranger)
        {
            throw new RejectedException($"person {stranger.TmdbPerson} is not in the title's TMDb credits");
        }

        // The challenge is spent even when the answer is wrong, so it cannot be retried.
        if (req.VariantId is { } challenged)
        {
            await using var cconn = await _db.OpenAsync(ct).ConfigureAwait(false);
            await CheckChallengeAsync(cconn, me, challenged, req).ConfigureAwait(false);
        }

        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        Guid titleId = await UpsertTitleAsync(conn, tx, key, c.Title.Kind).ConfigureAwait(false);

        bool newVariant = req.VariantId is null;
        Guid variantId;
        double canonicalRuntime;
        SubmissionFit fit;
        if (req.VariantId is { } vid)
        {
            var v = await conn.QueryFirstOrDefaultAsync<(Guid TitleId, double Runtime)>(
                "select title_id, runtime from variants where id = @vid", new { vid }, tx).ConfigureAwait(false);
            if (v.TitleId != titleId)
            {
                throw new RejectedException("variant does not belong to this title");
            }

            fit = req.Fit ?? throw new RejectedException("a submission to an existing variant needs its fit");
            if (FitProblem(fit, v.Runtime) is { } bad)
            {
                throw new RejectedException(bad);
            }

            (variantId, canonicalRuntime) = (vid, v.Runtime);
        }
        else
        {
            canonicalRuntime = c.Title.Runtime;
            fit = CanonicalTimeline.Identity(canonicalRuntime);
            variantId = await CreateVariantAsync(conn, tx, titleId, c).ConfigureAwait(false);
        }

        var canonical = CanonicalTimeline.From(c, fit, canonicalRuntime);

        // Copy-back: an install that fetched this title and then submits it is not a voice. A
        // partial fetch (another cut) only makes a submission to that same variant derived.
        bool derived = await conn.ExecuteScalarAsync<bool>(
            """
            select exists (select 1 from holdings h join variants v on v.id = h.variant_id
                           where h.install_id = @Id and v.title_id = @titleId
                             and (h.kind = 'fetched' or (h.kind = 'fetched-partial' and h.variant_id = @variantId)))
            """,
            new { me.Id, titleId, variantId },
            tx).ConfigureAwait(false);
        string status = derived ? SubmissionStatus.Derived
            : me.State == InstallState.Quarantined ? SubmissionStatus.Quarantined
            : SubmissionStatus.Live;

        // One live submission per (install, variant, analyser version): versions are never mixed
        // in a vote, and the old one stays until the new one is confirmed (design §4). Only a
        // newer analyser than any this install already submitted for the variant earns again.
        var prior = await conn.QueryFirstOrDefaultAsync<(Guid Id, int Version, int Earned, int Held)>(
            """
            select id, version, earned, held from submissions
            where install_id = @Id and variant_id = @variantId and version = @version and status <> 'replaced'
            order by created_at desc limit 1 for update
            """,
            new { me.Id, variantId, version },
            tx).ConfigureAwait(false);
        bool hasPrior = prior.Id != Guid.Empty;
        bool earnedBefore = await conn.ExecuteScalarAsync<bool>(
            "select exists (select 1 from submissions where install_id = @Id and variant_id = @variantId and version >= @version and status <> 'replaced')",
            new { me.Id, variantId, version },
            tx).ConfigureAwait(false);

        Guid submissionId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            insert into submissions (id, install_id, invite_id, variant_id, version, analyzer, model_pack, plugin_version, runtime,
                                     slope, "offset", valid_from, valid_to, body, canonical, status)
            values (@submissionId, @Id, @InviteId, @variantId, @version, @analyzer, @ModelPack, @Plugin, @Runtime,
                    @Slope, @Offset, @ValidFrom, @ValidTo, @body, @canonical, @status)
            """,
            new
            {
                submissionId, me.Id, me.InviteId, variantId, version, analyzer = c.Analyzer.Version, c.Analyzer.ModelPack, c.Analyzer.Plugin,
                c.Title.Runtime, fit.Slope, fit.Offset, fit.ValidFrom, fit.ValidTo,
                body = Gz.Pack(StoredBody(c)), canonical = Gz.Pack(canonical), status,
            },
            tx).ConfigureAwait(false);

        int credit = 0, held = 0;
        if (hasPrior)
        {
            await conn.ExecuteAsync("update submissions set status = 'replaced' where id = @Id", new { prior.Id }, tx).ConfigureAwait(false);
            await conn.ExecuteAsync("delete from unknown_faces where submission_id = @Id", new { prior.Id }, tx).ConfigureAwait(false);
        }

        // MusicBrainz ids are checked in the background (phase 5): a verified one lets a cue be
        // served from a single install.
        var mbids = c.Music.Select(m => m.Mbid).OfType<string>().Select(Guid.Parse).Distinct().ToArray();
        if (mbids.Length > 0)
        {
            await conn.ExecuteAsync(
                "insert into submission_mbids (submission_id, mbid) select @submissionId, unnest(@mbids) on conflict do nothing",
                new { submissionId, mbids },
                tx).ConfigureAwait(false);
            await JobQueue.EnqueueAsync(conn, tx, JobQueue.MusicBrainz, JobQueue.AllRef).ConfigureAwait(false);
        }

        // Unknown faces live only in their own table, never in the stored body.
        if (c.UnknownFaces.Count > 0)
        {
            await UnknownFaceService.StoreAsync(conn, tx, submissionId, me.Id, variantId, key, c, fit, canonicalRuntime).ConfigureAwait(false);
            await JobQueue.EnqueueAsync(conn, tx, JobQueue.Unknowns, JobQueue.AllRef).ConfigureAwait(false);
        }

        if (status == SubmissionStatus.Live && !earnedBefore)
        {
            (credit, held) = await Ledger.EarnAsync(conn, tx, me.Id, submissionId, variantId, Math.Min(c.Title.Runtime, canonicalRuntime + 360), _o).ConfigureAwait(false);
            await conn.ExecuteAsync("update submissions set earned = @credit, held = @held where id = @submissionId", new { credit, held, submissionId }, tx).ConfigureAwait(false);
        }
        else if (hasPrior)
        {
            // Same analyser again: the credit already earned moves to the replacement.
            await conn.ExecuteAsync("update ledger set submission_id = @submissionId where submission_id = @Id", new { submissionId, prior.Id }, tx).ConfigureAwait(false);
            await conn.ExecuteAsync(
                "update submissions n set earned = p.earned, held = p.held, credit_state = p.credit_state from submissions p where n.id = @submissionId and p.id = @Id",
                new { submissionId, prior.Id },
                tx).ConfigureAwait(false);
        }

        if (!derived)
        {
            await conn.ExecuteAsync(
                "insert into holdings (install_id, variant_id, kind) values (@Id, @variantId, 'contributed') on conflict do nothing",
                new { me.Id, variantId },
                tx).ConfigureAwait(false);
        }

        await JobQueue.EnqueueAsync(conn, tx, JobQueue.Consensus, JobQueue.ConsensusRef(variantId, version)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new SubmissionResult(derived ? SubmissionResult.Derived : SubmissionResult.Accepted, submissionId, variantId, newVariant, credit, held, null);
    }

    /// <summary>The stored DTO: unknown-face embeddings never live in a submission body.</summary>
    public static SharedContribution StoredBody(SharedContribution c) => c with { UnknownFaces = [] };

    private static string? FitProblem(SubmissionFit f, double canonicalRuntime)
    {
        if (!double.IsFinite(f.Slope) || !double.IsFinite(f.Offset) || !double.IsFinite(f.ValidFrom) || !double.IsFinite(f.ValidTo))
        {
            return "fit: not finite";
        }

        if (!Aligner.PlausibleSlope(f.Slope, AlignmentOptions.Default.SlopeTolerance))
        {
            return "fit: slope is neither 1 nor PAL";
        }

        if (f.ValidFrom < -1 || f.ValidTo > canonicalRuntime + 1 || f.ValidTo - f.ValidFrom < 0.5 * canonicalRuntime || f.Inliers < AlignmentOptions.Default.MinInliers)
        {
            return "fit: implausible range or too few inliers";
        }

        return null;
    }

    /// <summary>
    /// The possession challenge (design §3): the answer to a nonce this install got with the
    /// variant's reference, unused and younger than <see cref="ShareOptions.NonceTtlHours"/>, must
    /// match the variant's private anchors through the declared fit.
    /// </summary>
    private async Task CheckChallengeAsync(IDbConnection conn, Install me, Guid variantId, SubmissionRequest req)
    {
        if (req.Challenge is not { Nonce.Length: > 0, Windows.Length: > 0 } answer)
        {
            throw new RejectedException("a submission to an existing variant needs the possession challenge answer");
        }

        int used = await conn.ExecuteAsync(
            """
            update challenges set used_at = now()
            where nonce_hash = @hash and install_id = @Id and variant_id = @variantId and used_at is null
              and created_at > now() - make_interval(hours => @ttl)
            """,
            new { hash = Secrets.Hash(answer.Nonce), me.Id, variantId, ttl = _o.NonceTtlHours }).ConfigureAwait(false);
        if (used != 1)
        {
            throw new RejectedException("possession challenge: unknown, used or expired nonce");
        }

        var v = await conn.QuerySingleAsync<Endpoints.VariantAnchors>(
            "select id, runtime, every, length, anchor_count, anchors from variants where id = @variantId", new { variantId }).ConfigureAwait(false);
        var positions = PossessionChallenge.Pick(answer.Nonce, v.AnchorCount, AnchorSet.ReferenceStride(v.Runtime));
        var (passed, informative) = PossessionChallenge.Verify(v.ToSet(), positions, answer.Windows);
        if (!PossessionChallenge.Passes(passed, informative))
        {
            throw new RejectedException($"possession challenge failed ({passed} of {informative} windows matched)");
        }
    }

    private static async Task<Guid> UpsertTitleAsync(IDbConnection conn, IDbTransaction tx, string key, string kind)
    {
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return await conn.ExecuteScalarAsync<Guid>(
            """
            insert into titles (key, key_hash, prefix, kind) values (@key, @hash, @prefix, @kind)
            on conflict (key) do update set key = excluded.key
            returning id
            """,
            new { key, hash, prefix = TitleKey.HashPrefix(key), kind },
            tx).ConfigureAwait(false);
    }

    private static Task<Guid> CreateVariantAsync(IDbConnection conn, IDbTransaction tx, Guid titleId, SharedContribution c)
    {
        var set = c.Anchors.ToSet();
        return conn.ExecuteScalarAsync<Guid>(
            """
            insert into variants (title_id, edition, runtime, frame_rate, every, length, anchor_count, anchors)
            values (@titleId, @Edition, @Runtime, @FrameRate, @every, @length, @count, @anchors)
            returning id
            """,
            new
            {
                titleId, c.Title.Edition, c.Title.Runtime, c.Title.FrameRate, every = set.EverySeconds, length = set.Length,
                count = set.Count, anchors = Chromaprint.ToBytes(set.Windows.SelectMany(w => w).ToArray()),
            },
            tx);
    }
}
