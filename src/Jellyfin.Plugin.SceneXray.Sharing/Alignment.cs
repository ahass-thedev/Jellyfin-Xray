using System.Diagnostics;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// Thresholds of the alignment fit and its acceptance rules: design §3, tuned on real and synthetic
/// pairs in phase 0 (MR description). Where they differ from §3, dubs are why: anchors over dialogue
/// match only in the original language, so a dub aligns on its music and effects alone, at 26–65 %
/// of anchors with gaps of up to 21 minutes. Different cuts are told apart by the second-offset rule
/// instead, which no same-content pair ever tripped.
/// </summary>
public sealed record AlignmentOptions
{
    /// <summary>
    /// Inliers among the informative reference anchors inside the overlap. Same-language pairs
    /// measured ≥ 78 %; dubs 26–65 %. A dub matches on music and effects only, too sparsely to see a
    /// short shifted scene, so below this it is rejected (and analysed locally).
    /// </summary>
    public double MinInlierFraction { get; init; } = 0.6;

    public int MinInliers { get; init; } = 20;

    public double MaxRmsSeconds { get; init; } = 0.25;

    /// <summary>
    /// Longest stretch of informative anchors without an inlier, inside the overlap or at its ends
    /// (§3: 180 s). Accepted pairs measured up to 300 s (four reference anchors missed in a row,
    /// pitch-corrected PAL and partial dubs); a cut 5 minutes before the end left 332 s.
    /// </summary>
    public double MaxHoleSeconds { get; init; } = 310;

    /// <summary>Relative slack around a slope of 1 or the PAL ratio (either way).</summary>
    public double SlopeTolerance { get; init; } = 0.002;

    /// <summary>Share of the local runtime the fit must cover, after <see cref="EndSlackSeconds"/> at each end.</summary>
    public double MinCoverage { get; init; } = 0.95;

    public double EndSlackSeconds { get; init; } = 180;

    /// <summary>A pair is an inlier when its residual is at most this.</summary>
    public double InlierToleranceSeconds { get; init; } = 0.5;

    /// <summary>
    /// The PAL retry runs when the local runtime, with the PAL speed-up undone, is within this share
    /// of the canonical runtime plus <see cref="PalRuntimeSlackSeconds"/> (§3: 0.3 %, which a 20 s
    /// logo already exceeds). It only runs after the plain fit failed, so a wide gate costs one more
    /// fingerprint pass at most.
    /// </summary>
    public double PalRuntimeTolerance { get; init; } = 0.01;

    /// <summary>Different logos and trimmed credits: up to <see cref="EndSlackSeconds"/> at each end.</summary>
    public double PalRuntimeSlackSeconds { get; init; } = 360;

    public double MaxBitErrorRate { get; init; } = FingerprintMatcher.MaxBitErrorRate;

    public int CandidatesPerAnchor { get; init; } = 3;

    public int RansacIterations { get; init; } = 2000;

    /// <summary>
    /// Anchors that agree on a second offset (same slope, shifted by more than twice the inlier
    /// tolerance) are proof of a different cut: the audio on one side of a cut or insert still
    /// matches, only elsewhere. This many such anchors reject the fit.
    /// </summary>
    public int MinSecondLineAnchors { get; init; } = 3;

    public static AlignmentOptions Default { get; } = new();
}

/// <summary>One located anchor: its canonical time and a local time where its audio was found.</summary>
public readonly record struct AnchorPair(int Anchor, double Canonical, double Local, double BitErrorRate);

/// <summary>The fit t_local = Slope · t_canonical + Offset, its statistics and the verdict.</summary>
public sealed record AlignmentResult
{
    public bool Accepted => Reasons.Count == 0;

    /// <summary>Why the fit was rejected; empty when accepted.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary><c>plain</c>, or <c>pal</c> when the PAL re-fingerprint produced the fit.</summary>
    public string Kind { get; init; } = "plain";

    public double Slope { get; init; }

    public double Offset { get; init; }

    public int Inliers { get; init; }

    /// <summary>Informative reference anchors inside the overlap: the denominator of the inlier rule.</summary>
    public int Considered { get; init; }

    /// <summary>Anchors located at least once (before the fit).</summary>
    public int Located { get; init; }

    public double InlierFraction => Considered == 0 ? 0 : (double)Inliers / Considered;

    public double RmsSeconds { get; init; }

    public double MaxAbsResidualSeconds { get; init; }

    public double MaxHoleSeconds { get; init; }

    public double Coverage { get; init; }

    /// <summary>Anchors agreeing on the strongest second offset (cut evidence), and that offset minus <see cref="Offset"/>.</summary>
    public int SecondLineAnchors { get; init; }

    public double SecondLineShift { get; init; }

    /// <summary>
    /// Canonical range the fit is trusted on: the overlap, cut back to the outermost inliers where
    /// anchors beyond them failed to match. Shared data outside it is dropped, never extrapolated,
    /// so a difference hiding in the end slack (a short cut next to the credits) costs data instead
    /// of misplacing it.
    /// </summary>
    public double ValidFrom { get; init; }

    public double ValidTo { get; init; }

    /// <summary>Wall time of matching and fitting (not fingerprinting).</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>When the PAL retry ran: the attempt not reported here (plain or <c>pal</c>).</summary>
    public AlignmentResult? OtherAttempt { get; init; }

    public double ToLocal(double canonical) => (Slope * canonical) + Offset;

    public string Verdict => Accepted ? "ACCEPT" : "REJECT: " + string.Join("; ", Reasons);
}

/// <summary>
/// Aligns a variant's reference anchors with a local fingerprint (design §3): locate every anchor
/// in the dense local fingerprint, fit t_local = a·t + b by RANSAC and least squares, and accept
/// only a fit that explains the whole file. Different cuts are rejected, never mis-aligned.
/// </summary>
public static class Aligner
{
    public static AlignmentResult Align(AnchorSet reference, uint[] local, double canonicalRuntime, double localRuntime, AlignmentOptions? options = null)
    {
        var o = options ?? AlignmentOptions.Default;
        var sw = Stopwatch.StartNew();
        var informative = Enumerable.Range(0, reference.Count).Where(i => AnchorSet.IsInformative(reference.Windows[i])).ToList();
        var matcher = new FingerprintMatcher(reference.Windows);
        var located = matcher.Locate(local, o.CandidatesPerAnchor, o.MaxBitErrorRate);
        var pairs = informative
            .SelectMany(i => located[i].Select(l => new AnchorPair(i, reference.Time(i), l.QueryItem * FingerprintMatcher.ItemSeconds, l.BitErrorRate)))
            .ToList();
        var result = Fit(pairs, informative.Select(reference.Time).ToList(), reference.Length * FingerprintMatcher.ItemSeconds, canonicalRuntime, localRuntime, o);
        return result with { Located = informative.Count(i => located[i].Count > 0), Elapsed = sw.Elapsed };
    }

    /// <summary>
    /// <see cref="Align"/>, then the PAL retry (design §3.5): when the plain fit fails and the
    /// runtimes differ by the PAL ratio, the local audio is fingerprinted again with tempo and pitch
    /// undone (<paramref name="refingerprint"/> gets the <c>-af</c> chain) and fitted once more.
    /// </summary>
    public static async Task<AlignmentResult> AlignAsync(
        AnchorSet reference,
        uint[] local,
        double canonicalRuntime,
        double localRuntime,
        Func<string, CancellationToken, Task<uint[]>> refingerprint,
        AlignmentOptions? options = null,
        CancellationToken ct = default)
    {
        var o = options ?? AlignmentOptions.Default;
        var plain = Align(reference, local, canonicalRuntime, localRuntime, o);
        if (plain.Accepted || PalSpeed(canonicalRuntime, localRuntime, o) is not { } speed)
        {
            return plain;
        }

        // speed < 1 slows a PAL local file back to film speed. The new fingerprint runs on a time
        // base stretched by 1/speed; fit in it, then scale back to local time.
        var retry = Align(reference, await refingerprint(Chromaprint.PalFilter(speed), ct).ConfigureAwait(false), canonicalRuntime, localRuntime / speed, o);
        retry = retry with
        {
            Kind = "pal",
            Slope = retry.Slope * speed,
            Offset = retry.Offset * speed,
            RmsSeconds = retry.RmsSeconds * speed,
            MaxAbsResidualSeconds = retry.MaxAbsResidualSeconds * speed,
        };

        // Report the attempt that worked, or else the plain one (a PAL file whose pitch was
        // corrected matches better without the retry), with the other attached.
        return retry.Accepted
            ? retry with { OtherAttempt = plain, Elapsed = plain.Elapsed + retry.Elapsed }
            : plain with { OtherAttempt = retry, Elapsed = plain.Elapsed + retry.Elapsed };
    }

    /// <summary>The playback speed that undoes a PAL difference, or null when the runtimes don't suggest one.</summary>
    public static double? PalSpeed(double canonicalRuntime, double localRuntime, AlignmentOptions? options = null)
    {
        var o = options ?? AlignmentOptions.Default;
        double slack = (o.PalRuntimeTolerance * canonicalRuntime) + o.PalRuntimeSlackSeconds;
        double nearest = double.MaxValue;
        double? best = null;

        // speed < 1: local is PAL, slow it down; speed > 1: canonical is PAL, speed local up.
        foreach (double speed in new[] { 1 / Chromaprint.PalRatio, Chromaprint.PalRatio })
        {
            double gap = Math.Abs((localRuntime / speed) - canonicalRuntime);
            if (gap <= slack && gap < nearest)
            {
                (nearest, best) = (gap, speed);
            }
        }

        // A runtime that also fits film speed suggests a different cut, not PAL.
        return best is not null && Math.Abs(localRuntime - canonicalRuntime) < nearest ? null : best;
    }

    public static bool PlausibleSlope(double a, double tolerance) =>
        Math.Abs(a - 1) <= tolerance
        || Math.Abs((a / Chromaprint.PalRatio) - 1) <= tolerance
        || Math.Abs((a * Chromaprint.PalRatio) - 1) <= tolerance;

    /// <summary>
    /// RANSAC + least squares over located anchor pairs, then the acceptance rules.
    /// </summary>
    /// <param name="pairs">Every location of every informative anchor (several per anchor allowed).</param>
    /// <param name="informativeTimes">Canonical times of all informative reference anchors.</param>
    /// <param name="anchorSeconds">Length of one anchor.</param>
    public static AlignmentResult Fit(IReadOnlyList<AnchorPair> pairs, IReadOnlyList<double> informativeTimes, double anchorSeconds, double canonicalRuntime, double localRuntime, AlignmentOptions? options = null)
    {
        var o = options ?? AlignmentOptions.Default;
        if (pairs.Select(p => p.Anchor).Distinct().Count() < 2)
        {
            return new AlignmentResult { Reasons = ["no alignment: fewer than 2 anchors located"], Considered = informativeTimes.Count };
        }

        // 1. RANSAC on two-point samples, far enough apart for a precise slope. Seeded: the same
        //    input always gives the same verdict.
        var rng = new Random(20261027);
        double lo = pairs.Min(p => p.Canonical), hi = pairs.Max(p => p.Canonical);
        double minSeparation = Math.Max(30, 0.25 * (hi - lo));
        (double A, double B, int Count, double Sse) best = (1, 0, -1, double.MaxValue);
        for (int attempt = 0, tried = 0; tried < o.RansacIterations && attempt < o.RansacIterations * 20; attempt++)
        {
            var p = pairs[rng.Next(pairs.Count)];
            var q = pairs[rng.Next(pairs.Count)];
            double dc = q.Canonical - p.Canonical;
            if (p.Anchor == q.Anchor || Math.Abs(dc) < minSeparation)
            {
                continue;
            }

            tried++;
            double a = (q.Local - p.Local) / dc;
            if (!PlausibleSlope(a, o.SlopeTolerance * 3))
            {
                continue;
            }

            double b = p.Local - (a * p.Canonical);
            var (count, sse) = Score(pairs, a, b, o.InlierToleranceSeconds);
            if (count > best.Count || (count == best.Count && sse < best.Sse))
            {
                best = (a, b, count, sse);
            }
        }

        if (best.Count < 2)
        {
            return new AlignmentResult { Reasons = ["no alignment: no consistent line with a plausible slope"], Considered = informativeTimes.Count };
        }

        // 2. Least squares on the inliers, repeated until the inlier set settles.
        double fa = best.A, fb = best.B;
        var inliers = Inliers(pairs, fa, fb, o.InlierToleranceSeconds);
        for (int i = 0; i < 10 && inliers.Count >= 2; i++)
        {
            (fa, fb) = LeastSquares(inliers);
            var next = Inliers(pairs, fa, fb, o.InlierToleranceSeconds);
            bool same = next.Count == inliers.Count && next.Zip(inliers).All(z => z.First == z.Second);
            inliers = next;
            if (same)
            {
                break;
            }
        }

        return Judge(pairs, inliers, informativeTimes, anchorSeconds, canonicalRuntime, localRuntime, fa, fb, o);
    }

    private static AlignmentResult Judge(IReadOnlyList<AnchorPair> pairs, List<AnchorPair> inliers, IReadOnlyList<double> informativeTimes, double anchorSeconds, double canonicalRuntime, double localRuntime, double a, double b, AlignmentOptions o)
    {
        var reasons = new List<string>();

        // The overlap: canonical times whose audio exists in both files according to the fit.
        double from = Math.Max(0, -b / a), to = Math.Min(canonicalRuntime, (localRuntime - b) / a);
        var considered = informativeTimes.Where(t => t >= from && t + anchorSeconds <= to).ToList();
        var inOverlap = inliers.Where(p => p.Canonical >= from && p.Canonical + anchorSeconds <= to).OrderBy(p => p.Canonical).ToList();
        int n = inOverlap.Count;
        double fraction = considered.Count == 0 ? 0 : (double)n / considered.Count;
        if (n < o.MinInliers || fraction < o.MinInlierFraction)
        {
            reasons.Add($"inliers {n}/{considered.Count} ({fraction:P0}), need ≥{o.MinInliers} and ≥{o.MinInlierFraction:P0}");
        }

        var residuals = inOverlap.Select(p => p.Local - ((a * p.Canonical) + b)).ToList();
        double rms = n == 0 ? 0 : Math.Sqrt(residuals.Average(r => r * r));
        if (rms > o.MaxRmsSeconds)
        {
            reasons.Add($"RMS residual {rms:F3} s > {o.MaxRmsSeconds} s");
        }

        // Holes: informative anchors without an inlier, measured from the inlier (or overlap edge)
        // before to the one after. Silence between two inliers is not a hole; a different cut is.
        double maxHole = 0;
        if (n > 0)
        {
            var inlierTimes = inOverlap.Select(p => p.Canonical).ToList();
            var edges = new List<double> { from };
            edges.AddRange(inlierTimes);
            edges.Add(to);
            for (int i = 0; i + 1 < edges.Count; i++)
            {
                double gapStart = edges[i], gapEnd = edges[i + 1];
                bool missed = considered.Any(t => t > gapStart + 1e-6 && t < gapEnd - 1e-6 && !inlierTimes.Contains(t));
                if (missed)
                {
                    maxHole = Math.Max(maxHole, gapEnd - gapStart);
                }
            }

            if (maxHole > o.MaxHoleSeconds)
            {
                reasons.Add($"hole of {maxHole:F0} s without an inlier > {o.MaxHoleSeconds} s (a different cut)");
            }
        }

        var (secondCount, secondShift) = SecondLine(pairs, inliers, a, b, o.InlierToleranceSeconds);
        if (secondCount >= o.MinSecondLineAnchors)
        {
            reasons.Add($"{secondCount} anchors agree on a second offset {secondShift:+0.0;-0.0} s (a different cut)");
        }

        if (!PlausibleSlope(a, o.SlopeTolerance))
        {
            reasons.Add($"slope {a:F5} is neither 1 nor PAL");
        }

        double head = Math.Max(0, b), tail = Math.Max(0, localRuntime - ((a * canonicalRuntime) + b));
        double covered = localRuntime - head - tail;
        double coverage = covered / Math.Max(1, localRuntime - Math.Min(o.EndSlackSeconds, head) - Math.Min(o.EndSlackSeconds, tail));
        if (coverage < o.MinCoverage)
        {
            reasons.Add($"coverage {coverage:P1} < {o.MinCoverage:P0} of the local runtime");
        }

        return new AlignmentResult
        {
            Reasons = reasons,
            Slope = a,
            Offset = b,
            Inliers = n,
            Considered = considered.Count,
            RmsSeconds = rms,
            MaxAbsResidualSeconds = n == 0 ? 0 : residuals.Max(Math.Abs),
            MaxHoleSeconds = maxHole,
            Coverage = Math.Min(1, coverage),
            SecondLineAnchors = secondCount,
            SecondLineShift = secondShift,
            // Up to the overlap's edges, unless an informative anchor beyond the outermost inlier
            // failed to match there: then only up to that inlier.
            ValidFrom = n == 0 ? 0 : considered.Any(t => t < inOverlap[0].Canonical) ? inOverlap[0].Canonical : from,
            ValidTo = n == 0 ? 0 : considered.Any(t => t > inOverlap[^1].Canonical) ? inOverlap[^1].Canonical + anchorSeconds : to,
        };
    }

    // The largest group of distinct off-line anchors sharing one offset away from the fit.
    private static (int Count, double Shift) SecondLine(IReadOnlyList<AnchorPair> pairs, List<AnchorPair> inliers, double a, double b, double tol)
    {
        // Only anchors with no location on the fitted line count: repeated music also matches a
        // few seconds away, but its anchors match on the line too. Across a cut they cannot.
        var onLine = inliers.Select(p => p.Anchor).ToHashSet();
        var others = pairs.Where(p => !onLine.Contains(p.Anchor))
            .Select(p => (p.Anchor, Shift: p.Local - (a * p.Canonical) - b))
            .Where(x => Math.Abs(x.Shift) > 2 * tol)
            .OrderBy(x => x.Shift)
            .ToList();
        (int Count, double Shift) best = (0, 0);
        for (int i = 0, j = 0; i < others.Count; i++)
        {
            while (others[i].Shift - others[j].Shift > 2 * tol)
            {
                j++;
            }

            int count = others.Skip(j).Take(i - j + 1).Select(x => x.Anchor).Distinct().Count();
            if (count > best.Count)
            {
                best = (count, others[(i + j) / 2].Shift);
            }
        }

        return best;
    }

    // Per anchor, the closest of its locations counts.
    private static (int Count, double Sse) Score(IReadOnlyList<AnchorPair> pairs, double a, double b, double tol)
    {
        var bestByAnchor = new Dictionary<int, double>();
        foreach (var p in pairs)
        {
            double r = Math.Abs(p.Local - ((a * p.Canonical) + b));
            if (r <= tol && (!bestByAnchor.TryGetValue(p.Anchor, out double old) || r < old))
            {
                bestByAnchor[p.Anchor] = r;
            }
        }

        return (bestByAnchor.Count, bestByAnchor.Values.Sum(r => r * r));
    }

    private static List<AnchorPair> Inliers(IReadOnlyList<AnchorPair> pairs, double a, double b, double tol) =>
        pairs.Where(p => Math.Abs(p.Local - ((a * p.Canonical) + b)) <= tol)
            .GroupBy(p => p.Anchor)
            .Select(g => g.MinBy(p => Math.Abs(p.Local - ((a * p.Canonical) + b))))
            .OrderBy(p => p.Anchor)
            .ToList();

    private static (double A, double B) LeastSquares(List<AnchorPair> points)
    {
        double mx = points.Average(p => p.Canonical), my = points.Average(p => p.Local);
        double sxy = points.Sum(p => (p.Canonical - mx) * (p.Local - my));
        double sxx = points.Sum(p => (p.Canonical - mx) * (p.Canonical - mx));
        double a = sxx == 0 ? 1 : sxy / sxx;
        return (a, my - (a * mx));
    }
}
