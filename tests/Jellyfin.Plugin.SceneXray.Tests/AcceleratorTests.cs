using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Vision;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

public class AcceleratorTests
{
    private const string Dir = "onnxruntime_ep_openvino/";

    [Theory]
    [InlineData("libonnxruntime_providers_openvino_plugin.so")]
    [InlineData("libopenvino.so.2641")]
    [InlineData("libopenvino_intel_gpu_plugin.so")]
    [InlineData("libopenvino_auto_plugin.so")]
    [InlineData("libopenvino_onnx_frontend.so.2641")]
    [InlineData("libtbb.so.12")]
    [InlineData("libtbbmalloc.so.2")]
    [InlineData("onnxruntime_providers_openvino_plugin.dll")]
    [InlineData("openvino.dll")]
    [InlineData("openvino_intel_gpu_plugin.dll")]
    [InlineData("tbb12.dll")]
    [InlineData("openvino_runtime.manifest")]
    public void KeepsWhatTheGpuPathNeeds(string name) => Assert.True(Accelerator.WantOpenVinoFile(Dir + name));

    [Theory]
    [InlineData("libtbbbind_2_5.so.3")] // crashes on LXC CPU layouts
    [InlineData("tbbbind_2_5.dll")]
    [InlineData("libhwloc.so.15")]
    [InlineData("libtbbmalloc_proxy.so.2")]
    [InlineData("libopenvino_intel_cpu_plugin.so")]
    [InlineData("libopenvino_intel_npu_plugin.so")]
    [InlineData("openvino_intel_npu_compiler.dll")]
    [InlineData("libonnxruntime_providers_openvino_plugin.so.1.8.1")]
    [InlineData("__init__.py")]
    [InlineData("cache.json")]
    [InlineData("")]
    public void LeavesTheRestOut(string name) => Assert.False(Accelerator.WantOpenVinoFile(Dir + name));

    [Fact]
    public void IgnoresFilesOutsideThePackage() =>
        Assert.False(Accelerator.WantOpenVinoFile("onnxruntime_ep_openvino-1.8.1.dist-info/RECORD"));

    [Theory]
    [InlineData("linux-x64", true)]
    [InlineData("win-x64", true)]
    [InlineData("osx-arm64", true)]
    [InlineData("linux-arm64", false)]
    [InlineData("win-arm64", false)]
    public void KnowsWhichPlatformsHaveAGpuBackend(string rid, bool expected) =>
        Assert.Equal(expected, Accelerator.PlatformHasGpuBackend(rid));

    [Theory]
    [InlineData(InferenceDevice.Gpu, InferenceDevice.Cpu, InferenceDevice.Cpu, InferenceDevice.Cpu, true)] // faces back to CPU
    [InlineData(InferenceDevice.Cpu, InferenceDevice.Gpu, InferenceDevice.Cpu, InferenceDevice.Cpu, true)] // voices back to CPU
    [InlineData(InferenceDevice.Gpu, InferenceDevice.Cpu, InferenceDevice.Gpu, InferenceDevice.Cpu, false)] // unrelated save
    [InlineData(InferenceDevice.Cpu, InferenceDevice.Cpu, InferenceDevice.Gpu, InferenceDevice.Gpu, false)] // turning GPU on
    public void ACrashIsForgottenOnlyWhenAGpuSettingGoesBackToCpu(
        InferenceDevice faces, InferenceDevice voices, InferenceDevice newFaces, InferenceDevice newVoices, bool forgotten)
    {
        string dir = Path.Combine(Path.GetTempPath(), "xray-accel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string marker = Path.Combine(dir, "gpu-trial");
            File.WriteAllText(marker, "GPU");
            var accelerator = new Accelerator(dir, null!, NullLogger.Instance, new PluginConfiguration { InferenceDevice = faces, VoiceDevice = voices });
            accelerator.OnConfigurationChanged(new PluginConfiguration { InferenceDevice = newFaces, VoiceDevice = newVoices });
            Assert.Equal(forgotten, !File.Exists(marker));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AfterACrashTheGpuIsNotTriedAgain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xray-accel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "gpu-trial"), "GPU");
            var accelerator = new Accelerator(dir, null!, NullLogger.Instance, new PluginConfiguration());
            var backend = await accelerator.ResolveAsync(InferenceDevice.Gpu, 3, CancellationToken.None);
            Assert.False(backend.IsGpu);
            Assert.StartsWith("CPU (GPU unavailable: Jellyfin stopped", backend.Name, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
