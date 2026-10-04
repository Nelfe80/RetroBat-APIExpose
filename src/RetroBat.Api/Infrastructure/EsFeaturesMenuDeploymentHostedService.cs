using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Ecrit es_features_apiexpose.cfg au demarrage, et le reecrit quand la langue d'ES change : ses
/// libelles sont deja traduits (voir EsFeaturesAnnexe). ES ne lit ce fichier qu'a son demarrage, et
/// changer de langue le fait redemarrer : il trouve alors le fichier dans la nouvelle langue.
/// </summary>
public sealed class EsFeaturesMenuDeploymentHostedService : IHostedService, IDisposable
{
    private readonly EsFeaturesMenuDeploymentService _deploymentService;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<EsFeaturesMenuDeploymentHostedService> _logger;
    private readonly RetroBat.Domain.Interfaces.IEsSettingsChangeBus? _reglages;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private IDisposable? _abonnement;
    private string _langueEcrite = "";

    public EsFeaturesMenuDeploymentHostedService(
        EsFeaturesMenuDeploymentService deploymentService,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<EsFeaturesMenuDeploymentHostedService> logger,
        RetroBat.Domain.Interfaces.IEsSettingsChangeBus? reglages = null)
    {
        _deploymentService = deploymentService;
        _options = options;
        _logger = logger;
        _reglages = reglages;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.EsFeaturesMenu;
        _deploymentService.PrepareLogFilesOnStartup();

        if (!options.Enabled || !options.InstallOnStartup)
        {
            return;
        }

        _abonnement = _reglages?.Subscribe((_, token) => SuivreLaLangueAsync(token));

        try
        {
            _langueEcrite = _deploymentService.LangueDES();
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

    /// <summary>es_settings.cfg a change : si c'est la langue, l'annexe est reecrite dans la nouvelle.</summary>
    private async Task SuivreLaLangueAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.EsFeaturesMenu;
        if (!options.Enabled) return;
        await _porte.WaitAsync(cancellationToken);
        try
        {
            var langue = _deploymentService.LangueDES();
            if (string.Equals(langue, _langueEcrite, StringComparison.OrdinalIgnoreCase)) return;
            _langueEcrite = langue;
            var result = await _deploymentService.DeployAsync(dryRun: false, cancellationToken);
            _logger.LogInformation("ES features : langue d'ES passee a {Langue}, {Annexe} reecrit (Changed={Changed}).",
                langue, EsFeaturesAnnexe.NomDuFichier, result.Changed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "ES features : reecriture apres changement de langue impossible.");
        }
        finally
        {
            _porte.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        _porte.Dispose();
    }
}
