using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Context & Navigation")]
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
    [ProducesResponseType(typeof(ContextStateResponse), StatusCodes.Status200OK)]
    public ActionResult<ContextStateResponse> GetState()
    {
        return Ok(new ContextStateResponse
        {
            State = _context.Ui.State,
            SelectedSystem = _context.Ui.SelectedSystem,
            SelectedGame = _context.Ui.Selected,
            RunningGame = _context.Ui.Running
        });
    }

    /// <summary>
    /// Running game if any, otherwise the selected game. 404 when neither
    /// exists (e.g. right after APIExpose restarts, before any ES selection).
    /// Prefer <c>GET state</c> for new integrations.
    /// </summary>
    [HttpGet("current-game")]
    [ProducesResponseType(typeof(CurrentGameResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<CurrentGameResponse> GetCurrentGame()
    {
        var game = _context.Ui.Running ?? _context.Ui.Selected;
        if (game == null)
            return NotFound(new { message = "No game selected or running" });

        return Ok(new CurrentGameResponse
        {
            State = _context.Ui.State,
            SystemId = game.SystemId,
            GameId = game.GameId,
            Path = game.GamePath,
            Name = game.GameName,
            Launch = game.Launch,
            Details = game.Details
        });
    }

    /// <summary>
    /// Currently selected system, 404 when none. Prefer <c>GET state</c> for
    /// new integrations.
    /// </summary>
    [HttpGet("current-system")]
    [ProducesResponseType(typeof(CurrentSystemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<CurrentSystemResponse> GetCurrentSystem()
    {
        var sys = _context.Ui.SelectedSystem;
        if (sys == null)
            return NotFound(new { message = "No system selected" });

        return Ok(new CurrentSystemResponse
        {
            State = _context.Ui.State,
            System = sys
        });
    }

    /// <summary>
    /// Full context snapshot (schema version, node identity, UI state, time).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ContextSnapshotResponse), StatusCodes.Status200OK)]
    public ActionResult<ContextSnapshotResponse> Get()
    {
        return Ok(new ContextSnapshotResponse
        {
            SchemaVersion = _context.SchemaVersion,
            Node = _context.Node,
            Ui = _context.Ui,
            Time = new ContextTimeResponse { Utc = _context.UtcTime }
        });
    }
}
