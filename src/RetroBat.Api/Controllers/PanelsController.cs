using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using System.Text.Json;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PanelsController : ControllerBase
{
    private readonly PanelsCatalogService _panels;
    private readonly ControlFilesCatalogService _controlFiles;
    private readonly PanelDefinitionProjectionService _panelProjection;
    private readonly IEventBus _eventBus;
    private readonly PanelRemapExportService _remapExport;
    private readonly MameCfgDeployService _mameCfgDeploy;

    public PanelsController(
        PanelsCatalogService panels,
        ControlFilesCatalogService controlFiles,
        PanelDefinitionProjectionService panelProjection,
        IEventBus eventBus,
        PanelRemapExportService remapExport,
        MameCfgDeployService mameCfgDeploy)
    {
        _panels = panels;
        _controlFiles = controlFiles;
        _panelProjection = panelProjection;
        _eventBus = eventBus;
        _remapExport = remapExport;
        _mameCfgDeploy = mameCfgDeploy;
    }

    /// <summary>
    /// Lists the panel definitions available in the API catalog.
    /// </summary>
    /// <remarks>
    /// This endpoint exposes the available `system`, `game`, and `core` panel definitions without resolving a current context.
    /// It is useful for clients that want to browse what the API can expose before requesting a specific panel.
    /// </remarks>
    /// <param name="scope">Optional scope filter: `system`, `game`, or `core`.</param>
    /// <param name="systemId">Optional system filter.</param>
    /// <param name="rom">Optional game/rom slug filter.</param>
    /// <param name="core">Optional core filter.</param>
    /// <response code="200">Panel catalog entries.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PanelCatalogEntrySnapshot>), StatusCodes.Status200OK)]
    public IActionResult List([FromQuery] string? scope = null, [FromQuery] string? systemId = null, [FromQuery] string? rom = null, [FromQuery] string? core = null)
    {
        return Ok(_panels.ListPanels(scope, systemId, rom, core));
    }

    /// <summary>
    /// Returns the resolved system-level panel definition for a specific system.
    /// </summary>
    /// <remarks>
    /// This route targets the canonical system panel, which is the normal base for consoles and micro systems.
    /// </remarks>
    /// <response code="200">Resolved system panel snapshot.</response>
    /// <response code="404">No panel data could be resolved for the requested system.</response>
    [HttpGet("system/{systemId}")]
    [ProducesResponseType(typeof(PanelThemeSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSystem(string systemId, [FromQuery] string? activeLayout = null)
    {
        var snapshot = _panels.GetSystemSnapshot(systemId, activeLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return NotFound(new { message = $"No panel data resolved for system '{systemId}'." });
        }

        return Ok(snapshot);
    }

    /// <summary>
    /// Returns the resolved panel definition for a specific game.
    /// </summary>
    /// <remarks>
    /// Resolution still follows the project rule: game panel first when available, then system panel fallback.
    /// This is especially useful for arcade games while still allowing console game-specific overrides.
    /// </remarks>
    /// <response code="200">Resolved game panel snapshot.</response>
    /// <response code="404">No panel data could be resolved for the requested game.</response>
    [HttpGet("game/{systemId}/{rom}")]
    [ProducesResponseType(typeof(PanelThemeSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetGame(string systemId, string rom, [FromQuery] string? core = null, [FromQuery] string? activeLayout = null)
    {
        var snapshot = _panels.GetGameSnapshot(systemId, rom, core, activeLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return NotFound(new { message = $"No panel data resolved for game '{systemId}/{rom}'." });
        }

        return Ok(snapshot);
    }

    /// <summary>
    /// Returns the active panel definition projected for plugin consumption.
    /// </summary>
    /// <remarks>
    /// This keeps the raw dynpanel richness that the theme snapshot intentionally flattens:
    /// physical slots, inputs, outputs, axes, resolved output input refs, and a read-only export plan.
    /// </remarks>
    /// <response code="200">Projected panel definition.</response>
    /// <response code="404">No panel data could be resolved for the requested game.</response>
    [HttpGet("game/{systemId}/{rom}/definition")]
    [ProducesResponseType(typeof(PanelDefinitionProjection), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetGameDefinition(string systemId, string rom, [FromQuery] string? core = null, [FromQuery] string? activeLayout = null)
    {
        var snapshot = _panels.GetGameSnapshot(systemId, rom, core, activeLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return NotFound(new { message = $"No panel data resolved for game '{systemId}/{rom}'." });
        }

        return Ok(_panelProjection.Build(snapshot, ResolveActiveLayout(snapshot)));
    }

    /// <summary>
    /// Returns the generated control files expected for a ROM.
    /// </summary>
    /// <remarks>
    /// Entries are returned even when the local copy is missing, so a panel plugin can generate or deploy the missing files.
    /// </remarks>
    /// <response code="200">Known control file slots for the requested ROM.</response>
    [HttpGet("controls/{rom}")]
    [ProducesResponseType(typeof(PanelControlFilesSnapshot), StatusCodes.Status200OK)]
    public IActionResult GetControlFiles(string rom)
    {
        return Ok(_controlFiles.GetForRom(rom));
    }

    /// <summary>
    /// Downloads a generated control file from `resources/controls`.
    /// </summary>
    /// <response code="200">Control file content.</response>
    /// <response code="404">The requested control file is not available in `resources/controls`.</response>
    [HttpGet("controls/{rom}/files/{id}/content")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DownloadControlFile(string rom, string id)
    {
        if (!_controlFiles.TryGetFile(rom, id, out var file))
        {
            return NotFound(new { message = $"Control file '{id}' is not available for ROM '{rom}'." });
        }

        return PhysicalFile(file.ApiFilePath, GetControlFileContentType(file.FileName), file.FileName);
    }

    /// <summary>
    /// Returns the resolved panel snapshot for the current game/system context.
    /// </summary>
    /// <remarks>
    /// Resolution follows the current `.panels` strategy:
    /// game panel first when available, then system panel fallback.
    /// This is especially useful for arcade games, while consoles and micros usually resolve from the system scope.
    /// </remarks>
    /// <response code="200">Current resolved panel snapshot.</response>
    /// <response code="404">No panel data could be resolved for the current context.</response>
    [HttpGet("current")]
    [ProducesResponseType(typeof(PanelThemeSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetCurrent([FromQuery] string? systemId = null, [FromQuery] string? rom = null, [FromQuery] string? core = null, [FromQuery] string? activeLayout = null)
    {
        var snapshot = string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(rom) && string.IsNullOrWhiteSpace(core)
            ? _panels.GetCurrentSnapshot()
            : _panels.GetSnapshot(systemId, rom, core, activeLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return NotFound(new { message = "No panel data resolved for the current context." });
        }

        return Ok(snapshot);
    }

    /// <summary>
    /// Returns the current active panel definition projected for plugin consumption.
    /// </summary>
    /// <response code="200">Projected panel definition.</response>
    /// <response code="404">No panel data could be resolved for the current context.</response>
    [HttpGet("current/definition")]
    [ProducesResponseType(typeof(PanelDefinitionProjection), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetCurrentDefinition([FromQuery] string? systemId = null, [FromQuery] string? rom = null, [FromQuery] string? core = null, [FromQuery] string? activeLayout = null)
    {
        var snapshot = string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(rom) && string.IsNullOrWhiteSpace(core)
            ? _panels.GetCurrentSnapshot()
            : _panels.GetSnapshot(systemId, rom, core, activeLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return NotFound(new { message = "No panel data resolved for the current context." });
        }

        return Ok(_panelProjection.Build(snapshot, ResolveActiveLayout(snapshot)));
    }

    /// <summary>
    /// Publishes a transient panel preview to `/ws/panel` without changing the current ES context.
    /// </summary>
    /// <remarks>
    /// This is used by `panel_curator_ultimate.py` so the active LedManager instance receives
    /// the preview through the normal APIExpose WebSocket pipeline. The payload is wrapped as
    /// a `panel.state` event and intentionally does not need a monotonic sequence.
    /// </remarks>
    /// <response code="200">Preview event published.</response>
    [HttpPost("preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> PublishPreview([FromBody] JsonElement payload)
    {
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "panel.state",
            NodeId = "panel-curator",
            Payload = payload.Clone()
        });

        return Ok(new
        {
            ok = true,
            type = "panel.state",
            stream = "panel"
        });
    }

    /// <summary>
    /// Returns the generated theme XML for the current panel context.
    /// </summary>
    /// <remarks>
    /// The XML contains multiple layouts in a single document so a theme can select the active layout when that information is available.
    /// </remarks>
    /// <response code="200">Generated panel theme XML.</response>
    /// <response code="404">No panel data could be resolved for the current context.</response>
    [HttpGet("current/theme-xml")]
    [Produces("application/xml")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetCurrentThemeXml([FromQuery] string? systemId = null, [FromQuery] string? rom = null, [FromQuery] string? core = null, [FromQuery] string? activeLayout = null)
    {
        var snapshot = string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(rom) && string.IsNullOrWhiteSpace(core)
            ? _panels.GetCurrentSnapshot()
            : _panels.GetSnapshot(systemId, rom, core, activeLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return NotFound(new { message = "No panel data resolved for the current context." });
        }

        var xml = string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(rom) && string.IsNullOrWhiteSpace(core)
            ? _panels.GetCurrentThemeXml()
            : _panels.GetThemeXml(systemId, rom, core, activeLayout);
        return Content(xml, "application/xml; charset=utf-8");
    }

    /// <summary>
    /// Legacy endpoint. Panel exports now live in Themes Manager `.gameinfos`.
    /// </summary>
    /// <remarks>
    /// Use `POST /api/v1/theme-datas/current/export` or `POST /api/v1/theme-datas/export`.
    /// This endpoint no longer writes `.panels`.
    /// </remarks>
    /// <response code="410">`.panels` export is deprecated.</response>
    /// <response code="404">No panel data could be resolved for the requested context.</response>
    [HttpPost("current/export-theme-xml")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportCurrentThemeXml([FromQuery] string? systemId = null, [FromQuery] string? rom = null, [FromQuery] string? core = null, [FromQuery] string? activeLayout = null)
    {
        var snapshot = string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(rom) && string.IsNullOrWhiteSpace(core)
            ? _panels.GetCurrentSnapshot()
            : _panels.GetSnapshot(systemId, rom, core, activeLayout);
        if (snapshot.Layouts.Count == 0)
        {
            return NotFound(new { message = "No panel data resolved for the current context." });
        }

        await Task.CompletedTask;
        return StatusCode(StatusCodes.Status410Gone, new
        {
            message = "Legacy .panels export is deprecated. Use /api/v1/theme-datas/current/export for .gameinfos.",
            snapshot.SystemId,
            snapshot.Rom,
            snapshot.ActiveLayoutId
        });
    }

    /// <summary>
    /// Regenerates the content-directory RetroArch remaps: one system, or every
    /// system with a dynpanel when `system` is omitted.
    /// </summary>
    /// <remarks>
    /// Push counterpart of the event-driven generation (LedManagerSetup "Contrôles").
    /// Existing guards apply: marker + ownership registry, .bak before rewrite,
    /// user files never touched.
    /// </remarks>
    /// <response code="200">Deployment report (written / up-to-date / skipped per system).</response>
    [HttpPost("controls/remaps/deploy")]
    [ProducesResponseType(typeof(PanelRemapExportService.RemapDeployReport), StatusCodes.Status200OK)]
    public IActionResult DeployRemaps([FromQuery] string? system = null)
    {
        return Ok(_remapExport.DeployRemaps(system));
    }

    /// <summary>
    /// Merge-deploys the curated MAME cfg pack: one rom, or the whole pack when
    /// `rom` is omitted.
    /// </summary>
    /// <remarks>
    /// Only the pack's input ports are merged; MAME state (counters, DIP, mixer) is
    /// kept and manual binds are preserved with the pack forms OR-appended. Refused
    /// while MAME runs (it rewrites every cfg at exit).
    /// </remarks>
    /// <response code="200">Deployment report (written / merged / up-to-date / failed).</response>
    /// <response code="409">MAME is running.</response>
    [HttpPost("controls/mamecfg/deploy")]
    [ProducesResponseType(typeof(MameCfgDeployService.Report), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult DeployMameCfg([FromQuery] string? rom = null, [FromQuery] int offset = 0, [FromQuery] int limit = 0)
    {
        if (_mameCfgDeploy.IsMameRunning())
        {
            return Conflict(new { message = "MAME is running: close it before deploying (it rewrites cfg files at exit)." });
        }

        return Ok(_mameCfgDeploy.Deploy(rom, offset, limit));
    }

    /// <summary>
    /// Current wiring of a game's deployed MAME cfg, expressed in physical panel
    /// buttons (inverse cartography): { "P1_BUTTON1": [1, 2, 6, 8], … }.
    /// </summary>
    /// <response code="200">Wiring map; empty when the game has no deployed cfg.</response>
    [HttpGet("controls/mamecfg/current")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult CurrentMameCfgWiring([FromQuery] string rom)
        => Ok(new { rom, wiring = _mameCfgDeploy.CurrentWiring(rom) });

    private static string GetControlFileContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".cfg" => "application/xml",
            ".xml" => "application/xml",
            ".rmp" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    private static PanelThemeLayoutSnapshot? ResolveActiveLayout(PanelThemeSnapshot snapshot)
    {
        return snapshot.Layouts.FirstOrDefault(layout =>
            string.Equals(layout.Id, snapshot.ActiveLayoutId, StringComparison.OrdinalIgnoreCase)) ??
            snapshot.Layouts.FirstOrDefault();
    }
}
