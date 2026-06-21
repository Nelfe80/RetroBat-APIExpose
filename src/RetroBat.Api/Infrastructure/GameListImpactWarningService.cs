using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

public sealed class GameListImpactWarningService
{
    private static readonly TimeSpan WarningCooldown = TimeSpan.FromSeconds(15);
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ILogger<GameListImpactWarningService>? _logger;
    private readonly object _lock = new();
    private DateTime _lastWarningAtUtc = DateTime.MinValue;

    public GameListImpactWarningService(
        MediaRuntimeState runtimeState,
        IEmulationStationNotificationService notificationService,
        EmulationStationSettingsService settingsService,
        InterfaceTextService interfaceTextService,
        ILogger<GameListImpactWarningService>? logger = null)
    {
        _runtimeState = runtimeState;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _interfaceTextService = interfaceTextService;
        _logger = logger;
    }

    public bool IsGameSelected()
    {
        var status = _runtimeState.GetReloadGamesStatus(TimeSpan.Zero);
        return string.Equals(status.LastFrontendEvent, "game-selected", StringComparison.OrdinalIgnoreCase);
    }

    public async Task WarnIfGameSelectedAsync(string operationLabel, CancellationToken cancellationToken = default)
    {
        if (!IsGameSelected())
        {
            return;
        }

        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastWarningAtUtc < WarningCooldown)
            {
                return;
            }

            _lastWarningAtUtc = nowUtc;
        }

        var language = _settingsService.GetScrapingSettings().Language;
        var label = string.IsNullOrWhiteSpace(operationLabel)
            ? _interfaceTextService.Text("notification.gamelist_impact.default_subject", language)
            : operationLabel.Trim();
        var message = _interfaceTextService.Format(
            "notification.gamelist_impact.message",
            language,
            ("label", label));

        try
        {
            await _notificationService.MessageBoxAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Game-list impact warning skipped: EmulationStation API unavailable.");
        }
    }
}
