using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// Canonical title keys (design §2, §4): <c>movie:tmdb:603</c> (IMDb as fallback) and
/// <c>episode:tvdb:81189:s01e01</c> (TMDb series id as fallback). One title, one key, whichever
/// install computes it.
/// </summary>
public static partial class TitleKey
{
    /// <summary>Edition labels the service knows; anything else is <c>other</c>.</summary>
    public static readonly IReadOnlyList<string> Editions = ["theatrical", "extended", "directors", "unrated", "imax", "other"];

    public static string? Movie(int? tmdb, string? imdb)
    {
        if (tmdb is > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"movie:tmdb:{tmdb}");
        }

        string? id = imdb?.Trim().ToLowerInvariant();
        return id is not null && ImdbId().IsMatch(id) ? $"movie:imdb:{id}" : null;
    }

    public static string? Episode(int? seriesTvdb, int? seriesTmdb, int season, int episode)
    {
        if (season < 0 || episode < 0)
        {
            return null;
        }

        string se = string.Create(CultureInfo.InvariantCulture, $"s{season:00}e{episode:00}");
        if (seriesTvdb is > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"episode:tvdb:{seriesTvdb}:{se}");
        }

        return seriesTmdb is > 0 ? string.Create(CultureInfo.InvariantCulture, $"episode:tmdb:{seriesTmdb}:{se}") : null;
    }

    /// <summary>The key of a contribution's title, or null when it has no usable id.</summary>
    public static string? Of(SharedTitle t) => t.Kind switch
    {
        "movie" => Movie(t.Tmdb, t.Imdb),
        "episode" when t.Season is { } s && t.Episode is { } e => Episode(t.Series?.Tvdb, t.Series?.Tmdb, s, e),
        _ => null,
    };

    /// <summary>Hex SHA-256 of a title key: how availability answers name titles.</summary>
    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>The 16-bit hash prefix of the availability query (design §2): first two bytes of SHA-256(key), hex.</summary>
    public static string HashPrefix(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 2));

    /// <summary>
    /// Normalises a Jellyfin version name or <c>{edition-…}</c> tag to the edition vocabulary.
    /// Empty means theatrical. Only this label is sent, never the file name.
    /// </summary>
    public static string Edition(string? name)
    {
        string n = NonLetters().Replace((name ?? string.Empty).ToLowerInvariant(), " ").Trim();
        if (n.Length == 0 || n.Contains("theatrical", StringComparison.Ordinal))
        {
            return "theatrical";
        }

        if (n.Contains("director", StringComparison.Ordinal))
        {
            return "directors";
        }

        if (n.Contains("imax", StringComparison.Ordinal))
        {
            return "imax";
        }

        if (n.Contains("unrated", StringComparison.Ordinal) || n.Contains("uncut", StringComparison.Ordinal))
        {
            return "unrated";
        }

        if (n.Contains("extended", StringComparison.Ordinal))
        {
            return "extended";
        }

        return "other";
    }

    [GeneratedRegex("^tt[0-9]{7,10}$")]
    private static partial Regex ImdbId();

    [GeneratedRegex("[^a-z]+")]
    private static partial Regex NonLetters();
}
