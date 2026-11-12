using SkiaSharp;

namespace Jellyfin.Plugin.SceneXray.Vision;

/// <summary>Decodes still images (cast photos) with the SkiaSharp that Jellyfin already ships.</summary>
public static class ImageDecoder
{
    public static RgbImage? Decode(byte[] bytes, int maxSide = 1024)
    {
        using var bmp = SKBitmap.Decode(bytes);
        if (bmp is null)
        {
            return null;
        }

        float scale = Math.Min(1f, (float)maxSide / Math.Max(bmp.Width, bmp.Height));
        int w = Math.Max(1, (int)(bmp.Width * scale)), h = Math.Max(1, (int)(bmp.Height * scale));
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var scaled = new SKBitmap(info);
        if (!bmp.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
        {
            return null;
        }

        var rgba = scaled.Bytes;
        var img = new RgbImage(w, h);
        for (int i = 0, j = 0; i < w * h; i++, j += 4)
        {
            img.Data[3 * i] = rgba[j];
            img.Data[(3 * i) + 1] = rgba[j + 1];
            img.Data[(3 * i) + 2] = rgba[j + 2];
        }

        return img;
    }
}
