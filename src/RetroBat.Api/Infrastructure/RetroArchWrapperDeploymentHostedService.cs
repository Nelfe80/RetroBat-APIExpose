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
            // Sans deploiement automatique, personne ne dirait ce que vaut le wrapper : on
            // AUDITE quand meme, une fois, pour que la borne puisse l'annoncer. Un audit ne
            // touche a rien, il regarde.
            _ = Task.Run(async () =>
            {
                try { CabinetState.NoterWrapper(await _deploymentService.AuditAsync(_arret.Token).ConfigureAwait(false)); }
                catch (Exception ex) { _logger.LogDebug(ex, "RetroArch wrapper : audit d'etat impossible."); }
            }, CancellationToken.None);
            return Task.CompletedTask;
        }

        _ = Task.Run(() => DeployerAvecReprisesAsync(options, _arret.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Cadence lente, une fois les reprises rapides epuisees.</summary>
    public static readonly TimeSpan RepriseLente = TimeSpan.FromMinutes(5);

    /// <summary>
    /// L'attente avant la reprise suivante. Les <paramref name="maxRetries"/> premieres suivent
    /// l'intervalle configure ; ensuite, on continue a une cadence lente au lieu d'abandonner.
    /// Mesure du 15 septembre : abandonner apres 30 minutes laissait une borne ou l'on jouait
    /// juste apres une mise a jour avec l'ancien wrapper jusqu'au prochain redemarrage de l'API,
    /// parfois des jours, alors que la mise a jour avait bien pose le nouveau.
    /// </summary>
    public static TimeSpan DelaiAvantReprise(int essai, int maxRetries, TimeSpan intervalle) =>
        essai < Math.Max(0, maxRetries) ? intervalle : (intervalle > RepriseLente ? intervalle : RepriseLente);

    private async Task DeployerAvecReprisesAsync(
        ApiExposeOptions.RetroArchWrapperDeploymentOptions options,
        CancellationToken ct)
    {
        var intervalle = TimeSpan.FromSeconds(Math.Max(5, options.RetryIntervalSeconds));
        for (var essai = 0; ; essai++)
        {
            try
            {
                if (ct.IsCancellationRequested) return;
                var result = await _deploymentService.DeployAsync(options.DryRunOnStartup, ct).ConfigureAwait(false);
                // Ce que la borne dira d'elle-meme a la plateforme : une borne dont le wrapper
                // n'enveloppe aucun coeur ne mesurera aucun score, et personne ne le voyait.
                CabinetState.NoterWrapper(result);
                if (result.SkippedBecauseRetroArchRunning)
                {
                    var attente = DelaiAvantReprise(essai, options.MaxRetries, intervalle);
                    if (essai < options.MaxRetries)
                    {
                        _logger.LogInformation(
                            "RetroArch wrapper startup deployment postponed (RetroArch is running); retry in {Seconds}s ({Attempt}/{Max}).",
                            (int) attente.TotalSeconds, essai + 1, options.MaxRetries);
                    }
                    else if (essai == options.MaxRetries)
                    {
                        _logger.LogInformation(
                            "RetroArch wrapper startup deployment still postponed (RetroArch keeps running); checking every {Minutes} min until it stops.",
                            (int) attente.TotalMinutes);
                    }

                    await Task.Delay(attente, ct).ConfigureAwait(false);
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
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arret.Cancel();
        return Task.CompletedTask;
    }
}
