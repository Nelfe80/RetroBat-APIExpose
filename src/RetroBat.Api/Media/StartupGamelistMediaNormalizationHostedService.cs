using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class StartupGamelistMediaNormalizationHostedService : IHostedService
{
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<StartupGamelistMediaNormalizationHostedService>? _logger;

    public StartupGamelistMediaNormalizationHostedService(
        GamelistUpdateService gamelistUpdateService,
        EmulationStationSettingsService settingsService,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<StartupGamelistMediaNormalizationHostedService>? logger = null)
    {
        _gamelistUpdateService = gamelistUpdateService;
        _settingsService = settingsService;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_options.CurrentValue.ApiSettings.RepairGamelistsOnStartup)
            {
                _logger?.LogInformation("Startup gamelist media/text normalization disabled by API settings.");
                return;
            }

            var settingsSnapshot = _settingsService.GetScrapingSettings();
            var updatedSystems = await _gamelistUpdateService.RefreshSelectionsForAllSystemsAtStartupAsync(
                settingsSnapshot,
                cancellationToken);
            _logger?.LogInformation(
                "Startup gamelist media/text normalization completed: updatedSystems={UpdatedSystems}, language={Language}.",
                updatedSystems,
                settingsSnapshot.Language);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Startup gamelist media/text normalization failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
