using Jellyfin.Plugin.SceneXray.Sharing;

namespace Xray.Share.Server.Tests;

/// <summary>Synthetic fingerprints and contributions.</summary>
public static class Synth
{
    public const double Item = FingerprintMatcher.ItemSeconds;

    public static int Items(double seconds) => (int)Math.Round(seconds / Item);

    public static uint[] Fingerprint(double seconds, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, Items(seconds)).Select(_ => (uint)rng.NextInt64(0, 1L << 32)).ToArray();
    }

    /// <summary>Each bit flipped with probability p: another encode of the same audio.</summary>
    public static uint[] Noisy(uint[] fp, double p, int seed)
    {
        var rng = new Random(seed);
        return fp.Select(v =>
        {
            for (int b = 0; b < 32; b++)
            {
                if (rng.NextDouble() < p)
                {
                    v ^= 1u << b;
                }
            }

            return v;
        }).ToArray();
    }

    /// <summary>Another release: <paramref name="lead"/> seconds of other audio in front (a logo), re-encoded.</summary>
    public static uint[] Release(uint[] fp, double lead, int seed) =>
        Noisy(Fingerprint(lead, seed + 1000).Concat(fp).ToArray(), 0.05, seed);

    public static double Runtime(uint[] fp) => fp.Length * Item;

    public static List<double[]> Scenes(double runtime, double every = 60)
    {
        var scenes = new List<double[]>();
        for (double t = 0; t < runtime - 1; t += every)
        {
            scenes.Add([t, Math.Min(runtime, t + every)]);
        }

        return scenes;
    }

    public static SharedPerson Person(int tmdb, params double[][] intervals) => new(tmdb, "face", 0.8, intervals);

    public static SharedContribution Movie(int tmdb, uint[] fp, IEnumerable<SharedPerson>? people = null, string version = "2", IEnumerable<SharedMusicCue>? music = null, IEnumerable<double[]>? scenes = null, double shift = 0)
    {
        double runtime = Runtime(fp);
        var anchors = AnchorSet.Extract(fp, AnchorSet.EverySecondsFor(runtime));
        return new SharedContribution(
            new SharedTitle("movie", tmdb, null, null, null, null, null, "theatrical", runtime, 23.976),
            new SharedAnalyzer(version, "1.3.1.0", "Balanced", 1.0, "wespeaker-r34"),
            SharedAnchors.From(anchors),
            (scenes ?? Scenes(runtime)).Select(s => new[] { s[0] + shift, Math.Min(runtime, s[1] + shift) }).Where(s => s[1] > s[0]).ToList(),
            (people ?? []).Select(p => p with { Intervals = p.Intervals.Select(i => new[] { i[0] + shift, i[1] + shift }).ToList() }).ToList(),
            [],
            [],
            (music ?? []).Select(m => m with { Start = m.Start + shift, End = m.End + shift }).ToList());
    }

    public static string Key(int tmdb) => TitleKey.Movie(tmdb, null)!;
}
