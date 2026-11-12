using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// Raw Chromaprint fingerprints through ffmpeg's <c>chromaprint</c> muxer (Jellyfin's ffmpeg has it):
/// one 32-bit sub-fingerprint per <see cref="FingerprintMatcher.ItemSeconds"/> of audio.
/// </summary>
public static class Chromaprint
{
    /// <summary>NTSC film rate.</summary>
    public const double FilmFps = 24000.0 / 1001.0;

    /// <summary>A PAL release plays film at 25 fps: 4.1 % fast, and about 0.7 semitone sharp.</summary>
    public const double PalRatio = 25.0 / FilmFps;

    /// <summary>ffmpeg arguments that write the raw fingerprint of <paramref name="path"/> to stdout.</summary>
    /// <param name="audioStream">Index among the audio streams (<c>0:a:N</c>).</param>
    /// <param name="filter">Optional <c>-af</c> chain, e.g. <see cref="PalFilter"/>.</param>
    public static List<string> Args(string path, int audioStream = 0, double? start = null, double? length = null, string? filter = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (start is { } s)
        {
            args.AddRange(["-ss", s.ToString(CultureInfo.InvariantCulture)]);
        }

        if (length is { } l)
        {
            args.AddRange(["-t", l.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-i", path, "-map", $"0:a:{audioStream}", "-vn", "-sn", "-ac", "2"]);
        if (!string.IsNullOrEmpty(filter))
        {
            args.AddRange(["-af", filter]);
        }

        args.AddRange(["-f", "chromaprint", "-fp_format", "raw", "pipe:1"]);
        return args;
    }

    /// <summary>
    /// An <c>-af</c> chain that plays the audio <paramref name="speed"/> times as fast, shifting the
    /// pitch with it, exactly as a PAL transfer does. <c>speed = 1 / PalRatio</c> undoes a PAL
    /// speed-up (tempo and pitch); <c>speed = PalRatio</c> applies one.
    /// </summary>
    public static string PalFilter(double speed) =>
        string.Create(CultureInfo.InvariantCulture, $"aresample=48000,asetrate={Math.Round(48000 * speed):0},aresample=48000");

    /// <summary>Runs ffmpeg and returns the raw fingerprint.</summary>
    public static async Task<uint[]> FingerprintAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var reg = ct.Register(() =>
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
        });
        using var ms = new MemoryStream();
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string err = await errTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {err}");
        }

        return FromBytes(ms.ToArray());
    }

    public static uint[] FromBytes(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, uint>(bytes).ToArray();

    public static byte[] ToBytes(uint[] points) => MemoryMarshal.AsBytes(points.AsSpan()).ToArray();
}
