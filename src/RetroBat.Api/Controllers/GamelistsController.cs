using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;
using System.Xml.Linq;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Read-only view over the user's EmulationStation gamelists, so panel tools
/// (LedManagerSetup "Mes jeux"…) can search installed games by rom or display
/// name through the gateway instead of parsing roms/*/gamelist.xml themselves.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class GamelistsController : ControllerBase
{
    private readonly IGamelistStore _gamelists;

    public GamelistsController(IGamelistStore gamelists)
    {
        _gamelists = gamelists;
    }

    public sealed record GamelistGameEntry(string Rom, string Name);

    public sealed record GamelistGamesSnapshot(string SystemId, int Total, IReadOnlyList<GamelistGameEntry> Games);

    /// <summary>
    /// Lists a system's installed games as EmulationStation displays them.
    /// </summary>
    /// <remarks>
    /// Entries come from <c>roms/&lt;system&gt;/gamelist.xml</c>: `rom` is the file name
    /// without extension, `name` the ES display name. Optional `search` matches rom
    /// or name; optional `limit` caps the result.
    /// </remarks>
    /// <response code="200">Installed games of the system.</response>
    /// <response code="404">The system has no readable gamelist.</response>
    [HttpGet("{systemId}/games")]
    [ProducesResponseType(typeof(GamelistGamesSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetGames(string systemId, [FromQuery] string? search = null, [FromQuery] int limit = 0)
    {
        if (string.IsNullOrWhiteSpace(systemId) || !string.Equals(Path.GetFileName(systemId), systemId, StringComparison.Ordinal))
        {
            return NotFound(new { message = "Invalid system id." });
        }

        var path = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
        if (!System.IO.File.Exists(path))
        {
            return NotFound(new { message = $"No gamelist for system '{systemId}'." });
        }

        XDocument? doc;
        lock (_gamelists.GetLock(path))
        {
            doc = _gamelists.Load(path, LoadOptions.None);
        }

        if (doc?.Root is null)
        {
            return NotFound(new { message = $"Unreadable gamelist for system '{systemId}'." });
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<GamelistGameEntry>();
        foreach (var game in doc.Root.Elements("game"))
        {
            var rom = Path.GetFileNameWithoutExtension((string?)game.Element("path") ?? "");
            var name = ((string?)game.Element("name") ?? "").Trim();
            if (rom.Length == 0 || !seen.Add(rom))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search)
                && !rom.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            games.Add(new GamelistGameEntry(rom, name.Length > 0 ? name : rom));
        }

        var total = games.Count;
        if (limit > 0 && games.Count > limit)
        {
            games = games.Take(limit).ToList();
        }

        return Ok(new GamelistGamesSnapshot(systemId, total, games));
    }
}
