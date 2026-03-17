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
    /// Checks both the server data path (Jellyfin default) and the media-adjacent
    /// path (used when "Store trickplay images next to media" is enabled).
    /// Picks the highest available width tier for best face detection accuracy.
    /// </summary>
    public string? GetTrickplayDirectory(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
            return null;

        var idN = itemId.ToString("N");

        // Candidate roots: data-path first (Jellyfin default), then media-adjacent
        var candidates = new List<string>
        {
            Path.Combine(_appPaths.DataPath, "trickplay", idN),
        };

        var mediaPath = item.Path;
        if (!string.IsNullOrEmpty(mediaPath))
        {
            var mediaDir = Path.GetDirectoryName(mediaPath);
            if (mediaDir is not null)
                candidates.Add(Path.Combine(mediaDir, ".trickplay", idN));
        }

        foreach (var root in candidates)
        {
            if (!Directory.Exists(root))
                continue;

            var widthDirs = Directory
                .EnumerateDirectories(root)
                .Select(d => (path: d, width: int.TryParse(Path.GetFileName(d), out var w) ? w : 0))
                .Where(x => x.width > 0)
                .OrderByDescending(x => x.width)
                .ToList();

            if (widthDirs.Count == 0)
                continue;

            var best = widthDirs[0].path;
            _logger.LogInformation("Using trickplay dir {Path} for {ItemId}", best, itemId);
            return best;
        }

        _logger.LogWarning("No trickplay found for {ItemId}. DataPath={DataPath} Checked: {Paths}",
            itemId, _appPaths.DataPath, string.Join(", ", candidates));
        return null;
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
