using System.Numerics;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>A song found inside a title's audio.</summary>
public sealed record FingerprintMatch(int Track, double Start, double End, double Confidence);

/// <summary>Where a short track (an anchor) sits in the query: its first item and its bit-error rate there.</summary>
public sealed record TrackLocation(int Track, int QueryItem, double BitErrorRate);

/// <summary>
/// Finds library songs inside a long recording using raw Chromaprint fingerprints.
/// Candidate alignments come from exact sub-fingerprint hits voting on (track, offset), as in
/// any landmark matcher; each is then verified by bit-error rate over a sliding window, which
/// tolerates dialogue and effects mixed on top far better than exact hits alone.
/// The same index also locates short shared-data anchors (3 s windows) inside a title's own
/// fingerprint: <see cref="Locate"/>.
/// </summary>
public sealed class FingerprintMatcher
{
    /// <summary>Seconds per Chromaprint sub-fingerprint (default algorithm, 11025 Hz).</summary>
    public const double ItemSeconds = 0.1238;

    private const int Window = 40; // ~5 s
    /// <summary>Bit-error rate at or below which two windows are the same audio; unrelated audio sits at ~0.5.</summary>
    public const double MaxBitErrorRate = 0.35;
    private const int KeyBits = 17; // which half (1 bit) + the half's 16 bits

    private readonly IReadOnlyList<uint[]> _tracks;
    private readonly int[] _bucketStart; // CSR layout: postings for key k are [_bucketStart[k], _bucketStart[k+1])
    private readonly ulong[] _postings;  // track << 32 | position

    public FingerprintMatcher(IReadOnlyList<uint[]> tracks)
    {
        // Under dialogue and effects a whole 32-bit sub-fingerprint rarely survives intact, but
        // each 16-bit half often does. Index both halves of every second point: enough hits to
        // vote, at 16 bytes per indexed point.
        _tracks = tracks;
        var counts = new int[(1 << KeyBits) + 1];
        foreach (var t in tracks)
        {
            for (int p = 0; p < t.Length; p += 2)
            {
                counts[Lo(t[p]) + 1]++;
                counts[Hi(t[p]) + 1]++;
            }
        }

        for (int k = 1; k < counts.Length; k++)
        {
            counts[k] += counts[k - 1];
        }

        _bucketStart = (int[])counts.Clone();
        _postings = new ulong[counts[^1]];
        var fill = (int[])counts.Clone();
        for (int ti = 0; ti < tracks.Count; ti++)
        {
            var t = tracks[ti];
            for (int p = 0; p < t.Length; p += 2)
            {
                ulong posting = ((ulong)ti << 32) | (uint)p;
                _postings[fill[Lo(t[p])]++] = posting;
                _postings[fill[Hi(t[p])]++] = posting;
            }
        }
    }

    private static int Lo(uint v) => (int)(v & 0xFFFF);

    private static int Hi(uint v) => (1 << 16) | (int)(v >> 16);

    public List<FingerprintMatch> Match(uint[] query, double minSeconds)
    {
        // 1. Vote on (track, offset).
        var votes = Vote(query);

        // 2. Verify the strongest alignments.
        int minItems = (int)(minSeconds / ItemSeconds);
        var found = new List<FingerprintMatch>();
        foreach (var (key, _) in votes.Where(v => v.Value >= 4).OrderByDescending(v => v.Value).Take(64))
        {
            int track = (int)(key >> 32), offset = (int)(uint)key;
            found.AddRange(Verify(query, track, offset, minItems));
        }

        // 3. Where two songs (or two alignments of one) claim the same moment, keep the stronger.
        var kept = new List<FingerprintMatch>();
        foreach (var m in found.OrderByDescending(m => m.Confidence * (m.End - m.Start)))
        {
            if (kept.All(k => k.End <= m.Start || k.Start >= m.End))
            {
                kept.Add(m);
            }
        }

        return kept.OrderBy(m => m.Start).ToList();
    }

    /// <summary>
    /// For every track, the best places it occurs in <paramref name="query"/>: up to
    /// <paramref name="perTrack"/> locations whose bit-error rate over the whole track is at most
    /// <paramref name="maxBitErrorRate"/>, best first. Meant for short tracks (anchors), whose
    /// whole length is compared at once instead of through a sliding window.
    /// </summary>
    public List<TrackLocation>[] Locate(uint[] query, int perTrack = 3, double maxBitErrorRate = MaxBitErrorRate, int minVotes = 2)
    {
        var byTrack = new List<(int Offset, int Votes)>[_tracks.Count];
        foreach (var (key, n) in Vote(query))
        {
            if (n >= minVotes)
            {
                int track = (int)(key >> 32), offset = (int)(uint)key;
                (byTrack[track] ??= []).Add((offset, n));
            }
        }

        var result = new List<TrackLocation>[_tracks.Count];
        for (int t = 0; t < _tracks.Count; t++)
        {
            var tried = new List<TrackLocation>();
            var track = _tracks[t];
            foreach (var (offset, _) in (byTrack[t] ?? []).OrderByDescending(c => c.Votes).Take(perTrack * 4))
            {
                // offset = track position - query position, so the track starts at query item -offset.
                // Chromaprint frames of two encodes rarely line up exactly: also try one item either side.
                for (int start = -offset - 1; start <= -offset + 1; start++)
                {
                    if (start >= 0 && start + track.Length <= query.Length)
                    {
                        int err = 0;
                        for (int i = 0; i < track.Length; i++)
                        {
                            err += BitOperations.PopCount(query[start + i] ^ track[i]);
                        }

                        tried.Add(new TrackLocation(t, start, err / (32.0 * track.Length)));
                    }
                }
            }

            // Best first; neighbours of a location already kept are the same occurrence.
            var kept = new List<TrackLocation>();
            foreach (var l in tried.Where(l => l.BitErrorRate <= maxBitErrorRate).OrderBy(l => l.BitErrorRate))
            {
                if (kept.Count < perTrack && kept.All(k => Math.Abs(k.QueryItem - l.QueryItem) > 2))
                {
                    kept.Add(l);
                }
            }

            result[t] = kept;
        }

        return result;
    }

    // Keys far more common than average (silence, hum) carry no information.
    private Dictionary<long, int> Vote(uint[] query)
    {
        double avg = (double)_postings.Length / (1 << KeyBits);
        int stopWord = (int)Math.Max(64, avg * 20);

        var votes = new Dictionary<long, int>();
        for (int i = 0; i < query.Length; i++)
        {
            foreach (int key in new[] { Lo(query[i]), Hi(query[i]) })
            {
                int lo = _bucketStart[key], hi = _bucketStart[key + 1];
                if (hi - lo == 0 || hi - lo > stopWord)
                {
                    continue;
                }

                for (int j = lo; j < hi; j++)
                {
                    int track = (int)(_postings[j] >> 32), pos = (int)(uint)_postings[j];
                    long k = ((long)track << 32) | (uint)(pos - i);
                    votes[k] = votes.GetValueOrDefault(k) + 1;
                }
            }
        }

        return votes;
    }

    private IEnumerable<FingerprintMatch> Verify(uint[] query, int track, int offset, int minItems)
    {
        var song = _tracks[track];
        int qStart = Math.Max(0, -offset), qEnd = Math.Min(query.Length, song.Length - offset);
        if (qEnd - qStart < minItems)
        {
            yield break;
        }

        var errors = new int[qEnd - qStart];
        for (int i = qStart; i < qEnd; i++)
        {
            errors[i - qStart] = BitOperations.PopCount(query[i] ^ song[i + offset]);
        }

        // Sliding-window bit error rate; collect runs under the threshold.
        int run = -1;
        int windowErr = 0;
        for (int i = 0; i < errors.Length; i++)
        {
            windowErr += errors[i];
            if (i >= Window)
            {
                windowErr -= errors[i - Window];
            }

            bool good = i >= Window - 1 && windowErr / (32.0 * Window) <= MaxBitErrorRate;
            if (good && run < 0)
            {
                run = i - Window + 1;
            }

            if ((!good || i == errors.Length - 1) && run >= 0)
            {
                int end = good ? i + 1 : i;

                // The window lets a run bleed ~2.5 s past the song on each side; trim the edges
                // back to points that actually agree (unrelated audio differs in ~16 of 32 bits).
                while (run < end && errors[run] > 11)
                {
                    run++;
                }

                while (end > run && errors[end - 1] > 11)
                {
                    end--;
                }

                if (end - run >= minItems)
                {
                    double ber = errors.Skip(run).Take(end - run).Sum() / (32.0 * Math.Max(1, end - run));
                    yield return new FingerprintMatch(track, (qStart + run) * ItemSeconds, (qStart + end) * ItemSeconds, Math.Round(1 - (2 * ber), 3));
                }

                run = -1;
            }
        }
    }
}
