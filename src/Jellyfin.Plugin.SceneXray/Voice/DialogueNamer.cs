using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SceneXray.Voice;

/// <summary>A credited character, with the names people use for them on screen.</summary>
public sealed record Character(Guid PersonId, string ActorName, string Role, IReadOnlyList<string> Names);

/// <summary>
/// Names voice clusters from what people say, with no tags and no human help:
/// - someone says "Morty!" or "Hey, Morty," and a different voice answers: that voice is likely Morty;
/// - a voice that says "Morty" itself is unlikely to be Morty (people rarely say their own name);
/// - "I'm Rick" / "my name is Rick" / "it's me, Rick": that voice is Rick.
/// Each line is weak evidence; summed over an episode (and a series, via the voice gallery)
/// the main characters come out clearly. Only clear winners are named.
/// </summary>
public static partial class DialogueNamer
{
    private const double MaxReplyGap = 4.0;

    /// <summary>Splits a credit like "Buddy / Dark Buddy (voice)" into the names used for that character.</summary>
    public static List<string> NamesFromRole(string role)
    {
        var parts = Paren().Replace(role, string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length >= 2)
            .ToList();
        var names = new List<string>(parts);
        foreach (var part in parts)
        {
            // "Rick Sanchez" is mostly called "Rick"; titles stay attached ("Dr. Burr", "Mr. Mailbox"),
            // and in "Dark Buddy" (a variant of "Buddy") the first word is not a name.
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && !Title().IsMatch(words[0]) && words[0].Length >= 3 && !parts.Contains(words[^1]))
            {
                names.Add(words[0]);
            }
        }

        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns cluster -> (character index, score, margin) for clear winners only.
    /// <paramref name="cueCluster"/> maps cue index to voice cluster (-1 = unknown speaker).
    /// </summary>
    public static Dictionary<int, (int Character, double Score, double Margin)> Infer(
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyDictionary<int, int> cueCluster,
        IReadOnlyList<Character> characters,
        Action<string>? debug = null)
    {
        // A first name shared by two characters ("Grace" in two roles) can't tell them apart.
        var owners = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int c = 0; c < characters.Count; c++)
        {
            foreach (var n in characters[c].Names)
            {
                if (!owners.TryGetValue(n, out var l))
                {
                    owners[n] = l = [];
                }

                if (!l.Contains(c))
                {
                    l.Add(c);
                }
            }
        }

        var patterns = owners.Where(kv => kv.Value.Count == 1)
            .Select(kv => (Character: kv.Value[0], Name: kv.Key, Rx: new Regex(@"(?<![\p{L}'])" + Regex.Escape(kv.Key) + @"(?![\p{L}])", RegexOptions.CultureInvariant)))
            .ToList();

        var pos = new Dictionary<(int Cluster, int Character), double>();
        var neg = new Dictionary<(int Cluster, int Character), double>();
        void Add(Dictionary<(int, int), double> d, int cluster, int ch, double w)
        {
            if (cluster >= 0)
            {
                d[(cluster, ch)] = d.GetValueOrDefault((cluster, ch)) + w;
            }
        }

        int ClusterOf(int i) => i >= 0 && i < cues.Count && !cues[i].MultiSpeaker && cueCluster.TryGetValue(cues[i].Index, out var c) ? c : -1;

        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (cue.MultiSpeaker)
            {
                continue;
            }

            int speaker = ClusterOf(i);
            foreach (var (ch, name, rx) in patterns)
            {
                var m = rx.Match(cue.Text);
                if (!m.Success)
                {
                    continue;
                }

                if (SelfIntro(cue.Text, m.Index))
                {
                    Add(pos, speaker, ch, 3);
                    continue;
                }

                Add(neg, speaker, ch, 1);
                if (!Vocative(cue.Text, m.Index, name.Length))
                {
                    continue;
                }

                // The person spoken to tends to answer next, or to have just spoken.
                int next = ClusterOf(i + 1);
                if (next >= 0 && next != speaker && cues[i + 1].Start - cue.End <= MaxReplyGap)
                {
                    Add(pos, next, ch, 1);
                }

                int prev = ClusterOf(i - 1);
                if (prev >= 0 && prev != speaker && cue.Start - cues[i - 1].End <= MaxReplyGap)
                {
                    Add(pos, prev, ch, 0.5);
                }
            }
        }

        if (debug is not null)
        {
            foreach (var kv in pos.OrderByDescending(kv => kv.Value).Take(25))
            {
                debug($"cluster {kv.Key.Cluster} {characters[kv.Key.Character].Role}: +{kv.Value} -{neg.GetValueOrDefault(kv.Key)}");
            }
        }

        // Net score per (cluster, character): support minus half the counter-evidence (a voice
        // that keeps saying "Morty" is talking to Morty, so it is probably not Morty).
        var scored = pos.Select(kv => (kv.Key.Cluster, kv.Key.Character, Pos: kv.Value, Net: kv.Value - (0.5 * neg.GetValueOrDefault(kv.Key))))
            .Where(x => x.Pos >= 3 && x.Net >= 2)
            .ToList();
        double NetOf(int cluster, int ch) =>
            pos.GetValueOrDefault((cluster, ch)) - (0.5 * neg.GetValueOrDefault((cluster, ch)));

        // Greedy one-to-one assignment; each pick must clearly beat every rival for that cluster
        // and every other cluster competing for that character.
        var result = new Dictionary<int, (int, double, double)>();
        var taken = new HashSet<int>();
        foreach (var x in scored.OrderByDescending(x => x.Net))
        {
            if (result.ContainsKey(x.Cluster) || taken.Contains(x.Character))
            {
                continue;
            }

            double rival = 0;
            foreach (var ((cl, ch), _) in pos)
            {
                if ((cl == x.Cluster && ch != x.Character && !taken.Contains(ch)) || (ch == x.Character && cl != x.Cluster && !result.ContainsKey(cl)))
                {
                    rival = Math.Max(rival, NetOf(cl, ch));
                }
            }

            if (x.Net - rival >= 1.5)
            {
                result[x.Cluster] = (x.Character, x.Net, x.Net - rival);
                taken.Add(x.Character);
            }
        }

        return result;
    }

    private static bool SelfIntro(string text, int at)
    {
        var before = text[..at];
        return IntroBefore().IsMatch(before);
    }

    /// <summary>"Morty, come here" / "Come here, Morty!" / "Hey Morty" / "Morty?" — addressing, not talking about.</summary>
    private static bool Vocative(string text, int at, int length)
    {
        var before = text[..at].TrimEnd();
        var after = text[(at + length)..].TrimStart();
        bool startsClause = before.Length == 0 || before.EndsWith(',') || before.EndsWith('.') || before.EndsWith('!')
            || before.EndsWith('?') || before.EndsWith('-') || Greeting().IsMatch(before);
        bool endsClause = after.Length == 0 || after[0] is ',' or '!' or '?' or '.';
        return startsClause && endsClause;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Paren();

    [GeneratedRegex(@"^(Mr|Mrs|Ms|Miss|Dr|Doctor|Nurse|Mailman|Officer|Captain|Uncle|Aunt|Grandpa|Grandma|Sir|Lady|King|Queen|Prince|Princess|Professor|Coach|Agent|Detective|Mister|Big|Little|Old|Young|The)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    [GeneratedRegex(@"(?:\bI'm|\bI am|\bmy name is|\bit's me,?|\bthis is|\bcall me)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex IntroBefore();

    [GeneratedRegex(@"(?:^|[\s,])(?:hey|hi|hello|oh|yo|bye|goodbye|thanks|thank you|please|come on|okay|ok|look|listen|no|yes|yeah|sorry|dear|good night|good morning)$", RegexOptions.IgnoreCase)]
    private static partial Regex Greeting();
}
