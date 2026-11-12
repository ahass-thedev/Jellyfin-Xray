// "scenes" mode: XrayHarness scenes <video 1fps> <timeline.json>
using System.Text.Json;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Storage;

static class ScenesMode
{
    public static async Task Run(string[] a)
    {
        var frames = new List<FrameColor>();
        var fine = new List<FrameColor>();
        var hs = new List<FrameColor>();
        var o = new DecodeOptions("ffmpeg", "ffprobe", 1, 320, false, "");
        var (w, h, sar) = await FrameSource.ProbeAsync(o, a[1], default);
        await foreach (var (_, t, f) in FrameSource.ReadAsync(o, a[1], w, h, sar, default))
        {
            frames.Add(new FrameColor(t, f.ColorHistogram()));
            var b = new float[512]; var c = new float[128]; int n = 0;
            for (int y = 0; y < f.Height; y += 2) for (int x = 0; x < f.Width; x += 2)
            {
                int i = ((y * f.Width) + x) * 3; int r = f.Data[i], g = f.Data[i + 1], bl = f.Data[i + 2];
                b[((r >> 5) << 6) | ((g >> 5) << 3) | (bl >> 5)]++;
                int mx = Math.Max(r, Math.Max(g, bl)), mn = Math.Min(r, Math.Min(g, bl)); float sat = mx == 0 ? 0 : (mx - mn) / (float)mx;
                float hue = mx == mn ? 0 : mx == r ? ((g - bl) / (float)(mx - mn) + 6) % 6 : mx == g ? (bl - r) / (float)(mx - mn) + 2 : (r - g) / (float)(mx - mn) + 4;
                c[(Math.Min(15, (int)(hue / 6 * 16)) << 3) | Math.Min(7, (int)(sat * 8))]++; n++;
            }
            for (int k = 0; k < 512; k++) b[k] /= n; for (int k = 0; k < 128; k++) c[k] /= n;
            fine.Add(new FrameColor(t, b)); hs.Add(new FrameColor(t, c));
        }
        var tl = JsonSerializer.Deserialize<XrayTimeline>(File.ReadAllText(a[2]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var presence = tl.People.Concat(tl.VoicePeople).GroupBy(p => p.PersonId)
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Spans ?? p.Intervals).ToList());
        var names = tl.People.Concat(tl.VoicePeople).GroupBy(p => p.PersonId).ToDictionary(g => g.Key, g => g.First().Name);
        var scenes = SceneDetector.Detect(frames, presence);
        var lens = scenes.Select(s => s[1] - s[0]).OrderBy(x => x).ToList();
        Console.WriteLine($"{frames.Count} frames -> {scenes.Count} scenes; length median {lens[lens.Count / 2]:F0}s p10 {lens[lens.Count / 10]:F0}s p90 {lens[lens.Count * 9 / 10]:F0}s max {lens[^1]:F0}s");
        Console.WriteLine("scenes 6:00-10:00: " + string.Join("  ", scenes.Where(s => s[1] > 360 && s[0] < 600).Select(s => $"{s[0]:F0}-{s[1]:F0}")));
        foreach (var p in tl.People.Concat(tl.VoicePeople).GroupBy(p => p.PersonId).Take(14))
        {
            bool heardOnly = p.All(x => x.Source.StartsWith("voice"));
            var raw = ScenePresence.Union(p.SelectMany(x => x.Spans ?? x.Intervals));
            var exp = ScenePresence.Union(p.SelectMany(x => ScenePresence.Expand(x.Spans ?? x.Intervals, scenes, x.Source.StartsWith("voice"))));
            Console.WriteLine($"  {names[p.Key],-22} pieces {raw.Count,3} -> {exp.Count,3}   time {raw.Sum(i => i[1] - i[0]),5:F0}s -> {exp.Sum(i => i[1] - i[0]),5:F0}s");
        }
    }
}
