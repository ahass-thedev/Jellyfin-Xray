using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SceneXray.Services;

/// <summary>Picks the right audio and subtitle tracks of a title.</summary>
public static class MediaTracks
{
    /// <summary>The main dialogue track: the default one, else English, else the first.</summary>
    public static MediaStream? DialogueAudio(BaseItem item)
    {
        var audio = item.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).ToList();
        return audio.FirstOrDefault(s => s.IsDefault && !IsCommentary(s))
            ?? audio.FirstOrDefault(s => IsEnglish(s) && !IsCommentary(s))
            ?? audio.FirstOrDefault();
    }

    /// <summary>The best text subtitle for speech timing: English SDH (it tags speakers), else English, else any.</summary>
    public static MediaStream? DialogueSubtitles(BaseItem item)
    {
        var text = item.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream && !IsForced(s))
            .ToList();
        return text.FirstOrDefault(s => IsEnglish(s) && IsSdh(s))
            ?? text.FirstOrDefault(IsEnglish)
            ?? text.FirstOrDefault();
    }

    /// <summary>Extracts a text subtitle stream as SRT through ffmpeg (or reads an external .srt).</summary>
    public static async Task<string> ReadSrtAsync(BaseItem item, MediaStream sub, string ffmpeg, CancellationToken ct)
    {
        if (sub.IsExternal && sub.Path?.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await File.ReadAllTextAsync(sub.Path, ct).ConfigureAwait(false);
        }

        var (code, stdout, stderr) = await Analysis.FrameSource.RunAsync(
            ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", sub.IsExternal ? sub.Path! : item.Path,
             "-map", sub.IsExternal ? "0:s:0" : $"0:{sub.Index}", "-f", "srt", "pipe:1"],
            ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException("Could not read subtitles: " + stderr);
        }

        return stdout;
    }

    private static bool IsEnglish(MediaStream s) =>
        s.Language is "eng" or "en" || (s.Language is null && (s.Title ?? string.Empty).Contains("English", StringComparison.OrdinalIgnoreCase));

    private static bool IsSdh(MediaStream s) =>
        s.IsHearingImpaired || (s.Title ?? string.Empty).Contains("SDH", StringComparison.OrdinalIgnoreCase) || (s.Title ?? string.Empty).Contains("CC", StringComparison.Ordinal);

    private static bool IsForced(MediaStream s) => s.IsForced || (s.Title ?? string.Empty).Contains("Forced", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommentary(MediaStream s) => (s.Title ?? string.Empty).Contains("Commentary", StringComparison.OrdinalIgnoreCase);
}
