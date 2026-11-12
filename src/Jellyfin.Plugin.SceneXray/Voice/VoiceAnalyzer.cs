using System.Diagnostics;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Voice;

/// <summary>Which audio to decode.</summary>
/// <param name="FfmpegPath">Jellyfin's ffmpeg.</param>
/// <param name="AudioStreamIndex">Absolute stream index of the dialogue track.</param>
/// <param name="CenterChannel">Take only the centre channel (5.1/7.1), where films put dialogue.</param>
public sealed record VoiceOptions(string FfmpegPath, int AudioStreamIndex, bool CenterChannel, float ClusterThreshold = 0.25f);

/// <summary>One voice found in a title.</summary>
public sealed class VoiceCluster
{
    public int Id { get; init; }

    public List<SubtitleCue> Cues { get; } = [];

    public float[] Centroid { get; set; } = [];

    public double SpeechSeconds => Cues.Sum(c => c.Duration);
}

public sealed class VoiceAnalysis
{
    public List<SubtitleCue> Cues { get; init; } = [];

    /// <summary>Gets cue index -> cluster id, for cues that were embedded.</summary>
    public Dictionary<int, int> CueCluster { get; } = [];

    public List<VoiceCluster> Clusters { get; } = [];

    public int Embedded { get; set; }

    public double Seconds { get; set; }
}

/// <summary>
/// Voices in a title: every single-speaker subtitle line with real words becomes a voiceprint;
/// voiceprints are clustered into voices. Audio is streamed through a small window, never held whole.
/// </summary>
public static class VoiceAnalyzer
{
    private const int Rate = Fbank.SampleRate;
    private const double MaxCueSeconds = 8;

    public static async Task<VoiceAnalysis> AnalyzeAsync(
        SpeakerModel model, string path, List<SubtitleCue> cues, VoiceOptions o, double duration, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new VoiceAnalysis { Cues = cues };
        var queue = new Queue<SubtitleCue>(cues.Where(c => !c.MultiSpeaker && c.IsSpeech && c.Duration >= 1.0).OrderBy(c => c.Start));
        var embeddings = new List<(SubtitleCue Cue, float[] V)>();

        var psi = new ProcessStartInfo(o.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string[] channel = o.CenterChannel ? ["-af", "pan=mono|c0=FC"] : ["-ac", "1"];
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", $"0:{o.AudioStreamIndex}", "-vn", "-sn", "-dn" }
            .Concat(channel).Concat(["-ar", Rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1"]))
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        FrameSource.TryLowerPriority(proc);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        using var reg = ct.Register(() =>
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
        });

        // Sliding window: buf[0] is sample number bufStart.
        var buf = new short[Rate * 40];
        long bufStart = 0;
        int bufLen = 0;
        var bytes = new byte[64 * 1024];
        int carry = 0; // odd byte left from the previous read
        var stream = proc.StandardOutput.BaseStream;
        var scratch = new float[(int)(Rate * MaxCueSeconds)];
        long lastReport = 0;

        while (true)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(carry), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int total = carry + read, samples = total / 2;
            if (bufLen + samples > buf.Length)
            {
                // Keep only what the next pending cue still needs.
                long keepFrom = queue.Count > 0 ? (long)(queue.Peek().Start * Rate) : bufStart + bufLen;
                keepFrom = Math.Clamp(keepFrom, bufStart, bufStart + bufLen);
                int drop = (int)(keepFrom - bufStart);
                if (bufLen - drop + samples > buf.Length)
                {
                    drop = bufLen + samples - buf.Length; // cue longer than the window: lose its start
                }

                Array.Copy(buf, drop, buf, 0, bufLen - drop);
                bufLen -= drop;
                bufStart += drop;
            }

            for (int i = 0; i < samples; i++)
            {
                buf[bufLen++] = (short)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8));
            }

            carry = total % 2;
            if (carry == 1)
            {
                bytes[0] = bytes[total - 1];
            }

            long bufEnd = bufStart + bufLen;
            while (queue.Count > 0)
            {
                var cue = queue.Peek();
                long s = (long)((cue.Start + 0.05) * Rate), e = (long)(Math.Min(cue.End, cue.Start + MaxCueSeconds) * Rate);
                if (e > bufEnd)
                {
                    break;
                }

                queue.Dequeue();
                if (s < bufStart || e - s < Rate)
                {
                    continue;
                }

                int n = (int)(e - s), off = (int)(s - bufStart);
                for (int i = 0; i < n; i++)
                {
                    scratch[i] = buf[off + i] / 32768f;
                }

                if (model.Embed(scratch.AsSpan(0, n)) is { } v)
                {
                    embeddings.Add((cue, v));
                }
            }

            if (bufEnd - lastReport > Rate * 60)
            {
                lastReport = bufEnd;
                progress?.Report(Math.Min(0.95, bufEnd / (Rate * Math.Max(1, duration))));
            }
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string err = await errTask.ConfigureAwait(false);
        if (proc.ExitCode != 0 && embeddings.Count == 0)
        {
            throw new InvalidOperationException($"ffmpeg could not decode the dialogue track: {err}");
        }

        result.Embedded = embeddings.Count;
        var groups = VoiceClustering.Cluster(embeddings.Select(x => x.V).ToList(), o.ClusterThreshold);
        for (int g = 0; g < groups.Count; g++)
        {
            var cluster = new VoiceCluster { Id = g };
            foreach (int k in groups[g].OrderBy(k => embeddings[k].Cue.Start))
            {
                cluster.Cues.Add(embeddings[k].Cue);
                result.CueCluster[embeddings[k].Cue.Index] = g;
            }

            cluster.Centroid = Vec.Mean(groups[g].Select(k => (embeddings[k].V, 1f)), model.EmbeddingSize);
            result.Clusters.Add(cluster);
        }

        result.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        progress?.Report(1);
        return result;
    }
}
