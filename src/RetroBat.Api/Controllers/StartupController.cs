using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("System & Health")]
[Route("api/v1/startup")]
public sealed class StartupController : ControllerBase
{
    private readonly StartupReadinessState _readiness;

    public StartupController(StartupReadinessState readiness)
    {
        _readiness = readiness;
    }

    /// <summary>
    /// Readiness probe: 503 while APIExpose is still preparing (gamelists,
    /// menus, watchers), 200 once everything is up.
    /// </summary>
    [HttpGet("ready")]
    public IActionResult Ready()
    {
        if (!_readiness.IsReady)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "starting",
                ready = false
            });
        }

        return Ok(new
        {
            status = "ready",
            ready = true,
            readyAtUtc = _readiness.ReadyAtUtc
        });
    }

    /// <summary>Diagnostics of the startup gamelist preparation (most recent entries first).</summary>
    [HttpGet("gamelists")]
    public IActionResult Gamelists([FromQuery] int recent = 25)
    {
        return Ok(StartupGamelistPreparationDiagnostics.BuildStatus(recent));
    }
}
