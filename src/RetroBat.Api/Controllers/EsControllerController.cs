using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

[ApiController]
[Route("api/v1/es/controller")]
public class EsControllerController : ControllerBase
{
    private readonly EsControllerService _service;

    public EsControllerController(EsControllerService service)
    {
        _service = service;
    }

    /// <summary>
    /// Returns EmulationStation controller module status and the latest known APIExpose selection.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<EsControllerStatus> GetStatus()
    {
        return Ok(_service.GetStatus());
    }

    /// <summary>
    /// Sends one controller input one or more times.
    /// </summary>
    [HttpPost("tap")]
    public async Task<ActionResult<EsControllerActionResult>> Tap(
        [FromBody] EsControllerTapRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _service.TapAsync(request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// Sends an ordered sequence of controller inputs.
    /// </summary>
    [HttpPost("combo")]
    public async Task<ActionResult<EsControllerActionResult>> Combo(
        [FromBody] EsControllerComboRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _service.ComboAsync(request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// Releases all inputs and cancels the active navigation command.
    /// </summary>
    [HttpPost("stop")]
    public async Task<ActionResult<EsControllerActionResult>> Stop()
    {
        return Ok(await _service.StopAsync());
    }

    /// <summary>
    /// Probes the current EmulationStation view and detects the safest game navigation axis.
    /// </summary>
    [HttpPost("probe-view")]
    public async Task<ActionResult<EsControllerProbeViewResult>> ProbeView(
        [FromBody] EsControllerProbeViewRequest? request,
        CancellationToken cancellationToken)
    {
        return Ok(await _service.ProbeViewAsync(request ?? new EsControllerProbeViewRequest(), cancellationToken));
    }

    /// <summary>
    /// Sends a targeted right click to the EmulationStation window, useful to close some overlay menus.
    /// </summary>
    [HttpPost("right-click")]
    public async Task<ActionResult<EsControllerActionResult>> RightClick(
        [FromBody] EsControllerRightClickRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _service.RightClickAsync(request ?? new EsControllerRightClickRequest(), cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// Captures the current APIExpose selection for a later restore-selection call.
    /// </summary>
    [HttpPost("capture-selection")]
    public ActionResult<EsSelectionSnapshot> CaptureSelection()
    {
        return Ok(_service.CaptureSelection("manual"));
    }

    /// <summary>
    /// Returns the latest selection captured by APIExpose.
    /// </summary>
    [HttpGet("last-selection")]
    public ActionResult<EsSelectionSnapshot> GetLastSelection()
    {
        var selection = _service.GetLastSelection();
        if (selection == null)
        {
            return NotFound(new { message = "No selection captured yet." });
        }

        return Ok(selection);
    }

    /// <summary>
    /// Moves EmulationStation to a system. It can optionally enter the system gamelist.
    /// </summary>
    [HttpPost("goto-system")]
    public async Task<ActionResult<EsControllerActionResult>> GotoSystem(
        [FromBody] EsControllerGotoSystemRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _service.GotoSystemAsync(request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// Moves EmulationStation to a game inside a system without launching it.
    /// </summary>
    [HttpPost("goto-game")]
    public async Task<ActionResult<EsControllerActionResult>> GotoGame(
        [FromBody] EsControllerGotoGameRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _service.GotoGameAsync(request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// Restores the latest captured selection, or a provided target selection, without launching a game.
    /// </summary>
    [HttpPost("restore-selection")]
    public async Task<ActionResult<EsControllerActionResult>> RestoreSelection(
        [FromBody] EsControllerRestoreSelectionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _service.RestoreSelectionAsync(request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// Requests an immediate EmulationStation reloadgames and optionally restores the selection.
    /// </summary>
    [HttpPost("reloadgames")]
    public async Task<ActionResult<EsControllerReloadGamesResult>> ReloadGames(
        [FromBody] EsControllerReloadGamesRequest? request,
        CancellationToken cancellationToken)
    {
        return Ok(await _service.ReloadGamesAsync(request ?? new EsControllerReloadGamesRequest(), cancellationToken));
    }

    /// <summary>
    /// Audits the controller-related EmulationStation configuration files.
    /// </summary>
    [HttpGet("config/audit")]
    public ActionResult<EsControllerConfigAuditResult> AuditConfig()
    {
        return Ok(_service.AuditConfig());
    }

    /// <summary>
    /// Prepares controller config repair. Writes are intentionally dry-run by default.
    /// </summary>
    [HttpPost("config/repair")]
    public ActionResult<EsControllerConfigAuditResult> RepairConfig([FromBody] EsControllerConfigRepairRequest? request)
    {
        return Ok(_service.RepairConfig(request?.DryRun ?? true));
    }

    private ActionResult<EsControllerActionResult> ToActionResult(EsControllerActionResult result)
    {
        if (result.Success)
        {
            return Ok(result);
        }

        return result.Reason switch
        {
            "missing_system" or "invalid_input" or "empty_sequence" => BadRequest(result),
            "system_not_found" or "game_not_found" or "no_selection" => NotFound(result),
            "disabled" or "backend_not_ready" or "input_failed" or "current_system_unknown" or "current_game_unknown" or "systems_unavailable" or "games_unavailable" or "game_running" or "ambiguous_view" or "unknown_view" or "verification_timeout" or "system_navigation_stalled" or "system_navigation_enter_mismatch" or "page_navigation_left_system" or "page_navigation_stalled" or "page_navigation_correction_too_large" => StatusCode(StatusCodes.Status409Conflict, result),
            _ => StatusCode(StatusCodes.Status500InternalServerError, result)
        };
    }
}
