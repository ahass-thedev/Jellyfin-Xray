using Jellyfin.Data.Enums;
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
    private readonly ILogger<MetadataService> _logger;

    public MetadataService(
        ILibraryManager libraryManager,
        ILogger<MetadataService> logger)
    {
        _libraryManager = libraryManager;
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

        // In Jellyfin 10.9+ PersonInfo.Type is PersonKind enum, not a string
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
    /// Returns the trickplay directory for a media item, if one exists.
    /// Picks the highest available width tier for best face detection accuracy.
    /// </summary>
    public string? GetTrickplayDirectory(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
            return null;

        var mediaPath = item.Path;
        if (string.IsNullOrEmpty(mediaPath))
            return null;

        var mediaDir = Path.GetDirectoryName(mediaPath);
        if (mediaDir is null)
            return null;

        var trickplayRoot = Path.Combine(mediaDir, ".trickplay", itemId.ToString("N"));

        if (!Directory.Exists(trickplayRoot))
        {
            _logger.LogDebug("No trickplay directory for {ItemId} at {Path}", itemId, trickplayRoot);
            return null;
        }

        var widthDirs = Directory
            .EnumerateDirectories(trickplayRoot)
            .Select(d => (path: d, width: int.TryParse(Path.GetFileName(d), out var w) ? w : 0))
            .Where(x => x.width > 0)
            .OrderByDescending(x => x.width)
            .ToList();

        if (widthDirs.Count == 0)
            return null;

        var best = widthDirs[0].path;
        _logger.LogDebug("Using trickplay dir {Path} for {ItemId}", best, itemId);
        return best;
    }

    /// <summary>
    /// Returns all movie and episode item IDs in the library.
    /// </summary>
    public IReadOnlyList<Guid> GetAllAnalysableItemIds()
    {
        // BaseItemKind is in MediaBrowser.Model.Querying (Jellyfin 10.9)
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        };

        var result = _libraryManager.GetItemList(query);
        return result.Select(i => i.Id).ToList();
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

/// <summary>Lightweight DTO carrying the metadata the sidecar needs per actor.</summary>
public record ActorInfo(
    string Name,
    string Role,
    Guid PersonId,
    string? ImagePath);
