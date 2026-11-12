using System.Globalization;
using Jellyfin.Plugin.SceneXray.Sharing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.SceneXray.Share;

/// <summary>
/// A title's raw Chromaprint fingerprint, per audio track (design §3: anchors come free from the
/// music pass's fingerprint). Kept on disk under <c>share/fp/</c> only while sharing is on, and
/// only until the title's submission has gone out; otherwise it is computed and handed back.
/// </summary>
public sealed class FingerprintCache
{
    private readonly IMediaEncoder _encoder;

    public FingerprintCache(IMediaEncoder encoder) => _encoder = encoder;

    private static string Dir => Path.Combine(Plugin.Instance!.DataDirectory, "share", "fp");

    /// <param name="track">Index among the audio streams (<c>0:a:N</c>).</param>
    /// <param name="filter">An <c>-af</c> chain (the PAL retry); such fingerprints are never cached.</param>
    public async Task<uint[]> GetAsync(BaseItem item, int track, string? filter, CancellationToken ct)
    {
        var info = new FileInfo(item.Path);
        string file = Path.Combine(Dir, string.Create(CultureInfo.InvariantCulture, $"{item.Id:N}-{track}-{info.Length}-{info.LastWriteTimeUtc.Ticks}.bin"));
        if (filter is null && File.Exists(file))
        {
            return Chromaprint.FromBytes(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false));
        }

        var fp = await Chromaprint.FingerprintAsync(_encoder.EncoderPath, Chromaprint.Args(item.Path, track, null, null, filter), ct).ConfigureAwait(false);
        if (filter is null && Plugin.Instance!.Configuration.SharingEnabled)
        {
            Directory.CreateDirectory(Dir);
            Drop(item.Id);
            await File.WriteAllBytesAsync(file, Chromaprint.ToBytes(fp), ct).ConfigureAwait(false);
        }

        return fp;
    }

    public void Drop(Guid itemId)
    {
        if (Directory.Exists(Dir))
        {
            foreach (string f in Directory.EnumerateFiles(Dir, itemId.ToString("N") + "-*.bin"))
            {
                File.Delete(f);
            }
        }
    }

    public void Clear()
    {
        if (Directory.Exists(Dir))
        {
            foreach (string f in Directory.EnumerateFiles(Dir, "*.bin"))
            {
                File.Delete(f);
            }
        }
    }
}
