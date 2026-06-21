using RetroBat.Api.Controllers;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

public sealed class LocalMediaManagerActionHostedService : BackgroundService
{
    private const string PopulateAllSettingName = "global.apiexpose.local_media_manager.populate_all_requested";
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(700);
    private readonly LocalGamelistUpdateService _localGamelistUpdateService;
    private readonly IGamelistSelectionSyncService _gamelistSelectionSyncService;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly GameListImpactWarningService _gameListImpactWarningService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly MediaRuntimeState _mediaRuntimeState;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ILogger<LocalMediaManagerActionHostedService>? _logger;
    private IDisposable? _settingsSubscription;
    private string _lastObservedValue = string.Empty;
    private int _running;

    public LocalMediaManagerActionHostedService(
        LocalGamelistUpdateService localGamelistUpdateService,
        IGamelistSelectionSyncService gamelistSelectionSyncService,
        ApiExposeRuntimeOptionsService runtimeOptions,
        IEmulationStationNotificationService notificationService,
        GameListImpactWarningService gameListImpactWarningService,
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        MediaRuntimeState mediaRuntimeState,
        EmulationStationSettingsService settingsService,
        InterfaceTextService interfaceTextService,
        ILogger<LocalMediaManagerActionHostedService>? logger = null)
    {
        _localGamelistUpdateService = localGamelistUpdateService;
        _gamelistSelectionSyncService = gamelistSelectionSyncService;
        _runtimeOptions = runtimeOptions;
        _notificationService = notificationService;
        _gameListImpactWarningService = gameListImpactWarningService;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _mediaRuntimeState = mediaRuntimeState;
        _settingsService = settingsService;
        _interfaceTextService = interfaceTextService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastObservedValue = NormalizeSettingValue(ReadSettingValue(PopulateAllSettingName));
        _settingsSubscription = _settingsChangeBus.Subscribe((_, token) => HandleSettingsChangedAsync(token));
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _settingsSubscription?.Dispose();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleSettingsChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, cancellationToken);
            await _settingsStore.WaitForStableFileAsync(cancellationToken);
            var currentValue = NormalizeSettingValue(ReadSettingValue(PopulateAllSettingName));
            if (string.Equals(currentValue, _lastObservedValue, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastObservedValue = currentValue;
            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                return;
            }

            try
            {
                var language = _settingsService.GetScrapingSettings().Language;
                if (!_runtimeOptions.IsLocalMediaManagerEnabled())
                {
                    await _notificationService.MessageBoxAsync(
                        _interfaceTextService.Text("notification.local_media.disabled", language),
                        cancellationToken);
                    return;
                }

                await _gameListImpactWarningService.WarnIfGameSelectedAsync(
                    _interfaceTextService.Text("notification.local_media.warning_subject", language),
                    cancellationToken);
                await _notificationService.MessageBoxAsync(
                    _interfaceTextService.Text("notification.local_media.changed", language),
                    cancellationToken);
                await _notificationService.NotifyAsync(
                    _interfaceTextService.Text("notification.local_media.started", language),
                    cancellationToken);
                var result = await _localGamelistUpdateService.UpdateAsync(
                    new LocalGamelistUpdateRequest { Scope = "all" },
                    cancellationToken);
                var strictReallocatedSystems = await _gamelistSelectionSyncService.RefreshSelectionsForAllSystemsAsync(
                    cancellationToken: cancellationToken);
                if (strictReallocatedSystems > 0)
                {
                    result.ReloadGamesRequested |= _mediaRuntimeState.TryRequestReloadGamesBypassingLastGameSelected(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(8));
                }

                var reloadSuffix = result.ReloadGamesRequested
                    ? _interfaceTextService.Text("notification.local_media.reload_suffix", language)
                    : string.Empty;
                var strictSuffix = strictReallocatedSystems > 0
                    ? _interfaceTextService.Format("notification.local_media.strict_suffix", language, ("count", strictReallocatedSystems))
                    : string.Empty;
                var failedSuffix = result.SystemsFailed > 0
                    ? _interfaceTextService.Format("notification.local_media.failed_suffix", language, ("count", result.SystemsFailed))
                    : string.Empty;
                var message = result.SystemsFailed > 0
                    ? _interfaceTextService.Format(
                        "notification.local_media.partial_failed",
                        language,
                        ("systemsUpdated", result.SystemsUpdated),
                        ("systemsProcessed", result.SystemsProcessed),
                        ("gamesUpdated", result.GamesUpdated),
                        ("gamesProcessed", result.GamesProcessed),
                        ("strict", strictSuffix),
                        ("failed", failedSuffix))
                    : _interfaceTextService.Format(
                        "notification.local_media.completed",
                        language,
                        ("systemsUpdated", result.SystemsUpdated),
                        ("systemsProcessed", result.SystemsProcessed),
                        ("gamesUpdated", result.GamesUpdated),
                        ("gamesProcessed", result.GamesProcessed),
                        ("strict", strictSuffix),
                        ("reload", reloadSuffix));
                await _notificationService.NotifyAsync(message, cancellationToken);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced or service stopping.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Local Media Manager action watcher failed.");
            await _notificationService.NotifyAsync(
                _interfaceTextService.Text("notification.local_media.failed", _settingsService.GetScrapingSettings().Language),
                CancellationToken.None);
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static string NormalizeSettingValue(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized switch
        {
            "true" => "1",
            "True" => "1",
            "TRUE" => "1",
            "yes" => "1",
            "on" => "1",
            "false" => "0",
            "False" => "0",
            "FALSE" => "0",
            "no" => "0",
            "off" => "0",
            _ => normalized
        };
    }

    private string ReadSettingValue(string key)
    {
        return _settingsStore.ReadAllSettings().TryGetValue(key, out var value)
            ? value.Trim()
            : string.Empty;
    }
}
