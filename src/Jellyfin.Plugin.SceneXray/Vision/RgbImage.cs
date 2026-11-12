namespace Jellyfin.Plugin.SceneXray.Vision;

/// <summary>A packed RGB24 image.</summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 3];
        if (Data.Length < width * height * 3)
        {
            throw new ArgumentException("Buffer smaller than width*height*3.", nameof(data));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }

    /// <summary>Bilinear sample of one channel at a sub-pixel position (clamped to the edge).</summary>
    public float Sample(float x, float y, int channel)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float fx = x - x0, fy = y - y0;
        var d = Data;
        float a = d[((y0 * Width) + x0) * 3 + channel];
        float b = d[((y0 * Width) + x1) * 3 + channel];
        float c = d[((y1 * Width) + x0) * 3 + channel];
        float e = d[((y1 * Width) + x1) * 3 + channel];
        return (a * (1 - fx) * (1 - fy)) + (b * fx * (1 - fy)) + (c * (1 - fx) * fy) + (e * fx * fy);
    }

    /// <summary>Returns a horizontally mirrored copy.</summary>
    public RgbImage FlipHorizontal()
    {
        var o = new RgbImage(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int s = ((y * Width) + x) * 3, t = ((y * Width) + (Width - 1 - x)) * 3;
                o.Data[t] = Data[s];
                o.Data[t + 1] = Data[s + 1];
                o.Data[t + 2] = Data[s + 2];
            }
        }

        return o;
    }

    /// <summary>
    /// Scales the image into a <paramref name="dw"/>x<paramref name="dh"/> CHW float tensor, top-left
    /// aligned and padded (the SCRFD convention). Returns the scale applied.
    /// </summary>
    public float LetterboxInto(int dw, int dh, Span<float> chw, float mean, float std)
    {
        float scale = Math.Min((float)dw / Width, (float)dh / Height);
        int nw = Math.Min(dw, Math.Max(1, (int)(Width * scale))), nh = Math.Min(dh, Math.Max(1, (int)(Height * scale)));
        int plane = dw * dh;
        chw.Fill((0 - mean) / std);
        float inv = 1f / scale;
        for (int y = 0; y < nh; y++)
        {
            float sy = ((y + 0.5f) * inv) - 0.5f;
            for (int x = 0; x < nw; x++)
            {
                float sx = ((x + 0.5f) * inv) - 0.5f;
                int i = (y * dw) + x;
                chw[i] = (Sample(sx, sy, 0) - mean) / std;
                chw[plane + i] = (Sample(sx, sy, 1) - mean) / std;
                chw[(2 * plane) + i] = (Sample(sx, sy, 2) - mean) / std;
            }
        }

        return scale;
    }

    /// <summary>Normalised 32-bin luma histogram, used for cheap shot-cut detection.</summary>
    public float[] LumaHistogram()
    {
        var h = new float[32];
        int n = 0;
        // Every 4th pixel in each direction is plenty for a histogram.
        for (int y = 0; y < Height; y += 4)
        {
            for (int x = 0; x < Width; x += 4)
            {
                int i = ((y * Width) + x) * 3;
                int l = ((Data[i] * 77) + (Data[i + 1] * 150) + (Data[i + 2] * 29)) >> 8;
                h[l >> 3]++;
                n++;
            }
        }

        if (n > 0)
        {
            for (int i = 0; i < h.Length; i++)
            {
                h[i] /= n;
            }
        }

        return h;
    }

    /// <summary>
    /// Normalised 512-bin RGB colour histogram (8 levels per channel): a "which set is this"
    /// fingerprint. Coarser bins made different sets of one colourful film look alike.
    /// </summary>
    public float[] ColorHistogram()
    {
        var h = new float[512];
        int n = 0;
        int step = Math.Max(1, Width / 160); // ~160 samples per row is plenty
        for (int y = 0; y < Height; y += step)
        {
            for (int x = 0; x < Width; x += step)
            {
                int i = ((y * Width) + x) * 3;
                h[((Data[i] >> 5) << 6) | ((Data[i + 1] >> 5) << 3) | (Data[i + 2] >> 5)]++;
                n++;
            }
        }

        if (n > 0)
        {
            for (int i = 0; i < h.Length; i++)
            {
                h[i] /= n;
            }
        }

        return h;
    }

    /// <summary>Half the L1 distance between two normalised histograms (0 = same, 1 = disjoint).</summary>
    public static float HistogramDistance(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            s += Math.Abs(a[i] - b[i]);
        }

        return s / 2;
    }
}
