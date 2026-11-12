using Jellyfin.Plugin.SceneXray.Analysis;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

public class SceneTests
{
    private static float[] Look(int bin)
    {
        var h = new float[512];
        h[bin] = 1;
        return h;
    }

    private static List<FrameColor> Frames(params (int Seconds, int Bin)[] shots)
    {
        var frames = new List<FrameColor>();
        double t = 0;
        foreach (var (seconds, bin) in shots)
        {
            for (int i = 0; i < seconds; i++)
            {
                frames.Add(new FrameColor(t++, Look(bin)));
            }
        }

        return frames;
    }

    [Fact]
    public void ShotReverseShot_StaysOneScene_NewSetStartsAnother()
    {
        // Kitchen: A, B, A, B (cutting between two people), then the garden.
        var frames = Frames((10, 1), (8, 2), (9, 1), (7, 2), (20, 3));
        var empty = new Dictionary<Guid, List<double[]>>();
        var colors = frames.Select((f, i) => i < 34 ? new FrameColor(f.Time, Blend(f.Histogram, 0)) : f).ToList();
        var scenes = SceneDetector.Detect(colors, empty);
        Assert.Equal(2, scenes.Count);
        Assert.Equal([0, 34], scenes[0]);
    }

    [Fact]
    public void ALeadInEveryScene_DoesNotChainThemTogether()
    {
        var lead = Guid.NewGuid();
        var frames = Frames((20, 1), (20, 2), (20, 3));
        var presence = new Dictionary<Guid, List<double[]>> { [lead] = [[0, 60]] };
        Assert.Equal(3, SceneDetector.Detect(frames, presence).Count);
    }

    [Fact]
    public void Presence_SeenBrieflyMeansWholeScene_ButNotForAGlimpse()
    {
        double[][] scenes = [[0, 60], [60, 120]];
        Assert.Equal([[0, 60]], ScenePresence.Expand([[10, 13]], scenes, heard: false));
        Assert.Empty(ScenePresence.Expand([[10, 11]], scenes, heard: false)); // 1 s glimpse
        Assert.Equal([[0, 120]], ScenePresence.Expand([[10, 11], [70, 71]], scenes, heard: true)); // a line in each
    }

    [Fact]
    public void Presence_LongScenesUseFirstToLastAppearance()
    {
        double[][] scenes = [[0, 900]];
        Assert.Equal([[90, 220]], ScenePresence.Expand([[100, 110], [190, 200]], scenes, heard: false));
    }

    /// <summary>Two looks of the same set share most of their palette.</summary>
    private static float[] Blend(float[] h, int shared)
    {
        var o = new float[512];
        for (int i = 0; i < 512; i++)
        {
            o[i] = h[i] * 0.2f;
        }

        o[shared + 100] += 0.8f;
        return o;
    }
}
