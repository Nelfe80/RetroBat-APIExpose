using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Read surface of the Collections Pack Manager: installed pack index and the
/// collection family index driving dynamic collections.
/// </summary>
[ApiController]
[Tags("Themes & Collections")]
[Route("api/v1/collections")]
public sealed class CollectionsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Index of the collection packs installed from package-installer
    /// (path, hash, kind, collection name, install state).
    /// </summary>
    /// <response code="200">Installed pack index (empty when nothing was installed yet).</response>
    [HttpGet("packs")]
    [ProducesResponseType(typeof(CollectionPackInstallerIndex), StatusCodes.Status200OK)]
    public ActionResult<CollectionPackInstallerIndex> GetPacks()
    {
        var path = RetroBatPaths.CollectionPackInstallerIndexPath;
        if (!System.IO.File.Exists(path))
        {
            return Ok(new CollectionPackInstallerIndex { Packs = [] });
        }

        try
        {
            var index = JsonSerializer.Deserialize<CollectionPackInstallerIndex>(
                System.IO.File.ReadAllText(path), JsonOptions);
            return Ok(index ?? new CollectionPackInstallerIndex { Packs = [] });
        }
        catch (JsonException)
        {
            return Ok(new CollectionPackInstallerIndex { Packs = [] });
        }
    }

    /// <summary>
    /// Collection family index (one entry per game family) feeding the dynamic
    /// .xcc collections.
    /// </summary>
    /// <response code="200">Family entries, optionally filtered by family name.</response>
    [HttpGet("families")]
    [ProducesResponseType(typeof(IReadOnlyList<CollectionFamilyIndexEntry>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CollectionFamilyIndexEntry>> GetFamilies(
        [FromQuery] string? family = null,
        [FromQuery] int limit = 0)
    {
        var path = Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "gamelist_family.jsonl");
        if (!System.IO.File.Exists(path))
        {
            return Ok(Array.Empty<CollectionFamilyIndexEntry>());
        }

        var entries = new List<CollectionFamilyIndexEntry>();
        foreach (var line in System.IO.File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            CollectionFamilyIndexEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<CollectionFamilyIndexEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(family) &&
                !entry.CanonicalFamily.Contains(family, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(entry);
            if (limit > 0 && entries.Count >= limit)
            {
                break;
            }
        }

        return Ok(entries);
    }
}
