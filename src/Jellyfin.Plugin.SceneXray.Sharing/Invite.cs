using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// What a friend pastes into the plugin once (design §1): the registration code, good for up to
/// three installs, and the Cloudflare Access service token that lets requests reach the service at
/// all. One opaque line: <c>xray1.</c> + base64url(JSON).
/// </summary>
public sealed record Invite(
    [property: JsonPropertyName("c")] string Code,
    [property: JsonPropertyName("i")] string AccessClientId,
    [property: JsonPropertyName("s")] string AccessClientSecret)
{
    public const string Prefix = "xray1.";

    public string Format() => Prefix + Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(this));

    public static Invite? TryParse(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (!t.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var invite = JsonSerializer.Deserialize<Invite>(Base64Url.Decode(t[Prefix.Length..]));
            return invite is { Code.Length: > 0 } ? invite : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A new random registration code (128 bits).</summary>
    public static string NewCode() => Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
}

/// <summary>Bearer secrets and their stored form: the server keeps only SHA-256.</summary>
public static class Secrets
{
    /// <summary>A new install token: 256 random bits.</summary>
    public static string NewToken() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
