using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { status = "healthy", version = ApiExposeVersion.Current });
    }
}

[ApiController]
[Route("api/v1/[controller]")]
public class VersionController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { version = ApiExposeVersion.Current, name = "RetroBat Local API" });
    }
}
