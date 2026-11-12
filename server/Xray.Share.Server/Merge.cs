using System.Globalization;
using System.Text;
using Jellyfin.Plugin.SceneXray.Sharing;

namespace Xray.Share.Server;

/// <summary>
/// One submission as a vote. <c>Voice</c> is its invite: installs from one invite are one voice
/// (design §4.5). <c>Weight</c> is the install's trust, 0 when quarantined or outvoted.
/// <c>Voting</c> is false for installs younger than 7 days. [CoverFrom, CoverTo] is the canonical
/// range its fit was trusted on.
/// </summary>
public sealed record Ballot(Guid SubmissionId, Guid? InstallId, Guid Voice, double Weight, bool Voting, double CoverFrom, double CoverTo, CanonicalTimeline Timeline);

/// <summary>A submission's agreement with the consensus computed without its voice.</summary>
public sealed record BallotScore(double? Agreement, bool Corroborated, bool Outlier);

public sealed record MergeOptions
{
    /// <summary>Scene boundaries within ± this many seconds are one boundary.</summary>
    public double BoundaryTolerance { get; init; } = 1.5;

    /// <summary>A boundary is kept when ballots holding this share of the weight have it.</summary>
    public double BoundaryShare { get; init; } = 0.5;

    /// <summary>A (person, scene) cell is served at this support.</summary>
    public double CellSupport { get; init; } = 0.5;

    public double MusicToleranceSeconds { get; init; } = 3;

    /// <summary>Independent voices for <c>confirmed</c> (design D3).</summary>
    public int ConfirmedVoices { get; init; } = 2;

    public double OutlierBelow { get; init; } = 0.4;

    public int VoicesForOutliers { get; init; } = 3;

    public double UnconfirmedCap { get; init; } = 0.5;

    /// <summary>Voting cells when no ballot has scenes.</summary>
    public double PseudoSceneSeconds { get; init; } = 30;

    public static MergeOptions Default { get; } = new();
}

/// <summary>The merged body plus every ballot's score.</summary>
public sealed record MergeResult(ConsensusTimeline? Consensus, IReadOnlyDictionary<Guid, BallotScore> Scores);

/// <summary>
/// The multi-voice merge (design §4, "Consensus" and "Outliers and poisoning"). Pure: no
/// database, so the poisoning tests can drive it directly.
/// </summary>
public static class Merge
{
    /// <summary>
    /// Merges, scores every ballot against the consensus without its own voice, drops outliers
    /// and merges again. Only voting installs (older than 7 days) count; when none has voted yet,
    /// the others are served as <c>unconfirmed</c> so a new title is still available.
    /// </summary>
    /// <param name="musicVerified">The MusicBrainz recording behind a verified id, else null (phase 5).</param>
    public static MergeResult Run(Guid variantId, int version, double runtime, IReadOnlyList<Ballot> ballots, MergeOptions? options = null, Func<string, MusicBrainzRecording?>? musicVerified = null)
    {
        var o = options ?? MergeOptions.Default;
        var counted = ballots.Where(b => b.Weight > 0).ToList();
        var voting = counted.Where(b => b.Voting).ToList();
        bool votingPath = voting.Count > 0;
        var basis = votingPath ? voting : counted;

        var scores = new Dictionary<Guid, BallotScore>();
        if (votingPath)
        {
            foreach (var b in ballots)
            {
                scores[b.SubmissionId] = Score(b, voting, runtime, o);
            }
        }

        var kept = basis.Where(b => !(scores.TryGetValue(b.SubmissionId, out var s) && s.Outlier)).ToList();
        var built = Build(runtime, kept, o, musicVerified);
        if (built is null)
        {
            return new MergeResult(null, scores);
        }

        bool confirmed = votingPath && built.Voices >= o.ConfirmedVoices;
        double cap = confirmed ? 1 : o.UnconfirmedCap;
        IReadOnlyList<ConsensusPerson> Cap(IEnumerable<ConsensusPerson> people) => people.Select(p => p with { Confidence = Math.Round(Math.Min(cap, p.Confidence), 3) }).ToList();
        var body = new ConsensusTimeline(
            variantId,
            version,
            0,
            confirmed ? ConsensusTimeline.Confirmed : ConsensusTimeline.Unconfirmed,
            built.Voices,
            runtime,
            built.ServedScenes,
            Cap(built.People),
            Cap(built.VoicePeople),
            built.Music);
        return new MergeResult(body, scores);
    }

    private sealed record Built(
        List<double[]> Scenes,
        List<double[]> ServedScenes,
        int Voices,
        List<ConsensusPerson> People,
        List<ConsensusPerson> VoicePeople,
        List<ConsensusMusicCue> Music,
        HashSet<string> Cells);

    /// <summary>Per-ballot weight: a voice's weight is the mean trust of its installs, split between its ballots.</summary>
    private static Dictionary<Guid, double> Weights(IReadOnlyList<Ballot> ballots) =>
        ballots.GroupBy(b => b.Voice)
            .SelectMany(g => g.Select(b => (b.SubmissionId, W: g.Average(x => x.Weight) / g.Count())))
            .ToDictionary(x => x.SubmissionId, x => x.W);

    private static Built? Build(double runtime, IReadOnlyList<Ballot> ballots, MergeOptions o, Func<string, MusicBrainzRecording?>? musicVerified)
    {
        if (ballots.Count == 0)
        {
            return null;
        }

        var w = Weights(ballots);
        var scenes = Scenes(runtime, ballots, w, o);
        bool anyScenes = ballots.Any(b => b.Timeline.Scenes.Count > 0);
        var grid = anyScenes ? scenes : PseudoScenes(runtime, o.PseudoSceneSeconds);
        var cells = new HashSet<string>();
        var people = People(ballots, w, grid, b => b.Timeline.People, "f", cells, o);
        var voicePeople = People(ballots, w, grid, b => b.Timeline.VoicePeople, "v", cells, o);
        return new Built(grid, anyScenes ? scenes : [], ballots.Select(b => b.Voice).Distinct().Count(), people, voicePeople, Music(ballots, o, musicVerified), cells);
    }

    private static bool Covers(Ballot b, double from, double to) => from >= b.CoverFrom - 1 && to <= b.CoverTo + 1;

    /// <summary>Boundaries within ±1.5 s cluster; a cluster held by half the weight is kept.</summary>
    private static List<double[]> Scenes(double runtime, IReadOnlyList<Ballot> ballots, IReadOnlyDictionary<Guid, double> w, MergeOptions o)
    {
        var points = ballots
            .SelectMany(b => b.Timeline.Scenes.SelectMany(s => new[] { s[0], s[1] })
                .Where(t => t > 0.5 && t < runtime - 0.5)
                .Select(t => Math.Round(t, 2))
                .Distinct()
                .Select(t => (T: t, B: b)))
            .OrderBy(p => p.T)
            .ToList();
        var withScenes = ballots.Where(b => b.Timeline.Scenes.Count > 0).ToList();
        var kept = new List<double>();
        for (int i = 0; i < points.Count;)
        {
            int j = i;
            while (j < points.Count && points[j].T - points[i].T <= 2 * o.BoundaryTolerance)
            {
                j++;
            }

            var cluster = points.Skip(i).Take(j - i).GroupBy(p => p.B.SubmissionId).Select(g => g.First()).ToList();
            double t = cluster.Sum(p => p.T * w[p.B.SubmissionId]) / Math.Max(1e-9, cluster.Sum(p => w[p.B.SubmissionId]));
            double total = withScenes.Where(b => Covers(b, t, t)).Sum(b => w[b.SubmissionId]);
            if (total > 0 && cluster.Sum(p => w[p.B.SubmissionId]) >= o.BoundaryShare * total - 1e-9)
            {
                kept.Add(Math.Round(t, 2));
            }

            i = j;
        }

        var edges = new List<double> { 0 };
        edges.AddRange(kept);
        edges.Add(runtime);
        return edges.Zip(edges.Skip(1)).Where(e => e.Second - e.First > 0.05).Select(e => new[] { e.First, e.Second }).ToList();
    }

    private static List<double[]> PseudoScenes(double runtime, double every)
    {
        var list = new List<double[]>();
        for (double t = 0; t < runtime; t += every)
        {
            list.Add([t, Math.Min(runtime, t + every)]);
        }

        return list;
    }

    /// <summary>Scene indexes a person's intervals place them in.</summary>
    public static HashSet<int> Placed(IReadOnlyList<double[]> intervals, IReadOnlyList<double[]> grid)
    {
        var placed = new HashSet<int>();
        for (int s = 0; s < grid.Count; s++)
        {
            double len = grid[s][1] - grid[s][0];
            double overlap = 0;
            foreach (var i in intervals)
            {
                if (i[1] <= grid[s][0])
                {
                    continue;
                }

                if (i[0] >= grid[s][1])
                {
                    break;
                }

                overlap += Math.Min(i[1], grid[s][1]) - Math.Max(i[0], grid[s][0]);
            }

            if (overlap >= Math.Min(2, 0.25 * len))
            {
                placed.Add(s);
            }
        }

        return placed;
    }

    /// <summary>A ballot's own confidence: the mean of its people's, or 0.75 without people.</summary>
    private static double SubmissionConfidence(Ballot b)
    {
        var all = b.Timeline.People.Concat(b.Timeline.VoicePeople).ToList();
        return all.Count == 0 ? 0.75 : Math.Max(0.05, all.Average(p => p.Confidence));
    }

    /// <summary>
    /// Support of (person, scene) = Σ trust × confidence of ballots placing them there, over
    /// Σ trust × submission confidence of ballots covering the scene. Served at ≥ 0.5, with the
    /// stretches at least half the supporting weight agrees on.
    /// </summary>
    private static List<ConsensusPerson> People(IReadOnlyList<Ballot> ballots, IReadOnlyDictionary<Guid, double> w, IReadOnlyList<double[]> grid, Func<Ballot, IReadOnlyList<SharedPerson>> list, string kind, HashSet<string> cells, MergeOptions o)
    {
        var placements = new Dictionary<(int Person, int Scene), List<(Ballot B, SharedPerson P)>>();
        foreach (var b in ballots)
        {
            foreach (var p in list(b))
            {
                foreach (int s in Placed(p.Intervals, grid))
                {
                    if (!placements.TryGetValue((p.TmdbPerson, s), out var l))
                    {
                        placements[(p.TmdbPerson, s)] = l = [];
                    }

                    l.Add((b, p));
                }
            }
        }

        var conf = ballots.ToDictionary(b => b.SubmissionId, SubmissionConfidence);
        var byPerson = new Dictionary<int, (List<double[]> Intervals, List<double> Conf, HashSet<Guid> Voices)>();
        foreach (var ((person, s), supporters) in placements.OrderBy(kv => kv.Key.Scene))
        {
            var scene = grid[s];
            double den = ballots.Where(b => Covers(b, scene[0], scene[1])).Sum(b => w[b.SubmissionId] * conf[b.SubmissionId]);
            double num = supporters.Sum(x => w[x.B.SubmissionId] * x.P.Confidence);
            double support = den <= 0 ? 0 : num / den;
            if (support < o.CellSupport)
            {
                continue;
            }

            cells.Add(kind + ":" + person.ToString(CultureInfo.InvariantCulture) + ":" + s.ToString(CultureInfo.InvariantCulture));
            var stretches = Agreed(supporters.Select(x => (TimelineMapping.Clip(x.P.Intervals, scene[0], scene[1]), w[x.B.SubmissionId])).ToList());
            if (!byPerson.TryGetValue(person, out var acc))
            {
                byPerson[person] = acc = ([], [], []);
            }

            acc.Intervals.AddRange(stretches);
            acc.Conf.Add(Math.Min(1, support) * supporters.Average(x => x.P.Confidence));
            foreach (var x in supporters)
            {
                acc.Voices.Add(x.B.Voice);
            }
        }

        return byPerson
            .Select(kv => new ConsensusPerson(kv.Key, Math.Round(Math.Min(1, kv.Value.Conf.Average()), 3), kv.Value.Voices.Count, TimelineMapping.Merge(kv.Value.Intervals)))
            .Where(p => p.Intervals.Count > 0)
            .OrderByDescending(p => TimelineMapping.Total(p.Intervals))
            .ToList();
    }

    /// <summary>Where at least half the supporters' weight has the person.</summary>
    private static List<double[]> Agreed(IReadOnlyList<(List<double[]> Intervals, double W)> supporters)
    {
        double half = 0.5 * supporters.Sum(s => s.W);
        var events = supporters.SelectMany(s => s.Intervals.SelectMany(i => new[] { (T: i[0], D: s.W), (T: i[1], D: -s.W) }))
            .OrderBy(e => e.T).ThenBy(e => e.D)
            .ToList();
        var result = new List<double[]>();
        double level = 0, start = 0;
        foreach (var (t, d) in events)
        {
            bool before = level >= half - 1e-9;
            level += d;
            bool after = level >= half - 1e-9;
            if (!before && after)
            {
                start = t;
            }
            else if (before && !after && t - start > 0.05)
            {
                result.Add([Math.Round(start, 2), Math.Round(t, 2)]);
            }
        }

        return result;
    }

    public static string Normalize(string? s)
    {
        var sb = new StringBuilder();
        foreach (char c in (s ?? string.Empty).Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool SameSong(SharedMusicCue a, SharedMusicCue b) =>
        (a.Mbid is not null && b.Mbid is not null)
            ? string.Equals(a.Mbid, b.Mbid, StringComparison.OrdinalIgnoreCase)
            : Normalize(a.Title) == Normalize(b.Title) && Normalize(a.Artist) == Normalize(b.Artist);

    /// <summary>
    /// A cue is served when two independent voices agree on the span (±3 s) and the song, or from
    /// one voice when MusicBrainz confirms its recording id and the title matches (phase 5). Free
    /// text never reaches anyone from one install alone (design §4.4): a single-voice cue is
    /// served with MusicBrainz's title and artist, and so is any cue MusicBrainz knows.
    /// </summary>
    private static List<ConsensusMusicCue> Music(IReadOnlyList<Ballot> ballots, MergeOptions o, Func<string, MusicBrainzRecording?>? verified)
    {
        var cues = ballots.SelectMany(b => b.Timeline.Music.Select(m => (B: b, M: m))).OrderBy(x => x.M.Start).ToList();
        var used = new bool[cues.Count];
        var served = new List<ConsensusMusicCue>();
        for (int i = 0; i < cues.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var group = new List<int> { i };
            for (int j = i + 1; j < cues.Count; j++)
            {
                if (!used[j] && Math.Abs(cues[j].M.Start - cues[i].M.Start) <= o.MusicToleranceSeconds
                    && Math.Abs(cues[j].M.End - cues[i].M.End) <= o.MusicToleranceSeconds && SameSong(cues[i].M, cues[j].M)
                    && group.All(g => cues[g].B.SubmissionId != cues[j].B.SubmissionId))
                {
                    group.Add(j);
                }
            }

            var members = group.Select(g => cues[g]).ToList();
            int voices = members.Select(m => m.B.Voice).Distinct().Count();
            string? mbid = members.Select(m => m.M.Mbid).FirstOrDefault(m => m is not null);
            var mb = mbid is null ? null : verified?.Invoke(mbid);
            bool confirmedByMb = mb is not null && members.Any(m => Normalize(m.M.Title) == Normalize(mb.Title));
            if (voices < 2 && !confirmedByMb)
            {
                continue;
            }

            foreach (int g in group)
            {
                used[g] = true;
            }

            string Most(Func<SharedMusicCue, string?> f) => members.Select(m => f(m.M)).Where(v => !string.IsNullOrEmpty(v)).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key!).FirstOrDefault() ?? string.Empty;
            served.Add(new ConsensusMusicCue(
                Math.Round(Median(members.Select(m => m.M.Start)), 1),
                Math.Round(Median(members.Select(m => m.M.End)), 1),
                confirmedByMb ? mb!.Title : Most(m => m.Title),
                confirmedByMb ? mb!.Artist : Most(m => m.Artist) is { Length: > 0 } artist ? artist : null,
                confirmedByMb ? null : Most(m => m.Album) is { Length: > 0 } album ? album : null,
                mbid,
                Math.Round(members.Average(m => m.M.Confidence), 3)));
        }

        return served;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[(v.Count / 2) - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>
    /// Jaccard of the ballot's (person, scene) cells with the consensus of every other voice,
    /// inside the range the ballot covers. Installs of the same invite never corroborate each other.
    /// </summary>
    public static BallotScore Score(Ballot b, IReadOnlyList<Ballot> voting, double runtime, MergeOptions o)
    {
        var others = voting.Where(x => x.Voice != b.Voice).ToList();
        var loo = Build(runtime, others, o, null);
        if (loo is null)
        {
            return new BallotScore(null, false, false);
        }

        var mine = new HashSet<string>();
        foreach (var (kind, list) in new[] { ("f", b.Timeline.People), ("v", b.Timeline.VoicePeople) })
        {
            foreach (var p in list)
            {
                foreach (int s in Placed(p.Intervals, loo.Scenes))
                {
                    mine.Add(kind + ":" + p.TmdbPerson.ToString(CultureInfo.InvariantCulture) + ":" + s.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        bool InRange(string cell)
        {
            int s = int.Parse(cell[(cell.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
            return Covers(b, loo.Scenes[s][0], loo.Scenes[s][1]);
        }

        var theirs = loo.Cells.Where(InRange).ToHashSet();
        mine.RemoveWhere(c => !InRange(c));
        int union = mine.Union(theirs).Count();
        double agreement = union == 0 ? 1 : (double)mine.Intersect(theirs).Count() / union;
        int voices = others.Select(x => x.Voice).Append(b.Voice).Distinct().Count();
        return new BallotScore(Math.Round(agreement, 3), agreement >= o.OutlierBelow, voices >= o.VoicesForOutliers && agreement < o.OutlierBelow);
    }
}
