using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Jellyfin.Plugin.SceneXray.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.Vision;

/// <summary>
/// Downloads the InsightFace model packs on first use, keeps only the two files we need,
/// and verifies them by SHA-256. Models are research-licensed (non-commercial), so they
/// are fetched by the server rather than shipped with the plugin.
/// </summary>
public sealed class ModelStore
{
    private const string Release = "https://github.com/deepinsight/insightface/releases/download/v0.7/";

    private static readonly Dictionary<string, (string Zip, string Sha)> Files = new()
    {
        ["det_10g.onnx"] = ("buffalo_l.zip", "5838f7fe053675b1c7a08b633df49e7af5495cee0493c7dcf6697200b85b5b91"),
        ["w600k_r50.onnx"] = ("buffalo_l.zip", "4c06341c33c2ca1f86781dab0e829f88ad5b64be9fba56e56bc9ebdefc619e43"),
        ["det_500m.onnx"] = ("buffalo_s.zip", "5e4447f50245bbd7966bd6c0fa52938c61474a04ec7def48753668a9d8b4ea3a"),
        ["w600k_mbf.onnx"] = ("buffalo_s.zip", "9cc6e4a75f0e2bf0b1aed94578f144d15175f357bdc05e815e5c4a02b319eb4f"),
    };

    /// <summary>Speaker-recognition model (WeSpeaker ResNet34-LM, VoxCeleb; sherpa-onnx export).</summary>
    public const string SpeakerModel = "wespeaker_en_voxceleb_resnet34_LM.onnx";
    private const string SpeakerUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/" + SpeakerModel;
    private const string SpeakerSha = "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012";

    public async Task<string> EnsureSpeakerModelAsync(CancellationToken ct)
    {
        string path = Path.Combine(_root, SpeakerModel);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Verified(path, SpeakerSha))
            {
                return path;
            }

            Directory.CreateDirectory(_root);
            _log.LogInformation("Downloading speaker model from {Url}", SpeakerUrl);
            using (var client = _http.CreateClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                var bytes = await client.GetByteArrayAsync(SpeakerUrl, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            }

            if (!Verified(path, SpeakerSha))
            {
                File.Delete(path);
                throw new InvalidDataException("Speaker model failed its SHA-256 check.");
            }

            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Detector and recognizer file names for a pack.</summary>
    public static (string Detector, string Recognizer) FilesFor(ModelPack pack) => pack switch
    {
        ModelPack.Accurate => ("det_10g.onnx", "w600k_r50.onnx"),
        ModelPack.Fast => ("det_500m.onnx", "w600k_mbf.onnx"),
        _ => ("det_500m.onnx", "w600k_r50.onnx"),
    };

    private readonly string _root;
    private readonly IHttpClientFactory _http;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModelStore(string root, IHttpClientFactory http, ILogger log)
    {
        _root = root;
        _http = http;
        _log = log;
    }

    /// <summary>Returns local paths to (detector, recognizer), downloading them if needed.</summary>
    public async Task<(string Detector, string Recognizer)> EnsureAsync(ModelPack which, CancellationToken ct)
    {
        var (det, rec) = FilesFor(which);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await EnsureFileAsync(det, ct).ConfigureAwait(false), await EnsureFileAsync(rec, ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> EnsureFileAsync(string name, CancellationToken ct)
    {
        var (zipName, sha) = Files[name];
        string path = Path.Combine(_root, name);
        if (Verified(path, sha))
        {
            return path;
        }

        Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, zipName + ".part");
        _log.LogInformation("Downloading face model {Name} from {Url}", name, Release + zipName);
        using (var client = _http.CreateClient())
        {
            client.Timeout = TimeSpan.FromMinutes(30);
            using var resp = await client.GetAsync(Release + zipName, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zip);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        try
        {
            // Take every file this zip provides, so the other pack member needs no second download.
            using var archive = ZipFile.OpenRead(zip);
            foreach (var (file, src) in Files.Where(f => f.Value.Zip == zipName))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name == file)
                    ?? throw new InvalidDataException($"{file} missing from {zipName}");
                entry.ExtractToFile(Path.Combine(_root, file), overwrite: true);
                if (!Verified(Path.Combine(_root, file), src.Sha))
                {
                    File.Delete(Path.Combine(_root, file));
                    throw new InvalidDataException($"{file} failed its SHA-256 check.");
                }
            }
        }
        finally
        {
            File.Delete(zip);
        }

        _log.LogInformation("Face model {Name} ready", name);
        return path;
    }

    private static bool Verified(string path, string sha)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // Hashing 170 MB takes ~0.3 s; cache the verdict next to the file.
        string stamp = path + ".ok";
        var info = new FileInfo(path);
        string expect = $"{sha}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        if (File.Exists(stamp) && File.ReadAllText(stamp) == expect)
        {
            return true;
        }

        using var fs = File.OpenRead(path);
        bool ok = Convert.ToHexStringLower(SHA256.HashData(fs)) == sha;
        if (ok)
        {
            File.WriteAllText(stamp, expect);
        }

        return ok;
    }
}

/// <summary>
/// Loads the right ONNX Runtime native library for this machine. The plugin zip carries one per
/// platform as runtimes/&lt;rid&gt;/onnxruntime.native: the ".native" extension (instead of
/// .so/.dll/.dylib) keeps Jellyfin, which loads every *.dll in a plugin folder as .NET code,
/// from tripping over the Windows library. Jellyfin also loads plugins into their own context,
/// where the runtime would not probe the plugin folder anyway.
/// </summary>
public static class NativeLibraries
{
    /// <summary>Platforms the plugin ships ONNX Runtime for.</summary>
    public static readonly string[] Supported = ["linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-arm64"];

    private static int _done;

    /// <summary>Gets this machine's runtime id, e.g. "linux-arm64" on a 64-bit Raspberry Pi OS.</summary>
    public static string Rid
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                Architecture.X86 => "x86",
                var other => other.ToString().ToLowerInvariant(),
            };
            return os + "-" + arch;
        }
    }

    public static bool IsSupported => Supported.Contains(Rid);

    /// <summary>Why analysis cannot run here, in words for the log and the API.</summary>
    public static string UnsupportedReason => Rid switch
    {
        "linux-arm" => "X-Ray analysis needs a 64-bit OS on Raspberry Pi (Raspberry Pi OS 64-bit, Pi 4 or 5); this is 32-bit.",
        "osx-x64" => "X-Ray analysis needs an Apple Silicon Mac; Intel Macs are not supported.",
        var r => $"X-Ray analysis is not available on {r}. Supported: {string.Join(", ", Supported)}.",
    };

    public static void Register()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(UnsupportedReason);
        }

        if (Interlocked.Exchange(ref _done, 1) == 1)
        {
            return;
        }

        var ortAssembly = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly;
        string dir = Path.GetDirectoryName(typeof(NativeLibraries).Assembly.Location)!;
        try
        {
            NativeLibrary.SetDllImportResolver(ortAssembly, (name, asm, path) =>
            {
                if (!name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                foreach (var candidate in new[]
                {
                    Path.Combine(dir, "runtimes", Rid, "onnxruntime.native"),
                    Path.Combine(dir, "libonnxruntime.so"), // older single-platform builds
                })
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h))
                    {
                        return h;
                    }
                }

                return IntPtr.Zero; // default probing (tests, dev machines)
            });
        }
        catch (InvalidOperationException)
        {
            // A resolver is already set (plugin reloaded in the same process).
        }
    }
}
