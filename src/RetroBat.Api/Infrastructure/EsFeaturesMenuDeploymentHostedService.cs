using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

public sealed class EsFeaturesMenuDeploymentHostedService : IHostedService
{
    private readonly EsFeaturesMenuDeploymentService _deploymentService;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<EsFeaturesMenuDeploymentHostedService> _logger;

    public EsFeaturesMenuDeploymentHostedService(
        EsFeaturesMenuDeploymentService deploymentService,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<EsFeaturesMenuDeploymentHostedService> logger)
    {
        _deploymentService = deploymentService;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.EsFeaturesMenu;
        _deploymentService.PrepareLogFilesOnStartup();

        if (!options.Enabled || !options.InstallOnStartup)
        {
            return;
        }

        try
        {
            var result = await _deploymentService.DeployAsync(options.DryRunOnStartup, cancellationToken);
            _logger.LogInformation(
                "ES features menu deployment completed. Changed={Changed}, LocaleChanged={LocaleChanged}, Installed={Installed}, Features={Features}, MenuEntries={MenuEntries}, Locales={Locales}, RemovedShared={RemovedShared}, RemovedGlobal={RemovedGlobal}, DryRun={DryRun}, Warnings={WarningCount}",
                result.Changed,
                result.LocaleChanged,
                result.Installed,
                result.InstalledFeatureCount,
                result.InstalledMenuEntryCount,
                result.InstalledLocaleCount,
                result.RemovedSharedFeatureCount,
                result.RemovedGlobalFeatureCount,
                result.DryRun,
                result.Warnings.Count);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "ES features menu deployment failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
