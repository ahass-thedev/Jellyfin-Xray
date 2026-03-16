using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Orchestrates the full X-Ray analysis pipeline for a single media item:
///   1. Get cast (MetadataService)
///   2. Get trickplay image directory (MetadataService)
///   3. Split each trickplay sprite into frames
///   4. Send each frame to the sidecar for face matching (SidecarClient)
///   5. Persist the results (XRayStore)
/// </summary>
public class XRayService
{
    private readonly MetadataService _metadata;
    private readonly SidecarClient _sidecar;
    private readonly XRayStore _store;
    private readonly ILogger<XRayService> _logger;

    // Trickplay sprites are typically 320×180 tiles
    private const int TileWidth = 320;
    private const int TileHeight = 180;

    public XRayService(
        MetadataService metadata,
        SidecarClient sidecar,
        XRayStore store,
        ILogger<XRayService> logger)
    {
        _metadata = metadata;
        _sidecar = sidecar;
        _store = store;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Run the full analysis pipeline for a single item.
    /// Skips if xray data already exists (pass <paramref name="force"/> = true to re-analyse).
    /// </summary>
    public async Task AnalyzeAsync(Guid itemId, bool force, CancellationToken ct)
    {
        if (!force && _store.Exists(itemId))
        {
            _logger.LogDebug("Skipping {ItemId} — X-Ray data already exists", itemId);
            return;
        }

        _logger.LogInformation("Analysing item {ItemId}", itemId);

        var cast = _metadata.GetCast(itemId);
        if (cast.Count == 0)
        {
            _logger.LogWarning("No cast found for {ItemId} — skipping", itemId);
            return;
        }

        var trickplayDir = _metadata.GetTrickplayDirectory(itemId);
        if (trickplayDir is null)
        {
            _logger.LogWarning("No trickplay data for {ItemId} — skipping", itemId);
            return;
        }

        var spriteFiles = Directory
            .EnumerateFiles(trickplayDir, "*.jpg")
            .Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out _))
            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
            .ToList();

        if (spriteFiles.Count == 0)
        {
            _logger.LogWarning("Trickplay directory {Dir} has no .jpg files", trickplayDir);
            return;
        }

        var interval = Plugin.Instance?.Configuration.TrickplayIntervalSeconds ?? 10;
        var xrayData = new Dictionary<string, List<string>>();

        foreach (var spritePath in spriteFiles)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessSpriteAsync(spritePath, cast, interval, xrayData, ct)
                .ConfigureAwait(false);
        }

        await _store.SaveAsync(itemId, xrayData, ct).ConfigureAwait(false);
        _logger.LogInformation("Analysis complete for {ItemId}: {Count} entries", itemId, xrayData.Count);
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private async Task ProcessSpriteAsync(
        string spritePath,
        IReadOnlyList<ActorInfo> cast,
        int interval,
        Dictionary<string, List<string>> xrayData,
        CancellationToken ct)
    {
        int baseSecond = int.Parse(Path.GetFileNameWithoutExtension(spritePath));

        List<(int timestamp, byte[] jpegBytes)> frames;
        try
        {
            frames = SplitSprite(spritePath, baseSecond, interval);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to split sprite {Path}", spritePath);
            return;
        }

        foreach (var (timestamp, frameBytes) in frames)
        {
            ct.ThrowIfCancellationRequested();

            var matches = await _sidecar.MatchAsync(frameBytes, cast, ct).ConfigureAwait(false);
            if (matches.Count > 0)
            {
                xrayData[timestamp.ToString()] = matches.ToList();
                _logger.LogDebug("t={T}s → {Actors}", timestamp, string.Join(", ", matches));
            }
        }
    }

    /// <summary>
    /// Splits a trickplay sprite sheet into individual JPEG frames.
    /// Returns a list of (timestamp in seconds, JPEG bytes) tuples.
    /// </summary>
    private static List<(int timestamp, byte[] jpeg)> SplitSprite(
        string spritePath, int baseSecond, int interval)
    {
        using var sprite = Image.Load<Rgb24>(spritePath);

        int cols = sprite.Width / TileWidth;
        int rows = sprite.Height / TileHeight;

        var frames = new List<(int, byte[])>(cols * rows);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                int frameIndex = (row * cols) + col;
                int timestamp = baseSecond + (frameIndex * interval);

                var rect = new Rectangle(
                    col * TileWidth,
                    row * TileHeight,
                    Math.Min(TileWidth, sprite.Width - col * TileWidth),
                    Math.Min(TileHeight, sprite.Height - row * TileHeight));

                using var tile = sprite.Clone(ctx => ctx.Crop(rect));
                using var ms = new MemoryStream();
                tile.SaveAsJpeg(ms);
                frames.Add((timestamp, ms.ToArray()));
            }
        }

        return frames;
    }
}
