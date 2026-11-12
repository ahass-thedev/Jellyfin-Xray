using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SceneXray.Voice;

/// <summary>One subtitle cue.</summary>
public sealed partial record SubtitleCue(int Index, double Start, double End, string Text, string? SpeakerTag, bool MultiSpeaker)
{
    public double Duration => End - Start;

    /// <summary>Gets a value indicating whether the cue has real words, not just "Mmm!" or "Unh! Hunh!".</summary>
    public bool IsSpeech => Word().Matches(Text).Count(w => !Interjection().IsMatch(w.Value)) >= 2;

    [GeneratedRegex(@"\p{L}[\p{L}'-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"^(?:m+h*|h?m+|u+[hmn]+|h?u+n*h+|a+h+|o+h+|o+|a+|e+h|e+r+|u+g+h+|a+r+g+h+|g+a+h+|p+f+t+|s+h+|o+w+|h+a+(?:h+a+)*|w+h+o+a+|h+u+h+|t+s+k+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Interjection();
}

/// <summary>SRT parsing for speech timing and SDH speaker tags ("[Buddy]" on its own line, or "BUDDY: hi").</summary>
public static partial class Subtitles
{
    public static List<SubtitleCue> Parse(string srt)
    {
        var cues = new List<SubtitleCue>();
        srt = srt.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (Match m in Block().Matches(srt))
        {
            var lines = m.Groups["text"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => Html().Replace(l, string.Empty)).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            string? tag = null;
            var tagMatch = LeadingTag().Match(lines[0]);
            if (tagMatch.Success)
            {
                tag = tagMatch.Groups[1].Value.Trim();
                lines[0] = lines[0][tagMatch.Length..].Trim();
                if (lines[0].Length == 0)
                {
                    lines.RemoveAt(0);
                }
            }
            else if (ColonTag().Match(lines[0]) is { Success: true } colon)
            {
                tag = colon.Groups[1].Value.Trim();
                lines[0] = lines[0][colon.Length..].Trim();
            }

            // "-Bye, kids!\n-Bye, Mailman Miles!" is two people in one cue.
            bool multi = lines.Count(l => l.StartsWith('-')) >= 2 || (lines.Count >= 2 && lines.Skip(1).Any(l => l.StartsWith('-')));
            string text = string.Join(' ', lines);
            if (text.Length == 0 || OnlySound().IsMatch(text))
            {
                continue; // pure sound description, no speech
            }

            cues.Add(new SubtitleCue(cues.Count, Time(m.Groups["s"].Value), Time(m.Groups["e"].Value), text, tag, multi));
        }

        return cues;
    }

    private static double Time(string t) =>
        TimeSpan.ParseExact(t.Replace('.', ','), @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture).TotalSeconds;

    [GeneratedRegex(@"(?<s>\d\d:\d\d:\d\d[,.]\d{3})\s*-->\s*(?<e>\d\d:\d\d:\d\d[,.]\d{3})[^\n]*\n(?<text>(?:.+\n?)+)")]
    private static partial Regex Block();

    [GeneratedRegex(@"<[^>]+>|\{[^}]+\}")]
    private static partial Regex Html();

    [GeneratedRegex(@"^-?\s*\[([^\]]{1,40})\]\s*")]
    private static partial Regex LeadingTag();

    [GeneratedRegex(@"^-?\s*([A-Z][A-Z .'-]{1,25}):\s+")]
    private static partial Regex ColonTag();

    [GeneratedRegex(@"^[\s\-]*(?:[\[(][^\])]*[\])][\s\-]*)+$|^[\s♪♫]*$")]
    private static partial Regex OnlySound();
}
