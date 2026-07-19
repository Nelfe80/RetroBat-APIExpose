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
[Tags("Roms Manager")]
[Route("api/v1/[controller]")]
public class GamelistsController : ControllerBase
{
    private readonly IGamelistStore _gamelists;

    public GamelistsController(IGamelistStore gamelists)
    {
        _gamelists = gamelists;
    }

    public sealed record GamelistGameEntry(string Rom, string Name, string Path = "");

    public sealed record GamelistGamesSnapshot(string SystemId, int Total, IReadOnlyList<GamelistGameEntry> Games);

    public sealed record GamelistSystemEntry(string SystemId, int Games);

    public sealed record GamelistSystemsSnapshot(int Total, IReadOnlyList<GamelistSystemEntry> Systems);

    /// <summary>
    /// Lists the systems that have an installed gamelist (roms/*/gamelist.xml).
    /// </summary>
    /// <remarks>
    /// Feeds system → game pickers (tournament manager, Live Contest): pick the
    /// system here, then its games via <c>GET /api/v1/Gamelists/{systemId}/games</c>.
    /// </remarks>
    /// <response code="200">Systems with an installed gamelist.</response>
    [HttpGet]
    [ProducesResponseType(typeof(GamelistSystemsSnapshot), StatusCodes.Status200OK)]
    public IActionResult GetSystems()
    {
        var systems = new List<GamelistSystemEntry>();
        if (Directory.Exists(RetroBatPaths.RomsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(RetroBatPaths.RomsRoot))
            {
                var path = Path.Combine(directory, "gamelist.xml");
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                XDocument? doc;
                lock (_gamelists.GetLock(path))
                {
                    doc = _gamelists.Load(path, LoadOptions.None);
                }

                // Seuls les jeux dont la rom existe encore sur disque comptent
                // (même logique que les Setups) : un gamelist orphelin ne fait
                // pas apparaître le système.
                var count = doc?.Root?.Elements("game")
                    .Count(game => RomExists(directory, (string?)game.Element("path"))) ?? 0;
                if (count > 0)
                {
                    systems.Add(new GamelistSystemEntry(Path.GetFileName(directory), count));
                }
            }
        }

        systems.Sort((a, b) => string.Compare(a.SystemId, b.SystemId, StringComparison.OrdinalIgnoreCase));
        return Ok(new GamelistSystemsSnapshot(systems.Count, systems));
    }

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
        var systemDir = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        foreach (var game in doc.Root.Elements("game"))
        {
            var rawPath = ((string?)game.Element("path") ?? "").Trim();
            var rom = Path.GetFileNameWithoutExtension(rawPath);
            var name = ((string?)game.Element("name") ?? "").Trim();
            if (rom.Length == 0 || !seen.Add(rom) || !RomExists(systemDir, rawPath))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search)
                && !rom.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Chemin RetroBat-relatif prêt pour Commands/launch (additif) :
            // "./foo.zip" du gamelist → "roms/<system>/foo.zip".
            var fileName = Path.GetFileName(rawPath.Replace('\\', '/'));
            var launchPath = fileName.Length > 0 ? $"roms/{systemId}/{fileName}" : "";
            games.Add(new GamelistGameEntry(rom, name.Length > 0 ? name : rom, launchPath));
        }

        var total = games.Count;
        if (limit > 0 && games.Count > limit)
        {
            games = games.Take(limit).ToList();
        }

        return Ok(new GamelistGamesSnapshot(systemId, total, games));
    }

    private static bool RomExists(string systemDir, string? rawPath)
    {
        var value = (rawPath ?? "").Trim().Replace('\\', '/');
        if (value.Length == 0)
        {
            return false;
        }

        try
        {
            var full = Path.IsPathFullyQualified(value)
                ? value
                : Path.GetFullPath(Path.Combine(systemDir, value.TrimStart('.', '/')));
            // Un « jeu » peut être un dossier (ps2, dossiers .m3u éclatés…).
            return System.IO.File.Exists(full) || Directory.Exists(full);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
