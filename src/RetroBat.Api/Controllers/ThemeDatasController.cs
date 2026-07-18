using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Themes & Collections")]
[Route("api/v1/theme-datas")]
public sealed class ThemeDatasController : ControllerBase
{
    private readonly DatasThemeExposeService _datasThemeExposeService;

    public ThemeDatasController(DatasThemeExposeService datasThemeExposeService)
    {
        _datasThemeExposeService = datasThemeExposeService;
    }

    /// <summary>
    /// Returns the generated consolidated theme XML for the current context.
    /// </summary>
    [HttpGet("current/xml")]
    [Produces("application/xml")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public IActionResult GetCurrentXml([FromQuery] string? systemId = null, [FromQuery] string? rom = null, [FromQuery] string? core = null)
    {
        var xml = string.IsNullOrWhiteSpace(systemId)
            ? _datasThemeExposeService.GetCurrentThemeXml()
            : _datasThemeExposeService.GetThemeXml(systemId, rom, core);
        return Content(xml, "application/xml; charset=utf-8");
    }

    /// <summary>
    /// Exports the current context to the consolidated `.gameinfos` theme XML.
    /// </summary>
    [HttpPost("current/export")]
    [ProducesResponseType(typeof(DatasThemeFileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportCurrent([FromQuery] string? systemId = null, [FromQuery] string? rom = null)
    {
        var result = string.IsNullOrWhiteSpace(systemId)
            ? await _datasThemeExposeService.ExportCurrentAsync(HttpContext.RequestAborted)
            : await _datasThemeExposeService.ExportAsync(systemId, rom, HttpContext.RequestAborted);
        return Ok(result);
    }

    /// <summary>
    /// Regenerates all consolidated `.gameinfos` theme XML files from dynpanels.
    /// </summary>
    [HttpPost("export")]
    [ProducesResponseType(typeof(DatasThemeExportResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportAll()
    {
        var result = await _datasThemeExposeService.ExportAllAsync(HttpContext.RequestAborted);
        return Ok(result);
    }

    /// <summary>
    /// Audits generated `.gameinfos` files, markers and legacy theme folders.
    /// </summary>
    [HttpGet("audit")]
    [ProducesResponseType(typeof(DatasThemeAuditResult), StatusCodes.Status200OK)]
    public IActionResult Audit()
    {
        return Ok(_datasThemeExposeService.Audit());
    }
}
