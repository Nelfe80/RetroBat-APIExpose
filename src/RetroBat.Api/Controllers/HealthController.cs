using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Système & santé")]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    /// <summary>Liveness probe: returns healthy plus the running version.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { status = "healthy", version = ApiExposeVersion.Current });
    }
}

[ApiController]
[Tags("Système & santé")]
[Route("api/v1/[controller]")]
public class VersionController : ControllerBase
{
    /// <summary>Version and product name of the local API.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { version = ApiExposeVersion.Current, name = "RetroBat Local API" });
    }
}
