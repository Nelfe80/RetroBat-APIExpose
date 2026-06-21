using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

public class InstallerDeploymentHostedService : IHostedService
{
    private readonly InstallerDeploymentService _deploymentService;
    private readonly IOptions<ApiExposeOptions> _options;
    private readonly ILogger<InstallerDeploymentHostedService> _logger;

    public InstallerDeploymentHostedService(
        InstallerDeploymentService deploymentService,
        IOptions<ApiExposeOptions> options,
        ILogger<InstallerDeploymentHostedService> logger)
    {
        _deploymentService = deploymentService;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value.InstallerDeployment;
        if (!options.Enabled)
        {
            return;
        }

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
