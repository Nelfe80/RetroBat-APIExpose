using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

public class RetroArchWrapperDeploymentHostedService : IHostedService
{
    private readonly RetroArchWrapperDeploymentService _deploymentService;
    private readonly IOptions<ApiExposeOptions> _options;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ILogger<RetroArchWrapperDeploymentHostedService> _logger;

    public RetroArchWrapperDeploymentHostedService(
        RetroArchWrapperDeploymentService deploymentService,
        IOptions<ApiExposeOptions> options,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ILogger<RetroArchWrapperDeploymentHostedService> logger)
    {
        _deploymentService = deploymentService;
        _options = options;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    private readonly CancellationTokenSource _arret = new();

    /// <summary>
    /// Au demarrage, le wrapper se pose (ou se rafraichit) sur les cores. En ARRIERE-PLAN :
    /// le watcher EmulationStation garde la priorite, et une borne qui vient d'etre
    /// installee n'a pas a attendre 157 copies avant de repondre. Si RetroArch tourne deja
    /// (le joueur a lance un jeu avant que l'API ne soit la), on reessaie plus tard plutot
    /// que d'attendre le prochain demarrage : c'est a l'installation que ca compte.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value.RetroArchWrapperDeployment;
        if (!options.AutoDeploy || !_runtimeOptions.IsRetroArchWrapperEnabled())
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(() => DeployerAvecReprisesAsync(options, _arret.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task DeployerAvecReprisesAsync(
        ApiExposeOptions.RetroArchWrapperDeploymentOptions options,
        CancellationToken ct)
    {
        var intervalle = TimeSpan.FromSeconds(Math.Max(5, options.RetryIntervalSeconds));
        for (var essai = 0; essai <= Math.Max(0, options.MaxRetries); essai++)
        {
            try
            {
                if (ct.IsCancellationRequested) return;
                var result = await _deploymentService.DeployAsync(options.DryRunOnStartup, ct).ConfigureAwait(false);
                if (result.SkippedBecauseRetroArchRunning)
                {
                    _logger.LogInformation(
                        "RetroArch wrapper startup deployment postponed (RetroArch is running); retry in {Seconds}s ({Attempt}/{Max}).",
                        (int) intervalle.TotalSeconds, essai + 1, options.MaxRetries);
                    await Task.Delay(intervalle, ct).ConfigureAwait(false);
                    continue;
                }

                _logger.LogInformation(
                    "RetroArch wrapper startup deployment completed. Checked={CheckedCores}, Pending={PendingDeployments}, Deployed={DeployedCores}, Stale={StaleWrappers}, Refreshed={RefreshedCores}, DryRun={DryRun}",
                    result.CheckedCores,
                    result.PendingDeployments,
                    result.DeployedCores,
                    result.StaleWrappers,
                    result.RefreshedCores,
                    result.DryRun);
                foreach (var warning in result.Warnings)
                {
                    _logger.LogWarning("RetroArch wrapper deployment: {Warning}", warning);
                }
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception)
            {
                // A deployment issue must not prevent the API from running; the maintenance endpoints can be used to diagnose.
                _logger.LogWarning(exception, "RetroArch wrapper startup deployment failed.");
                return;
            }
        }
        _logger.LogWarning("RetroArch wrapper startup deployment gave up: RetroArch kept running.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arret.Cancel();
        return Task.CompletedTask;
    }
}
