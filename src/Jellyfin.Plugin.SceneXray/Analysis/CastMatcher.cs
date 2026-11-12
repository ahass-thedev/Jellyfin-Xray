using Jellyfin.Plugin.SceneXray.Storage;
using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Analysis;

/// <summary>A cast member who might appear, with their reference face embeddings.</summary>
public sealed class CastCandidate
{
    public required Guid PersonId { get; init; }

    public required string Name { get; init; }

    public string? Role { get; init; }

    /// <summary>Gets the billing order (0 = top-billed).</summary>
    public int Order { get; init; }

    public List<float[]> References { get; } = [];
}

public sealed record CastAssignment(PersonCluster Cluster, CastCandidate Person, float Score, float Margin);

/// <summary>
/// Names whole person clusters, not frames. A cluster gets a name only when it clearly beats
/// the runner-up; one actor may own several clusters (a costume or age change) only when the
/// extra cluster is a strong match and never shares a frame with the first.
/// </summary>
public static class CastMatcher
{
    public static List<CastAssignment> Match(
        IReadOnlyList<PersonCluster> clusters,
        IReadOnlyList<CastCandidate> cast,
        double frameSeconds,
        float threshold,
        float margin,
        float weakThreshold = 0.2f,
        double minClusterSeconds = 1.5)
    {
        // Two tiers. Strong: clearly the person. Weak: a lower score (typically an old film
        // against a recent headshot) is accepted only with a wide lead, enough screen time and
        // only for someone not already found, one cluster each.
        float weakMargin = Math.Max(margin, 0.08f);
        var withRefs = cast.Where(c => c.References.Count > 0).ToList();
        var proposals = new List<CastAssignment>();
        var weak = new List<CastAssignment>();
        if (withRefs.Count == 0)
        {
            return proposals;
        }

        foreach (var cl in clusters)
        {
            if (cl.ScreenTime(frameSeconds) < minClusterSeconds)
            {
                continue;
            }

            float s1 = float.MinValue, s2 = float.MinValue;
            CastCandidate? p1 = null;
            foreach (var p in withRefs)
            {
                float s = Score(cl.Centroid, p.References);
                if (s > s1)
                {
                    s2 = s1;
                    s1 = s;
                    p1 = p;
                }
                else if (s > s2)
                {
                    s2 = s;
                }
            }

            float m = withRefs.Count == 1 ? s1 : s1 - s2;
            if (p1 is not null && s1 >= threshold && m >= margin)
            {
                proposals.Add(new CastAssignment(cl, p1, s1, m));
            }
            else if (p1 is not null && weakThreshold > 0 && s1 >= weakThreshold && m >= weakMargin
                && cl.ScreenTime(frameSeconds) >= 3)
            {
                weak.Add(new CastAssignment(cl, p1, s1, m));
            }
        }

        var accepted = new List<CastAssignment>();
        var framesByPerson = new Dictionary<Guid, HashSet<int>>();
        foreach (var a in proposals.OrderByDescending(p => p.Score))
        {
            if (framesByPerson.TryGetValue(a.Person.PersonId, out var owned))
            {
                bool strong = a.Score >= threshold + 0.08f;
                if (!strong || FrameSets.Conflict(owned, a.Cluster.Frames))
                {
                    continue;
                }

                owned.UnionWith(a.Cluster.Frames);
            }
            else
            {
                framesByPerson[a.Person.PersonId] = [.. a.Cluster.Frames];
            }

            accepted.Add(a);
        }

        foreach (var a in weak.OrderByDescending(p => p.Margin))
        {
            if (framesByPerson.TryAdd(a.Person.PersonId, [.. a.Cluster.Frames]))
            {
                accepted.Add(a);
            }
        }

        AttachFragments(clusters, accepted, frameSeconds);
        return accepted;
    }

    /// <summary>
    /// Short or off-angle fragments rarely match a studio portrait, but they do match the same
    /// actor's own footage. Attach an unnamed cluster to a named one when video-to-video
    /// similarity is clear and the two never share a frame.
    /// </summary>
    internal static void AttachFragments(IReadOnlyList<PersonCluster> clusters, List<CastAssignment> accepted, double frameSeconds, float threshold = 0.3f, float margin = 0.05f)
    {
        if (accepted.Count == 0)
        {
            return;
        }

        var named = accepted.Select(a => a.Cluster).ToHashSet();
        var personFrames = accepted.GroupBy(a => a.Person.PersonId)
            .ToDictionary(g => g.Key, g => g.SelectMany(a => a.Cluster.Frames).ToHashSet());
        var additions = new List<CastAssignment>();
        foreach (var cl in clusters.Where(c => !named.Contains(c)))
        {
            var ranked = accepted
                .Select(a => (A: a, S: Vec.Dot(cl.Centroid, a.Cluster.Centroid)))
                .GroupBy(x => x.A.Person.PersonId)
                .Select(g => g.MaxBy(x => x.S))
                .OrderByDescending(x => x.S)
                .ToList();
            var best = ranked[0];
            float second = ranked.Count > 1 ? ranked[1].S : 0;
            var owned = personFrames[best.A.Person.PersonId];
            if (best.S >= threshold && best.S - second >= margin && !FrameSets.Conflict(owned, cl.Frames))
            {
                owned.UnionWith(cl.Frames);
                additions.Add(new CastAssignment(cl, best.A.Person, best.A.Score, best.S - second));
            }
        }

        accepted.AddRange(additions);
    }

    /// <summary>
    /// Similarity of a cluster to a person: the mean of the two best references when there are
    /// several, so one odd photo can neither make nor break a match.
    /// </summary>
    internal static float Score(float[] centroid, List<float[]> refs)
    {
        if (refs.Count == 1)
        {
            return Vec.Dot(centroid, refs[0]);
        }

        float a = float.MinValue, b = float.MinValue;
        foreach (var r in refs)
        {
            float s = Vec.Dot(centroid, r);
            if (s > a)
            {
                b = a;
                a = s;
            }
            else if (s > b)
            {
                b = s;
            }
        }

        return refs.Count >= 3 ? (a + b) / 2 : a;
    }
}

/// <summary>Turns named clusters into per-person scene intervals.</summary>
public static class TimelineBuilder
{
    public static List<TimelinePerson> Build(
        IEnumerable<CastAssignment> assignments,
        double frameSeconds,
        double sceneGapSeconds,
        double minScreenTimeSeconds)
    {
        var people = new List<TimelinePerson>();
        foreach (var g in assignments.GroupBy(a => a.Person.PersonId))
        {
            var person = g.First().Person;
            var (screen, merged) = Spans(g.SelectMany(a => a.Cluster.Tracks), frameSeconds, sceneGapSeconds);
            if (screen < minScreenTimeSeconds)
            {
                continue;
            }

            people.Add(new TimelinePerson
            {
                PersonId = person.PersonId,
                Name = person.Name,
                Role = person.Role,
                ScreenTimeSeconds = Math.Round(screen, 1),
                Confidence = Math.Round(g.Max(a => a.Score), 3),
                Intervals = merged,
                Spans = g.SelectMany(a => a.Cluster.Tracks)
                    .Select(t => new[] { Math.Round(Math.Max(0, t.Start - (frameSeconds / 2)), 1), Math.Round(t.End + (frameSeconds / 2), 1) })
                    .OrderBy(s => s[0])
                    .ToList(),
            });
        }

        return people.OrderByDescending(p => p.ScreenTimeSeconds).ToList();
    }

    /// <summary>Screen time and scene intervals (gaps up to <paramref name="gap"/> bridged) for tracks.</summary>
    public static (double Screen, List<double[]> Intervals) Spans(IEnumerable<FaceTrack> tracks, double frameSeconds, double gap)
    {
        var spans = tracks
            .Select(t => (Start: Math.Max(0, t.Start - (frameSeconds / 2)), End: t.End + (frameSeconds / 2)))
            .OrderBy(s => s.Start)
            .ToList();

        // Screen time counts each moment once even if two of a person's tracks overlap.
        double screen = 0, coveredTo = double.MinValue;
        var merged = new List<double[]>();
        foreach (var (s, e) in spans)
        {
            screen += Math.Max(0, e - Math.Max(s, coveredTo));
            coveredTo = Math.Max(coveredTo, e);
            if (merged.Count > 0 && s - merged[^1][1] <= gap)
            {
                merged[^1][1] = Math.Max(merged[^1][1], e);
            }
            else
            {
                merged.Add([s, e]);
            }
        }

        return (Math.Round(screen, 1), merged.Select(m => new[] { Math.Round(m[0], 1), Math.Round(m[1], 1) }).ToList());
    }
}
