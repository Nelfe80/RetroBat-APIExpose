using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/startup")]
public sealed class StartupController : ControllerBase
{
    private readonly StartupReadinessState _readiness;

    public StartupController(StartupReadinessState readiness)
    {
        _readiness = readiness;
    }

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

    [HttpGet("gamelists")]
    public IActionResult Gamelists([FromQuery] int recent = 25)
    {
        return Ok(StartupGamelistPreparationDiagnostics.BuildStatus(recent));
    }
}
