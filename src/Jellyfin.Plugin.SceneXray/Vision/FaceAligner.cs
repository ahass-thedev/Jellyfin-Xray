namespace Jellyfin.Plugin.SceneXray.Vision;

/// <summary>
/// Warps a face onto the 112x112 ArcFace template using a least-squares similarity
/// transform (Umeyama) fitted to the five detector landmarks.
/// </summary>
public static class FaceAligner
{
    public const int Size = 112;

    // Eyes, nose tip, mouth corners in the canonical ArcFace 112x112 crop.
    private static readonly float[] Template =
    [
        38.2946f, 51.6963f,
        73.5318f, 51.5014f,
        56.0252f, 71.7366f,
        41.5493f, 92.3655f,
        70.7299f, 92.2041f,
    ];

    /// <summary>Returns the aligned 112x112 crop.</summary>
    public static RgbImage Align(RgbImage source, ReadOnlySpan<float> landmarks)
    {
        // Fit dst = s*R*src + t, then invert to map each output pixel back into the source.
        var (a, b, tx, ty) = EstimateSimilarity(landmarks, Template);

        // Forward: x' = a*x - b*y + tx ; y' = b*x + a*y + ty.
        float det = (a * a) + (b * b);
        float ia = a / det, ib = -b / det;
        var o = new RgbImage(Size, Size);
        for (int v = 0; v < Size; v++)
        {
            for (int u = 0; u < Size; u++)
            {
                float dx = u - tx, dy = v - ty;
                float sx = (ia * dx) - (ib * dy);
                float sy = (ib * dx) + (ia * dy);
                int k = ((v * Size) + u) * 3;
                if (sx < -1 || sy < -1 || sx > source.Width || sy > source.Height)
                {
                    continue; // leave black outside the frame
                }

                o.Data[k] = (byte)source.Sample(sx, sy, 0);
                o.Data[k + 1] = (byte)source.Sample(sx, sy, 1);
                o.Data[k + 2] = (byte)source.Sample(sx, sy, 2);
            }
        }

        return o;
    }

    /// <summary>
    /// Least-squares 2D similarity from <paramref name="src"/> to <paramref name="dst"/> (pairs of x,y).
    /// Returns (a, b, tx, ty) with a = s*cos(theta), b = s*sin(theta).
    /// </summary>
    internal static (float A, float B, float Tx, float Ty) EstimateSimilarity(ReadOnlySpan<float> src, ReadOnlySpan<float> dst)
    {
        int n = src.Length / 2;
        double mxs = 0, mys = 0, mxd = 0, myd = 0;
        for (int i = 0; i < n; i++)
        {
            mxs += src[2 * i];
            mys += src[(2 * i) + 1];
            mxd += dst[2 * i];
            myd += dst[(2 * i) + 1];
        }

        mxs /= n; mys /= n; mxd /= n; myd /= n;
        double sxx = 0, sxy = 0, var = 0;
        for (int i = 0; i < n; i++)
        {
            double xs = src[2 * i] - mxs, ys = src[(2 * i) + 1] - mys;
            double xd = dst[2 * i] - mxd, yd = dst[(2 * i) + 1] - myd;
            sxx += (xs * xd) + (ys * yd); // dot
            sxy += (xs * yd) - (ys * xd); // cross
            var += (xs * xs) + (ys * ys);
        }

        if (var < 1e-9)
        {
            return (1, 0, (float)(mxd - mxs), (float)(myd - mys));
        }

        double a = sxx / var, b = sxy / var;
        double tx = mxd - ((a * mxs) - (b * mys));
        double ty = myd - ((b * mxs) + (a * mys));
        return ((float)a, (float)b, (float)tx, (float)ty);
    }

    /// <summary>Variance of the Laplacian on the crop's luma: a cheap sharpness score.</summary>
    public static float Sharpness(RgbImage crop)
    {
        int w = crop.Width, h = crop.Height;
        var l = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            l[i] = (0.299f * crop.Data[3 * i]) + (0.587f * crop.Data[(3 * i) + 1]) + (0.114f * crop.Data[(3 * i) + 2]);
        }

        double sum = 0, sum2 = 0;
        int n = 0;
        // Centre region only: the template's border is mostly background.
        for (int y = 20; y < h - 20; y++)
        {
            for (int x = 20; x < w - 20; x++)
            {
                float lap = l[((y - 1) * w) + x] + l[((y + 1) * w) + x] + l[(y * w) + x - 1] + l[(y * w) + x + 1] - (4 * l[(y * w) + x]);
                sum += lap;
                sum2 += lap * lap;
                n++;
            }
        }

        double mean = sum / n;
        return (float)((sum2 / n) - (mean * mean));
    }
}
