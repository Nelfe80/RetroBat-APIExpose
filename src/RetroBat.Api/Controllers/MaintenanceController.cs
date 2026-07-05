using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Providers.EmulationStation;

namespace RetroBat.Api.Controllers;

[ApiController]
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

    [HttpGet("installer/audit")]
    public async Task<ActionResult<InstallerDeploymentResult>> AuditInstaller(CancellationToken cancellationToken)
    {
        var result = await _installerDeploymentService.AuditAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("installer/deploy")]
    public async Task<ActionResult<InstallerDeploymentResult>> DeployInstaller(
        [FromBody] InstallerDeployRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _installerDeploymentService.DeployAsync(request?.DryRun ?? false, cancellationToken);
        return Ok(result);
    }

    [HttpGet("retroarch-wrapper/audit")]
    public async Task<ActionResult<RetroArchWrapperDeploymentResult>> AuditRetroArchWrapper(CancellationToken cancellationToken)
    {
        var result = await _wrapperDeploymentService.AuditAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("retroarch-wrapper/deploy")]
    public async Task<ActionResult<RetroArchWrapperDeploymentResult>> DeployRetroArchWrapper(
        [FromBody] RetroArchWrapperDeployRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _wrapperDeploymentService.DeployAsync(request?.DryRun ?? false, cancellationToken);
        return Ok(result);
    }

    [HttpGet("emulationstation/cache")]
    public ActionResult<object> GetEmulationStationCache()
    {
        return Ok(_emulationStationWatcherProvider.GetCacheSnapshot());
    }

    [HttpPost("emulationstation/cache/clear")]
    public ActionResult<object> ClearEmulationStationCache()
    {
        return Ok(_emulationStationWatcherProvider.ClearCaches("maintenance endpoint"));
    }
}
