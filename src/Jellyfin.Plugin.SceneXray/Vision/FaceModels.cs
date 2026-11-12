using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.SceneXray.Vision;

/// <summary>A detected face in source-image pixels.</summary>
public sealed record FaceBox(float X1, float Y1, float X2, float Y2, float Score, float[] Landmarks)
{
    public float Width => X2 - X1;

    public float Height => Y2 - Y1;

    public float Area => Math.Max(0, Width) * Math.Max(0, Height);

    public float IoU(FaceBox o)
    {
        float ix = Math.Max(0, Math.Min(X2, o.X2) - Math.Max(X1, o.X1));
        float iy = Math.Max(0, Math.Min(Y2, o.Y2) - Math.Max(Y1, o.Y1));
        float inter = ix * iy;
        float u = Area + o.Area - inter;
        return u <= 0 ? 0 : inter / u;
    }

    /// <summary>
    /// 1 for a frontal face, falling towards 0 in profile: how centred the nose is between the eyes.
    /// </summary>
    public float Frontality()
    {
        float lx = Landmarks[0], rx = Landmarks[2], nx = Landmarks[4];
        float eyeSpan = rx - lx;
        if (eyeSpan <= 1)
        {
            return 0;
        }

        float off = Math.Abs(((nx - lx) / eyeSpan) - 0.5f); // 0 centred, 0.5 at an eye
        return Math.Clamp(1 - (off * 2), 0, 1);
    }
}

/// <summary>Detection + embedding, the seam a remote (GPU) worker can implement later.</summary>
public interface IFaceEngine : IDisposable
{
    /// <summary>Gets the embedding dimension.</summary>
    int EmbeddingSize { get; }

    IReadOnlyList<FaceBox> Detect(RgbImage image, float minScore = 0.5f);

    /// <summary>L2-normalised embeddings for aligned 112x112 crops.</summary>
    float[][] Embed(IReadOnlyList<RgbImage> alignedCrops);
}

/// <summary>SCRFD detector + ArcFace recogniser on ONNX Runtime (CPU, or a GPU through <see cref="Accelerator"/>).</summary>
public sealed class OnnxFaceEngine : IFaceEngine
{
    private const int DetSize = 640;
    private static readonly int[] Strides = [8, 16, 32];
    private readonly InferenceSession _det;
    private readonly InferenceSession _rec;
    private readonly string _detInput;
    private readonly string _recInput;
    private readonly object _lock = new();
    private readonly int _maxBatch;

    public OnnxFaceEngine(string detectorPath, string recognizerPath, int threads)
        : this(detectorPath, recognizerPath, InferenceBackend.Cpu(threads))
    {
    }

    public OnnxFaceEngine(string detectorPath, string recognizerPath, InferenceBackend backend)
    {
        SessionOptions Options()
        {
            var opts = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, // batch-size shape warnings are noise
            };
            backend.Configure(opts, string.Empty);
            return opts;
        }

        _maxBatch = Math.Max(1, backend.MaxRecognizerBatch);
        _det = new InferenceSession(detectorPath, Options());
        _rec = new InferenceSession(recognizerPath, Options());
        _detInput = _det.InputMetadata.Keys.First();
        _recInput = _rec.InputMetadata.Keys.First();
        EmbeddingSize = _rec.OutputMetadata.Values.First().Dimensions[^1];
    }

    public int EmbeddingSize { get; }

    public IReadOnlyList<FaceBox> Detect(RgbImage image, float minScore = 0.5f)
    {
        // Fit the long side to DetSize and round the short side up to the coarsest stride, so a
        // widescreen frame costs ~half a square input instead of feeding the network padding.
        float fit = (float)DetSize / Math.Max(image.Width, image.Height);
        int dw = RoundUp((int)Math.Ceiling(image.Width * fit), 32), dh = RoundUp((int)Math.Ceiling(image.Height * fit), 32);
        var input = new DenseTensor<float>([1, 3, dh, dw]);
        float scale = image.LetterboxInto(dw, dh, input.Buffer.Span, 127.5f, 128f);

        List<FaceBox> found = [];
        lock (_lock)
        {
            using var results = _det.Run([NamedOnnxValue.CreateFromTensor(_detInput, input)]);

            // SCRFD emits, per stride, scores [N,1], boxes [N,4] and landmarks [N,10] (sometimes with a
            // leading batch dim). Group them by anchor count instead of trusting the output order.
            var outs = results.Select(r => r.AsTensor<float>()).ToList();
            foreach (int stride in Strides)
            {
                int gw = dw / stride, cells = gw * (dh / stride);
                Tensor<float>? scores = null, boxes = null, kps = null;
                int anchors = 0;
                foreach (var t in outs)
                {
                    int last = t.Dimensions[^1];
                    long rows = t.Length / last;
                    if (rows % cells != 0 || rows / cells is < 1 or > 3)
                    {
                        continue;
                    }

                    anchors = (int)(rows / cells);
                    if (last == 1) { scores = t; }
                    else if (last == 4) { boxes = t; }
                    else if (last == 10) { kps = t; }
                }

                if (scores is null || boxes is null || kps is null)
                {
                    throw new InvalidOperationException("Unexpected SCRFD outputs: the detector must have landmark heads.");
                }

                Decode(stride, gw, cells, anchors, scores, boxes, kps, minScore, scale, found);
            }
        }

        return Nms(found, 0.4f);
    }

    private static int RoundUp(int v, int m) => (v + m - 1) / m * m;

    private static void Decode(int stride, int gw, int cells, int anchors, Tensor<float> scores, Tensor<float> boxes, Tensor<float> kps, float minScore, float scale, List<FaceBox> into)
    {
        var s = ((DenseTensor<float>)scores).Buffer.Span;
        var b = ((DenseTensor<float>)boxes).Buffer.Span;
        var k = ((DenseTensor<float>)kps).Buffer.Span;
        for (int i = 0; i < cells * anchors; i++)
        {
            if (s[i] < minScore)
            {
                continue;
            }

            int cell = i / anchors;
            float cx = (cell % gw) * stride, cy = (cell / gw) * stride;
            float x1 = (cx - (b[(4 * i) + 0] * stride)) / scale;
            float y1 = (cy - (b[(4 * i) + 1] * stride)) / scale;
            float x2 = (cx + (b[(4 * i) + 2] * stride)) / scale;
            float y2 = (cy + (b[(4 * i) + 3] * stride)) / scale;
            var lm = new float[10];
            for (int j = 0; j < 5; j++)
            {
                lm[2 * j] = (cx + (k[(10 * i) + (2 * j)] * stride)) / scale;
                lm[(2 * j) + 1] = (cy + (k[(10 * i) + (2 * j) + 1] * stride)) / scale;
            }

            into.Add(new FaceBox(x1, y1, x2, y2, s[i], lm));
        }
    }

    internal static List<FaceBox> Nms(List<FaceBox> boxes, float iou)
    {
        var sorted = boxes.OrderByDescending(b => b.Score).ToList();
        var keep = new List<FaceBox>();
        foreach (var b in sorted)
        {
            // Also drop boxes mostly inside a stronger one: SCRFD sometimes fires twice on one
            // face at different scales, which would later look like two people in one frame.
            if (keep.All(k => k.IoU(b) < iou && Containment(k, b) < 0.6f))
            {
                keep.Add(b);
            }
        }

        return keep;
    }

    private static float Containment(FaceBox a, FaceBox b)
    {
        float ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        float iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        float small = Math.Min(a.Area, b.Area);
        return small <= 0 ? 0 : ix * iy / small;
    }

    public float[][] Embed(IReadOnlyList<RgbImage> alignedCrops)
    {
        var result = new float[alignedCrops.Count][];
        for (int start = 0; start < alignedCrops.Count; start += _maxBatch)
        {
            int n = Math.Min(_maxBatch, alignedCrops.Count - start);
            var input = new DenseTensor<float>([n, 3, FaceAligner.Size, FaceAligner.Size]);
            var span = input.Buffer.Span;
            int plane = FaceAligner.Size * FaceAligner.Size;
            for (int c = 0; c < n; c++)
            {
                var d = alignedCrops[start + c].Data;
                int off = c * 3 * plane;
                for (int p = 0; p < plane; p++)
                {
                    span[off + p] = (d[3 * p] - 127.5f) / 127.5f;
                    span[off + plane + p] = (d[(3 * p) + 1] - 127.5f) / 127.5f;
                    span[off + (2 * plane) + p] = (d[(3 * p) + 2] - 127.5f) / 127.5f;
                }
            }

            lock (_lock)
            {
                using var results = _rec.Run([NamedOnnxValue.CreateFromTensor(_recInput, input)]);
                var o = results.First().AsTensor<float>();
                var flat = ((DenseTensor<float>)o).Buffer.Span;
                int dim = EmbeddingSize;
                for (int c = 0; c < n; c++)
                {
                    var v = flat.Slice(c * dim, dim).ToArray();
                    Vec.Normalize(v);
                    result[start + c] = v;
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        _det.Dispose();
        _rec.Dispose();
    }
}

/// <summary>Small vector helpers.</summary>
public static class Vec
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var acc = System.Numerics.Vector<float>.Zero;
        int w = System.Numerics.Vector<float>.Count, i = 0;
        for (; i <= a.Length - w; i += w)
        {
            acc += new System.Numerics.Vector<float>(a.Slice(i, w)) * new System.Numerics.Vector<float>(b.Slice(i, w));
        }

        float s = System.Numerics.Vector.Sum(acc);
        for (; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    public static void Normalize(Span<float> v)
    {
        float n = MathF.Sqrt(Dot(v, v));
        if (n > 1e-9f)
        {
            for (int i = 0; i < v.Length; i++)
            {
                v[i] /= n;
            }
        }
    }

    /// <summary>Weighted mean of unit vectors, re-normalised.</summary>
    public static float[] Mean(IEnumerable<(float[] V, float W)> items, int dim)
    {
        var m = new float[dim];
        foreach (var (v, w) in items)
        {
            for (int i = 0; i < dim; i++)
            {
                m[i] += v[i] * w;
            }
        }

        Normalize(m);
        return m;
    }
}
