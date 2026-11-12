// VoiceHarness fit <pcm> <model.onnx> [lines]: how far fixed-length buckets move voiceprints, per strategy (CPU only).
using Jellyfin.Plugin.SceneXray.Vision;
using Jellyfin.Plugin.SceneXray.Voice;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

internal static class FitMode
{
    public static void Run(string[] args)
    {
        var pcm = File.ReadAllBytes(args[1]);
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, 2 * i) / 32768f;
        int lines = args.Length > 3 ? int.Parse(args[3]) : 300;
        using var session = new InferenceSession(args[2], new SessionOptions { IntraOpNumThreads = 8 });
        var fbank = new Fbank();
        var rng = new Random(7);
        var feats = new List<(float[] F, int Frames)>();
        while (feats.Count < lines)
        {
            int len = rng.Next(16000, 8 * 16000);
            var f = fbank.Compute(samples.AsSpan(rng.Next(0, samples.Length - len), len), 32768f, out int frames);
            Fbank.MeanNormalize(f, frames);
            feats.Add((f, frames));
        }

        float[] Embed(float[] f, int t)
        {
            using var r = session.Run([NamedOnnxValue.CreateFromTensor("feats", new DenseTensor<float>(f, [1, t, Fbank.Bins]))]);
            var v = r.First().AsTensor<float>().ToArray();
            Vec.Normalize(v);
            return v;
        }

        var exact = feats.Select(x => Embed(x.F, x.Frames)).ToList();
        foreach (var (name, fit) in new (string, Func<int, int>)[]
        {
            ("ceil 100", n => (n + 99) / 100 * 100),
            ("nearest 100", n => Math.Max(100, (n + 50) / 100 * 100)),
            ("ceil 50", n => (n + 49) / 50 * 50),
            ("nearest 50", n => Math.Max(100, (n + 25) / 50 * 50)),
            ("geometric 12%", n => Geometric(n)),
        })
        {
            var cos = new List<(int Frames, float Cos)>();
            for (int i = 0; i < feats.Count; i++)
            {
                var (f, frames) = feats[i];
                int t = fit(frames);
                float[] g = t >= frames ? SpeakerModel.Repeat(f, frames, t) : f[..(t * Fbank.Bins)];
                cos.Add((frames, Vec.Dot(exact[i], Embed(g, t))));
            }

            var all = cos.Select(c => c.Cos).Order().ToList();
            var shortL = cos.Where(c => c.Frames < 300).Select(c => c.Cos).Order().ToList();
            int buckets = Enumerable.Range(98, 701).Select(fit).Distinct().Count();
            Console.WriteLine($"{name,-14} buckets={buckets,2}  all: min={all[0]:F3} p1={all[all.Count / 100]:F3} p5={all[all.Count / 20]:F3} med={all[all.Count / 2]:F4} | <3s: min={shortL[0]:F3} p5={shortL[shortL.Count / 20]:F3}");
        }
    }

    /// <summary>VoiceHarness fitsrt <pcm> <srt> <model>: the same on real subtitle lines, against moving each line by 50 ms.</summary>
    public static void RunSrt(string[] args)
    {
        var pcm = File.ReadAllBytes(args[1]);
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, 2 * i) / 32768f;
        var cues = Subtitles.Parse(File.ReadAllText(args[2])).Where(c => !c.MultiSpeaker && c.IsSpeech && c.Duration >= 1.0).ToList();
        using var session = new InferenceSession(args[3], new SessionOptions { IntraOpNumThreads = 8 });
        var fbank = new Fbank();
        float[] Embed(float[] f, int t)
        {
            using var r = session.Run([NamedOnnxValue.CreateFromTensor("feats", new DenseTensor<float>(f, [1, t, Fbank.Bins]))]);
            var v = r.First().AsTensor<float>().ToArray();
            Vec.Normalize(v);
            return v;
        }

        var variants = new (string Name, double Shift, Func<int, int>? Fit)[]
        {
            ("exact", 0, null), ("shift 50 ms", 0.05, null), ("ceil 100", 0, n => (n + 99) / 100 * 100), ("geometric 12%", 0, Geometric),
        };
        var emb = variants.Select(_ => new List<float[]>()).ToArray();
        foreach (var c in cues)
        {
            var outs = new float[variants.Length][];
            bool ok = true;
            for (int k = 0; k < variants.Length; k++)
            {
                double start = c.Start + 0.05 + variants[k].Shift;
                int s = (int)(start * 16000), e = Math.Min(samples.Length, (int)(Math.Min(c.End, c.Start + 8) * 16000));
                if (e - s < 16000) { ok = false; break; }
                var f = fbank.Compute(samples.AsSpan(s, e - s), 32768f, out int frames);
                Fbank.MeanNormalize(f, frames);
                int t = variants[k].Fit?.Invoke(frames) ?? frames;
                outs[k] = Embed(SpeakerModel.Repeat(f, frames, t), t);
            }

            if (ok) for (int k = 0; k < variants.Length; k++) emb[k].Add(outs[k]);
        }

        var baseClusters = Labels(VoiceClustering.Cluster(emb[0], 0.25f), emb[0].Count);
        Console.WriteLine($"{emb[0].Count} lines");
        for (int k = 1; k < variants.Length; k++)
        {
            var cos = emb[0].Zip(emb[k]).Select(p => Vec.Dot(p.First, p.Second)).Order().ToList();
            int flips = 0, pairs = 0;
            for (int i = 0; i < emb[0].Count; i++)
                for (int j = i + 1; j < emb[0].Count; j++)
                {
                    pairs++;
                    if ((Vec.Dot(emb[0][i], emb[0][j]) >= 0.25f) != (Vec.Dot(emb[k][i], emb[k][j]) >= 0.25f)) flips++;
                }

            var labels = Labels(VoiceClustering.Cluster(emb[k], 0.25f), emb[k].Count);
            Console.WriteLine($"{variants[k].Name,-14} cos to exact: min={cos[0]:F3} p5={cos[cos.Count / 20]:F3} med={cos[cos.Count / 2]:F4}; pair decisions flipped {flips * 100.0 / pairs:F2}%; clustering agreement (Rand) {Rand(baseClusters, labels):F3}");
        }
    }

    private static int[] Labels(List<List<int>> clusters, int n)
    {
        var l = new int[n];
        for (int c = 0; c < clusters.Count; c++) foreach (int i in clusters[c]) l[i] = c;
        return l;
    }

    private static double Rand(int[] a, int[] b)
    {
        long agree = 0, total = 0;
        for (int i = 0; i < a.Length; i++)
            for (int j = i + 1; j < a.Length; j++) { total++; if ((a[i] == a[j]) == (b[i] == b[j])) agree++; }
        return agree / (double)total;
    }

    private static int Geometric(int n)
    {
        double b = 100;
        while (b < n) b *= 1.12;
        return (int)Math.Round(b);
    }
}
