using System.Security.Cryptography;
using System.Text;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class DatasThemeExposeSettingsWatcherHostedService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(600);
    private readonly DatasThemeExposeService _datasThemeExposeService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DatasThemeExposeSettingsWatcherHostedService>? _logger;
    private string _lastSignature = string.Empty;
    private Dictionary<string, string> _lastSettings = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _settingsSubscription;

    public DatasThemeExposeSettingsWatcherHostedService(
        DatasThemeExposeService datasThemeExposeService,
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        IEventBus eventBus,
        ILogger<DatasThemeExposeSettingsWatcherHostedService>? logger = null)
    {
        _datasThemeExposeService = datasThemeExposeService;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _eventBus = eventBus;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastSettings = ReadDatasThemeSettings();
        _lastSignature = ComputeDatasThemeSignature(_lastSettings);
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
            var settings = ReadDatasThemeSettings();
            var signature = ComputeDatasThemeSignature(settings);
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                return;
            }

            var changedPanelKeys = GetChangedPanelKeys(_lastSettings, settings).ToList();
            var panelSettingsChanged = changedPanelKeys.Count > 0;
            _lastSettings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);
            _lastSignature = signature;
            if (panelSettingsChanged)
            {
                await _eventBus.PublishAsync(new EventEnvelope
                {
                    Type = "panel.settings.changed",
                    Payload = new
                    {
                        Source = "es_settings.cfg",
                        ChangedKeys = changedPanelKeys,
                        PublishedAtUtc = DateTime.UtcNow
                    }
                });
            }

            var result = await _datasThemeExposeService.ExportAllAsync(cancellationToken);
            _logger?.LogInformation(
                "Themes Manager export completed after settings change. Enabled={Enabled}, Cpo={Cpo}, HighScore={HighScore}, Systems={Systems}, Games={Games}, Changed={Changed}",
                result.Enabled,
                result.CpoEnabled,
                result.HighScoreEnabled,
                result.SystemFilesScanned,
                result.GameFilesScanned,
                result.FilesChanged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer write.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Themes Manager settings watcher failed.");
        }
    }

    private static string ComputeDatasThemeSignature(IReadOnlyDictionary<string, string> values)
    {
        var joined = string.Join(
            "\n",
            values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private Dictionary<string, string> ReadDatasThemeSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["global.apiexpose.datas_theme_expose.enabled"] = string.Empty,
            ["global.apiexpose.datas_theme_expose.high_score.enabled"] = string.Empty,
            ["global.apiexpose.datas_theme_expose.cpo_control_panel.enabled"] = string.Empty,
            ["global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled"] = string.Empty,
            ["global.apiexpose.datas_theme_expose.cpo.general_panel_buttons"] = "auto",
            ["global.apiexpose.control_manager.buttons_per_player"] = "6"
        };
        foreach (var pair in _settingsStore.ReadAllSettings())
        {
            var name = pair.Key.Trim();
            if (!IsDatasThemeSetting(name))
            {
                continue;
            }

            var value = NormalizeDatasThemeSettingValue(name, pair.Value.Trim());
            if (string.IsNullOrWhiteSpace(value) && IsApiExposePanelSetting(name))
            {
                continue;
            }

            settings[name] = value;
        }

        return settings;
    }

    private static bool IsDatasThemeSetting(string name)
    {
        return string.Equals(name, "global.apiexpose.datas_theme_expose.enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.datas_theme_expose.high_score.enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.datas_theme_expose.cpo_control_panel.enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.datas_theme_expose.cpo.general_panel_buttons", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.control_manager.buttons_per_player", StringComparison.OrdinalIgnoreCase) ||
            IsApiExposePanelSetting(name);
    }

    private static string NormalizeDatasThemeSettingValue(string name, string value)
    {
        if (string.Equals(name, "global.apiexpose.datas_theme_expose.cpo.general_panel_buttons", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(value) ? "auto" : value;
        }

        if (IsApiExposePanelSetting(name))
        {
            return string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : value;
        }

        return value;
    }

    private static bool IsApiExposePanelSetting(string name)
    {
        var normalized = name.Trim();
        return normalized.StartsWith("apiexpose_panel", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(".apiexpose_panel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPanelSetting(string name)
    {
        return string.Equals(name, "global.apiexpose.datas_theme_expose.cpo_control_panel.enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.datas_theme_expose.cpo.general_panel_buttons", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "global.apiexpose.control_manager.buttons_per_player", StringComparison.OrdinalIgnoreCase) ||
            IsApiExposePanelSetting(name);
    }

    private static IEnumerable<string> GetChangedPanelKeys(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current)
    {
        return previous.Keys
            .Concat(current.Keys)
            .Where(IsPanelSetting)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key =>
            {
                previous.TryGetValue(key, out var previousValue);
                current.TryGetValue(key, out var currentValue);
                return !string.Equals(previousValue ?? string.Empty, currentValue ?? string.Empty, StringComparison.Ordinal);
            })
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
    }
}
