using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("System & Health")]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    /// <summary>Liveness probe: returns healthy plus the running version.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get()
    {
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
