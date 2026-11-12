namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>One stretch of canonical time that maps into the local file at its own offset.</summary>
public sealed record AlignedSegment(double CanonicalFrom, double CanonicalTo, double Offset, int Inliers, double RmsSeconds);

/// <summary>
/// A piecewise fit (design §3, phase 5): one slope, several offsets. t_local = Slope · t + Offset
/// of the segment holding t; canonical time outside every segment has no local counterpart.
/// </summary>
public sealed record PiecewiseResult
{
    public bool Accepted => Reasons.Count == 0;

    public IReadOnlyList<string> Reasons { get; init; } = [];

    public double Slope { get; init; } = 1;

    public IReadOnlyList<AlignedSegment> Segments { get; init; } = [];

    /// <summary>Share of the local runtime the segments explain.</summary>
    public double Coverage { get; init; }

    public string Verdict => Accepted ? $"ACCEPT ({Segments.Count} segments, {Coverage:P0})" : "REJECT: " + string.Join("; ", Reasons);

    /// <summary>Maps canonical intervals through the segments; what falls between them is dropped.</summary>
    public List<double[]> ToLocal(IEnumerable<double[]> canonical, double localRuntime)
    {
        var list = canonical.ToList();
        return TimelineMapping.Merge(Segments.SelectMany(s =>
            TimelineMapping.ToLocal(list, Slope, s.Offset, s.CanonicalFrom, s.CanonicalTo, localRuntime)));
    }
}

public sealed record PiecewiseOptions
{
    /// <summary>Inliers a segment needs.</summary>
    public int MinSegmentInliers { get; init; } = 8;

    /// <summary>Shortest segment worth keeping.</summary>
    public double MinSegmentSeconds { get; init; } = 120;

    /// <summary>Data this close to a splice is dropped, on both sides (design §3: ±60 s).</summary>
    public double SpliceMarginSeconds { get; init; } = 60;

    /// <summary>The segments must explain at least this share of the local runtime.</summary>
    public double MinCoverage { get; init; } = 0.5;

    public int MaxSegments { get; init; } = 8;

    public static PiecewiseOptions Default { get; } = new();
}

/// <summary>
/// Aligns another cut of a title piecewise, so it can reuse the shared data for the scenes the
/// two cuts have in common (design §3, "Piecewise alignment"). Used only on the receiving side,
/// and only after the single-line fit rejected the file: a different cut never joins a variant.
/// </summary>
public static class PiecewiseAligner
{
    public static PiecewiseResult Align(AnchorSet reference, uint[] local, double canonicalRuntime, double localRuntime, AlignmentOptions? options = null, PiecewiseOptions? piecewise = null)
    {
        var o = options ?? AlignmentOptions.Default;
        var p = piecewise ?? PiecewiseOptions.Default;
        var informative = Enumerable.Range(0, reference.Count).Where(i => AnchorSet.IsInformative(reference.Windows[i])).ToList();
        var located = new FingerprintMatcher(reference.Windows).Locate(local, o.CandidatesPerAnchor, o.MaxBitErrorRate);
        var pairs = informative
            .SelectMany(i => located[i].Select(l => new AnchorPair(i, reference.Time(i), l.QueryItem * FingerprintMatcher.ItemSeconds, l.BitErrorRate)))
            .ToList();
        return Fit(pairs, informative.Select(reference.Time).ToList(), reference.Length * FingerprintMatcher.ItemSeconds, canonicalRuntime, localRuntime, o, p);
    }

    public static PiecewiseResult Fit(IReadOnlyList<AnchorPair> pairs, IReadOnlyList<double> informativeTimes, double anchorSeconds, double canonicalRuntime, double localRuntime, AlignmentOptions o, PiecewiseOptions p)
    {
        double tol = o.InlierToleranceSeconds;

        // 1. For each plausible slope, peel off lines: the offset shared by the most anchors, then
        //    the next among the anchors left, and so on.
        List<(double Offset, List<AnchorPair> Pairs)> best = [];
        double bestSlope = 1;
        foreach (double slope in new[] { 1, Chromaprint.PalRatio, 1 / Chromaprint.PalRatio })
        {
            var lines = Lines(pairs, slope, tol, p);
            if (lines.Sum(l => l.Pairs.Count) > best.Sum(l => l.Pairs.Count))
            {
                (best, bestSlope) = (lines, slope);
            }
        }

        if (best.Count < 2)
        {
            return new PiecewiseResult { Reasons = ["fewer than two consistent offsets"] };
        }

        // 2. Refine the shared slope by least squares within lines, then each line's offset.
        double sxy = 0, sxx = 0;
        foreach (var (_, ps) in best)
        {
            double mc = ps.Average(x => x.Canonical), ml = ps.Average(x => x.Local);
            sxy += ps.Sum(x => (x.Canonical - mc) * (x.Local - ml));
            sxx += ps.Sum(x => (x.Canonical - mc) * (x.Canonical - mc));
        }

        double a = sxx > 0 ? sxy / sxx : bestSlope;
        if (!Aligner.PlausibleSlope(a, o.SlopeTolerance))
        {
            return new PiecewiseResult { Reasons = [$"slope {a:F5} is neither 1 nor PAL"] };
        }

        var lineOffsets = best.Select(l => l.Pairs.Average(x => x.Local - (a * x.Canonical))).ToList();

        // 3. Label every anchor by the line it sits on, in canonical order; runs of one label are
        //    segments. Short runs (repeated music, a stray match) are noise and do not split.
        var labelled = new List<(AnchorPair P, int Line)>();
        foreach (var g in pairs.GroupBy(x => x.Anchor))
        {
            var hits = g.SelectMany(x => lineOffsets.Select((b, k) => (P: x, K: k, R: Math.Abs(x.Local - ((a * x.Canonical) + b)))))
                .Where(x => x.R <= tol)
                .OrderBy(x => x.R)
                .ToList();
            if (hits.Count > 0)
            {
                labelled.Add((hits[0].P, hits[0].K));
            }
        }

        labelled.Sort((x, y) => x.P.Canonical.CompareTo(y.P.Canonical));
        var runs = Runs(labelled);
        runs = Runs(runs.Where(r => r.Count >= 3).SelectMany(r => r).ToList());

        var segments = new List<AlignedSegment>();
        foreach (var run in runs.Where(r => r.Count >= p.MinSegmentInliers))
        {
            double b = lineOffsets[run[0].Line];
            double from = run[0].P.Canonical, to = run[^1].P.Canonical + anchorSeconds;
            var residuals = run.Select(x => x.P.Local - ((a * x.P.Canonical) + b)).ToList();
            double rms = Math.Sqrt(residuals.Average(r => r * r));
            int considered = informativeTimes.Count(t => t >= from - 1e-6 && t + anchorSeconds <= to + 1e-6);
            if (rms <= o.MaxRmsSeconds && considered > 0 && (double)run.Count / considered >= o.MinInlierFraction)
            {
                segments.Add(new AlignedSegment(from, to, b, run.Count, rms));
            }
        }

        // 4. Splices: data within ±60 s of where one segment hands over to the next is dropped.
        segments = segments.OrderBy(s => s.CanonicalFrom).ToList();
        var trimmed = new List<AlignedSegment>();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            double from = i > 0 ? s.CanonicalFrom + p.SpliceMarginSeconds : s.CanonicalFrom;
            double to = i < segments.Count - 1 ? s.CanonicalTo - p.SpliceMarginSeconds : s.CanonicalTo;
            if (to - from >= p.MinSegmentSeconds)
            {
                trimmed.Add(s with { CanonicalFrom = from, CanonicalTo = Math.Min(canonicalRuntime, to) });
            }
        }

        var reasons = new List<string>();
        if (trimmed.Count < 2)
        {
            reasons.Add($"{trimmed.Count} usable segment(s); a single line is the plain fit's job");
        }

        // The local file must play the segments in order, without overlap.
        for (int i = 1; i < trimmed.Count; i++)
        {
            if ((a * trimmed[i].CanonicalFrom) + trimmed[i].Offset < (a * trimmed[i - 1].CanonicalTo) + trimmed[i - 1].Offset - 1)
            {
                reasons.Add("segments overlap or play out of order in the local file");
                break;
            }
        }

        double covered = trimmed.Sum(s => Math.Max(0, Math.Min(localRuntime, (a * s.CanonicalTo) + s.Offset) - Math.Max(0, (a * s.CanonicalFrom) + s.Offset)));
        double coverage = localRuntime > 0 ? covered / localRuntime : 0;
        if (coverage < p.MinCoverage)
        {
            reasons.Add($"segments cover {coverage:P0} of the local runtime, need {p.MinCoverage:P0}");
        }

        return new PiecewiseResult { Reasons = reasons, Slope = a, Segments = trimmed, Coverage = Math.Min(1, coverage) };
    }

    private static List<List<(AnchorPair P, int Line)>> Runs(List<(AnchorPair P, int Line)> labelled)
    {
        var runs = new List<List<(AnchorPair P, int Line)>>();
        foreach (var x in labelled)
        {
            if (runs.Count > 0 && runs[^1][^1].Line == x.Line)
            {
                runs[^1].Add(x);
            }
            else
            {
                runs.Add([x]);
            }
        }

        return runs;
    }

    private static List<(double Offset, List<AnchorPair> Pairs)> Lines(IReadOnlyList<AnchorPair> pairs, double slope, double tol, PiecewiseOptions p)
    {
        var pool = pairs.Select(x => (P: x, D: x.Local - (slope * x.Canonical))).OrderBy(x => x.D).ToList();
        var lines = new List<(double, List<AnchorPair>)>();
        var used = new HashSet<int>();
        while (lines.Count < p.MaxSegments)
        {
            var left = pool.Where(x => !used.Contains(x.P.Anchor)).ToList();
            (int Count, int Lo, int Hi) bestWindow = (0, 0, 0);
            for (int i = 0, j = 0; i < left.Count; i++)
            {
                while (left[i].D - left[j].D > 2 * tol)
                {
                    j++;
                }

                int distinct = left.Skip(j).Take(i - j + 1).Select(x => x.P.Anchor).Distinct().Count();
                if (distinct > bestWindow.Count)
                {
                    bestWindow = (distinct, j, i);
                }
            }

            if (bestWindow.Count < p.MinSegmentInliers)
            {
                break;
            }

            var members = left.Skip(bestWindow.Lo).Take(bestWindow.Hi - bestWindow.Lo + 1)
                .GroupBy(x => x.P.Anchor).Select(g => g.First().P).ToList();
            lines.Add((members.Average(x => x.Local - (slope * x.Canonical)), members));
            used.UnionWith(members.Select(x => x.Anchor));
        }

        return lines;
    }
}
