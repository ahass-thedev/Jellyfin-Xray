// Phase-0 harness for shared X-Ray data: can a timeline made on file A be aligned to file B?
// A plays the contributor (its anchors become the variant's reference), B the receiving install.
// usage: AlignHarness align <A> <B> [--stream-a N] [--stream-b N] [--stride N] [--ffmpeg path] [--tsv label]
//          [--every S] [--b-pal B.fp] [--min-second-line N] [--min-fraction F] [--min-inliers N] [--max-rms S] [--max-hole S] [--min-coverage F] [--tolerance S] [--pal-tolerance F]
//        AlignHarness fp <media> <out.fp> [--stream N] [--af filter] [--ffmpeg path]
// A and B are media files, or raw fingerprints (*.fp) written by `fp`. B must be media for the PAL retry.
using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.SceneXray.Sharing;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var opts = new Dictionary<string, string>();
var pos = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal)) { opts[args[i][2..]] = args[++i]; }
    else { pos.Add(args[i]); }
}

string ffmpeg = opts.GetValueOrDefault("ffmpeg") ?? Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg";
int Int(string k, int d) => opts.TryGetValue(k, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : d;
double Num(string k, double d) => opts.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : d;

// Threshold overrides for tuning; defaults are the library's.
var o = AlignmentOptions.Default;
o = o with
{
    MinInlierFraction = Num("min-fraction", o.MinInlierFraction),
    MinInliers = Int("min-inliers", o.MinInliers),
    MaxRmsSeconds = Num("max-rms", o.MaxRmsSeconds),
    MaxHoleSeconds = Num("max-hole", o.MaxHoleSeconds),
    MinCoverage = Num("min-coverage", o.MinCoverage),
    InlierToleranceSeconds = Num("tolerance", o.InlierToleranceSeconds),
    PalRuntimeTolerance = Num("pal-tolerance", o.PalRuntimeTolerance),
    MinSecondLineAnchors = Int("min-second-line", o.MinSecondLineAnchors),
};

async Task<(uint[] Fp, double Seconds)> Load(string path, int stream, string? filter = null)
{
    var sw = Stopwatch.StartNew();
    if (path.EndsWith(".fp", StringComparison.OrdinalIgnoreCase))
    {
        return (Chromaprint.FromBytes(File.ReadAllBytes(path)), 0);
    }

    // FP_CACHE=<dir> keeps every fingerprint, so thresholds can be re-tuned without ffmpeg.
    string? cache = Environment.GetEnvironmentVariable("FP_CACHE");
    string? cached = null;
    if (!string.IsNullOrEmpty(cache))
    {
        var fi = new FileInfo(path);
        string key = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{stream}|{filter}")));
        cached = Path.Combine(cache, $"{Path.GetFileNameWithoutExtension(path)}.{stream}.{key[..10]}{(filter is null ? "" : ".pal")}.fp");
        if (File.Exists(cached))
        {
            return (Chromaprint.FromBytes(File.ReadAllBytes(cached)), 0);
        }
    }

    var fp = await Chromaprint.FingerprintAsync(ffmpeg, Chromaprint.Args(path, stream, filter: filter));
    if (cached is not null)
    {
        File.WriteAllBytes(cached, Chromaprint.ToBytes(fp));
        File.WriteAllText(cached + ".txt", $"{path}\t{stream}\t{filter}\t{sw.Elapsed.TotalSeconds:F1}\n");
    }

    return (fp, sw.Elapsed.TotalSeconds);
}

switch (pos.FirstOrDefault())
{
    case "fp":
    {
        var (fp, secs) = await Load(pos[1], Int("stream", 0), opts.GetValueOrDefault("af"));
        File.WriteAllBytes(pos[2], Chromaprint.ToBytes(fp));
        Console.WriteLine($"{pos[2]}: {fp.Length} items = {fp.Length * FingerprintMatcher.ItemSeconds:F1} s audio, in {secs:F1} s");
        return 0;
    }

    case "align":
    {
        string a = pos[1], b = pos[2];
        var (fa, ta) = await Load(a, Int("stream-a", 0));
        var (fb, tb) = await Load(b, Int("stream-b", 0));
        double ra = fa.Length * FingerprintMatcher.ItemSeconds, rb = fb.Length * FingerprintMatcher.ItemSeconds;
        var all = AnchorSet.Extract(fa, Num("every", AnchorSet.EverySecondsFor(ra)));
        int stride = Int("stride", AnchorSet.ReferenceStride(ra));
        var reference = all.Reference(stride);
        double palSecs = 0;
        var result = await Aligner.AlignAsync(reference, fb, ra, rb, async (filter, ct) =>
        {
            if (b.EndsWith(".fp", StringComparison.OrdinalIgnoreCase))
            {
                // Offline re-tuning: --b-pal names B's cached PAL re-fingerprint.
                return opts.TryGetValue("b-pal", out var palFp) ? Chromaprint.FromBytes(File.ReadAllBytes(palFp)) : [];
            }

            var (fp, secs) = await Load(b, Int("stream-b", 0), filter);
            palSecs = secs;
            return fp;
        }, o);

        if (opts.TryGetValue("tsv", out var label))
        {
            var p = result.OtherAttempt;
            Console.WriteLine(string.Join('\t',
                label, result.Accepted ? "ACCEPT" : "REJECT", result.Kind, $"{result.Slope:F5}", $"{result.Offset:F2}",
                $"{result.Inliers}/{result.Considered}", $"{result.InlierFraction:P0}", $"{result.RmsSeconds:F3}",
                $"{result.MaxHoleSeconds:F0}", $"{result.Coverage:P1}", $"{result.SecondLineAnchors}@{result.SecondLineShift:F1}", $"{ra:F0}", $"{rb:F0}",
                $"{ta:F1}", $"{tb:F1}", $"{palSecs:F1}", $"{result.Elapsed.TotalSeconds:F2}",
                p is null ? "" : $"{p.Kind}:{p.Inliers}/{p.Considered}:{(p.Accepted ? "ok" : string.Join(" / ", p.Reasons))}",
                string.Join("; ", result.Reasons)));
            return 0;
        }

        Console.WriteLine($"A (canonical): {a}\n  {ra:F1} s audio, {fa.Length} items, fingerprinted in {ta:F1} s");
        Console.WriteLine($"B (local):     {b}\n  {rb:F1} s audio, {fb.Length} items, fingerprinted in {tb:F1} s");
        Console.WriteLine($"Anchors: {all.Count} every {all.EverySeconds} s; reference = every {stride}th ({reference.Count}, {reference.Count * reference.Length * 4 / 1024.0:F1} KB)");
        if (result.OtherAttempt is { } other)
        {
            Console.WriteLine($"PAL retry ran (re-fingerprinted in {palSecs:F1} s). Unreported {other.Kind} fit: {other.Inliers}/{other.Considered} inliers, {other.Verdict}");
        }

        Console.WriteLine($"Fit ({result.Kind}): t_B = {result.Slope:F6} · t_A {(result.Offset < 0 ? "-" : "+")} {Math.Abs(result.Offset):F3} s");
        Console.WriteLine($"  located {result.Located}, inliers {result.Inliers}/{result.Considered} ({result.InlierFraction:P1})");
        Console.WriteLine($"  RMS residual {result.RmsSeconds:F3} s, max |residual| {result.MaxAbsResidualSeconds:F3} s");
        Console.WriteLine($"  longest hole {result.MaxHoleSeconds:F0} s, coverage {result.Coverage:P1}, valid canonical range {result.ValidFrom:F0}-{result.ValidTo:F0} s");
        Console.WriteLine($"  second offset: {result.SecondLineAnchors} anchors at {result.SecondLineShift:+0.0;-0.0} s");
        Console.WriteLine($"  matching + fit {result.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Verdict: {result.Verdict}");
        return result.Accepted ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("usage: AlignHarness align <A> <B> [--stream-a N] [--stream-b N] [--stride N] [--ffmpeg path] [--tsv label]\n       AlignHarness fp <media> <out.fp> [--stream N] [--af filter] [--ffmpeg path]");
        return 2;
}
