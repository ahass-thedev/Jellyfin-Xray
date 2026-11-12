using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SceneXray.Configuration;

/// <summary>Model pack used for face detection and recognition.</summary>
public enum ModelPack
{
    /// <summary>SCRFD-500M detector + ArcFace R50. Full recognition accuracy, light detection (default).</summary>
    Balanced = 0,

    /// <summary>SCRFD-10G detector + ArcFace R50. Finds smaller faces; ~3x slower than Balanced.</summary>
    Accurate = 1,

    /// <summary>SCRFD-500M detector + ArcFace MobileFaceNet. Fastest, weakest recognition.</summary>
    Fast = 2,
}

/// <summary>What runs a set of models (faces, voices).</summary>
public enum InferenceDevice
{
    /// <summary>The CPU, with <see cref="PluginConfiguration.InferenceThreads"/> threads (default; works everywhere).</summary>
    Cpu = 0,

    /// <summary>An Intel GPU (Linux, Windows) or Apple Silicon; falls back to the CPU when there is none.</summary>
    Gpu = 1,
}

/// <summary>Plugin settings, edited on the plugin's configuration page.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- What to analyse -------------------------------------------------

    /// <summary>Gets or sets library ids to analyse; empty means every movie and TV library.</summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether animated titles skip face analysis (music still runs).</summary>
    public bool SkipAnimation { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether starting playback of an unanalysed item queues it first.</summary>
    public bool AnalyzeOnPlayback { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether titles are queued as soon as they are added to the library.</summary>
    public bool AnalyzeNewMedia { get; set; } = true;

    // ---- Face pipeline ---------------------------------------------------

    /// <summary>Gets or sets the model pack.</summary>
    public ModelPack ModelPack { get; set; } = ModelPack.Balanced;

    /// <summary>Gets or sets frames sampled per second of video.</summary>
    public double SampleFps { get; set; } = 1.0;

    /// <summary>Gets or sets the width frames are decoded at (faces are cropped from this).</summary>
    public int DecodeWidth { get; set; } = 960;

    /// <summary>Gets or sets the CPU threads ONNX Runtime may use (for each title being analysed).</summary>
    public int InferenceThreads { get; set; } = 2;

    /// <summary>
    /// Gets or sets how many titles are analysed at the same time (1-4). The models are loaded once and shared,
    /// so more mainly costs CPU and decoding; a GPU with headroom makes 2 worthwhile.
    /// </summary>
    public int MaxConcurrentAnalyses { get; set; } = 1;

    /// <summary>Gets or sets what runs the face models.</summary>
    public InferenceDevice InferenceDevice { get; set; } = InferenceDevice.Cpu;

    /// <summary>Gets or sets what runs the speaker (voice) model.</summary>
    public InferenceDevice VoiceDevice { get; set; } = InferenceDevice.Cpu;

    /// <summary>Gets or sets a value indicating whether to decode video on the GPU (VAAPI), falling back to software.</summary>
    public bool HardwareDecode { get; set; } = true;

    /// <summary>Gets or sets the VAAPI render node.</summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";

    /// <summary>Gets or sets the smallest face height (pixels at decode width) worth recognising.</summary>
    public int MinFaceHeight { get; set; } = 40;

    /// <summary>Gets or sets the cosine similarity needed to join two face tracks into one person.</summary>
    public double ClusterThreshold { get; set; } = 0.45;

    /// <summary>Gets or sets the similarity a person cluster needs against a cast photo to be named.</summary>
    public double IdentifyThreshold { get; set; } = 0.32;

    /// <summary>Gets or sets the lower score accepted only with a wide lead (old films vs recent photos); 0 disables.</summary>
    public double WeakIdentifyThreshold { get; set; } = 0.2;

    /// <summary>Gets or sets the lead the best cast match needs over the runner-up.</summary>
    public double IdentifyMargin { get; set; } = 0.06;

    /// <summary>Gets or sets the gap (seconds) bridged between appearances of the same person in one scene.</summary>
    public double SceneGapSeconds { get; set; } = 6;

    /// <summary>Gets or sets the minimum total screen time (seconds) before a person is listed for a title.</summary>
    public double MinScreenTimeSeconds { get; set; } = 4;

    /// <summary>Gets or sets a value indicating whether confident matches become extra reference faces for that actor.</summary>
    public bool LearnFromMatches { get; set; } = true;

    /// <summary>Gets or sets an optional TMDb API key (v3) used to fetch extra reference photos per actor.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets how many extra TMDb photos to use per actor.</summary>
    public int TmdbPhotosPerPerson { get; set; } = 4;

    // ---- Voices ----------------------------------------------------------

    /// <summary>Gets or sets a value indicating whether to find who is speaking (voice casts, costumed and off-screen characters).</summary>
    public bool VoicesEnabled { get; set; } = true;

    // ---- Music -----------------------------------------------------------

    /// <summary>Gets or sets a value indicating whether to identify music.</summary>
    public bool MusicEnabled { get; set; } = true;

    /// <summary>Gets or sets music library ids to fingerprint; empty means every music library.</summary>
    public string[] MusicLibraryIds { get; set; } = [];

    /// <summary>Gets or sets the shortest music match (seconds) worth showing.</summary>
    public double MinMusicSeconds { get; set; } = 8;

    /// <summary>Gets or sets an optional AudD API token for songs not in the local library.</summary>
    public string AuddApiToken { get; set; } = string.Empty;

    /// <summary>Gets or sets seconds between AudD samples.</summary>
    public double AuddSampleIntervalSeconds { get; set; } = 60;

    /// <summary>Gets or sets the AudD request cap per title.</summary>
    public int AuddMaxRequestsPerItem { get; set; } = 30;

    // ---- Sharing ---------------------------------------------------------

    /// <summary>
    /// Gets or sets a value indicating whether this server shares X-Ray data with the sharing
    /// service, both ways (docs/shared-data-design.md). Off by default, and it can only be turned on
    /// with an invite.
    /// </summary>
    public bool SharingEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a title someone starts watching may use shared data
    /// from another cut of it, for the scenes the two cuts share, until it is analysed here.
    /// </summary>
    public bool SharingUseOtherCuts { get; set; }

    /// <summary>Gets or sets the sharing service. Not on the settings page: changeable here only, for testing.</summary>
    public string SharingServerUrl { get; set; } = "https://xray-share.hassanein.me";
}
