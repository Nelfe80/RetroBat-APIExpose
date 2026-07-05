using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ContextController : ControllerBase
{
    private readonly ApiContext _context;

    public ContextController(ApiContext context)
    {
        _context = context;
    }

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
