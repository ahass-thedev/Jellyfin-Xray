using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Voice;

/// <summary>A credited person who might be speaking.</summary>
public sealed class VoiceCandidate
{
    public required Character Character { get; init; }

    /// <summary>Gets voiceprints learned for this actor from other titles.</summary>
    public List<float[]> References { get; init; } = [];

    /// <summary>Gets raw on-screen face spans in this title (live action only).</summary>
    public List<double[]> FaceSpans { get; init; } = [];
}

/// <summary>A voice tied to a person, and how sure we are.</summary>
public sealed record VoiceLabel(VoiceCluster Cluster, VoiceCandidate Person, string Source, double Score, bool Learnable);

/// <summary>
/// Names voices automatically, strongest evidence first:
/// 1. SDH speaker tags ("[Buddy]") on the voice's lines;
/// 2. the voice gallery (this actor's voice, learned from other titles);
/// 3. names said in the dialogue (<see cref="DialogueNamer"/>);
/// 4. live action: whose face is on screen while the voice speaks.
/// Nobody is ever asked to label anything.
/// </summary>
public static class VoiceNamer
{
    public const float GalleryThreshold = 0.55f;
    public const float GalleryMargin = 0.1f;

    public static List<VoiceLabel> Name(VoiceAnalysis a, IReadOnlyList<VoiceCandidate> cast)
    {
        var labels = new List<VoiceLabel>();
        var open = a.Clusters.Where(c => c.Cues.Count >= 3).ToList();
        void Take(VoiceCluster c, VoiceCandidate p, string source, double score, bool learn)
        {
            labels.Add(new VoiceLabel(c, p, source, score, learn));
            open.Remove(c);
        }

        // 1. Speaker tags. A tag names a character when exactly one candidate answers to it.
        foreach (var c in open.ToList())
        {
            var votes = c.Cues.Where(q => q.SpeakerTag is not null)
                .Select(q => Resolve(q.SpeakerTag!, cast))
                .Where(p => p is not null)
                .GroupBy(p => p!)
                .OrderByDescending(g => g.Count())
                .ToList();
            int tagged = c.Cues.Count(q => q.SpeakerTag is not null);
            if (votes.Count > 0 && votes[0].Count() >= 2 && votes[0].Count() >= 0.6 * tagged)
            {
                Take(c, votes[0].Key!, "tags", votes[0].Count() / (double)tagged, votes[0].Count() >= 3);
            }
        }

        // 2. Voice gallery. A person may own several voices (Buddy and Dark Buddy are one actor).
        foreach (var c in open.ToList())
        {
            var ranked = cast.Where(p => p.References.Count > 0)
                .Select(p => (P: p, S: p.References.Max(r => Vec.Dot(c.Centroid, r))))
                .OrderByDescending(x => x.S)
                .ToList();
            if (ranked.Count > 0 && ranked[0].S >= GalleryThreshold && ranked[0].S - (ranked.Count > 1 ? ranked[1].S : 0) >= GalleryMargin)
            {
                Take(c, ranked[0].P, "gallery", ranked[0].S, ranked[0].S >= 0.7);
            }
        }

        // 3. Dialogue, for voices and characters still unclaimed.
        var claimed = labels.Select(l => l.Person).ToHashSet();
        var free = cast.Where(p => !claimed.Contains(p)).ToList();
        var openMap = a.CueCluster.Where(kv => open.Any(c => c.Id == kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (clusterId, (ch, score, margin)) in DialogueNamer.Infer(a.Cues, openMap, free.Select(p => p.Character).ToList()))
        {
            var c = open.First(x => x.Id == clusterId);
            Take(c, free[ch], "dialogue", score, score >= 4 && margin >= 3);
        }

        // 4. Faces on screen while the voice speaks (the speaker is usually the one in shot).
        foreach (var c in open.Where(c => c.Cues.Count >= 8).ToList())
        {
            double speech = c.SpeechSeconds;
            var ranked = cast.Where(p => p.FaceSpans.Count > 0)
                .Select(p => (P: p, F: c.Cues.Sum(q => Overlap(q, p.FaceSpans)) / speech))
                .OrderByDescending(x => x.F)
                .ToList();
            if (ranked.Count > 0 && ranked[0].F >= 0.5 && ranked[0].F >= 2 * (ranked.Count > 1 ? ranked[1].F : 0))
            {
                Take(c, ranked[0].P, "faces", ranked[0].F, ranked[0].F >= 0.6 && c.Cues.Count >= 15);
            }
        }

        return labels;
    }

    /// <summary>Speaking intervals for a person's voices, bridging gaps within a scene.</summary>
    public static (double Seconds, List<double[]> Intervals) Spans(IEnumerable<VoiceCluster> clusters, double gap)
    {
        var cues = clusters.SelectMany(c => c.Cues).OrderBy(c => c.Start).ToList();
        var merged = new List<double[]>();
        foreach (var q in cues)
        {
            if (merged.Count > 0 && q.Start - merged[^1][1] <= gap)
            {
                merged[^1][1] = Math.Max(merged[^1][1], q.End);
            }
            else
            {
                merged.Add([q.Start, q.End]);
            }
        }

        return (Math.Round(cues.Sum(c => c.Duration), 1), merged.Select(m => new[] { Math.Round(m[0], 1), Math.Round(m[1], 1) }).ToList());
    }

    private static VoiceCandidate? Resolve(string tag, IReadOnlyList<VoiceCandidate> cast)
    {
        var hits = cast.Where(p => p.Character.Names.Any(n =>
                n.Equals(tag, StringComparison.OrdinalIgnoreCase)
                || tag.EndsWith(" " + n, StringComparison.OrdinalIgnoreCase))) // "Nurse Grace" -> Grace
            .ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    private static double Overlap(SubtitleCue q, List<double[]> spans)
    {
        double total = 0;
        foreach (var s in spans)
        {
            total += Math.Max(0, Math.Min(q.End, s[1]) - Math.Max(q.Start, s[0]));
        }

        return Math.Min(total, q.Duration);
    }
}
