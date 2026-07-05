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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value.RetroArchWrapperDeployment;
        if (!options.Enabled || !_runtimeOptions.IsRetroArchWrapperEnabled())
        {
            return;
        }

        try
        {
            var result = await _deploymentService.DeployAsync(options.DryRunOnStartup, cancellationToken);
            _logger.LogInformation(
                "RetroArch wrapper startup deployment completed. Checked={CheckedCores}, Pending={PendingDeployments}, Deployed={DeployedCores}, DryRun={DryRun}",
                result.CheckedCores,
                result.PendingDeployments,
                result.DeployedCores,
                result.DryRun);
        }
        catch (Exception exception)
        {
            // A deployment issue must not prevent the API from starting; the Swagger endpoint can be used to diagnose.
            _logger.LogWarning(exception, "RetroArch wrapper startup deployment failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
