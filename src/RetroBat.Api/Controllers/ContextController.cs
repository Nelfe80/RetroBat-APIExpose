using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Contexte & navigation")]
[Route("api/v1/[controller]")]
public class ContextController : ControllerBase
{
    private readonly ApiContext _context;

    public ContextController(ApiContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Consolidated UI state: selected system, selected game and running game
    /// in one call. Preferred entry point (the SDK uses this one).
    /// </summary>
    [HttpGet("state")]
    public IActionResult GetState()
    {
        return Ok(new
        {
            state = _context.Ui.State,
            selectedSystem = _context.Ui.SelectedSystem,
            selectedGame = _context.Ui.Selected,
            runningGame = _context.Ui.Running
        });
    }

    /// <summary>
    /// Running game if any, otherwise the selected game. 404 when neither
    /// exists (e.g. right after APIExpose restarts, before any ES selection).
    /// Prefer <c>GET state</c> for new integrations.
    /// </summary>
    [HttpGet("current-game")]
    public IActionResult GetCurrentGame()
    {
        var game = _context.Ui.Running ?? _context.Ui.Selected;
        if (game == null)
            return NotFound(new { message = "No game selected or running" });

        return Ok(new {
            state = _context.Ui.State,
            systemId = game.SystemId,
            gameId = game.GameId,
            path = game.GamePath,
            name = game.GameName,
            launch = game.Launch,
            details = game.Details
        });
    }

    /// <summary>
    /// Currently selected system, 404 when none. Prefer <c>GET state</c> for
    /// new integrations.
    /// </summary>
    [HttpGet("current-system")]
    public IActionResult GetCurrentSystem()
    {
        var sys = _context.Ui.SelectedSystem;
        if (sys == null)
            return NotFound(new { message = "No system selected" });

        return Ok(new {
            state = _context.Ui.State,
            system = sys
        });
    }

    /// <summary>
    /// Full context snapshot (schema version, node identity, UI state, time).
    /// </summary>
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new {
            schemaVersion = _context.SchemaVersion,
            node = _context.Node,
            ui = _context.Ui,
            time = new { utc = _context.UtcTime }
        });
    }
}
