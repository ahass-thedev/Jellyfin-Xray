namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// Moves intervals between a file's own time and a variant's canonical time through an accepted
/// fit t_local = a · t_canonical + b (design §3.6). Anything outside the range the fit is trusted
/// on, or outside the target runtime, is clipped: dropped ends are discarded, never extrapolated.
/// </summary>
public static class TimelineMapping
{
    /// <summary>Pieces shorter than this after clipping are dropped.</summary>
    public const double MinPieceSeconds = 0.05;

    /// <summary>Local → canonical, for a contributor's fit.</summary>
    public static List<double[]> ToCanonical(IEnumerable<double[]> local, double slope, double offset, double validFrom, double validTo, double canonicalRuntime) =>
        Map(local, t => (t - offset) / slope, Math.Max(0, validFrom), Math.Min(canonicalRuntime, validTo));

    /// <summary>Canonical → local, for a receiver's fit.</summary>
    public static List<double[]> ToLocal(IEnumerable<double[]> canonical, double slope, double offset, double validFrom, double validTo, double localRuntime)
    {
        var clipped = Clip(canonical, validFrom, validTo);
        return Map(clipped, t => (slope * t) + offset, 0, localRuntime);
    }

    /// <summary>Maps every interval with <paramref name="f"/> (increasing), then clips to [lo, hi].</summary>
    public static List<double[]> Map(IEnumerable<double[]> intervals, Func<double, double> f, double lo, double hi) =>
        Clip(intervals.Where(i => i.Length >= 2).Select(i => new[] { f(i[0]), f(i[1]) }), lo, hi);

    public static List<double[]> Clip(IEnumerable<double[]> intervals, double lo, double hi) =>
        intervals
            .Where(i => i.Length >= 2)
            .Select(i => new[] { Math.Round(Math.Max(lo, i[0]), 2), Math.Round(Math.Min(hi, i[1]), 2) })
            .Where(i => i[1] - i[0] >= MinPieceSeconds)
            .ToList();

    /// <summary>Total length of sorted, non-overlapping intervals.</summary>
    public static double Total(IEnumerable<double[]> intervals) => intervals.Sum(i => Math.Max(0, i[1] - i[0]));

    /// <summary>Length of the intersection of two sets of intervals.</summary>
    public static double Overlap(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        double sum = 0;
        int i = 0, j = 0;
        var sa = a.OrderBy(x => x[0]).ToList();
        var sb = b.OrderBy(x => x[0]).ToList();
        while (i < sa.Count && j < sb.Count)
        {
            double lo = Math.Max(sa[i][0], sb[j][0]), hi = Math.Min(sa[i][1], sb[j][1]);
            if (hi > lo)
            {
                sum += hi - lo;
            }

            if (sa[i][1] < sb[j][1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return sum;
    }

    /// <summary>Sorts and merges overlapping or touching intervals.</summary>
    public static List<double[]> Merge(IEnumerable<double[]> intervals, double gap = 0)
    {
        var result = new List<double[]>();
        foreach (var i in intervals.Where(x => x.Length >= 2 && x[1] > x[0]).OrderBy(x => x[0]))
        {
            if (result.Count > 0 && i[0] <= result[^1][1] + gap)
            {
                result[^1][1] = Math.Max(result[^1][1], i[1]);
            }
            else
            {
                result.Add([i[0], i[1]]);
            }
        }

        return result;
    }
}
