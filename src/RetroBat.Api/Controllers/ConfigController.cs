using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("System & Health")]
[Route("api/v1/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly ApiExposeRuntimeOptionsService _runtimeOptionsService;

    public ConfigController(ApiExposeRuntimeOptionsService runtimeOptionsService)
    {
        _runtimeOptionsService = runtimeOptionsService;
    }

    /// <summary>
    /// Returns APIExpose local module options with appsettings defaults and RetroBat menu overrides.
    /// </summary>
    /// <remarks>
    /// This endpoint reads <c>appsettings.json</c> through the current options monitor and
    /// <c>es_settings.cfg</c> directly. It reports the effective value for each root menu option.
    /// Some options are declared for upcoming modules and may not be enforced by runtime services yet.
    /// </remarks>
    [HttpGet("local-options")]
    [ProducesResponseType(typeof(ApiExposeLocalOptionsSnapshot), StatusCodes.Status200OK)]
    public ActionResult<ApiExposeLocalOptionsSnapshot> GetLocalOptions()
    {
        return Ok(_runtimeOptionsService.GetLocalOptionsSnapshot());
    }
}
