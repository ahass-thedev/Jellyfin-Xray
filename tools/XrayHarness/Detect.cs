// "detect" mode: XrayHarness detect <image> <detector.onnx> <recognizer.onnx> <width> [castDir]
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Vision;

static class DetectMode
{
    public static void Run(string[] a)
    {
        using var engine = new OnnxFaceEngine(a[2], a[3], 4);
        var full = ImageDecoder.Decode(File.ReadAllBytes(a[1]), 4096)!;
        int w = int.Parse(a[4]);
        var img = ImageDecoder.Decode(File.ReadAllBytes(a[1]), w)!;
        Console.WriteLine($"image {img.Width}x{img.Height}");
        var refs = new List<CastCandidate>();
        if (a.Length > 5)
            foreach (var d in Directory.GetDirectories(a[5]))
            {
                var c = new CastCandidate { PersonId = Guid.NewGuid(), Name = Path.GetFileName(d) };
                foreach (var f in Directory.GetFiles(d)) { var im = ImageDecoder.Decode(File.ReadAllBytes(f)); var e = im is null ? null : ReferenceFaces.Embed(engine, im); if (e != null) c.References.Add(e); }
                refs.Add(c);
            }
        foreach (var b in engine.Detect(img, 0.3f).OrderByDescending(b => b.Height))
        {
            var crop = FaceAligner.Align(img, b.Landmarks);
            var e = engine.Embed([crop])[0];
            var best = refs.Select(r => (r.Name, S: r.References.Count == 0 ? -1 : r.References.Max(x => Vec.Dot(x, e)))).OrderByDescending(x => x.S).Take(2);
            Console.WriteLine($"  face h={b.Height,5:F0}px score={b.Score:F2} frontal={b.Frontality():F2} x={b.X1:F0} | " + string.Join(", ", best.Select(x => $"{x.Name} {x.S:F2}")));
        }
    }
}
