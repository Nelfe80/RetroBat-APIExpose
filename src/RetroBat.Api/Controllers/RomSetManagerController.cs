using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Roms Manager")]
[Route("api/v1/rom-set-manager")]
public sealed class RomSetManagerController : ControllerBase
{
    private readonly RomSetManagerService _service;
    private readonly MediaRuntimeState _runtimeState;
    private readonly GameListImpactWarningService _gameListImpactWarningService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly InterfaceTextService _interfaceTextService;

    public RomSetManagerController(
        RomSetManagerService service,
        MediaRuntimeState runtimeState,
        GameListImpactWarningService gameListImpactWarningService,
        EmulationStationSettingsService settingsService,
        InterfaceTextService interfaceTextService)
    {
        _service = service;
        _runtimeState = runtimeState;
        _gameListImpactWarningService = gameListImpactWarningService;
        _settingsService = settingsService;
        _interfaceTextService = interfaceTextService;
    }

    /// <summary>
    /// Returns the effective ROM Set Manager options from appsettings and RetroBat menu overrides.
    /// </summary>
    [HttpGet("options")]
    public ActionResult<RomSetManagerOptionsSnapshot> GetOptions()
    {
        return Ok(_service.GetOptions());
    }

    /// <summary>
    /// Audits which gamelist entries would be hidden or restored by the ROM Set Manager.
    /// </summary>
    [HttpPost("audit")]
    public async Task<ActionResult<RomSetManagerApplyResponse>> Audit(
        [FromBody] RomSetManagerApplyRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _service.AuditAsync(request, cancellationToken));
    }

    /// <summary>
    /// Applies ROM Set Manager filters by writing APIExpose-owned hidden flags into gamelist.xml.
    /// </summary>
    [HttpPost("apply")]
    public async Task<ActionResult<RomSetManagerApplyResponse>> Apply(
        [FromBody] RomSetManagerApplyRequest request,
        CancellationToken cancellationToken)
    {
        request.DryRun = false;
        await _gameListImpactWarningService.WarnIfGameSelectedAsync(
            _interfaceTextService.Text("notification.gamelist_impact.romset_apply_subject", CurrentLanguage()),
            cancellationToken);
        QueueWorkflow(request, restore: false, "api-apply");
        return Ok(BuildQueuedResponse(request, restore: false));
    }

    /// <summary>
    /// Applies current ROM Set Manager filters immediately, then audits the resulting gamelists.
    /// </summary>
    [HttpPost("apply-current-and-check")]
    public async Task<ActionResult<RomSetManagerApplyCurrentCheckResponse>> ApplyCurrentAndCheck(
        [FromBody] RomSetManagerApplyCurrentCheckRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new RomSetManagerApplyCurrentCheckRequest();
        await _gameListImpactWarningService.WarnIfGameSelectedAsync(
            _interfaceTextService.Text("notification.gamelist_impact.romset_apply_subject", CurrentLanguage()),
            cancellationToken);

        var allSystems = request.AllSystems || string.IsNullOrWhiteSpace(request.SystemId);
        var applyRequest = new RomSetManagerApplyRequest
        {
            SystemId = allSystems ? null : request.SystemId?.Trim(),
            AllSystems = allSystems,
            DryRun = false,
            ReloadGames = request.ReloadGames,
            ClaimExistingHidden = request.ClaimExistingHidden
        };

        var apply = await _service.ApplyAsync(applyRequest, cancellationToken);
        var verification = await _service.AuditAsync(new RomSetManagerApplyRequest
        {
            SystemId = applyRequest.SystemId,
            AllSystems = applyRequest.AllSystems,
            DryRun = true,
            ReloadGames = false
        }, cancellationToken);

        var response = new RomSetManagerApplyCurrentCheckResponse
        {
            Coherent = verification.GamesToRestore == 0 && verification.Warnings.Count == 0,
            Apply = apply,
            Verification = verification
        };
        response.IgnoredSystems.AddRange(["ports", "retrobat", "screenshots"]);
        if (verification.GamesToRestore > 0)
        {
            response.Notes.Add($"{verification.GamesToRestore} APIExpose-owned entries would still be restored by the current filters.");
        }

        if (verification.Warnings.Count > 0)
        {
            response.Notes.AddRange(verification.Warnings);
        }

        response.Message = response.Coherent
            ? "Current Roms Manager filters applied and verified."
            : "Current Roms Manager filters applied, but verification found remaining differences.";
        return Ok(response);
    }

    /// <summary>
    /// Removes only hidden flags previously owned by the ROM Set Manager.
    /// </summary>
    [HttpPost("restore")]
    public async Task<ActionResult<RomSetManagerApplyResponse>> Restore(
        [FromBody] RomSetManagerApplyRequest request,
        CancellationToken cancellationToken)
    {
        await _gameListImpactWarningService.WarnIfGameSelectedAsync(
            _interfaceTextService.Text("notification.gamelist_impact.romset_restore_subject", CurrentLanguage()),
            cancellationToken);
        QueueWorkflow(request, restore: true, "api-restore");
        return Ok(BuildQueuedResponse(request, restore: true));
    }

    private void QueueWorkflow(RomSetManagerApplyRequest request, bool restore, string reason)
    {
        var scope = request.AllSystems ? "all" : "system";
        var systemId = request.AllSystems ? string.Empty : request.SystemId?.Trim() ?? string.Empty;
        _runtimeState.RequestRomSetManagerWorkflow(
            TimeSpan.FromSeconds(1),
            new RomSetManagerWorkflowRequest(restore, scope, systemId, reason));
    }

    private RomSetManagerApplyResponse BuildQueuedResponse(RomSetManagerApplyRequest request, bool restore)
    {
        var options = _service.GetOptions();
        var response = new RomSetManagerApplyResponse
        {
            DryRun = false,
            Restore = restore,
            Enabled = options.Enabled,
            Options = options,
            ReloadGamesRequested = true,
            Message = restore
                ? "Roms Manager restore queued. EmulationStation will be refreshed."
                : "Roms Manager apply queued. EmulationStation will be refreshed."
        };

        if (request.AllSystems)
        {
            response.Systems.Add("all");
        }
        else if (!string.IsNullOrWhiteSpace(request.SystemId))
        {
            response.Systems.Add(request.SystemId.Trim());
        }

        return response;
    }

    private string CurrentLanguage()
    {
        return _settingsService.GetScrapingSettings().Language;
    }
}

