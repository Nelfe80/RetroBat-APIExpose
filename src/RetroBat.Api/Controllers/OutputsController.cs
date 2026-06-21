using Microsoft.AspNetCore.Mvc;
using RetroBat.Domain.Interfaces;
using RetroBat.Providers.MameOutputs;
using RetroBat.Providers.RetroArchWrapper;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class OutputsController : ControllerBase
{
    private readonly IEnumerable<IProvider> _providers;

    public OutputsController(IEnumerable<IProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// Returns the latest in-memory snapshot of MAME network outputs.
    /// </summary>
    /// <response code="200">Current MAME output snapshot.</response>
    /// <response code="404">MAME outputs provider is not registered.</response>
    [HttpGet("mame")]
    [ProducesResponseType(typeof(RetroBat.Domain.Events.MameOutputEvent), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetMameOutputs()
    {
        var provider = _providers.OfType<MameOutputsProvider>().FirstOrDefault();
        if (provider == null)
        {
            return NotFound(new { message = "MAME outputs provider not registered" });
        }

        return Ok(provider.GetSnapshot());
    }

    /// <summary>
    /// Returns the current RetroArch wrapper runtime snapshot ingested from the local named pipe.
    /// </summary>
    /// <remarks>
    /// This endpoint exposes the consolidated runtime state read from <c>\\.\pipe\RetroBatArcadePipe</c>.
    /// It reflects the latest parsed wrapper signals, the resolved ROM/system context, and the current in-memory signal cache.
    /// </remarks>
    /// <response code="200">Current RetroArch wrapper runtime snapshot.</response>
    /// <response code="404">RetroArch wrapper provider is not registered.</response>
    [HttpGet("retroarch")]
    [ProducesResponseType(typeof(RetroBat.Domain.Events.RetroArchRuntimeSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetRetroArchOutputs()
    {
        var provider = _providers.OfType<RetroArchWrapperProvider>().FirstOrDefault();
        if (provider == null)
        {
            return NotFound(new { message = "RetroArch wrapper provider not registered" });
        }

        return Ok(provider.GetSnapshot());
    }

    /// <summary>
    /// Returns the resolved RetroArch wrapper RAM definition for the current game context.
    /// </summary>
    /// <remarks>
    /// The API resolves the current <c>resources/ram/&lt;system&gt;/&lt;rom&gt;.MEM</c> file and any matching <c>alias.json</c> entry.
    /// This endpoint describes the definition file the wrapper runtime should currently map against.
    /// </remarks>
    /// <response code="200">Resolved RetroArch RAM definition snapshot.</response>
    /// <response code="404">RetroArch wrapper provider is not registered.</response>
    [HttpGet("retroarch/definition")]
    [ProducesResponseType(typeof(RetroBat.Domain.Events.RetroArchDefinitionSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetRetroArchDefinition()
    {
        var provider = _providers.OfType<RetroArchWrapperProvider>().FirstOrDefault();
        if (provider == null)
        {
            return NotFound(new { message = "RetroArch wrapper provider not registered" });
        }

        return Ok(provider.GetDefinitionSnapshot());
    }

}
