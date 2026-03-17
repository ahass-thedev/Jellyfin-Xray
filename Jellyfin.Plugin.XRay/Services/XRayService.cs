using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Orchestrates the full X-Ray analysis pipeline for a single media item:
///   1. Get cast (MetadataService)
///   2. Get trickplay sprite info (MetadataService)
///   3. Split each trickplay sprite sheet into individual frames
///   4. Send each frame to the sidecar for face matching (SidecarClient)
///   5. Persist the results (XRayStore)
/// </summary>
public class XRayService
{
    private readonly MetadataService _metadata;
    private readonly SidecarClient _sidecar;
    private readonly XRayStore _store;
    private readonly ILogger<XRayService> _logger;

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

        var trickplay = _metadata.GetTrickplayInfo(itemId);
        if (trickplay is null)
        {
            _logger.LogWarning("No trickplay data for {ItemId} — skipping", itemId);
            return;
        }

        var spriteFiles = Directory
            .EnumerateFiles(trickplay.Directory, "*.jpg")
            .Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out _))
            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
            .ToList();

        if (spriteFiles.Count == 0)
        {
            _logger.LogWarning("Trickplay directory {Dir} has no .jpg files", trickplay.Directory);
            return;
        }

        var interval = Plugin.Instance?.Configuration.TrickplayIntervalSeconds ?? 10;
        var tilesPerSprite = trickplay.Cols * trickplay.Rows;
        var xrayData = new Dictionary<string, List<string>>();

        for (int i = 0; i < spriteFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Sprite files are numbered sequentially (0.jpg, 1.jpg, …).
            // Each sprite covers (cols * rows) time slots of `interval` seconds.
            int baseSecond = i * tilesPerSprite * interval;
            await ProcessSpriteAsync(spriteFiles[i], trickplay, baseSecond, cast, interval, xrayData, ct)
                .ConfigureAwait(false);
        }

        await _store.SaveAsync(itemId, xrayData, ct).ConfigureAwait(false);
        _logger.LogInformation("Analysis complete for {ItemId}: {Count} timestamp entries", itemId, xrayData.Count);
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private async Task ProcessSpriteAsync(
        string spritePath,
        TrickplayInfo trickplay,
        int baseSecond,
        IReadOnlyList<ActorInfo> cast,
        int interval,
        Dictionary<string, List<string>> xrayData,
        CancellationToken ct)
    {
        List<(int timestamp, byte[] jpegBytes)> frames;
        try
        {
            frames = SplitSprite(spritePath, trickplay.Cols, trickplay.Rows, baseSecond, interval);
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
    /// Tile dimensions are computed from the image size and the grid layout.
    /// Returns a list of (timestamp in seconds, JPEG bytes) tuples.
    /// </summary>
    private static List<(int timestamp, byte[] jpeg)> SplitSprite(
        string spritePath, int cols, int rows, int baseSecond, int interval)
    {
        using var sprite = Image.Load<Rgb24>(spritePath);

        int tileWidth = sprite.Width / cols;
        int tileHeight = sprite.Height / rows;

        var frames = new List<(int, byte[])>(cols * rows);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                int frameIndex = (row * cols) + col;
                int timestamp = baseSecond + (frameIndex * interval);

                var rect = new Rectangle(
                    col * tileWidth,
                    row * tileHeight,
                    Math.Min(tileWidth, sprite.Width - col * tileWidth),
                    Math.Min(tileHeight, sprite.Height - row * tileHeight));

                using var tile = sprite.Clone(ctx => ctx.Crop(rect));
                using var ms = new MemoryStream();
                tile.SaveAsJpeg(ms);
                frames.Add((timestamp, ms.ToArray()));
            }
        }

        return frames;
    }
}
