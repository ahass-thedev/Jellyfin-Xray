// Dev harness for voices: VoiceHarness <pcm s16le 16k mono> <srt> <model.onnx> [cacheEmbeddings.bin]
// or VoiceHarness bench ... (Bench.cs): CPU vs GPU speed and agreement.
// Uses SDH speaker tags as ground truth to measure how well voiceprints separate speakers.
using Jellyfin.Plugin.SceneXray.Vision;
using Jellyfin.Plugin.SceneXray.Voice;

if (args[0] == "bench") { await BenchMode.Run(args); return; }
if (args[0] == "fit") { FitMode.Run(args); return; }
if (args[0] == "fitsrt") { FitMode.RunSrt(args); return; }
var pcm = File.ReadAllBytes(args[0]);
var samples = new float[pcm.Length / 2];
for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, 2 * i) / 32768f;
if (args[1] == "fbank")
{
    var fb = new Fbank().Compute(samples.AsSpan(16000 * 200, 16000 * 3), 1f, out int fr);
    Console.WriteLine($"{fr} [{string.Join(" ", fb.Take(6).Select(x => x.ToString("F3")))}] [{string.Join(" ", fb.Skip(100 * 80 + 40).Take(6).Select(x => x.ToString("F3")))}]");
    return;
}

var cues = Subtitles.Parse(File.ReadAllText(args[1]));
using var model = new SpeakerModel(args[2], InferenceBackend.Cpu(4), fixedLengths: args.Length > 3 && args[3] == "fixed");

var sw = System.Diagnostics.Stopwatch.StartNew();
var emb = new Dictionary<int, float[]>();
foreach (var c in cues.Where(c => !c.MultiSpeaker && c.IsSpeech && c.Duration >= 1.0))
{
    int s = (int)((c.Start + 0.05) * 16000), e = Math.Min(samples.Length, (int)(Math.Min(c.End, c.Start + 8) * 16000));
    if (e - s < 16000) continue;
    var v = model.Embed(samples.AsSpan(s, e - s));
    if (v != null) emb[c.Index] = v;
}
Console.WriteLine($"{cues.Count} cues, {emb.Count} embedded in {sw.Elapsed.TotalSeconds:F1}s");

// Ground truth: tagged cues whose tag names a speaker with >= 5 tagged cues.
var tagged = cues.Where(c => c.SpeakerTag != null && emb.ContainsKey(c.Index)).ToList();
var speakers = tagged.GroupBy(c => c.SpeakerTag!).Where(g => g.Count() >= 5).Select(g => g.Key).ToHashSet();
tagged = tagged.Where(c => speakers.Contains(c.SpeakerTag!)).ToList();
Console.WriteLine($"ground truth: {tagged.Count} cues, speakers: {string.Join(", ", speakers)}");
var same = new List<float>(); var diff = new List<float>();
for (int i = 0; i < tagged.Count; i++)
  for (int j = i + 1; j < tagged.Count; j++)
    (tagged[i].SpeakerTag == tagged[j].SpeakerTag ? same : diff).Add(Vec.Dot(emb[tagged[i].Index], emb[tagged[j].Index]));
float Pct(List<float> l, double p) { var s = l.OrderBy(x => x).ToList(); return s.Count == 0 ? float.NaN : s[(int)(p * (s.Count - 1))]; }
Console.WriteLine($"same-speaker cos: p10={Pct(same,.1):F2} p50={Pct(same,.5):F2} | diff-speaker cos: p50={Pct(diff,.5):F2} p90={Pct(diff,.9):F2} p99={Pct(diff,.99):F2}");
// EER
float best = 1, bestT = 0;
for (float t = -0.2f; t < 0.9f; t += 0.01f) { float frr = same.Count(x => x < t) / (float)same.Count, far = diff.Count(x => x >= t) / (float)diff.Count; if (Math.Abs(frr - far) < best) { best = Math.Abs(frr - far); bestT = t; Console.Write(""); } }
float eFrr = same.Count == 0 ? float.NaN : same.Count(x => x < bestT) / (float)same.Count;
Console.WriteLine($"EER ~{eFrr:P1} at cos {bestT:F2}");
// Centroid-of-5 vs single cue: how separable is a speaker profile?
var rng = new Random(1); int ok = 0, n = 0;
foreach (var c in tagged)
{
    var profiles = speakers.ToDictionary(sp => sp, sp => Vec.Mean(tagged.Where(t => t.SpeakerTag == sp && t.Index != c.Index).OrderBy(_ => rng.Next()).Take(5).Select(t => (emb[t.Index], 1f)), model.EmbeddingSize));
    var bestSp = profiles.MaxBy(kv => Vec.Dot(kv.Value, emb[c.Index])).Key;
    ok += bestSp == c.SpeakerTag ? 1 : 0; n++;
}
if (n > 0) Console.WriteLine($"single cue -> nearest 5-cue profile: {ok}/{n} = {ok / (double)n:P1} correct");

// ---- Cartoon-mode simulation: cluster without tags, score clusters against cleaned tags.
string? Gt(string? tag) => tag switch
{
    null => null,
    "Nurse Grace" => "Grace",
    var t when t.Equals("kids", StringComparison.OrdinalIgnoreCase) || t.Equals("all", StringComparison.OrdinalIgnoreCase) => null,
    var t => t,
};
var ids = emb.Keys.OrderBy(i => i).ToList();
foreach (float th in new[] { 0.15f, 0.2f, 0.25f, 0.3f })
{
    var clusters = Jellyfin.Plugin.SceneXray.Voice.VoiceClustering.Cluster(ids.Select(i => emb[i]).ToList(), th);
    int pure = 0, total = 0;
    var lines = new List<string>();
    foreach (var cl in clusters.OrderByDescending(c => c.Count).Take(12))
    {
        var gts = cl.Select(k => Gt(cues[ids[k]].SpeakerTag)).Where(g => g != null).GroupBy(g => g).OrderByDescending(g => g.Count()).ToList();
        int cnt = gts.Sum(g => g.Count());
        if (cnt > 0) { pure += gts[0].Count(); total += cnt; }
        lines.Add($"  size={cl.Count,4} gt: " + string.Join(", ", gts.Take(3).Select(g => $"{g.Key}x{g.Count()}")));
    }
    Console.WriteLine($"threshold {th}: {clusters.Count} clusters ({clusters.Count(c => c.Count >= 10)} with >=10 cues); purity of top 12 = {pure}/{total}");
    if (th == 0.2f || th == 0.25f) lines.ForEach(Console.WriteLine);
}

// ---- Dialogue naming without tags, scored against tags. args[3] = roles TSV (actor \t role).
if (args.Length > 3)
{
    var chars = File.ReadAllLines(args[3]).Select(l => l.Split('\t')).Where(p => p.Length == 2 && p[1].Length > 0)
        .Select(p => new Character(Guid.NewGuid(), p[0], p[1], DialogueNamer.NamesFromRole(p[1]))).ToList();
    var clusters = VoiceClustering.Cluster(ids.Select(i => emb[i]).ToList(), 0.25f);
    var cueCluster = new Dictionary<int, int>();
    for (int c = 0; c < clusters.Count; c++) foreach (var k in clusters[c]) cueCluster[ids[k]] = c;
    var named = DialogueNamer.Infer(cues, cueCluster, chars);
    Console.WriteLine($"dialogue naming: {named.Count} clusters named");
    foreach (var (cl, (ch, score, margin)) in named.OrderByDescending(kv => clusters[kv.Key].Count))
    {
        var gts = clusters[cl].Select(k => Gt(cues[ids[k]].SpeakerTag)).Where(g => g != null).GroupBy(g => g).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}");
        Console.WriteLine($"  cluster size={clusters[cl].Count,4} -> {chars[ch].Role,-40} ({chars[ch].ActorName}) score={score:F1} margin={margin:F1} | tags: {string.Join(", ", gts.Take(3))}");
    }
}

if (args.Length > 4 && args[4] == "show")
{
    var chars2 = File.ReadAllLines(args[3]).Select(l => l.Split('\t')).Where(p => p.Length == 2 && p[1].Length > 0)
        .Select(p => new Character(Guid.NewGuid(), p[0], p[1], DialogueNamer.NamesFromRole(p[1]))).ToList();
    var cl2 = VoiceClustering.Cluster(ids.Select(i => emb[i]).ToList(), 0.25f);
    var map2 = new Dictionary<int, int>();
    for (int c = 0; c < cl2.Count; c++) foreach (var k in cl2[c]) map2[ids[k]] = c;
    foreach (var (cl, (ch, _, _)) in DialogueNamer.Infer(cues, map2, chars2, d => Console.WriteLine("   dbg " + d + $" (size {cl2[int.Parse(d.Split(' ')[1])].Count})")))
    {
        Console.WriteLine($"== {chars2[ch].Role} ({cl2[cl].Count} lines):");
        foreach (var k in cl2[cl].Take(8)) Console.WriteLine("     " + cues[ids[k]].Text);
    }
    Console.WriteLine("cluster sizes: " + string.Join(" ", cl2.Select(c => c.Count).OrderByDescending(x => x).Take(12)));
}

// ---- Gallery calibration: centroid of half a speaker's lines vs the other half / other speakers.
if (tagged.Count > 0)
{
    var bySp = tagged.Where(c => Gt(c.SpeakerTag) != null).GroupBy(c => Gt(c.SpeakerTag)!).Where(g => g.Count() >= 10).ToList();
    foreach (int k in new[] { 5, 10 })
    {
        var halves = bySp.ToDictionary(g => g.Key, g => (
            A: Vec.Mean(g.Where((_, i) => i % 2 == 0).Take(k).Select(c => (emb[c.Index], 1f)), model.EmbeddingSize),
            B: Vec.Mean(g.Where((_, i) => i % 2 == 1).Take(k).Select(c => (emb[c.Index], 1f)), model.EmbeddingSize)));
        var s1 = halves.Values.Select(h => Vec.Dot(h.A, h.B)).OrderBy(x => x).ToList();
        var d1 = (from x in halves from y in halves where x.Key != y.Key select Vec.Dot(x.Value.A, y.Value.B)).OrderBy(x => x).ToList();
        Console.WriteLine($"centroids of {k} lines: same min={s1[0]:F2} median={s1[s1.Count / 2]:F2} | different median={d1[d1.Count / 2]:F2} max={d1[^1]:F2} p95={d1[(int)(0.95 * (d1.Count - 1))]:F2}");
    }
}
