using System.Globalization;
using System.Text.Json;
using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>An unknown face as the naming rules see it. Intervals are canonical.</summary>
public sealed record UnknownRow(
    Guid Id,
    Guid SubmissionId,
    Guid? InstallId,
    Guid Voice,
    Guid VariantId,
    int Version,
    string TitleKey,
    string Recognizer,
    bool HasEmbedding,
    IReadOnlyList<double[]> Intervals,
    Guid? Group,
    int? Person,
    int? Voices,
    string? Rule);

/// <summary>A name found for an unknown face, and how.</summary>
public sealed record Naming(int Person, double Confidence, int Voices, string Rule)
{
    public const string ByTime = "time";
    public const string ByFace = "face";
}

/// <summary>
/// The naming rules of "Naming unknown faces together" (design §2), pure so they can be tested
/// without a database.
/// </summary>
public static class FaceNaming
{
    /// <summary>P must cover this share of U's screen time…</summary>
    public const double MinCoverage = 0.7;

    /// <summary>…and no other candidate may reach this share of P's overlap.</summary>
    public const double MaxRunnerUp = 0.5;

    /// <summary>Cosine similarity that links two unknowns; stricter than the plugin's 0.45.</summary>
    public const double LinkSimilarity = 0.5;

    /// <summary>
    /// Rule 1, the same title by time: the consensus of the other installs places P where U is.
    /// </summary>
    public static Naming? ByTime(IReadOnlyList<double[]> unknown, IReadOnlyList<ConsensusPerson> others, IReadOnlySet<int> alreadyNamed)
    {
        double total = TimelineMapping.Total(unknown);
        if (total <= 0)
        {
            return null;
        }

        var ranked = others
            .Select(p => (P: p, Overlap: TimelineMapping.Overlap(unknown, p.Intervals)))
            .Where(x => x.Overlap > 0)
            .OrderByDescending(x => x.Overlap)
            .ToList();
        if (ranked.Count == 0)
        {
            return null;
        }

        var best = ranked[0];
        double coverage = best.Overlap / total;
        if (coverage < MinCoverage || (ranked.Count > 1 && ranked[1].Overlap >= MaxRunnerUp * best.Overlap) || alreadyNamed.Contains(best.P.TmdbPerson))
        {
            return null;
        }

        return new Naming(best.P.TmdbPerson, Math.Round(Math.Min(1, coverage), 3), best.P.Voices, Naming.ByTime);
    }

    /// <summary>
    /// Rule 2's grouping: links (strongest first) join unknowns into groups, except that two
    /// unknowns of one submission never share a group (the plugin's cannot-link rule). Existing
    /// groups are kept. Returns each row's group; rows alone have none.
    /// </summary>
    public static Dictionary<Guid, Guid?> Groups(IReadOnlyList<UnknownRow> rows, IEnumerable<(Guid A, Guid B, double Similarity)> links)
    {
        var parent = rows.ToDictionary(r => r.Id, r => r.Id);
        var subs = rows.ToDictionary(r => r.Id, r => new HashSet<Guid> { r.SubmissionId });
        Guid Find(Guid x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        bool Union(Guid a, Guid b)
        {
            Guid ra = Find(a), rb = Find(b);
            if (ra == rb || subs[ra].Overlaps(subs[rb]))
            {
                return false;
            }

            parent[rb] = ra;
            subs[ra].UnionWith(subs[rb]);
            return true;
        }

        foreach (var g in rows.Where(r => r.Group is not null).GroupBy(r => r.Group))
        {
            var first = g.First();
            foreach (var r in g.Skip(1))
            {
                Union(first.Id, r.Id);
            }
        }

        foreach (var (a, b, _) in links.Where(l => l.Similarity >= LinkSimilarity && parent.ContainsKey(l.A) && parent.ContainsKey(l.B)).OrderByDescending(l => l.Similarity))
        {
            Union(a, b);
        }

        var result = new Dictionary<Guid, Guid?>();
        foreach (var component in rows.GroupBy(r => Find(r.Id)))
        {
            Guid? id = component.Count() < 2 ? null : component.Select(r => r.Group).Where(g => g is not null).Min() ?? Guid.NewGuid();
            foreach (var r in component)
            {
                result[r.Id] = id;
            }
        }

        return result;
    }

    /// <summary>
    /// Rule 2's spread: a group whose members were named by time, all to the same P, passes P to
    /// its other members, but only to those whose title has P in its TMDb credits. A wrong link can
    /// at worst mislabel a credited actor, never invent a person.
    /// </summary>
    public static Dictionary<Guid, Naming> Spread(
        IReadOnlyList<UnknownRow> rows,
        IReadOnlyDictionary<Guid, Guid?> groups,
        IReadOnlyDictionary<Guid, Naming> byTime,
        Func<string, int, bool> credited,
        Func<UnknownRow, int, bool> namedInOwnSubmission)
    {
        var spread = new Dictionary<Guid, Naming>();
        foreach (var component in rows.Where(r => groups.GetValueOrDefault(r.Id) is not null).GroupBy(r => groups[r.Id]))
        {
            var evidence = component
                .Select(r => byTime.TryGetValue(r.Id, out var n) ? n : r.Rule == Naming.ByTime && r.Person is { } p ? new Naming(p, 0.7, r.Voices ?? 1, Naming.ByTime) : null)
                .OfType<Naming>()
                .ToList();
            if (evidence.Select(e => e.Person).Distinct().Count() != 1)
            {
                continue; // nobody named, or the group disagrees with itself
            }

            int person = evidence[0].Person;
            var name = new Naming(person, Math.Round(0.9 * evidence.Max(e => e.Confidence), 3), evidence.Max(e => e.Voices), Naming.ByFace);
            foreach (var r in component.Where(r => !byTime.ContainsKey(r.Id) && r.Rule != Naming.ByTime))
            {
                if (credited(r.TitleKey, person) && !namedInOwnSubmission(r, person))
                {
                    spread[r.Id] = name;
                }
            }
        }

        return spread;
    }
}

/// <summary>
/// Runs the naming rules over all unknown faces, as the queued <c>unknowns</c> job. A name, once
/// found, is kept; more evidence may raise its voices. The embedding is nulled when the face is
/// named: only the TMDb id stays.
/// </summary>
public sealed class UnknownFaceService
{
    private readonly Db _db;
    private readonly ConsensusService _consensus;
    private readonly ICreditSource _credits;

    public UnknownFaceService(Db db, ConsensusService consensus, ICreditSource credits)
    {
        _db = db;
        _consensus = consensus;
        _credits = credits;
    }

    private sealed class Stored
    {
        public Guid Id { get; set; }

        public Guid SubmissionId { get; set; }

        public Guid? InstallId { get; set; }

        public Guid InviteId { get; set; }

        public Guid VariantId { get; set; }

        public int Version { get; set; }

        public string TitleKey { get; set; } = string.Empty;

        public string Recognizer { get; set; } = string.Empty;

        public bool HasEmbedding { get; set; }

        public string Intervals { get; set; } = "[]";

        public Guid? LinkGroup { get; set; }

        public int? Person { get; set; }

        public int? Voices { get; set; }

        public string? Rule { get; set; }

        public double? Confidence { get; set; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        var stored = (await conn.QueryAsync<Stored>(
            """
            select u.id, u.submission_id, u.install_id, s.invite_id, u.variant_id, s.version, u.title_key, u.recognizer,
                   u.embedding is not null as has_embedding, u.intervals::text as intervals, u.link_group, u.person, u.voices, u.rule, u.confidence
            from unknown_faces u join submissions s on s.id = u.submission_id
            """).ConfigureAwait(false)).ToList();
        if (stored.Count == 0)
        {
            return;
        }

        var rows = stored.Select(s => new UnknownRow(
            s.Id, s.SubmissionId, s.InstallId, s.InviteId, s.VariantId, s.Version, s.TitleKey, s.Recognizer, s.HasEmbedding,
            JsonSerializer.Deserialize<List<double[]>>(s.Intervals) ?? [], s.LinkGroup, s.Person, s.Voices, s.Rule)).ToList();

        // Rule 2's links: nearest unknowns by the same recognizer, never from the same submission.
        var links = new List<(Guid, Guid, double)>();
        foreach (var r in rows.Where(r => r.HasEmbedding))
        {
            var near = await conn.QueryAsync<(Guid Id, double Similarity)>(
                """
                select o.id, 1 - (o.embedding <=> u.embedding) as similarity
                from unknown_faces u, unknown_faces o
                where u.id = @Id and o.id <> u.id and o.recognizer = u.recognizer and o.embedding is not null and o.submission_id <> u.submission_id
                order by o.embedding <=> u.embedding limit 20
                """,
                new { r.Id }).ConfigureAwait(false);
            links.AddRange(near.Where(n => n.Similarity >= FaceNaming.LinkSimilarity).Select(n => (r.Id, n.Id, n.Similarity)));
        }

        var groups = FaceNaming.Groups(rows, links);

        // Rule 1 per (variant, version, voice): the consensus of every other voice.
        var own = new Dictionary<Guid, HashSet<int>>();
        foreach (Guid sub in rows.Select(r => r.SubmissionId).Distinct())
        {
            byte[] canonical = await conn.ExecuteScalarAsync<byte[]>("select canonical from submissions where id = @sub", new { sub }).ConfigureAwait(false);
            var t = Gz.Unpack<CanonicalTimeline>(canonical);
            own[sub] = t.People.Concat(t.VoicePeople).Select(p => p.TmdbPerson).ToHashSet();
        }

        var byTime = new Dictionary<Guid, Naming>();
        foreach (var g in rows.GroupBy(r => (r.VariantId, r.Version, r.Voice)))
        {
            double runtime = await conn.ExecuteScalarAsync<double>("select runtime from variants where id = @VariantId", new { g.Key.VariantId }).ConfigureAwait(false);
            var (_, ballots) = await _consensus.LoadAsync(conn, null, g.Key.VariantId, g.Key.Version).ConfigureAwait(false);
            var others = Merge.Run(g.Key.VariantId, g.Key.Version, runtime, ballots.Where(b => b.Voice != g.Key.Voice).ToList()).Consensus?.People ?? [];
            foreach (var r in g)
            {
                if (FaceNaming.ByTime(r.Intervals, others, own[r.SubmissionId]) is { } n)
                {
                    byTime[r.Id] = n;
                }
            }
        }

        var cast = new Dictionary<string, CastList?>();
        foreach (string key in rows.Select(r => r.TitleKey).Distinct())
        {
            cast[key] = await CastAsync(conn, key, rows.First(r => r.TitleKey == key).SubmissionId, ct).ConfigureAwait(false);
        }

        var spread = FaceNaming.Spread(rows, groups, byTime, (key, p) => cast[key] is { Ids: not null } c && c.Allows(p), (r, p) => own[r.SubmissionId].Contains(p));

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var s in stored)
        {
            var name = byTime.GetValueOrDefault(s.Id) ?? spread.GetValueOrDefault(s.Id);
            bool better = name is not null && (s.Person is null || (name.Person == s.Person && name.Voices > (s.Voices ?? 0)) || (s.Rule == Naming.ByFace && name.Rule == Naming.ByTime));
            if (better)
            {
                await conn.ExecuteAsync(
                    """
                    update unknown_faces set person = @Person, confidence = @Confidence, voices = @Voices, rule = @Rule,
                           resolved_revision = nextval('unknown_revision'), resolved_at = coalesce(resolved_at, now()),
                           embedding = null, link_group = @group
                    where id = @id
                    """,
                    new { name!.Person, name.Confidence, name.Voices, name.Rule, group = groups[s.Id], id = s.Id },
                    tx).ConfigureAwait(false);
            }
            else if (groups[s.Id] != s.LinkGroup)
            {
                await conn.ExecuteAsync("update unknown_faces set link_group = @group where id = @id", new { group = groups[s.Id], id = s.Id }, tx).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The credit guard's cast list: the cache, else TMDb with the title from a submission. Null when unknown.</summary>
    private async Task<CastList?> CastAsync(System.Data.IDbConnection conn, string key, Guid submissionId, CancellationToken ct)
    {
        try
        {
            byte[] body = await conn.ExecuteScalarAsync<byte[]>("select body from submissions where id = @submissionId", new { submissionId }).ConfigureAwait(false);
            return await _credits.CastAsync(key, Gz.Unpack<SharedContribution>(body).Title, ct).ConfigureAwait(false);
        }
        catch (CreditsUnavailableException)
        {
            return null; // no spread this run; the next run tries again
        }
    }

    /// <summary>Stores a submission's unknown faces, mapped into canonical time.</summary>
    public static async Task StoreAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid submissionId, Guid installId, Guid variantId, string titleKey, SharedContribution c, SubmissionFit fit, double canonicalRuntime)
    {
        foreach (var u in c.UnknownFaces)
        {
            var intervals = TimelineMapping.ToCanonical(u.Intervals, fit.Slope, fit.Offset, fit.ValidFrom, fit.ValidTo, canonicalRuntime);
            var v = FaceSignature.Decode(u.Embedding);
            if (intervals.Count == 0 || v is null)
            {
                continue;
            }

            string vector = "[" + string.Join(',', v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
            await conn.ExecuteAsync(
                """
                insert into unknown_faces (submission_id, install_id, variant_id, title_key, local_id, recognizer, embedding, screen_time, intervals)
                values (@submissionId, @installId, @variantId, @titleKey, @Id, @Recognizer, @vector::halfvec, @ScreenTime, @json::jsonb)
                """,
                new { submissionId, installId, variantId, titleKey, u.Id, u.Recognizer, vector, u.ScreenTime, json = JsonSerializer.Serialize(intervals) },
                tx).ConfigureAwait(false);
        }
    }
}
