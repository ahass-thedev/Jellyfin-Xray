using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Sharing;
using Jellyfin.Plugin.SceneXray.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.Music;

/// <summary>
/// Music in scene. Two sources:
/// 1. Your own music library: every song is fingerprinted once (Chromaprint, through Jellyfin's
///    ffmpeg) and searched for inside each title's soundtrack. Free and offline.
/// 2. AudD (optional token): short clips are sent for identification, preferring moments the
///    subtitles mark as music (♪), capped per title.
/// </summary>
public sealed partial class MusicService
{
    private const double MaxSongSeconds = 600;
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MusicService> _log;
    private readonly string _dir;
    private readonly object _catalogLock = new();
    private readonly Share.FingerprintCache _fingerprints;
    private FingerprintMatcher? _matcher;
    private List<CatalogEntry>? _matcherTracks;

    public MusicService(ILibraryManager library, IMediaEncoder encoder, IHttpClientFactory http, Share.FingerprintCache fingerprints, ILogger<MusicService> log)
    {
        _fingerprints = fingerprints;
        _library = library;
        _encoder = encoder;
        _http = http;
        _log = log;
        _dir = Path.Combine(Plugin.Instance!.DataDirectory, "music");
        Directory.CreateDirectory(Path.Combine(_dir, "fp"));
    }

    private string CatalogPath => Path.Combine(_dir, "catalog.json");

    public sealed class CatalogEntry
    {
        public Guid ItemId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Artist { get; set; }

        public string? Album { get; set; }

        public string Signature { get; set; } = string.Empty;
    }

    // ---------------------------------------------------------------- library index

    /// <summary>Fingerprints new or changed songs and drops removed ones.</summary>
    public async Task IndexLibraryAsync(PluginConfiguration cfg, IProgress<double> progress, CancellationToken ct)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false,
        };
        if (cfg.MusicLibraryIds.Length > 0)
        {
            query.TopParentIds = cfg.MusicLibraryIds.Select(Guid.Parse).ToArray();
        }

        var songs = _library.GetItemList(query).OfType<Audio>().Where(a => File.Exists(a.Path)).ToList();
        var old = LoadCatalog().ToDictionary(e => e.ItemId);
        var next = new List<CatalogEntry>();
        int done = 0, added = 0;
        foreach (var song in songs)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(song.Path);
            string sig = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            string fp = FingerprintPath(song.Id);
            if (!(old.TryGetValue(song.Id, out var prev) && prev.Signature == sig && File.Exists(fp)))
            {
                try
                {
                    var points = await FingerprintAsync(song.Path, null, MaxSongSeconds, ct).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(fp, System.Runtime.InteropServices.MemoryMarshal.AsBytes(points.AsSpan()).ToArray(), ct).ConfigureAwait(false);
                    added++;
                }
                catch (InvalidOperationException ex)
                {
                    _log.LogDebug(ex, "Skipping {Path}", song.Path);
                    continue;
                }
            }

            next.Add(new CatalogEntry
            {
                ItemId = song.Id,
                Title = song.Name,
                Artist = song.Artists.FirstOrDefault() ?? song.AlbumArtists.FirstOrDefault(),
                Album = song.Album,
                Signature = sig,
            });
            progress.Report(100.0 * ++done / Math.Max(1, songs.Count));
        }

        foreach (var gone in old.Keys.Except(next.Select(n => n.ItemId)))
        {
            File.Delete(FingerprintPath(gone));
        }

        lock (_catalogLock)
        {
            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(next, TimelineStore.Json));
            _matcher = null; // rebuilt lazily
        }

        _log.LogInformation("Music index: {Count} songs ({Added} new or changed)", next.Count, added);
    }

    public int IndexedSongs => LoadCatalog().Count;

    private List<CatalogEntry> LoadCatalog()
    {
        lock (_catalogLock)
        {
            if (!File.Exists(CatalogPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<CatalogEntry>>(File.ReadAllText(CatalogPath), TimelineStore.Json) ?? [];
        }
    }

    private string FingerprintPath(Guid id) => Path.Combine(_dir, "fp", id.ToString("N") + ".bin");

    private (FingerprintMatcher? Matcher, List<CatalogEntry> Tracks) Matcher()
    {
        lock (_catalogLock)
        {
            if (_matcher is null)
            {
                var tracks = LoadCatalog().Where(e => File.Exists(FingerprintPath(e.ItemId))).ToList();
                if (tracks.Count == 0)
                {
                    return (null, tracks);
                }

                var fps = tracks.Select(e => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(File.ReadAllBytes(FingerprintPath(e.ItemId))).ToArray()).ToList();
                _matcher = new FingerprintMatcher(fps);
                _matcherTracks = tracks;
            }

            return (_matcher, _matcherTracks!);
        }
    }

    /// <summary>Frees the in-memory index (it can be large) when analysis goes idle.</summary>
    public void ReleaseIndex()
    {
        lock (_catalogLock)
        {
            _matcher = null;
            _matcherTracks = null;
        }
    }

    // ---------------------------------------------------------------- per title

    public async Task<List<MusicCue>> IdentifyAsync(Video item, PluginConfiguration cfg, double duration, CancellationToken ct)
    {
        var cues = new List<MusicCue>();
        var (matcher, tracks) = Matcher();
        if (matcher is not null)
        {
            // The same fingerprint gives the sharing anchors; the cache keeps it while sharing is on.
            var points = await _fingerprints.GetAsync(item, 0, null, ct).ConfigureAwait(false);
            foreach (var m in matcher.Match(points, cfg.MinMusicSeconds))
            {
                var t = tracks[m.Track];
                cues.Add(new MusicCue
                {
                    Start = Math.Round(m.Start, 1),
                    End = Math.Round(m.End, 1),
                    Title = t.Title,
                    Artist = t.Artist,
                    Album = t.Album,
                    ItemId = t.ItemId,
                    Source = "library",
                    Confidence = m.Confidence,
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.AuddApiToken))
        {
            var hints = await SubtitleMusicHintsAsync(item, ct).ConfigureAwait(false);
            cues.AddRange(await AuddAsync(item.Path, cfg, duration, hints, cues, ct).ConfigureAwait(false));
        }

        return cues.OrderBy(c => c.Start).ToList();
    }

    private async Task<List<MusicCue>> AuddAsync(string path, PluginConfiguration cfg, double duration, List<(double Start, double End)> hints, List<MusicCue> known, CancellationToken ct)
    {
        const double Clip = 12;
        double step = Math.Max(20, cfg.AuddSampleIntervalSeconds);

        // Subtitled music first, then an even grid; skip moments the library already explains.
        var samples = hints.Select(h => Math.Min(h.Start + 3, Math.Max(h.Start, h.End - Clip)))
            .Concat(Enumerable.Range(0, (int)(duration / step)).Select(i => (i * step) + (step / 2)))
            .Where(t => t + Clip < duration && !known.Any(k => t >= k.Start - 5 && t <= k.End))
            .Distinct()
            .Take(Math.Max(0, cfg.AuddMaxRequestsPerItem))
            .Order()
            .ToList();

        var cues = new List<MusicCue>();
        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        foreach (double t in samples)
        {
            ct.ThrowIfCancellationRequested();
            byte[] wav = await ClipAsync(path, t, Clip, ct).ConfigureAwait(false);
            using var form = new MultipartFormDataContent
            {
                { new StringContent(cfg.AuddApiToken.Trim()), "api_token" },
            };
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "clip.wav");
            AuddResponse? r;
            try
            {
                using var resp = await client.PostAsync("https://api.audd.io/", form, ct).ConfigureAwait(false);
                r = await JsonSerializer.DeserializeAsync<AuddResponse>(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _log.LogWarning(ex, "AudD request failed");
                break;
            }

            if (r?.Status == "error")
            {
                _log.LogWarning("AudD error: {Message}", r.Error?.Message);
                break; // bad token / out of quota: stop spending
            }

            if (r?.Result is not { Title: { Length: > 0 } } song)
            {
                continue;
            }

            // A hint region gives the song's extent; a grid sample only the clip itself.
            var region = hints.FirstOrDefault(h => t >= h.Start && t <= h.End);
            double start = region.End > 0 ? region.Start : t, end = region.End > 0 ? region.End : t + Clip;
            if (cues.LastOrDefault() is { } last && last.Title == song.Title && start - last.End <= step + 1)
            {
                last.End = Math.Max(last.End, end);
                continue;
            }

            cues.Add(new MusicCue
            {
                Start = Math.Round(start, 1),
                End = Math.Round(end, 1),
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                Source = "audd",
                Confidence = 0.9,
            });
        }

        return cues;
    }

    // ---------------------------------------------------------------- ffmpeg helpers

    private async Task<uint[]> FingerprintAsync(string path, double? start, double? length, CancellationToken ct)
    {
        var bytes = await RunBinaryAsync(_encoder.EncoderPath, Chromaprint.Args(path, 0, start, length), ct).ConfigureAwait(false);
        return Chromaprint.FromBytes(bytes);
    }

    private Task<byte[]> ClipAsync(string path, double start, double length, CancellationToken ct) =>
        RunBinaryAsync(
            _encoder.EncoderPath,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", start.ToString(CultureInfo.InvariantCulture),
             "-t", length.ToString(CultureInfo.InvariantCulture), "-i", path, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-f", "wav", "pipe:1"],
            ct);

    private static async Task<byte[]> RunBinaryAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        FrameSource.TryLowerPriority(proc);
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

        return ms.ToArray();
    }

    // ---------------------------------------------------------------- subtitles

    /// <summary>Time ranges a text subtitle marks as music (♪ lines, "[music]", "(singing)").</summary>
    private async Task<List<(double Start, double End)>> SubtitleMusicHintsAsync(Video item, CancellationToken ct)
    {
        var sub = item.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
            .OrderByDescending(s => s.Language is "eng" or "en")
            .FirstOrDefault();
        if (sub is null)
        {
            return [];
        }

        string srt;
        try
        {
            var bytes = sub.IsExternal && sub.Path?.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) == true
                ? await File.ReadAllBytesAsync(sub.Path, ct).ConfigureAwait(false)
                : await RunBinaryAsync(
                    _encoder.EncoderPath,
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", sub.IsExternal ? sub.Path! : item.Path,
                     "-map", sub.IsExternal ? "0:s:0" : $"0:{sub.Index}", "-f", "srt", "pipe:1"],
                    ct).ConfigureAwait(false);
            srt = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _log.LogDebug(ex, "No subtitle music hints for {Item}", item.Name);
            return [];
        }

        return ParseMusicCues(srt);
    }

    internal static List<(double Start, double End)> ParseMusicCues(string srt)
    {
        var regions = new List<(double Start, double End)>();
        srt = srt.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (Match m in SrtBlock().Matches(srt))
        {
            string text = m.Groups["text"].Value;
            if (!MusicMarker().IsMatch(text))
            {
                continue;
            }

            double s = SrtTime(m.Groups["s"].Value), e = SrtTime(m.Groups["e"].Value);
            if (regions.Count > 0 && s - regions[^1].End <= 8)
            {
                regions[^1] = (regions[^1].Start, Math.Max(regions[^1].End, e));
            }
            else
            {
                regions.Add((s, e));
            }
        }

        return regions;
    }

    private static double SrtTime(string t) =>
        TimeSpan.ParseExact(t.Replace('.', ','), @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture).TotalSeconds;

    [GeneratedRegex(@"(?<s>\d\d:\d\d:\d\d[,.]\d{3})\s*-->\s*(?<e>\d\d:\d\d:\d\d[,.]\d{3})[^\n]*\n(?<text>(?:.+\n?)+)")]
    private static partial Regex SrtBlock();

    [GeneratedRegex(@"[♪♫]|\[(?:[^\]]*\b)?(?:music|singing|song)\b|\((?:[^)]*\b)?(?:music|singing|song)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MusicMarker();

    private sealed class AuddResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("result")]
        public AuddSong? Result { get; set; }

        [JsonPropertyName("error")]
        public AuddError? Error { get; set; }
    }

    private sealed class AuddSong
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("artist")]
        public string? Artist { get; set; }

        [JsonPropertyName("album")]
        public string? Album { get; set; }
    }

    private sealed class AuddError
    {
        [JsonPropertyName("error_message")]
        public string? Message { get; set; }
    }
}
