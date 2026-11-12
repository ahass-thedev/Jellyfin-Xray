using Jellyfin.Plugin.SceneXray.Sharing;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

/// <summary>Piecewise alignment of other cuts (design §3, phase 5).</summary>
public class PiecewiseTests
{
    private const double Item = FingerprintMatcher.ItemSeconds;

    private static int Items(double seconds) => (int)Math.Round(seconds / Item);

    private static uint[] Random(int items, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, items).Select(_ => (uint)rng.NextInt64(0, 1L << 32)).ToArray();
    }

    private static uint[] Noisy(uint[] fp, double p, int seed)
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

    private static readonly uint[] Film = Random(Items(3600), 1);

    private static (AlignmentResult Plain, PiecewiseResult Piecewise) Align(uint[] local)
    {
        double runtime = Film.Length * Item;
        var reference = AnchorSet.Extract(Film).Reference(3);
        return (Aligner.Align(reference, local, runtime, local.Length * Item), PiecewiseAligner.Align(reference, local, runtime, local.Length * Item));
    }

    [Fact]
    public void AnExtendedCutReusesTheSharedScenesAroundTheInsert()
    {
        // 4 extra minutes at 50 %: the plain fit rejects it, piecewise maps both halves.
        int at = Items(1800);
        var local = Noisy(Film.Take(at).Concat(Random(Items(240), 2)).Concat(Film.Skip(at)).ToArray(), 0.05, 3);
        var (plain, pw) = Align(local);
        Assert.False(plain.Accepted);
        Assert.True(pw.Accepted, pw.Verdict);
        Assert.Equal(2, pw.Segments.Count);
        Assert.InRange(pw.Segments[0].Offset, -0.3, 0.3);
        Assert.InRange(pw.Segments[1].Offset, 239.7, 240.3);

        // Canonical 600 s is local 600 s; 2400 s is local 2640 s; the splice ±60 s is dropped.
        var mapped = pw.ToLocal([[590, 610], [2390, 2410], [1790, 1810]], local.Length * Item);
        Assert.Equal(2, mapped.Count);
        Assert.InRange(mapped[0][0], 589.7, 590.3);
        Assert.InRange(mapped[1][0], 2629.7, 2630.3);
    }

    [Fact]
    public void ATheatricalCutReusesTheExtendedData()
    {
        // The local file lacks 3 minutes at 30 %.
        int at = Items(1080);
        var local = Noisy(Film.Take(at).Concat(Film.Skip(at + Items(180))).ToArray(), 0.05, 4);
        var (_, pw) = Align(local);
        Assert.True(pw.Accepted, pw.Verdict);
        Assert.InRange(pw.Segments[1].Offset, -180.3, -179.7);
    }

    [Fact]
    public void ASameCutOrAnotherTitleIsNotPiecewise()
    {
        Assert.False(Align(Noisy(Film, 0.05, 5)).Piecewise.Accepted); // one line: the plain fit's job
        Assert.False(Align(Random(Items(3600), 6)).Piecewise.Accepted);
    }
}
