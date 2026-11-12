using Jellyfin.Plugin.SceneXray.Vision;
using Jellyfin.Plugin.SceneXray.Voice;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

public class VoiceTests
{
    [Fact]
    public void Subtitles_ParseTagsSpeakersAndSkipNoise()
    {
        const string Srt = "1\n00:00:01,000 --> 00:00:03,000\n[Buddy]\nHello, friends!\n\n2\n00:00:04,000 --> 00:00:06,000\n-Bye, kids!\n-Bye, Mailman Miles!\n\n3\n00:00:07,000 --> 00:00:08,000\n[door creaks]\n\n4\n00:00:09,000 --> 00:00:10,000\nMmm! Unh!\n\n5\n00:00:11,000 --> 00:00:13,000\nRICK: Morty, grab on!\n";
        var cues = Subtitles.Parse(Srt);
        Assert.Equal(4, cues.Count); // the pure sound cue is dropped
        Assert.Equal("Buddy", cues[0].SpeakerTag);
        Assert.Equal("Hello, friends!", cues[0].Text);
        Assert.True(cues[1].MultiSpeaker);
        Assert.False(cues[2].IsSpeech);
        Assert.Equal("RICK", cues[3].SpeakerTag);
        Assert.Equal("Morty, grab on!", cues[3].Text);
    }

    [Theory]
    [InlineData(98, 100)] // a 1 s line
    [InlineData(100, 100)]
    [InlineData(101, 200)]
    [InlineData(798, 800)] // the 8 s cap
    public void Gpu_LinesRunInWholeSecondBuckets(int frames, int bucket) => Assert.Equal(bucket, SpeakerModel.Bucket(frames));

    [Fact]
    public void Gpu_PaddingRepeatsTheLineFromItsStart()
    {
        var feats = Enumerable.Range(0, 3 * Fbank.Bins).Select(i => (float)(i / Fbank.Bins)).ToArray(); // frames 0, 1, 2
        var padded = SpeakerModel.Repeat(feats, 3, 7);
        Assert.Equal(7 * Fbank.Bins, padded.Length);
        Assert.Equal([0f, 1f, 2f, 0f, 1f, 2f, 0f], Enumerable.Range(0, 7).Select(f => padded[f * Fbank.Bins]));
        Assert.Same(feats, SpeakerModel.Repeat(feats, 3, 3));
    }

    [Fact]
    public void Roles_BecomeNames()
    {
        Assert.Equal(["Buddy", "Dark Buddy"], DialogueNamer.NamesFromRole("Buddy / Dark Buddy (voice)"));
        Assert.Equal(["Rick Sanchez", "Rick"], DialogueNamer.NamesFromRole("Rick Sanchez (voice)"));
        Assert.Equal(["Dr. Burr"], DialogueNamer.NamesFromRole("Dr. Burr"));
        Assert.Empty(DialogueNamer.NamesFromRole("(voice)"));
    }

    [Fact]
    public void Dialogue_NamesTheVoiceThatAnswers()
    {
        // Voice 0 keeps calling "Morty"; voice 1 answers and calls "Rick".
        var lines = new List<(int Voice, string Text)>();
        for (int i = 0; i < 6; i++)
        {
            lines.Add((0, "Morty, come on!"));
            lines.Add((1, "Aw geez, Rick!"));
        }

        var cues = lines.Select((l, i) => new SubtitleCue(i, i * 3, (i * 3) + 2, l.Text, null, false)).ToList();
        var map = lines.Select((l, i) => (i, l.Voice)).ToDictionary(x => x.i, x => x.Voice);
        var chars = new List<Character>
        {
            new(Guid.NewGuid(), "Ian Cardoni", "Rick Sanchez (voice)", DialogueNamer.NamesFromRole("Rick Sanchez (voice)")),
            new(Guid.NewGuid(), "Harry Belden", "Morty Smith (voice)", DialogueNamer.NamesFromRole("Morty Smith (voice)")),
        };

        var named = DialogueNamer.Infer(cues, map, chars);
        Assert.Equal(0, named[0].Character); // voice 0 is Rick
        Assert.Equal(1, named[1].Character); // voice 1 is Morty
    }

    [Fact]
    public void Namer_UsesTags_IncludingTitledVariants()
    {
        var buddy = new VoiceCandidate { Character = new Character(Guid.NewGuid(), "Keegan-Michael Key", "Buddy / Dark Buddy (voice)", ["Buddy", "Dark Buddy"]) };
        var grace = new VoiceCandidate { Character = new Character(Guid.NewGuid(), "Cristin Milioti", "Grace", ["Grace"]) };
        var a = new VoiceAnalysis { Cues = [] };
        var c0 = new VoiceCluster { Id = 0, Centroid = [1, 0] };
        var c1 = new VoiceCluster { Id = 1, Centroid = [0, 1] };
        for (int i = 0; i < 4; i++)
        {
            c0.Cues.Add(new SubtitleCue(i, i, i + 1, "line one", i < 3 ? "Buddy" : null, false));
            c1.Cues.Add(new SubtitleCue(10 + i, 10 + i, 11 + i, "line two", i < 2 ? "Nurse Grace" : null, false));
        }

        a.Clusters.AddRange([c0, c1]);
        var labels = VoiceNamer.Name(a, [buddy, grace]);
        Assert.Equal(buddy, labels.Single(l => l.Cluster == c0).Person);
        Assert.Equal(grace, labels.Single(l => l.Cluster == c1).Person);
    }

    [Fact]
    public void Clustering_SeparatesVoices()
    {
        var rng = new Random(3);
        float[] Around(float[] c) { var v = c.Select(x => x + (float)((rng.NextDouble() - 0.5) * 0.4)).ToArray(); Vec.Normalize(v); return v; }
        float[] a = [1, 0, 0, 0], b = [0, 1, 0, 0];
        var items = Enumerable.Range(0, 10).Select(i => Around(i % 2 == 0 ? a : b)).ToList();
        var clusters = VoiceClustering.Cluster(items, 0.5f);
        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, c => Assert.True(c.All(i => i % 2 == c[0] % 2)));
    }
}

public class InjectionTests
{
    [Fact]
    public void Overlay_IsOnlyInjectedIntoHtml()
    {
        var html = Services.StartupService.InjectOverlay(new() { Contents = "<html><head></head><body></body></html>" });
        Assert.Contains("SceneXray/overlay.js", html, StringComparison.Ordinal);

        const string Chunk = "(self.webpackChunk=self.webpackChunk||[]).push([[1],{1:e=>{e.exports='<div></div>'}}]);";
        Assert.Equal(Chunk, Services.StartupService.InjectOverlay(new() { Contents = Chunk }));
    }
}
