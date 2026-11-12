#:property PublishAot=false
// Release step, run by CI on every merge to main.
//  1. If the csproj version has no GitHub release yet, publish dist/xray_<version>.zip as one.
//  2. Repair the GitHub side: remove the old project's GitHub Actions workflow (it rebuilt the
//     retired sidecar plugin on every tag and overwrote manifest.json) and its stray assets.
//  3. Keep images/logo.png (the catalogue image) current on GitHub, and rebuild manifest.json
//     (the plugin repository URL people add to Jellyfin) from the releases themselves, with
//     checksums of the actual zips, so the manifest can't drift from what exists.
//
//   dotnet run scripts/Publish.cs               (CI: needs GITHUB_TOKEN; zip built by build.sh)
//   DRY_RUN=1 dotnet run scripts/Publish.cs     (prints what it would do, writes nothing)
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string Repo = "ahass-thedev/Jellyfin-Xray";
const string PluginGuid = "5b3f1e9a-7c42-4d1b-9a60-2f8e4c7d3a11";
const string TargetAbi = "12.3.0.0";
const string LegacyWorkflow = ".github/workflows/build.yml";
string api = $"https://api.github.com/repos/{Repo}";
string manifestUrl = $"https://raw.githubusercontent.com/{Repo}/main/manifest.json";
const string ImagePath = "images/logo.png";
string imageUrl = $"https://raw.githubusercontent.com/{Repo}/main/{ImagePath}";

bool dryRun = Environment.GetEnvironmentVariable("DRY_RUN") == "1";
string token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
if (token.Length == 0 && !dryRun)
{
    throw new InvalidOperationException("GITHUB_TOKEN is not set (it is a protected variable: main only).");
}

string csproj = File.ReadAllText("src/Jellyfin.Plugin.SceneXray/Jellyfin.Plugin.SceneXray.csproj");
string version = Regex.Match(csproj, "<AssemblyVersion>(.+?)</AssemblyVersion>").Groups[1].Value;
string assetName = $"xray_{version}.zip";
string commitTitle = (Environment.GetEnvironmentVariable("CI_COMMIT_TITLE") ?? string.Empty).Trim();
string changelog = (Environment.GetEnvironmentVariable("CI_COMMIT_DESCRIPTION") ?? string.Empty).Trim();
changelog = changelog.Length > 0 ? changelog : commitTitle.Length > 0 ? commitTitle : $"X-Ray {version}";

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("jellyfin-xray-release");
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
if (token.Length > 0)
{
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

async Task<JsonNode?> Send(HttpMethod method, string url, HttpContent? body = null, bool allow404 = false)
{
    using var req = new HttpRequestMessage(method, url) { Content = body };
    using var resp = await http.SendAsync(req);
    string text = await resp.Content.ReadAsStringAsync();
    if (allow404 && resp.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null;
    }

    if (!resp.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"{method} {url} -> {(int)resp.StatusCode}: {text}");
    }

    return text.Length == 0 ? null : JsonNode.Parse(text);
}

StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

void Say(string s) => Console.WriteLine((dryRun ? "[dry run] " : string.Empty) + s);

// ---- 1. Publish this version if it isn't released yet ------------------------------------------
var release = await Send(HttpMethod.Get, $"{api}/releases/tags/v{version}", allow404: true);
bool hasAsset = release?["assets"]?.AsArray().Any(a => a!["name"]!.GetValue<string>() == assetName) == true;
if (hasAsset)
{
    Say($"v{version} is already released; nothing to publish.");
}
else
{
    string zip = $"dist/{assetName}";
    if (!File.Exists(zip))
    {
        throw new InvalidOperationException($"{zip} is missing: run build.sh first.");
    }

    Say($"Publishing v{version} ({new FileInfo(zip).Length / 1048576} MB).");
    if (!dryRun)
    {
        release ??= await Send(HttpMethod.Post, $"{api}/releases", Json(new
        {
            tag_name = $"v{version}",
            target_commitish = "main",
            name = $"X-Ray {version}",
            body = changelog + "\n\nInstall: in Jellyfin (12.3 or newer) go to Dashboard > Plugins > Repositories, add "
                + manifestUrl + ", then install X-Ray from the catalogue and restart Jellyfin. "
                + "Licensed under GPL-3.0; includes code from File Transformation by IAmParadox27.",
        }));
        var upload = new ByteArrayContent(File.ReadAllBytes(zip));
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        await Send(HttpMethod.Post, $"https://uploads.github.com/repos/{Repo}/releases/{release!["id"]!.GetValue<long>()}/assets?name={assetName}", upload);
        release = await Send(HttpMethod.Get, $"{api}/releases/tags/v{version}");
    }
}

// ---- 2. Repair: the old project's workflow and its stray assets --------------------------------
var legacy = await Send(HttpMethod.Get, $"{api}/contents/{LegacyWorkflow}?ref=main", allow404: true);
if (legacy is not null)
{
    Say($"Removing {LegacyWorkflow} (it rebuilt the retired sidecar plugin on every tag).");
    if (!dryRun)
    {
        await Send(HttpMethod.Delete, $"{api}/contents/{LegacyWorkflow}", Json(new
        {
            message = "Remove the old build workflow: X-Ray releases are published from GitLab CI",
            sha = legacy["sha"]!.GetValue<string>(),
            branch = "main",
        }));
    }
}

var releases = (await Send(HttpMethod.Get, $"{api}/releases?per_page=100"))!.AsArray();
var versions = new List<(Version Sort, JsonObject Entry)>();
foreach (var r in releases)
{
    string tag = r!["tag_name"]!.GetValue<string>();
    if (!Version.TryParse(tag.TrimStart('v'), out var v) || v < new Version(1, 3))
    {
        continue; // releases before 1.3 belong to the retired sidecar plugin
    }

    foreach (var a in r["assets"]!.AsArray())
    {
        string name = a!["name"]!.GetValue<string>();
        if (!name.StartsWith("xray_", StringComparison.Ordinal))
        {
            Say($"Deleting stray asset {name} from {tag}.");
            if (!dryRun)
            {
                await Send(HttpMethod.Delete, $"{api}/releases/assets/{a["id"]!.GetValue<long>()}");
            }

            continue;
        }

        // ---- 3. Manifest entry from the real asset -------------------------------------------
        string url = a["browser_download_url"]!.GetValue<string>();
        string md5 = Convert.ToHexStringLower(MD5.HashData(await http.GetByteArrayAsync(url)));
        string body = r["body"]?.GetValue<string>() ?? string.Empty;
        int cut = body.IndexOf("\n\nInstall:", StringComparison.Ordinal);
        versions.Add((v, new JsonObject
        {
            ["version"] = v.ToString(),
            ["changelog"] = (cut >= 0 ? body[..cut] : body).Trim(),
            ["targetAbi"] = TargetAbi,
            ["sourceUrl"] = url,
            ["checksum"] = md5,
            ["timestamp"] = r["published_at"]?.GetValue<string>() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        }));
    }
}

var manifest = new JsonArray(new JsonObject
{
    ["guid"] = PluginGuid,
    ["name"] = "X-Ray",
    ["description"] = "Amazon-style X-Ray for movies and shows: who is in the current scene (faces, plus voices for cartoons and costumed characters) and the song that is playing.",
    ["overview"] = "Who is on screen, and what is playing.",
    ["owner"] = "achmadoo",
    ["category"] = "General",
    ["imageUrl"] = imageUrl,
    ["versions"] = new JsonArray(versions.OrderByDescending(x => x.Sort).Select(x => (JsonNode)x.Entry).ToArray()),
});
string text = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

// The catalogue image, kept next to the manifest so its URL never changes.
byte[] image = File.ReadAllBytes(ImagePath);
var currentImage = await Send(HttpMethod.Get, $"{api}/contents/{ImagePath}?ref=main", allow404: true);
string imageContent = Convert.ToBase64String(image);
if (currentImage?["content"]?.GetValue<string>().Replace("\n", string.Empty, StringComparison.Ordinal) == imageContent)
{
    Say("Catalogue image is up to date.");
}
else if (dryRun)
{
    Say($"Would upload {ImagePath} ({image.Length:N0} bytes).");
}
else
{
    await Send(HttpMethod.Put, $"{api}/contents/{ImagePath}", Json(new
    {
        message = "Update the plugin's catalogue image",
        content = imageContent,
        sha = currentImage?["sha"]?.GetValue<string>(),
        branch = "main",
    }));
    Say($"Catalogue image updated: {imageUrl}");
}

var current = await Send(HttpMethod.Get, $"{api}/contents/manifest.json?ref=main", allow404: true);
string currentText = current is null ? string.Empty
    : Encoding.UTF8.GetString(Convert.FromBase64String(current["content"]!.GetValue<string>().Replace("\n", string.Empty, StringComparison.Ordinal)));
Say($"Manifest versions: {string.Join(", ", versions.OrderByDescending(x => x.Sort).Select(x => x.Sort))}.");
if (currentText == text)
{
    Say("Manifest is up to date.");
}
else if (dryRun)
{
    Console.WriteLine(text);
}
else
{
    await Send(HttpMethod.Put, $"{api}/contents/manifest.json", Json(new
    {
        message = $"Update plugin manifest (X-Ray {version})",
        content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
        sha = current?["sha"]?.GetValue<string>(),
        branch = "main",
    }));
    Say($"Manifest updated: {manifestUrl}");
}
