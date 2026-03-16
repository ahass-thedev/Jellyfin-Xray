using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Persists and retrieves X-Ray data (timestamp → actor list) for each media item.
///
/// Files are stored at:
///   {PluginDataPath}/xray/{itemId}.json
///
/// JSON format:
///   { "42": ["Tom Hanks", "Robin Wright"], "130": ["Gary Sinise"] }
/// </summary>
public class XRayStore
{
    private readonly ILogger<XRayStore> _logger;
    private readonly string _dataPath;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public XRayStore(ILogger<XRayStore> logger)
    {
        _logger = logger;
        _dataPath = Plugin.Instance?.DataPath
            ?? Path.Combine(Path.GetTempPath(), "xray");
        Directory.CreateDirectory(_dataPath);
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns true if an xray.json exists for the given item.
    /// </summary>
    public bool Exists(Guid itemId)
        => File.Exists(FilePath(itemId));

    /// <summary>
    /// Returns the actors visible at timestamp <paramref name="seconds"/>,
    /// or an empty list if no data exists or no entry covers that time.
    /// </summary>
    public IReadOnlyList<string> GetActorsAt(Guid itemId, int seconds)
    {
        var data = Load(itemId);
        if (data is null || data.Count == 0)
            return Array.Empty<string>();

        // Find the largest recorded timestamp ≤ seconds
        int interval = Plugin.Instance?.Configuration.TrickplayIntervalSeconds ?? 10;
        var candidates = data.Keys
            .Select(k => int.TryParse(k, out var v) ? v : -1)
            .Where(t => t >= 0 && t <= seconds)
            .ToList();

        if (candidates.Count == 0)
            return Array.Empty<string>();

        int closest = candidates.Max();

        // If the nearest entry is more than 2 intervals old, don't show stale data
        if (seconds - closest > interval * 2)
            return Array.Empty<string>();

        return data.TryGetValue(closest.ToString(), out var actors)
            ? actors
            : Array.Empty<string>();
    }

    /// <summary>
    /// Persists the full X-Ray map for an item, replacing any existing file.
    /// </summary>
    public async Task SaveAsync(Guid itemId, Dictionary<string, List<string>> data, CancellationToken ct)
    {
        var path = FilePath(itemId);
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        _logger.LogInformation("Saved X-Ray data for {ItemId}: {Count} entries", itemId, data.Count);
    }

    /// <summary>
    /// Deletes the xray data file for an item (forces re-analysis).
    /// </summary>
    public void Invalidate(Guid itemId)
    {
        var path = FilePath(itemId);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Invalidated X-Ray data for {ItemId}", itemId);
        }
    }

    // ------------------------------------------------------------------
    // Internal
    // ------------------------------------------------------------------

    private Dictionary<string, List<string>>? Load(Guid itemId)
    {
        var path = FilePath(itemId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse X-Ray data for {ItemId}", itemId);
            return null;
        }
    }

    private string FilePath(Guid itemId)
        => Path.Combine(_dataPath, $"{itemId:N}.json");
}
