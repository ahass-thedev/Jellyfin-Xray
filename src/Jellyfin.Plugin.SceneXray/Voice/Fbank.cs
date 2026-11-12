namespace Jellyfin.Plugin.SceneXray.Voice;

/// <summary>
/// Kaldi-compatible log mel filterbank (the features speaker models are trained on):
/// 16 kHz, 25 ms frames every 10 ms, DC removal, pre-emphasis 0.97, povey window,
/// 512-point power spectrum, 80 mel bins from 20 Hz to Nyquist, natural log.
/// </summary>
public sealed class Fbank
{
    public const int SampleRate = 16000;
    public const int Bins = 80;
    private const int FrameLength = 400;
    private const int FrameShift = 160;
    private const int FftSize = 512;
    private readonly float[] _window = new float[FrameLength];
    private readonly (int Start, float[] Weights)[] _mel = new (int, float[])[Bins];

    public Fbank()
    {
        for (int i = 0; i < FrameLength; i++)
        {
            _window[i] = MathF.Pow(0.5f - (0.5f * MathF.Cos(2 * MathF.PI * i / (FrameLength - 1))), 0.85f);
        }

        static double Mel(double f) => 1127.0 * Math.Log(1 + (f / 700.0));
        double lowMel = Mel(20), highMel = Mel(SampleRate / 2.0), delta = (highMel - lowMel) / (Bins + 1);
        double binWidth = (double)SampleRate / FftSize;
        for (int b = 0; b < Bins; b++)
        {
            double left = lowMel + (b * delta), center = left + delta, right = center + delta;
            var weights = new List<float>();
            int first = -1;
            for (int i = 0; i < FftSize / 2; i++)
            {
                double m = Mel(binWidth * i);
                if (m > left && m < right)
                {
                    first = first < 0 ? i : first;
                    weights.Add((float)(m <= center ? (m - left) / (center - left) : (right - m) / (right - center)));
                }
                else if (first >= 0)
                {
                    break;
                }
            }

            _mel[b] = (Math.Max(0, first), weights.ToArray());
        }
    }

    /// <summary>Features [frames x 80] for samples in [-1, 1]; <paramref name="scale"/> 32768 gives Kaldi's int16 range.</summary>
    public float[] Compute(ReadOnlySpan<float> samples, float scale, out int frames)
    {
        frames = samples.Length < FrameLength ? 0 : 1 + ((samples.Length - FrameLength) / FrameShift);
        var feats = new float[frames * Bins];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var frame = new float[FrameLength];
        for (int f = 0; f < frames; f++)
        {
            var src = samples.Slice(f * FrameShift, FrameLength);
            float mean = 0;
            for (int i = 0; i < FrameLength; i++)
            {
                frame[i] = src[i] * scale;
                mean += frame[i];
            }

            mean /= FrameLength;
            for (int i = 0; i < FrameLength; i++)
            {
                frame[i] -= mean;
            }

            for (int i = FrameLength - 1; i > 0; i--)
            {
                frame[i] -= 0.97f * frame[i - 1];
            }

            frame[0] -= 0.97f * frame[0];
            Array.Clear(re);
            Array.Clear(im);
            for (int i = 0; i < FrameLength; i++)
            {
                re[i] = frame[i] * _window[i];
            }

            Fft(re, im);
            for (int b = 0; b < Bins; b++)
            {
                var (start, w) = _mel[b];
                double e = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int i = start + k;
                    e += w[k] * ((re[i] * re[i]) + (im[i] * im[i]));
                }

                feats[(f * Bins) + b] = (float)Math.Log(Math.Max(e, 1.1920929e-07));
            }
        }

        return feats;
    }

    /// <summary>Subtract the per-bin mean over time (what both supported models expect).</summary>
    public static void MeanNormalize(float[] feats, int frames)
    {
        if (frames == 0)
        {
            return;
        }

        for (int b = 0; b < Bins; b++)
        {
            double m = 0;
            for (int f = 0; f < frames; f++)
            {
                m += feats[(f * Bins) + b];
            }

            float mean = (float)(m / frames);
            for (int f = 0; f < frames; f++)
            {
                feats[(f * Bins) + b] -= mean;
            }
        }
    }

    /// <summary>In-place iterative radix-2 complex FFT.</summary>
    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + (len / 2);
                    float tr = (re[b] * cr) - (im[b] * ci), ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    float ncr = (cr * wr) - (ci * wi);
                    ci = (cr * wi) + (ci * wr);
                    cr = ncr;
                }
            }
        }
    }
}
