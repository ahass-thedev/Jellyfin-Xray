// Dev harness: runs the face pipeline on a local file without Jellyfin.
// usage: XrayHarness <video> <castDir(name.jpg...)> <detector.onnx> <recognizer.onnx> [fps] [threads] [cpu|gpu]
using System.Text.Json;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Jellyfin.Plugin.SceneXray.Vision;

if (args[0] == "detect") { DetectMode.Run(args); return; }
if (args[0] == "scenes") { await ScenesMode.Run(args); return; }
var (video, castDir, det, rec) = (args[0], args[1], args[2], args[3]);
double fps = args.Length > 4 ? double.Parse(args[4]) : 1;
int threads = args.Length > 5 ? int.Parse(args[5]) : 2;
var device = args.Length > 6 && args[6] == "gpu" ? InferenceDevice.Gpu : InferenceDevice.Cpu;
NativeLibraries.Register();
var accelerator = new Accelerator(Path.Combine(Path.GetTempPath(), "xray-harness-runtimes"), new HarnessHttp(), NullLogger.Instance, new PluginConfiguration());
var backend = await accelerator.ResolveAsync(device, threads, default);
var load = System.Diagnostics.Stopwatch.StartNew();
using var engine = new OnnxFaceEngine(det, rec, backend);
Console.WriteLine($"backend: {backend.Name} (sessions in {load.ElapsedMilliseconds} ms)");
var clock = System.Diagnostics.Stopwatch.StartNew();

var cast = new List<CastCandidate>();
int order = 0;
// castDir holds either name.jpg files or name/ folders of photos.
var entries = Directory.GetFiles(castDir, "*.jpg").Select(f => (Name: Path.GetFileNameWithoutExtension(f), Files: new[] { f }))
    .Concat(Directory.GetDirectories(castDir).Select(d => (Name: Path.GetFileName(d), Files: Directory.GetFiles(d, "*.jpg").Order().ToArray())));
foreach (var (name, files) in entries.OrderBy(e => e.Name))
{
    var c = new CastCandidate { PersonId = Guid.NewGuid(), Name = name, Order = order++ };
    foreach (var f in files)
    {
        var img = ImageDecoder.Decode(File.ReadAllBytes(f));
        var e = img is null ? null : ReferenceFaces.Embed(engine, img);
        if (e is not null) c.References.Add(e);
    }
    Console.WriteLine($"ref {c.Name}: {c.References.Count}/{files.Length}");
    cast.Add(c);
}

var opts = new FaceAnalysisOptions(new DecodeOptions("ffmpeg", "ffprobe", fps, 960, File.Exists("/dev/dri/renderD128"), "/dev/dri/renderD128"), 40, 0.45f, 0.32f, 0.06f, 0.2f, 6, 4);
var res = await new FaceAnalyzer(engine).AnalyzeAsync(video, cast, opts, 480, new Progress<double>(p => Console.Error.Write($"\r{p:P0}  ")), default);
Console.WriteLine();
Console.WriteLine($"analysis: {clock.Elapsed.TotalSeconds:F1} s");
Console.WriteLine(JsonSerializer.Serialize(res.Stats));
foreach (var p in res.People)
{
    Console.WriteLine($"{p.Name,-20} screen={p.ScreenTimeSeconds,6}s conf={p.Confidence:F3} scenes=" + string.Join(" ", p.Intervals.Select(i => $"{i[0]:F0}-{i[1]:F0}")));
}
Console.WriteLine($"learned refs: {res.Learned.Count}");
foreach (var d in res.Diagnostics) Console.WriteLine(d);

internal sealed class HarnessHttp : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
