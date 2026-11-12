using System.Text.Json;
using Jellyfin.Plugin.SceneXray.Storage;

namespace Jellyfin.Plugin.SceneXray.Analysis;

/// <summary>
/// Cached reference embeddings per person (one file per model pack): embeddings of cast photos,
/// keyed by photo, and "learned" embeddings taken from confident matches or admin assignments.
/// </summary>
public sealed class Gallery
{
    private const int MaxLearnedPerPerson = 12;
    private readonly string _file;
    private readonly object _lock = new();
    private Dictionary<Guid, PersonEntry> _people = [];
    private bool _dirty;

    public Gallery(string file)
    {
        _file = file;
        if (File.Exists(file))
        {
            try
            {
                _people = JsonSerializer.Deserialize<Dictionary<Guid, PersonEntry>>(File.ReadAllText(file), TimelineStore.Json) ?? [];
            }
            catch (JsonException)
            {
                _people = [];
            }
        }
    }

    public sealed class PersonEntry
    {
        /// <summary>Gets or sets photo key -> base64 embedding ("" = photo has no usable face).</summary>
        public Dictionary<string, string> Photos { get; set; } = [];

        public List<LearnedRef> Learned { get; set; } = [];

        public DateTime? TmdbCheckedAt { get; set; }
    }

    public sealed class LearnedRef
    {
        public Guid SourceItemId { get; set; }

        public string Embedding { get; set; } = string.Empty;

        public double Score { get; set; }

        public bool Manual { get; set; }
    }

    /// <summary>Gets a cached photo embedding; found=true with null value means "no usable face".</summary>
    public bool TryGetPhoto(Guid person, string key, out float[]? embedding)
    {
        lock (_lock)
        {
            embedding = null;
            if (_people.TryGetValue(person, out var e) && e.Photos.TryGetValue(key, out var b64))
            {
                embedding = EmbeddingCodec.Decode(b64);
                return true;
            }

            return false;
        }
    }

    public void SetPhoto(Guid person, string key, float[]? embedding)
    {
        lock (_lock)
        {
            Entry(person).Photos[key] = EmbeddingCodec.Encode(embedding);
            _dirty = true;
        }
    }

    public DateTime? TmdbCheckedAt(Guid person)
    {
        lock (_lock)
        {
            return _people.TryGetValue(person, out var e) ? e.TmdbCheckedAt : null;
        }
    }

    public void MarkTmdbChecked(Guid person)
    {
        lock (_lock)
        {
            Entry(person).TmdbCheckedAt = DateTime.UtcNow;
            _dirty = true;
        }
    }

    /// <summary>All embeddings to compare against for this person, excluding ones learned from <paramref name="exceptItem"/>.</summary>
    public List<float[]> References(Guid person, Guid exceptItem)
    {
        lock (_lock)
        {
            if (!_people.TryGetValue(person, out var e))
            {
                return [];
            }

            // Never let a title vouch for itself: learned faces from this very file are left out,
            // so re-analysing cannot snowball an old mistake.
            return e.Photos.Values.Select(EmbeddingCodec.Decode).OfType<float[]>()
                .Concat(e.Learned.Where(l => l.SourceItemId != exceptItem).Select(l => EmbeddingCodec.Decode(l.Embedding)).OfType<float[]>())
                .ToList();
        }
    }

    public void Learn(Guid person, Guid sourceItem, float[] embedding, double score, bool manual)
    {
        lock (_lock)
        {
            var e = Entry(person);
            e.Learned.RemoveAll(l => l.SourceItemId == sourceItem && l.Manual == manual);
            e.Learned.Add(new LearnedRef { SourceItemId = sourceItem, Embedding = EmbeddingCodec.Encode(embedding), Score = score, Manual = manual });
            if (e.Learned.Count > MaxLearnedPerPerson)
            {
                // Keep admin assignments first, then the strongest automatic matches.
                e.Learned = e.Learned.OrderByDescending(l => l.Manual).ThenByDescending(l => l.Score).Take(MaxLearnedPerPerson).ToList();
            }

            _dirty = true;
        }
    }

    public void Forget(Guid person, Guid sourceItem)
    {
        lock (_lock)
        {
            if (_people.TryGetValue(person, out var e) && e.Learned.RemoveAll(l => l.SourceItemId == sourceItem) > 0)
            {
                _dirty = true;
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_people, TimelineStore.Json));
            File.Move(tmp, _file, overwrite: true);
            _dirty = false;
        }
    }

    private PersonEntry Entry(Guid person)
    {
        if (!_people.TryGetValue(person, out var e))
        {
            e = new PersonEntry();
            _people[person] = e;
        }

        return e;
    }
}

/// <summary>float[] &lt;-&gt; base64 (a 512-d embedding is 2.7 KB instead of ~5 KB of JSON numbers).</summary>
public static class EmbeddingCodec
{
    public static string Encode(float[]? v) =>
        v is null ? string.Empty : Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan()));

    public static float[]? Decode(string? b64)
    {
        if (string.IsNullOrEmpty(b64))
        {
            return null;
        }

        var bytes = Convert.FromBase64String(b64);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }
}
