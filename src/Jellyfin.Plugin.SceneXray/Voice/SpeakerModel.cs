using Jellyfin.Plugin.SceneXray.Vision;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.SceneXray.Voice;

/// <summary>
/// Speaker embedding model (sherpa-onnx export) on ONNX Runtime (CPU, or a GPU through <see cref="Accelerator"/>).
/// On the CPU one session takes lines of any length. A GPU gets one session per length bucket
/// (1 s steps) with the length fixed, and each line's features are repeated up to its bucket:
/// OpenVINO would otherwise recompile the network for every new length, and Core ML computes the
/// pooling's frame count wrongly when the length is dynamic (every voiceprint came out NaN).
/// </summary>
public sealed class SpeakerModel : IDisposable
{
    /// <summary>Frames per length bucket on a GPU (100 frames = 1 s).</summary>
    public const int BucketFrames = 100;

    /// <summary>Lines are at most 8 s (<see cref="VoiceAnalyzer"/>), so these buckets are set up front.</summary>
    private const int WarmBuckets = 8;

    private readonly string _path;
    private readonly InferenceBackend _backend;
    private readonly bool _fixedLengths;
    private readonly Dictionary<int, InferenceSession> _sessions = [];
    private readonly string _input;
    private readonly float _scale;
    private readonly Fbank _fbank = new();

    public SpeakerModel(string path, int threads)
        : this(path, InferenceBackend.Cpu(threads))
    {
    }

    public SpeakerModel(string path, InferenceBackend backend)
        : this(path, backend, backend.IsGpu)
    {
    }

    /// <param name="fixedLengths">Use length buckets (always on a GPU; the harness also tries it on the CPU).</param>
    public SpeakerModel(string path, InferenceBackend backend, bool fixedLengths)
    {
        _path = path;
        _backend = backend;
        _fixedLengths = fixedLengths;
        var first = Session(fixedLengths ? BucketFrames : 0);
        _input = first.InputMetadata.Keys.First();

        // normalize_samples=1 means the model was trained on samples in [-1, 1]; otherwise int16 range.
        _scale = first.ModelMetadata.CustomMetadataMap.TryGetValue("normalize_samples", out var n) && n == "1" ? 1f : 32768f;
        EmbeddingSize = first.OutputMetadata.Values.First().Dimensions[^1];
    }

    public int EmbeddingSize { get; }

    /// <summary>The bucket a line of <paramref name="frames"/> frames runs in.</summary>
    public static int Bucket(int frames) => (frames + BucketFrames - 1) / BucketFrames * BucketFrames;

    /// <summary>L2-normalised voiceprint of 16 kHz mono samples in [-1, 1], or null if too short.</summary>
    public float[]? Embed(ReadOnlySpan<float> samples)
    {
        var feats = _fbank.Compute(samples, _scale, out int frames);
        if (frames < 50)
        {
            return null;
        }

        Fbank.MeanNormalize(feats, frames);
        int length = frames;
        if (_fixedLengths)
        {
            length = Bucket(frames);
            feats = Repeat(feats, frames, length);
        }

        var input = new DenseTensor<float>(feats, [1, length, Fbank.Bins]);
        float[] v;
        lock (_sessions) // titles analysed at once share the model; sessions are created per length
        {
            using var results = Session(_fixedLengths ? length : 0).Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
            v = results.First().AsTensor<float>().ToArray();
        }

        Vec.Normalize(v);
        return v;
    }

    /// <summary>
    /// Fills <paramref name="length"/> frames by playing the line again from its start. The model
    /// pools the mean and spread over time, which repeating the same speech barely moves (zeros would).
    /// </summary>
    public static float[] Repeat(float[] feats, int frames, int length)
    {
        if (length == frames)
        {
            return feats;
        }

        var padded = new float[length * Fbank.Bins];
        for (int done = 0; done < length; done += frames)
        {
            int n = Math.Min(frames, length - done);
            Array.Copy(feats, 0, padded, done * Fbank.Bins, n * Fbank.Bins);
        }

        return padded;
    }

    /// <summary>
    /// Embeds quiet noise: a GPU compiles its kernels for every bucket here (inside the crash
    /// marker, and before a title starts) rather than in the middle of one.
    /// </summary>
    public void Warm()
    {
        var rng = new Random(1);
        int buckets = _fixedLengths ? WarmBuckets : 1;
        for (int b = 1; b <= buckets; b++)
        {
            var noise = new float[Fbank.SampleRate * b];
            for (int i = 0; i < noise.Length; i++)
            {
                noise[i] = (float)(rng.NextDouble() - 0.5) * 0.01f;
            }

            var v = Embed(noise) ?? throw new InvalidOperationException("The speaker model returned nothing for its test run.");
            if (float.IsNaN(v[0]))
            {
                throw new InvalidOperationException(_backend.Name + " returned an invalid voiceprint for the speaker model.");
            }
        }
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values)
        {
            s.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>The session for one fixed length, or (0) the one that takes any length.</summary>
    private InferenceSession Session(int length)
    {
        if (_sessions.TryGetValue(length, out var session))
        {
            return session;
        }

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        if (length > 0)
        {
            opts.AddFreeDimensionOverrideByName("B", 1);
            opts.AddFreeDimensionOverrideByName("T", length);
        }

        _backend.Configure(opts, length > 0 ? "speaker-" + length : string.Empty);
        session = new InferenceSession(_path, opts);
        _sessions[length] = session;
        return session;
    }
}
