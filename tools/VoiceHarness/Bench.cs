// VoiceHarness bench <pcm s16le 16k mono> <model.onnx> <cpu|gpu> [lines] [dynamic]
// Embeds random 1-8 s lines on the CPU and on the chosen backend: time per line, and how close the voiceprints are.
using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Vision;
using Jellyfin.Plugin.SceneXray.Voice;
using Microsoft.Extensions.Logging.Abstractions;

internal static class BenchMode
{
    public static async Task Run(string[] args)
    {
        var pcm = File.ReadAllBytes(args[1]);
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, 2 * i) / 32768f;
        int lines = args.Length > 4 ? int.Parse(args[4]) : 300;
        var rng = new Random(7);
        var cuts = Enumerable.Range(0, lines).Select(_ =>
        {
            int len = rng.Next(16000, 8 * 16000);
            return (Start: rng.Next(0, samples.Length - len), Length: len);
        }).ToList();

        NativeLibraries.Register();
        var device = args[3] == "gpu" ? InferenceDevice.Gpu : InferenceDevice.Cpu;
        var acc = new Accelerator(Path.Combine(Path.GetTempPath(), "xray-harness-runtimes"), new BenchHttp(), NullLogger.Instance, new PluginConfiguration());
        var backend = await acc.ResolveAsync(device, 2, default);
        var cpu = Run(new SpeakerModel(args[2], InferenceBackend.Cpu(2)), "CPU", samples, cuts);
        var fixedCpu = Run(new SpeakerModel(args[2], InferenceBackend.Cpu(2), fixedLengths: true), "CPU, fixed lengths", samples, cuts);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var model = new SpeakerModel(args[2], backend, fixedLengths: args.Length <= 5 || args[5] != "dynamic");
        model.Warm();
        Console.WriteLine($"{backend.Name}: loaded and warmed in {sw.Elapsed.TotalSeconds:F1} s");
        var dev = Run(model, backend.Name, samples, cuts);
        var fixedCos = cpu.Zip(fixedCpu).Select(p => Vec.Dot(p.First!, p.Second!)).Order().ToList();
        Console.WriteLine($"cos(CPU, CPU fixed lengths): min={fixedCos[0]:F4} p1={fixedCos[fixedCos.Count / 100]:F4} median={fixedCos[fixedCos.Count / 2]:F4}");
        Console.WriteLine($"null: cpu {cpu.Count(v => v == null)} dev {dev.Count(v => v == null)}; NaN: dev {dev.Count(v => v != null && v.Any(float.IsNaN))}");
        var cos = cpu.Zip(dev).Where(p => p.First != null && p.Second != null).Select(p => Vec.Dot(p.First!, p.Second!)).Where(x => !float.IsNaN(x)).Order().ToList();
        Console.WriteLine($"cos(CPU, {backend.Name}): min={cos[0]:F4} p1={cos[cos.Count / 100]:F4} median={cos[cos.Count / 2]:F4}");
    }

    private static List<float[]?> Run(SpeakerModel model, string name, float[] samples, List<(int Start, int Length)> cuts)
    {
        using var _ = model;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = model.Embed(samples.AsSpan(cuts[0].Start, cuts[0].Length));
        double firstMs = sw.Elapsed.TotalMilliseconds;
        var result = new List<float[]?> { first };
        var times = new List<double>();
        foreach (var c in cuts.Skip(1))
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            result.Add(model.Embed(samples.AsSpan(c.Start, c.Length)));
            times.Add(t.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        Console.WriteLine($"{name}: {cuts.Count} lines in {sw.Elapsed.TotalSeconds:F1} s; first {firstMs:F0} ms, then median {times[times.Count / 2]:F1} ms, p90 {times[times.Count * 9 / 10]:F1} ms, max {times[^1]:F0} ms");
        return result;
    }

    private sealed class BenchHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
