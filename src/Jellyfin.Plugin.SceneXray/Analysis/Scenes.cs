using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Analysis;

/// <summary>One sampled frame's colour fingerprint.</summary>
public readonly record struct FrameColor(double Time, float[] Histogram);

/// <summary>
/// Groups shots into scenes. A conversation cuts back and forth between angles of the same
/// set, so a shot that "links back" to a recent shot (similar colours, or the same people on
/// screen) continues that scene; a shot that links back to nothing starts a new one. This is
/// the classic overlapping-links method.
/// </summary>
public static class SceneDetector
{
    public const double ShotCut = 0.4;
    public const double LinkDistance = 0.25;

    /// <summary>Sharing a person links two shots only if their sets are also roughly alike:
    /// a lead who is in every scene would otherwise chain the whole film together.</summary>
    public const double PeopleLinkDistance = 0.35;
    public const double LinkWindowSeconds = 30;
    public const double MinSceneSeconds = 8;

    private sealed class Shot
    {
        public double Start;
        public double End;
        public float[] Color = [];
        public HashSet<Guid> People = [];
    }

    /// <summary>Returns scenes as sorted, contiguous [start, end] pairs covering the title.</summary>
    /// <param name="frames">Frames in time order (about one per second).</param>
    /// <param name="presence">Per person, raw spans on screen or speaking; shared people link shots.</param>
    public static List<double[]> Detect(
        IReadOnlyList<FrameColor> frames,
        IReadOnlyDictionary<Guid, List<double[]>> presence,
        double step = 1,
        double linkDistance = LinkDistance,
        double peopleLinkDistance = PeopleLinkDistance,
        double window = LinkWindowSeconds)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        // 1. Shots.
        var shots = new List<Shot>();
        var current = new List<FrameColor> { frames[0] };
        for (int i = 1; i <= frames.Count; i++)
        {
            if (i == frames.Count || RgbImage.HistogramDistance(frames[i - 1].Histogram, frames[i].Histogram) > ShotCut)
            {
                shots.Add(new Shot
                {
                    Start = current[0].Time,
                    End = current[^1].Time + step,
                    Color = Mean(current.Select(f => f.Histogram)),
                });
                if (i < frames.Count)
                {
                    current = [frames[i]];
                }
            }
            else
            {
                current.Add(frames[i]);
            }
        }

        foreach (var shot in shots)
        {
            foreach (var (person, spans) in presence)
            {
                if (spans.Sum(s => Math.Max(0, Math.Min(s[1], shot.End) - Math.Max(s[0], shot.Start))) >= 0.75)
                {
                    shot.People.Add(person);
                }
            }
        }

        // 2. Links: shot i joins every shot back to the furthest one it links to.
        var sameAsPrevious = new bool[shots.Count];
        for (int i = 1; i < shots.Count; i++)
        {
            for (int j = i - 1; j >= 0 && shots[i].Start - shots[j].End <= window; j--)
            {
                double d = RgbImage.HistogramDistance(shots[i].Color, shots[j].Color);
                bool linked = d <= linkDistance || (d <= peopleLinkDistance && shots[i].People.Overlaps(shots[j].People));
                if (linked)
                {
                    for (int k = j + 1; k <= i; k++)
                    {
                        sameAsPrevious[k] = true;
                    }
                }
            }
        }

        var scenes = new List<double[]>();
        for (int i = 0; i < shots.Count; i++)
        {
            if (i > 0 && sameAsPrevious[i])
            {
                scenes[^1][1] = shots[i].End;
            }
            else
            {
                scenes.Add([shots[i].Start, shots[i].End]);
            }
        }

        // 3. Fold slivers (a cutaway, a title card) into the previous scene.
        var merged = new List<double[]>();
        foreach (var s in scenes)
        {
            if (merged.Count > 0 && (s[1] - s[0] < MinSceneSeconds || merged[^1][1] - merged[^1][0] < MinSceneSeconds))
            {
                merged[^1][1] = s[1];
            }
            else
            {
                merged.Add(s);
            }
        }

        return merged.Select(s => new[] { Math.Round(s[0], 1), Math.Round(s[1], 1) }).ToList();
    }

    private static float[] Mean(IEnumerable<float[]> hists)
    {
        float[]? sum = null;
        int n = 0;
        foreach (var h in hists)
        {
            sum ??= new float[h.Length];
            for (int i = 0; i < h.Length; i++)
            {
                sum[i] += h[i];
            }

            n++;
        }

        for (int i = 0; i < sum!.Length; i++)
        {
            sum[i] /= n;
        }

        return sum;
    }
}

/// <summary>
/// "In scene", the way X-Ray means it: someone seen on screen for a moment, or heard, is in the
/// whole scene, not just the shots their face is in. Very long scenes fall back to the span
/// from their first to last appearance, so a 10-minute sequence doesn't keep someone who left.
/// </summary>
public static class ScenePresence
{
    public const double MinSeenSeconds = 2;
    public const double MinHeardSeconds = 0.8;
    public const double LongSceneSeconds = 300;

    public static List<double[]> Expand(IReadOnlyList<double[]> raw, IReadOnlyList<double[]> scenes, bool heard)
    {
        if (scenes.Count == 0)
        {
            return raw.ToList();
        }

        double need = heard ? MinHeardSeconds : MinSeenSeconds;
        var result = new List<double[]>();
        foreach (var scene in scenes)
        {
            var inside = raw.Where(r => r[1] > scene[0] && r[0] < scene[1]).ToList();
            double time = inside.Sum(r => Math.Min(r[1], scene[1]) - Math.Max(r[0], scene[0]));
            if (time < need)
            {
                continue;
            }

            if (scene[1] - scene[0] <= LongSceneSeconds)
            {
                result.Add([scene[0], scene[1]]);
            }
            else
            {
                result.Add([Math.Max(scene[0], inside.Min(r => r[0]) - 10), Math.Min(scene[1], inside.Max(r => r[1]) + 20)]);
            }
        }

        return Union(result);
    }

    public static List<double[]> Union(IEnumerable<double[]> intervals)
    {
        var merged = new List<double[]>();
        foreach (var i in intervals.OrderBy(i => i[0]))
        {
            if (merged.Count > 0 && i[0] <= merged[^1][1] + 0.5)
            {
                merged[^1][1] = Math.Max(merged[^1][1], i[1]);
            }
            else
            {
                merged.Add([i[0], i[1]]);
            }
        }

        return merged;
    }
}
