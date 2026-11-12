using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Vision;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.Services;

/// <summary>
/// Builds the candidate list for a title: credited actors (an episode's guest stars plus the
/// series regulars), each with reference embeddings from their Jellyfin photo, a few extra
/// TMDb portraits (different ages and looks) and faces learned from other titles.
/// </summary>
public sealed class CastResolver
{
    private const int MaxCast = 80;
    private static readonly TimeSpan TmdbRecheck = TimeSpan.FromDays(60);
    private readonly ILibraryManager _library;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<CastResolver> _log;

    public CastResolver(ILibraryManager library, IHttpClientFactory http, ILogger<CastResolver> log)
    {
        _library = library;
        _http = http;
        _log = log;
    }

    public async Task<List<CastCandidate>> ResolveAsync(BaseItem item, IFaceEngine engine, Gallery gallery, PluginConfiguration cfg, CancellationToken ct)
    {
        var credits = new List<PersonInfo>(_library.GetPeople(item));
        if (item is Episode { Series: { } series })
        {
            credits.AddRange(_library.GetPeople(series));
        }

        var actors = credits
            .Where(p => p.Type is PersonKind.Actor or PersonKind.GuestStar && !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p.Type == PersonKind.GuestStar ? 0 : 1).First()) // the episode's own credit wins
            .Take(MaxCast)
            .ToList();

        string? tmdbKey = TmdbKey(cfg);
        var result = new List<CastCandidate>();
        int order = 0;
        foreach (var credit in actors)
        {
            ct.ThrowIfCancellationRequested();
            var person = _library.GetPerson(credit.Name);
            Guid id = person?.Id ?? credit.Id;
            if (id == Guid.Empty)
            {
                continue;
            }

            var candidate = new CastCandidate { PersonId = id, Name = credit.Name, Role = credit.Role, Order = order++ };

            // 1. The person's Jellyfin portrait.
            if (person?.GetImageInfo(ImageType.Primary, 0) is { } img && !string.IsNullOrEmpty(img.Path))
            {
                string key = img.IsLocalFile ? $"jf:{img.Path}:{img.DateModified.Ticks}" : $"jf:{img.Path}";
                await AddPhotoAsync(candidate, gallery, engine, key, () => LoadAsync(img.Path, img.IsLocalFile, ct)).ConfigureAwait(false);
            }

            // 2. Extra TMDb portraits.
            string? tmdbId = null;
            person?.ProviderIds.TryGetValue("Tmdb", out tmdbId);
            if (tmdbId is null)
            {
                credit.ProviderIds?.TryGetValue("Tmdb", out tmdbId);
            }

            if (tmdbKey is not null && tmdbId is not null && cfg.TmdbPhotosPerPerson > 0)
            {
                await AddTmdbPhotosAsync(candidate, gallery, engine, tmdbKey, tmdbId, cfg.TmdbPhotosPerPerson, ct).ConfigureAwait(false);
            }

            // 3. Everything already known about the face (photos cached earlier + learned).
            candidate.References.Clear();
            candidate.References.AddRange(gallery.References(id, item.Id));
            result.Add(candidate);
        }

        gallery.Save();
        return result;
    }

    private async Task AddPhotoAsync(CastCandidate c, Gallery gallery, IFaceEngine engine, string key, Func<Task<byte[]?>> load)
    {
        if (gallery.TryGetPhoto(c.PersonId, key, out _))
        {
            return;
        }

        float[]? embedding = null;
        try
        {
            var bytes = await load().ConfigureAwait(false);
            var image = bytes is null ? null : ImageDecoder.Decode(bytes);
            embedding = image is null ? null : ReferenceFaces.Embed(engine, image);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _log.LogDebug(ex, "Could not load photo {Key} for {Name}", key, c.Name);
            return; // transient: try again next time
        }

        gallery.SetPhoto(c.PersonId, key, embedding);
    }

    private async Task AddTmdbPhotosAsync(CastCandidate c, Gallery gallery, IFaceEngine engine, string apiKey, string tmdbId, int count, CancellationToken ct)
    {
        if (gallery.TmdbCheckedAt(c.PersonId) is { } at && DateTime.UtcNow - at < TmdbRecheck)
        {
            return;
        }

        try
        {
            using var client = _http.CreateClient();
            var images = await client.GetFromJsonAsync<TmdbImages>(
                $"https://api.themoviedb.org/3/person/{Uri.EscapeDataString(tmdbId)}/images?api_key={apiKey}", ct).ConfigureAwait(false);
            foreach (var p in images?.Profiles.Take(count) ?? [])
            {
                string url = "https://image.tmdb.org/t/p/w300" + p.FilePath;
                await AddPhotoAsync(c, gallery, engine, "tmdb:" + p.FilePath, () => client.GetByteArrayAsync(url, ct)!).ConfigureAwait(false);
            }

            gallery.MarkTmdbChecked(c.PersonId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.LogDebug(ex, "TMDb photos unavailable for {Name}", c.Name);
        }
    }

    private async Task<byte[]?> LoadAsync(string path, bool local, CancellationToken ct)
    {
        if (local)
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
        }

        using var client = _http.CreateClient();
        return await client.GetByteArrayAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The configured key, else the key Jellyfin's own TMDb provider uses (read at runtime, never
    /// copied into this plugin), so extra photos work without signing up for anything.
    /// </summary>
    private static string? TmdbKey(PluginConfiguration cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
        {
            return cfg.TmdbApiKey.Trim();
        }

        try
        {
            var asm = AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == "MediaBrowser.Providers");
            var field = asm?.GetType("MediaBrowser.Providers.Plugins.Tmdb.TmdbUtils")
                ?.GetField("ApiKey", BindingFlags.Public | BindingFlags.Static);
            return (field?.IsLiteral == true ? field.GetRawConstantValue() : field?.GetValue(null)) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class TmdbImages
    {
        [JsonPropertyName("profiles")]
        public List<TmdbImage> Profiles { get; set; } = [];
    }

    private sealed class TmdbImage
    {
        [JsonPropertyName("file_path")]
        public string FilePath { get; set; } = string.Empty;
    }
}
