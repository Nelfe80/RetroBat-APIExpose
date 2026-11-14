using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

public class InstallerDeploymentHostedService : IHostedService
{
    private readonly InstallerDeploymentService _deploymentService;
    private readonly IOptions<ApiExposeOptions> _options;
    private readonly ILogger<InstallerDeploymentHostedService> _logger;
    private readonly IHostApplicationLifetime? _vie;

    public InstallerDeploymentHostedService(
        InstallerDeploymentService deploymentService,
        IOptions<ApiExposeOptions> options,
        ILogger<InstallerDeploymentHostedService> logger,
        IHostApplicationLifetime? vie = null)
    {
        _deploymentService = deploymentService;
        _options = options;
        _logger = logger;
        _vie = vie;
    }

    /// <summary>Tache du dernier deploiement lance (tests).</summary>
    internal Task? Deploiement { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value.InstallerDeployment;
        if (!options.Enabled)
        {
            return Task.CompletedTask;
        }

        // (brief 13/11, PERF-API-01) 3 425 fichiers verifies pour 0 change a chaque demarrage (5,9 s le 14/11) : la
        // verification ne retarde plus l'ecoute d'ES, elle part une fois l'hote demarre.
        var termine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Deploiement = termine.Task;
        ApresLeDemarrage.Lancer(_vie, _logger, "le deploiement de l'installeur", async arret =>
        {
            try { await DeployerAsync(options, arret).ConfigureAwait(false); }
            finally { termine.TrySetResult(); }
        });
        return Task.CompletedTask;
    }

    private async Task DeployerAsync(ApiExposeOptions.InstallerDeploymentOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _deploymentService.DeployAsync(options.DryRunOnStartup, cancellationToken);
            _logger.LogInformation(
                "Installer deployment completed. Checked={CheckedFiles}, Missing={MissingFiles}, Changed={ChangedFiles}, Copied={CopiedFiles}, DryRun={DryRun}",
                result.CheckedFiles,
                result.MissingFiles,
                result.ChangedFiles,
                result.CopiedFiles,
                result.DryRun);
        }
        catch (Exception exception)
        {
            // Installation helpers should never prevent the API from starting.
            _logger.LogWarning(exception, "Installer deployment failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
