using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// Retrieves metadata needed for X-Ray analysis using Jellyfin's internal
/// ILibraryManager — no API keys or HTTP calls required.
/// </summary>
public class MetadataService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<MetadataService> _logger;

    public MetadataService(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        ILogger<MetadataService> logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Returns cast members for a media item, ordered by billing order.
    /// </summary>
    public IReadOnlyList<ActorInfo> GetCast(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("Item {ItemId} not found in library", itemId);
            return Array.Empty<ActorInfo>();
        }

        var people = _libraryManager.GetPeople(item);

        var actors = people
            .Where(p => p.Type == PersonKind.Actor)
            .OrderBy(p => p.SortOrder ?? int.MaxValue)
            .ToList();

        _logger.LogDebug("Item {ItemId} has {Count} actors", itemId, actors.Count);

        var result = new List<ActorInfo>(actors.Count);
        foreach (var person in actors)
        {
            var personItem = _libraryManager.GetPerson(person.Name);
            var imagePath = GetPersonImagePath(personItem);

            result.Add(new ActorInfo(
                Name: person.Name,
                Role: person.Role ?? string.Empty,
                PersonId: personItem?.Id ?? Guid.Empty,
                ImagePath: imagePath));
        }

        return result;
    }

    /// <summary>
    /// Returns trickplay info for a media item, or null if none exists.
    /// Jellyfin stores trickplay as: {mediaFilenameWithoutExt}.trickplay/{width} - {cols}x{rows}/
    /// Picks the highest available width tier for best face detection accuracy.
    /// </summary>
    public TrickplayInfo? GetTrickplayInfo(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
            return null;

        var mediaDir = Path.GetDirectoryName(item.Path);
        var mediaName = Path.GetFileNameWithoutExtension(item.Path);
        if (mediaDir is null || mediaName is null)
            return null;

        // Jellyfin trickplay: "{mediaName}.trickplay/{width} - {cols}x{rows}/"
        var trickplayRoot = Path.Combine(mediaDir, mediaName + ".trickplay");

        if (!Directory.Exists(trickplayRoot))
        {
            _logger.LogWarning(
                "No trickplay for {ItemId} — expected at {Path}",
                itemId, trickplayRoot);
            return null;
        }

        TrickplayInfo? best = null;
        foreach (var dir in Directory.EnumerateDirectories(trickplayRoot))
        {
            var dirName = Path.GetFileName(dir);
            if (TryParseTrickplayDirName(dirName, out var width, out var cols, out var rows))
            {
                if (best is null || width > best.Width)
                    best = new TrickplayInfo(dir, width, cols, rows);
            }
        }

        if (best is null)
        {
            _logger.LogWarning("No valid trickplay subdirectory in {Path}", trickplayRoot);
            return null;
        }

        _logger.LogInformation(
            "Using trickplay {Dir} ({Width}px, {Cols}x{Rows}) for {ItemId}",
            best.Directory, best.Width, best.Cols, best.Rows, itemId);
        return best;
    }

    /// <summary>
    /// Returns all movie and episode item IDs in the library.
    /// </summary>
    public IReadOnlyList<Guid> GetAllAnalysableItemIds()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        };

        var result = _libraryManager.GetItemList(query);
        return result.Select(i => i.Id).ToList();
    }

    /// <summary>
    /// Parses a trickplay subdirectory name in the format "{width} - {cols}x{rows}".
    /// Example: "320 - 10x10" → width=320, cols=10, rows=10.
    /// </summary>
    private static bool TryParseTrickplayDirName(string name, out int width, out int cols, out int rows)
    {
        width = cols = rows = 0;
        var dashIdx = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx < 0 || !int.TryParse(name[..dashIdx], out width))
            return false;

        var grid = name[(dashIdx + 3)..]; // "10x10"
        var xIdx = grid.IndexOf('x');
        if (xIdx < 0)
            return false;

        return int.TryParse(grid[..xIdx], out cols) && int.TryParse(grid[(xIdx + 1)..], out rows);
    }

    private static string? GetPersonImagePath(Person? person)
    {
        if (person is null)
            return null;

        var image = person.GetImages(ImageType.Primary).FirstOrDefault();
        if (image is not null && File.Exists(image.Path))
            return image.Path;

        return null;
    }
}

/// <summary>Trickplay sprite sheet metadata for one width tier.</summary>
public record TrickplayInfo(string Directory, int Width, int Cols, int Rows);

/// <summary>Lightweight DTO carrying the metadata the sidecar needs per actor.</summary>
public record ActorInfo(
    string Name,
    string Role,
    Guid PersonId,
    string? ImagePath);
