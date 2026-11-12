using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Analysis;

/// <summary>Where the ffmpeg binaries live and how to decode.</summary>
public sealed record DecodeOptions(string FfmpegPath, string FfprobePath, double Fps, int Width, bool Hardware, string VaapiDevice);

/// <summary>Streams evenly sampled RGB frames out of a video through ffmpeg.</summary>
public static class FrameSource
{
    /// <summary>Probes display size, accounting for anamorphic pixels.</summary>
    public static async Task<(int Width, int Height, double Sar)> ProbeAsync(DecodeOptions o, string path, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(
            o.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,sample_aspect_ratio", "-of", "json", path],
            ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"ffprobe failed ({code}): {stderr}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var s = doc.RootElement.GetProperty("streams")[0];
        int w = s.GetProperty("width").GetInt32(), h = s.GetProperty("height").GetInt32();
        double sar = 1;
        if (s.TryGetProperty("sample_aspect_ratio", out var sarEl) && sarEl.GetString() is { } sarText)
        {
            var parts = sarText.Split(':');
            if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
                && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0)
            {
                sar = n / d;
            }
        }

        return (w, h, sar);
    }

    internal static (int Width, int Height) OutputSize(int srcW, int srcH, double sar, int targetW)
    {
        double displayW = srcW * sar;
        int w = Math.Min(targetW, (int)displayW) & ~1;
        int h = (int)Math.Round(w * srcH / displayW) & ~1;
        return (Math.Max(2, w), Math.Max(2, h));
    }

    /// <summary>Yields (index, timeSeconds, frame). Tries VAAPI first when asked, then software.</summary>
    public static async IAsyncEnumerable<(int Index, double Time, RgbImage Frame)> ReadAsync(
        DecodeOptions o,
        string path,
        int srcW,
        int srcH,
        double sar,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (w, h) = OutputSize(srcW, srcH, sar, o.Width);
        bool yielded = false;
        if (o.Hardware && File.Exists(o.VaapiDevice))
        {
            string fps = o.Fps.ToString(CultureInfo.InvariantCulture);
            var hw = new List<string>
            {
                "-hwaccel", "vaapi", "-hwaccel_device", o.VaapiDevice, "-hwaccel_output_format", "vaapi",
                "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", $"fps={fps},scale_vaapi=w={w}:h={h}:format=nv12,hwdownload,format=nv12,format=rgb24",
            };
            await foreach (var f in DecodeAsync(o, hw, w, h, ct).ConfigureAwait(false))
            {
                if (f.Frame is null)
                {
                    break; // hardware path failed before producing anything
                }

                yielded = true;
                yield return (f.Index, f.Index / o.Fps, f.Frame);
            }

            if (yielded)
            {
                yield break;
            }
        }

        var sw = new List<string>
        {
            "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={o.Fps.ToString(CultureInfo.InvariantCulture)},scale={w}:{h}:flags=bilinear,setsar=1,format=rgb24",
        };
        await foreach (var f in DecodeAsync(o, sw, w, h, ct).ConfigureAwait(false))
        {
            if (f.Frame is null)
            {
                throw new InvalidOperationException("ffmpeg could not decode " + path + ": " + f.Error);
            }

            yield return (f.Index, f.Index / o.Fps, f.Frame);
        }
    }

    private static async IAsyncEnumerable<(int Index, RgbImage? Frame, string? Error)> DecodeAsync(
        DecodeOptions o, List<string> args, int w, int h, [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo(o.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in new[] { "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        TryLowerPriority(proc);
        var err = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (err.Length < 4000)
                {
                    err.AppendLine(line);
                }
            }
        });

        using var reg = ct.Register(() => TryKill(proc));
        var stream = proc.StandardOutput.BaseStream;
        int size = w * h * 3, index = 0;
        while (true)
        {
            var buf = new byte[size];
            int read = 0;
            while (read < size)
            {
                int n = await stream.ReadAsync(buf.AsMemory(read, size - read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < size)
            {
                break;
            }

            yield return (index++, new RgbImage(w, h, buf), null);
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        await errTask.ConfigureAwait(false);
        if (index == 0 && proc.ExitCode != 0)
        {
            yield return (0, null, err.ToString());
        }
    }

    internal static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + exe);
        using var reg = ct.Register(() => TryKill(proc));
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }

    internal static void TryLowerPriority(Process p)
    {
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // Not permitted in every container; analysis still works.
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // already gone
        }
    }
}
