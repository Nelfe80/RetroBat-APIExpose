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
    private readonly DataPackSyncService _dataPack;
    private readonly SelfUpdateService _selfUpdate;

    public MaintenanceController(
        InstallerDeploymentService installerDeploymentService,
        RetroArchWrapperDeploymentService wrapperDeploymentService,
        EmulationStationWatcherProvider emulationStationWatcherProvider,
        DataPackSyncService dataPack,
        SelfUpdateService selfUpdate)
    {
        _installerDeploymentService = installerDeploymentService;
        _wrapperDeploymentService = wrapperDeploymentService;
        _emulationStationWatcherProvider = emulationStationWatcherProvider;
        _dataPack = dataPack;
        _selfUpdate = selfUpdate;
    }

    /// <summary>Y a-t-il une version plus recente d'APIExpose ? Rien n'est telecharge.</summary>
    [HttpGet("update/check")]
    public async Task<ActionResult<SelfUpdateStatus>> CheckUpdate(CancellationToken cancellationToken)
    {
        var etat = await _selfUpdate.VerifierAsync(cancellationToken);
        return Ok(etat);
    }

    /// <summary>
    /// Prend la derniere version publiee : lance RetroBat.Api.Update.exe, qui arretera cette
    /// API, la remplacera et la relancera. Refuse pendant un jeu ou un replay (le champ `busy`
    /// dit pourquoi). `force=true` reapplique meme si la version est deja la.
    /// </summary>
    [HttpPost("update/apply")]
    public async Task<ActionResult<SelfUpdateStatus>> ApplyUpdate([FromQuery] bool force, CancellationToken cancellationToken)
    {
        var etat = await _selfUpdate.AppliquerAsync(force, cancellationToken);
        return Ok(etat);
    }

    /// <summary>Le bilan de la derniere synchronisation du Data Pack officiel.</summary>
    [HttpGet("datapack/status")]
    public ActionResult<object> DataPackStatus()
    {
        return Ok(new { last = _dataPack.Dernier });
    }

    /// <summary>
    /// Synchronise le Data Pack officiel maintenant (fichier par fichier depuis le depot,
    /// bases par systeme depuis la release). C'est ce que RetroBat.Api.Update.exe appelle
    /// apres avoir mis le programme a jour.
    /// </summary>
    [HttpPost("datapack/sync")]
    public async Task<ActionResult<DataPackSyncResult>> DataPackSync(CancellationToken cancellationToken)
    {
        var result = await _dataPack.SyncNowAsync(cancellationToken);
        return Ok(result);
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
