using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Providers.EmulationStation;

namespace RetroBat.Api.Controllers;

[ApiController]
[Tags("Internal & Prototype")]
[Route("api/v1/[controller]")]
public class MaintenanceController : ControllerBase
{
    private readonly InstallerDeploymentService _installerDeploymentService;
    private readonly RetroArchWrapperDeploymentService _wrapperDeploymentService;
    private readonly EmulationStationWatcherProvider _emulationStationWatcherProvider;

    public MaintenanceController(
        InstallerDeploymentService installerDeploymentService,
        RetroArchWrapperDeploymentService wrapperDeploymentService,
        EmulationStationWatcherProvider emulationStationWatcherProvider)
    {
        _installerDeploymentService = installerDeploymentService;
        _wrapperDeploymentService = wrapperDeploymentService;
        _emulationStationWatcherProvider = emulationStationWatcherProvider;
    }

    /// <summary>Audits the installer deployment without writing anything.</summary>
    [HttpGet("installer/audit")]
    public async Task<ActionResult<InstallerDeploymentResult>> AuditInstaller(CancellationToken cancellationToken)
    {
        var result = await _installerDeploymentService.AuditAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>Deploys the installer assets (set dryRun=true to preview).</summary>
    [HttpPost("installer/deploy")]
    public async Task<ActionResult<InstallerDeploymentResult>> DeployInstaller(
        [FromBody] InstallerDeployRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _installerDeploymentService.DeployAsync(request?.DryRun ?? false, cancellationToken);
        return Ok(result);
    }

    /// <summary>Audits the RetroArch wrapper deployment without writing anything.</summary>
    [HttpGet("retroarch-wrapper/audit")]
    public async Task<ActionResult<RetroArchWrapperDeploymentResult>> AuditRetroArchWrapper(CancellationToken cancellationToken)
    {
        var result = await _wrapperDeploymentService.AuditAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>Deploys the RetroArch wrapper (set dryRun=true to preview).</summary>
    [HttpPost("retroarch-wrapper/deploy")]
    public async Task<ActionResult<RetroArchWrapperDeploymentResult>> DeployRetroArchWrapper(
        [FromBody] RetroArchWrapperDeployRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _wrapperDeploymentService.DeployAsync(request?.DryRun ?? false, cancellationToken);
        return Ok(result);
    }

    /// <summary>Snapshot of the EmulationStation watcher caches (diagnostic).</summary>
    [HttpGet("emulationstation/cache")]
    public ActionResult<object> GetEmulationStationCache()
    {
        return Ok(_emulationStationWatcherProvider.GetCacheSnapshot());
    }

    /// <summary>Clears the EmulationStation watcher caches (forces a cold re-read).</summary>
    [HttpPost("emulationstation/cache/clear")]
    public ActionResult<object> ClearEmulationStationCache()
    {
        return Ok(_emulationStationWatcherProvider.ClearCaches("maintenance endpoint"));
    }
}
