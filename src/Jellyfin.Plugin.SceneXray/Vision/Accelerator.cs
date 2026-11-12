using System.IO.Compression;
using System.Security.Cryptography;
using Jellyfin.Plugin.SceneXray.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.Plugin.SceneXray.Vision;

/// <summary>Where the face or voice models run, resolved from the settings.</summary>
/// <param name="Name">What it is, in words for the log and the settings page.</param>
/// <param name="Configure">Adds the execution provider to a session's options. The second argument names the session's
/// compiled-model cache when one model file is loaded with different fixed shapes (Core ML keys its cache on the file
/// alone, so they would share one); empty for the default cache.</param>
/// <param name="MaxRecognizerBatch">Faces per recogniser call (voices are always one line per call). GPUs take one at a time: Core ML because the model declares batch 1, OpenVINO because every new batch size recompiles the network.</param>
/// <param name="IsGpu">Whether a GPU does the work.</param>
public sealed record InferenceBackend(string Name, Action<SessionOptions, string> Configure, int MaxRecognizerBatch, bool IsGpu)
{
    public static InferenceBackend Cpu(int threads) => new(
        "CPU",
        (o, _) =>
        {
            o.IntraOpNumThreads = Math.Max(1, threads);
            o.InterOpNumThreads = 1;
        },
        16,
        false);
}

/// <summary>
/// Picks the GPU backend for this machine when the settings ask for one:
/// <list type="bullet">
/// <item>Intel GPUs (Arc, Iris Xe, UHD) on Linux and Windows x64, through Intel's OpenVINO plugin
/// for ONNX Runtime. It is about 70 MB, so it is downloaded (from PyPI, checked by SHA-256) the
/// first time GPU is chosen instead of shipping in every plugin zip.</item>
/// <item>Apple Silicon, through Core ML, which is built into the bundled ONNX Runtime.</item>
/// </list>
/// Anything else, or any failure, falls back to the CPU and says why on the settings page.
/// </summary>
public sealed class Accelerator
{
    private const string OpenVinoVersion = "1.8.1";
    private const string OpenVinoEp = "OpenVINOExecutionProvider";

    // onnxruntime-ep-openvino wheels (self-contained: the plugin plus the OpenVINO runtime).
    private static readonly Dictionary<string, (string Url, string Sha)> OpenVinoWheels = new()
    {
        ["linux-x64"] = (
            "https://files.pythonhosted.org/packages/88/01/4e72a8a65b584f01d6e59eb453bae397b82b2e51df1b5c94efa05e17800b/onnxruntime_ep_openvino-1.8.1-py3-none-manylinux_2_28_x86_64.whl",
            "3832643158c8c187fcab6db661cde2a8c06a2b3559d95d0f2411ac0e51c5e924"),
        ["win-x64"] = (
            "https://files.pythonhosted.org/packages/75/98/224ead07344390258ae0515ed285ba2951e9c1405f4507443a03bb7dd5c7/onnxruntime_ep_openvino-1.8.1-py3-none-win_amd64.whl",
            "1acd878be173b02bb8b953460f2f59b121e5f6e47ebc6aad490c0590cf6718fb"),
    };

    private readonly string _root;
    private readonly IHttpClientFactory _http;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _failure;
    private OrtEpDevice? _openVinoDevice;
    private InferenceDevice _faceDevice;
    private InferenceDevice _voiceDevice;

    public Accelerator(string root, IHttpClientFactory http, ILogger log, PluginConfiguration current)
    {
        _root = root;
        _http = http;
        _log = log;
        _faceDevice = current.InferenceDevice;
        _voiceDevice = current.VoiceDevice;
    }

    /// <summary>
    /// Gets the file that exists while a GPU is first tried. A native crash there takes Jellyfin
    /// down with it; finding the file at the next start means that happened, so the GPU stays off.
    /// </summary>
    private string CrashMarker => Path.Combine(_root, "gpu-trial");

    /// <summary>Whether the plugin can use a GPU on this platform at all.</summary>
    public static bool PlatformHasGpuBackend(string rid) => rid == "osx-arm64" || OpenVinoWheels.ContainsKey(rid);

    /// <summary>Switching faces or voices from GPU to CPU clears a GPU failure, so choosing GPU again retries it.</summary>
    public void OnConfigurationChanged(PluginConfiguration cfg)
    {
        bool backToCpu = (_faceDevice == InferenceDevice.Gpu && cfg.InferenceDevice == InferenceDevice.Cpu)
            || (_voiceDevice == InferenceDevice.Gpu && cfg.VoiceDevice == InferenceDevice.Cpu);
        _faceDevice = cfg.InferenceDevice;
        _voiceDevice = cfg.VoiceDevice;
        if (backToCpu)
        {
            _failure = null;
            File.Delete(CrashMarker);
        }
    }

    /// <summary>The backend for one kind of model (faces or voices); the CPU unless <paramref name="device"/> is a GPU that works.</summary>
    public async Task<InferenceBackend> ResolveAsync(InferenceDevice device, int threads, CancellationToken ct)
    {
        if (device != InferenceDevice.Gpu)
        {
            return InferenceBackend.Cpu(threads);
        }

        if (_failure is null && File.Exists(CrashMarker))
        {
            _failure = "Jellyfin stopped while the GPU was being set up. Switch it to CPU, save, and back to GPU to try again.";
        }

        if (_failure is not null)
        {
            return CpuFallback(threads);
        }

        try
        {
            return await GpuAsync(threads, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ex.Message, threads);
        }
    }

    /// <summary>Records that the GPU did not work; analysis carries on on the CPU, which this returns.</summary>
    public InferenceBackend Fail(string reason, int threads)
    {
        _failure = reason;
        _log.LogWarning("X-Ray: GPU unavailable, using the CPU: {Reason}", reason);
        return CpuFallback(threads);
    }

    private InferenceBackend CpuFallback(int threads) =>
        InferenceBackend.Cpu(threads) with { Name = "CPU (GPU unavailable: " + _failure + ")" };

    /// <summary>Wraps the first use of a GPU backend (creating sessions and a test run) in the crash marker.</summary>
    public T Trial<T>(InferenceBackend backend, Func<T> create)
    {
        if (!backend.IsGpu)
        {
            return create();
        }

        Directory.CreateDirectory(_root);
        File.WriteAllText(CrashMarker, backend.Name);
        var result = create();
        File.Delete(CrashMarker);
        return result;
    }

    private async Task<InferenceBackend> GpuAsync(int threads, CancellationToken ct)
    {
        string rid = NativeLibraries.Rid;
        if (rid == "osx-arm64")
        {
            string cache = Path.Combine(_root, "coreml-cache");
            return new InferenceBackend(
                "GPU: Apple Silicon (Core ML)",
                (o, variant) => o.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                {
                    ["MLComputeUnits"] = "ALL",
                    ["ModelFormat"] = "MLProgram",
                    ["ModelCacheDirectory"] = CacheDir(cache, variant),
                }),
                1,
                true);
        }

        if (!OpenVinoWheels.ContainsKey(rid))
        {
            throw new PlatformNotSupportedException($"GPU analysis supports Intel GPUs on Linux and Windows x64, and Apple Silicon; this is {rid}.");
        }

        var device = await OpenVinoDeviceAsync(rid, ct).ConfigureAwait(false);
        string name = device.EpMetadata.Entries.TryGetValue("FULL_DEVICE_NAME", out var full) ? full : "Intel GPU";
        string kernels = Path.Combine(_root, "openvino-cache");
        return new InferenceBackend(
            "GPU: " + name + " (OpenVINO)",
            (o, variant) =>
            {
                o.IntraOpNumThreads = Math.Max(1, threads); // for the few nodes left on the CPU
                o.InterOpNumThreads = 1;
                o.AppendExecutionProvider(OrtEnv.Instance(), [device], new Dictionary<string, string>
                {
                    // Compiled kernels are cached, so a session loads in under a second after the first.
                    ["load_config"] = "{\"GPU\":{\"CACHE_DIR\":\"" + CacheDir(kernels, variant).Replace("\\", "/", StringComparison.Ordinal) + "\"}}",
                });
            },
            1,
            true);
    }

    private static string CacheDir(string root, string variant)
    {
        string dir = variant.Length == 0 ? root : Path.Combine(root, variant);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<OrtEpDevice> OpenVinoDeviceAsync(string rid, CancellationToken ct)
    {
        if (_openVinoDevice is not null)
        {
            return _openVinoDevice;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_openVinoDevice is not null)
            {
                return _openVinoDevice;
            }

            string library = await EnsureOpenVinoAsync(rid, ct).ConfigureAwait(false);
            var env = OrtEnv.Instance();
            if (!env.GetEpDevices().Any(d => d.EpName == OpenVinoEp))
            {
                env.RegisterExecutionProviderLibrary("openvino", library);
            }

            // Prefer a discrete card over integrated graphics when both are there.
            var gpus = env.GetEpDevices()
                .Where(d => d.EpName == OpenVinoEp && d.HardwareDevice.Type == OrtHardwareDeviceType.GPU)
                .OrderByDescending(d => d.EpMetadata.Entries.TryGetValue("DEVICE_TYPE", out var t) && t == "discrete")
                .ToList();
            if (gpus.Count == 0)
            {
                throw new InvalidOperationException(
                    "OpenVINO found no Intel GPU. In Docker, pass the GPU through (for example --device /dev/dri) and make sure Intel's compute runtime is installed; the official Jellyfin image has it.");
            }

            _log.LogInformation("X-Ray: models set to GPU will run on {Gpu} through OpenVINO", gpus[0].EpMetadata.Entries.GetValueOrDefault("FULL_DEVICE_NAME", "an Intel GPU"));
            _openVinoDevice = gpus[0];
            return _openVinoDevice;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Downloads and unpacks the OpenVINO plugin once; returns the plugin library's path.</summary>
    private async Task<string> EnsureOpenVinoAsync(string rid, CancellationToken ct)
    {
        var (url, sha) = OpenVinoWheels[rid];
        string dir = Path.Combine(_root, "openvino-" + OpenVinoVersion);
        string library = Path.Combine(dir, rid.StartsWith("win", StringComparison.Ordinal)
            ? "onnxruntime_providers_openvino_plugin.dll"
            : "libonnxruntime_providers_openvino_plugin.so");
        string done = Path.Combine(dir, ".complete");
        if (File.Exists(done) && File.Exists(library))
        {
            return library;
        }

        Directory.CreateDirectory(_root);
        string wheel = Path.Combine(_root, "openvino-" + OpenVinoVersion + ".whl.part");
        _log.LogInformation("X-Ray: downloading the OpenVINO GPU runtime ({Version}) from {Url}", OpenVinoVersion, url);
        try
        {
            using (var client = _http.CreateClient())
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(wheel);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            await using (var check = File.OpenRead(wheel))
            {
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)) != sha)
                {
                    throw new InvalidDataException("The OpenVINO download failed its SHA-256 check.");
                }
            }

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            Directory.CreateDirectory(dir);
            using (var archive = ZipFile.OpenRead(wheel))
            {
                foreach (var entry in archive.Entries.Where(e => WantOpenVinoFile(e.FullName)))
                {
                    entry.ExtractToFile(Path.Combine(dir, entry.Name), overwrite: true);
                }
            }

            File.WriteAllText(done, OpenVinoVersion);
        }
        finally
        {
            File.Delete(wheel);
        }

        return library;
    }

    /// <summary>
    /// The files the GPU path needs from the wheel. Left out: the CPU and NPU plugins (ONNX
    /// Runtime's own CPU code covers the CPU; together they are over 100 MB), the duplicate
    /// versioned copies of the plugin, and TBB's NUMA binding (tbbbind and the hwloc it loads),
    /// which crashes on the CPU layout of LXC containers. Without it TBB assumes one NUMA node.
    /// </summary>
    public static bool WantOpenVinoFile(string path)
    {
        if (!path.StartsWith("onnxruntime_ep_openvino/", StringComparison.Ordinal))
        {
            return false;
        }

        string name = path["onnxruntime_ep_openvino/".Length..];
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        bool library = name.EndsWith(".dll", StringComparison.Ordinal) || name.Contains(".so", StringComparison.Ordinal) || name.EndsWith(".manifest", StringComparison.Ordinal);
        bool skipped = name.Contains("npu", StringComparison.Ordinal)
            || name.Contains("intel_cpu", StringComparison.Ordinal)
            || name.Contains("tbbbind", StringComparison.Ordinal)
            || name.Contains("tbbmalloc_proxy", StringComparison.Ordinal)
            || name.Contains("hwloc", StringComparison.Ordinal)
            || name.StartsWith("libonnxruntime_providers_openvino_plugin.so.", StringComparison.Ordinal);
        return library && !skipped;
    }
}
