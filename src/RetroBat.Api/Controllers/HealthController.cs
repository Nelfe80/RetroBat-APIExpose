using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("System & Health")]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    private readonly StartupReadinessState? _readiness;

    public HealthController(StartupReadinessState? readiness = null)
    {
        _readiness = readiness;
    }

    /// <summary>
    /// Liveness probe: returns healthy plus the running version, or 503 « stopping » once the API
    /// is shutting down after EmulationStation left. The ES start hook then replaces it.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<HealthResponse> Get()
    {
        // « stopping » et non « unhealthy » : le script de demarrage d'ES cherche le mot « healthy ».
        if (_readiness?.ArretEnCours == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new HealthResponse { Status = "stopping", Version = ApiExposeVersion.Current });
        }

        return Ok(new HealthResponse { Status = "healthy", Version = ApiExposeVersion.Current });
    }
}

[ApiController]
[Tags("System & Health")]
[Route("api/v1/[controller]")]
public class VersionController : ControllerBase
{
    /// <summary>Version and product name of the local API.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(VersionResponse), StatusCodes.Status200OK)]
    public ActionResult<VersionResponse> Get()
    {
        return Ok(new VersionResponse { Version = ApiExposeVersion.Current, Name = "RetroBat Local API" });
    }
}
