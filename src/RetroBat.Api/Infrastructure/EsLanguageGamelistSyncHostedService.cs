using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

public sealed class EsLanguageGamelistSyncHostedService : BackgroundService
{
    private const string SyncSettingName = "global.apiexpose.api.sync_gamelists_with_system_language";
    private const string DefaultSystemLanguage = "en_US";
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan EmulationStationSettleDelay = TimeSpan.FromSeconds(4);

    private readonly MediaRuntimeState _mediaRuntimeState;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<EsLanguageGamelistSyncHostedService>? _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private IDisposable? _settingsSubscription;
    private string _lastKnownSystemLanguage = string.Empty;

    public EsLanguageGamelistSyncHostedService(
        MediaRuntimeState mediaRuntimeState,
        EmulationStationSettingsService settingsService,
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<EsLanguageGamelistSyncHostedService>? logger = null)
    {
        _mediaRuntimeState = mediaRuntimeState;
        _settingsService = settingsService;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastKnownSystemLanguage = ReadSystemLanguageReference();
        _settingsSubscription = _settingsChangeBus.Subscribe((_, token) => HandleSettingsChangedAsync(token));
        _logger?.LogInformation(
            "ES language gamelist sync watcher initialized: language={Language}.",
            string.IsNullOrWhiteSpace(_lastKnownSystemLanguage) ? "(none)" : _lastKnownSystemLanguage);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _settingsSubscription?.Dispose();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _settingsSubscription?.Dispose();
        _sync.Dispose();
        base.Dispose();
    }

    private async Task HandleSettingsChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, cancellationToken);
            await _settingsStore.WaitForStableFileAsync(cancellationToken);
            await _sync.WaitAsync(cancellationToken);
            try
            {
                _settingsService.Invalidate();
                var currentLanguage = ReadSystemLanguageReference();
                if (!IsSyncEnabled())
                {
                    _lastKnownSystemLanguage = currentLanguage;
                    return;
                }

                if (string.Equals(currentLanguage, _lastKnownSystemLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var previousLanguage = _lastKnownSystemLanguage;
                _logger?.LogInformation(
                    "ES system language changed: previous={PreviousLanguage}, current={CurrentLanguage}. Queueing gamelist synchronization workflow.",
                    string.IsNullOrWhiteSpace(previousLanguage) ? "(none)" : previousLanguage,
                    currentLanguage);

                await Task.Delay(EmulationStationSettleDelay, cancellationToken);
                await _settingsStore.WaitForStableFileAsync(cancellationToken);
                _lastKnownSystemLanguage = currentLanguage;
                _mediaRuntimeState.RequestLanguageGamelistSyncWorkflow(
                    TimeSpan.FromSeconds(1),
                    new LanguageGamelistSyncWorkflowRequest(
                        previousLanguage,
                        currentLanguage,
                        "system-language-changed"));
                _logger?.LogInformation(
                    "ES system language gamelist synchronization workflow queued: language={Language}.",
                    currentLanguage);
            }
            finally
            {
                _sync.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer settings write or service shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ES system language gamelist synchronization failed.");
        }
    }

    private string ReadSystemLanguageReference()
    {
        var settings = _settingsStore.ReadAllSettings();
        if (!settings.TryGetValue("Language", out var language))
        {
            return DefaultSystemLanguage;
        }

        var normalized = NormalizeLanguage(language);
        return string.IsNullOrWhiteSpace(normalized)
            ? DefaultSystemLanguage
            : normalized;
    }

    private bool IsSyncEnabled()
    {
        var settings = _settingsStore.ReadAllSettings();
        return settings.TryGetValue(SyncSettingName, out var value)
            ? ParseBool(value, _options.CurrentValue.ApiSettings.SyncGamelistsWithSystemLanguage)
            : _options.CurrentValue.ApiSettings.SyncGamelistsWithSystemLanguage;
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            // empty = auto/default (ES saves the default switch state as "")
            _ => fallback
        };
    }

    private static string NormalizeLanguage(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('-', '_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        return parts.Length == 1
            ? parts[0].ToLowerInvariant()
            : $"{parts[0].ToLowerInvariant()}_{parts[1].ToUpperInvariant()}";
    }
}
