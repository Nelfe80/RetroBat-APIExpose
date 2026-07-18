using System.Security.Cryptography;
using System.Text;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Infrastructure;

public sealed class ApiExposeSettingsNotificationHostedService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(700);
    private static readonly HashSet<string> SilentSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.apiexpose.local_media_manager.populate_all_requested"
    };
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ApiExposeAppsettingsSyncService _appsettingsSyncService;
    private readonly GameListImpactWarningService _gameListImpactWarningService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly EsNotifyDeduplicationService _notifyDeduplication;
    private readonly ILogger<ApiExposeSettingsNotificationHostedService>? _logger;
    private readonly HttpClient _esHttpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:1234"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    private Dictionary<string, string> _lastSettings = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSignature = string.Empty;
    private IDisposable? _settingsSubscription;

    public ApiExposeSettingsNotificationHostedService(
        ApiExposeRuntimeOptionsService runtimeOptions,
        ApiExposeAppsettingsSyncService appsettingsSyncService,
        GameListImpactWarningService gameListImpactWarningService,
        EmulationStationSettingsService settingsService,
        InterfaceTextService interfaceTextService,
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        EsNotifyDeduplicationService notifyDeduplication,
        ILogger<ApiExposeSettingsNotificationHostedService>? logger = null)
    {
        _runtimeOptions = runtimeOptions;
        _appsettingsSyncService = appsettingsSyncService;
        _gameListImpactWarningService = gameListImpactWarningService;
        _settingsService = settingsService;
        _interfaceTextService = interfaceTextService;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _notifyDeduplication = notifyDeduplication;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastSettings = ReadApiExposeSettings();
        _lastSignature = ComputeSignature(_lastSettings);
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
            var current = ReadApiExposeSettings();
            var signature = ComputeSignature(current);
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                return;
            }

            var allChanges = ResolveChangedSettings(_lastSettings, current, includeSilent: true);
            var changed = allChanges
                .Where(change => !SilentSettingNames.Contains(change.Key))
                .ToList();
            _lastSettings = current;
            _lastSignature = signature;
            var synchronizedCount = _appsettingsSyncService.ApplyEsSettingsChanges(allChanges);
            if (synchronizedCount > 0)
            {
                _logger?.LogInformation(
                    "ES interface changes synchronized into appsettings.json before runtime reload: {Count}",
                    synchronizedCount);
            }

            if (changed.Count == 0)
            {
                return;
            }

            await NotifySettingsChangedAsync(changed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer write.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "APIExpose settings notification watcher failed.");
        }
    }

    private async Task NotifySettingsChangedAsync(
        IReadOnlyList<ApiExposeSettingChange> changes,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "APIExpose settings changed: {Changes}",
            string.Join(", ", changes.Select(change => $"{change.Key}:{change.PreviousValue}->{change.NewValue}")));

        var labels = _runtimeOptions.GetLocalOptionsSnapshot()
            .Entries
            .ToDictionary(entry => entry.Key, entry => entry.Label, StringComparer.OrdinalIgnoreCase);
        var language = _settingsService.GetScrapingSettings().Language;
        var main = changes[0];
        var label = ResolveSettingLabel(main.Key, labels, language);
        var value = FormatSettingValue(main.Key, main.NewValue, language);
        var suffix = changes.Count > 1
            ? _interfaceTextService.Format("notification.settings.more_suffix", language, ("count", changes.Count - 1))
            : string.Empty;

        var enableNotifications = changes
            .Select(change => ResolveEnableNotification(change, labels, language))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();
        if (enableNotifications.Count > 0)
        {
            foreach (var message in enableNotifications)
            {
                await PostNotifyRawAsync(message!, cancellationToken);
            }
        }
        else
        {
            await PostNotifyRawAsync(
                _interfaceTextService.Format(
                    "notification.settings.changed",
                    language,
                    ("label", label),
                    ("value", value),
                    ("suffix", suffix)),
                cancellationToken);
        }

        if (changes.Any(change => IsGameListImpactingSetting(change.Key)))
        {
            await _gameListImpactWarningService.WarnIfGameSelectedAsync(
                _interfaceTextService.Text("notification.settings.game_impact_subject", language),
                cancellationToken);
        }

        foreach (var warning in changes.Select(change => ResolveDisableWarning(change, labels, language)).Where(message => !string.IsNullOrWhiteSpace(message)))
        {
            await PostMessageBoxRawAsync(warning!, cancellationToken);
        }
    }

    private Dictionary<string, string> ReadApiExposeSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _settingsStore.ReadAllSettings())
        {
            if (IsApiExposeSetting(pair.Key))
            {
                settings[pair.Key] = pair.Value;
            }
        }

        return settings;
    }

    private static List<ApiExposeSettingChange> ResolveChangedSettings(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current,
        bool includeSilent)
    {
        return previous.Keys
            .Concat(current.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key =>
            {
                if (!includeSilent && SilentSettingNames.Contains(key))
                {
                    return false;
                }

                previous.TryGetValue(key, out var previousValue);
                current.TryGetValue(key, out var currentValue);
                return !string.Equals(previousValue ?? string.Empty, currentValue ?? string.Empty, StringComparison.Ordinal);
            })
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                previous.TryGetValue(key, out var previousValue);
                current.TryGetValue(key, out var currentValue);
                return new ApiExposeSettingChange(key, previousValue ?? string.Empty, currentValue ?? string.Empty);
            })
            .ToList();
    }

    private static string ComputeSignature(IReadOnlyDictionary<string, string> values)
    {
        var joined = string.Join(
            "\n",
            values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static bool IsApiExposeSetting(string name)
    {
        return name.StartsWith("global.apiexpose.", StringComparison.OrdinalIgnoreCase) ||
            IsApiExposePanelSetting(name);
    }

    private string FormatSettingValue(string key, string value, string language)
    {
        return value.Trim() switch
        {
            "1" => _interfaceTextService.Text("setting.value.enabled", language),
            "0" => _interfaceTextService.Text("setting.value.disabled", language),
            "" when IsSwitchLikeSetting(key) => _interfaceTextService.Text("setting.value.disabled", language),
            "" => _interfaceTextService.Text("setting.value.empty", language),
            var other => other
        };
    }

    private string ResolveSettingLabel(
        string key,
        IReadOnlyDictionary<string, string> labels,
        string language)
    {
        if (TryResolveApiExposePanelSystemId(key, out var panelSystemId))
        {
            return $"APIExpose Panel {HumanizeSystemId(panelSystemId)}";
        }

        var textKey = key.Trim().ToLowerInvariant() switch
        {
            "global.apiexpose.romset.never_hide_favorites" => "setting.romset.never_hide_favorites",
            "global.apiexpose.romset.only_retroachievements" => "setting.romset.only_retroachievements",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(textKey))
        {
            var translated = _interfaceTextService.Text(textKey, language);
            if (!string.Equals(translated, textKey, StringComparison.OrdinalIgnoreCase))
            {
                return translated;
            }
        }

        if (!IsFrenchLanguage(language))
        {
            return HumanizeSettingKey(key);
        }

        return labels.TryGetValue(key, out var knownLabel) && !string.IsNullOrWhiteSpace(knownLabel)
            ? knownLabel
            : key;
    }

    private static bool IsApiExposePanelSetting(string name)
    {
        var normalized = (name ?? string.Empty).Trim();
        return normalized.StartsWith("apiexpose_panel", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(".apiexpose_panel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveApiExposePanelSystemId(string key, out string systemId)
    {
        systemId = string.Empty;
        var normalized = (key ?? string.Empty).Trim();
        var markerIndex = normalized.IndexOf(".apiexpose_panel", StringComparison.OrdinalIgnoreCase);
        if (markerIndex > 0)
        {
            systemId = normalized[..markerIndex];
            return !string.IsNullOrWhiteSpace(systemId);
        }

        const string prefix = "apiexpose_panel_";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            normalized.Length > prefix.Length)
        {
            systemId = normalized[prefix.Length..];
            return !string.IsNullOrWhiteSpace(systemId);
        }

        return false;
    }

    private static string HumanizeSystemId(string systemId)
    {
        return string.Join(
            " ",
            (systemId ?? string.Empty)
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static bool IsFrenchLanguage(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().Replace('-', '_').ToLowerInvariant();
        return normalized.Equals("fr", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("fr_", StringComparison.OrdinalIgnoreCase);
    }

    private static string HumanizeSettingKey(string key)
    {
        var normalized = (key ?? string.Empty).Trim();
        if (normalized.StartsWith("global.apiexpose.", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["global.apiexpose.".Length..];
        }

        if (normalized.StartsWith("apiexpose_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["apiexpose_".Length..];
        }

        return string.Join(
            " ",
            normalized
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static bool IsSwitchLikeSetting(string key)
    {
        var normalized = (key ?? string.Empty).Trim();
        return normalized.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".requested", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.debug_report", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.never_hide_favorites", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.only_retroachievements", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("global.apiexpose.romset.show_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameListImpactingSetting(string key)
    {
        var normalized = (key ?? string.Empty).Trim();
        return normalized.StartsWith("global.apiexpose.media_allocation.", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.never_hide_favorites", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.only_retroachievements", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("global.apiexpose.romset.show_", StringComparison.OrdinalIgnoreCase) ||
            (normalized.StartsWith("global.apiexpose.romset.", StringComparison.OrdinalIgnoreCase) &&
                normalized.EndsWith("_mode", StringComparison.OrdinalIgnoreCase)) ||
            normalized.Equals("global.apiexpose.romset.profile", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.ra_mode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.rom_version", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.variant_mode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.api.region_profile", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.api.language_profile", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.region_profile", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.language_profile", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.translations", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.arcade_handling", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.romset.output_mode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("global.apiexpose.rom_set_manager.enabled", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveDisableWarning(
        ApiExposeSettingChange change,
        IReadOnlyDictionary<string, string> labels,
        string language)
    {
        if (!IsEnabledValue(change.Key, change.PreviousValue) || !IsDisabledValue(change.Key, change.NewValue))
        {
            return null;
        }

        var warningKey = change.Key.Trim().ToLowerInvariant() switch
        {
            "global.apiexpose.enabled" => "notification.settings.disable_warning.apiexpose",
            "global.apiexpose.local_media_manager.enabled" => "notification.settings.disable_warning.local_media",
            "global.apiexpose.scraping.auto_enabled" => "notification.settings.disable_warning.scraping",
            "global.apiexpose.collections_auto_installer.hyperbat_theme.enabled" => "notification.settings.disable_warning.themes_deployment",
            "global.apiexpose.datas_theme_expose.enabled" => "notification.settings.disable_warning.themes_manager",
            "global.apiexpose.datas_theme_expose.cpo_control_panel.enabled" => "notification.settings.disable_warning.cpo",
            "global.apiexpose.marquee_manager.enabled" => "notification.settings.disable_warning.marquee",
            "global.apiexpose.rom_set_manager.enabled" => "notification.settings.disable_warning.romset",
            "global.apiexpose.collections_pack_manager.enabled" => "notification.settings.disable_warning.collections_pack",
            "global.apiexpose.game_events_manager.enabled" => "notification.settings.disable_warning.game_events",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(warningKey))
        {
            return null;
        }

        return _interfaceTextService.Format(
            warningKey,
            language,
            ("label", ResolveSettingLabel(change.Key, labels, language)));
    }

    private string? ResolveEnableNotification(
        ApiExposeSettingChange change,
        IReadOnlyDictionary<string, string> labels,
        string language)
    {
        if (!IsDisabledValue(change.Key, change.PreviousValue) || !IsEnabledValue(change.Key, change.NewValue) || !IsParentSwitch(change.Key))
        {
            return null;
        }

        return _interfaceTextService.Format(
            "notification.settings.enabled",
            language,
            ("label", ResolveSettingLabel(change.Key, labels, language)));
    }

    private static bool IsParentSwitch(string key)
    {
        return (key ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "global.apiexpose.enabled" => true,
            "global.apiexpose.local_media_manager.enabled" => true,
            "global.apiexpose.scraping.auto_enabled" => true,
            "global.apiexpose.collections_auto_installer.hyperbat_theme.enabled" => true,
            "global.apiexpose.datas_theme_expose.enabled" => true,
            "global.apiexpose.datas_theme_expose.cpo_control_panel.enabled" => true,
            "global.apiexpose.marquee_manager.enabled" => true,
            "global.apiexpose.rom_set_manager.enabled" => true,
            "global.apiexpose.collections_pack_manager.enabled" => true,
            "global.apiexpose.game_events_manager.enabled" => true,
            _ => false
        };
    }

    private async Task PostNotifyRawAsync(string message, CancellationToken cancellationToken)
    {
        var safeMessage = message.Trim();
        if (!_notifyDeduplication.TryAccept(safeMessage))
        {
            _logger?.LogDebug("ES notify setting duplicate suppressed: {Message}", safeMessage);
            return;
        }

        try
        {
            using var content = new StringContent(safeMessage, Encoding.UTF8, "text/plain");
            using var response = await _esHttpClient.PostAsync("/notify", content, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                _notifyDeduplication.ForgetIfCurrent(safeMessage);
                _logger?.LogDebug("ES notify setting returned HTTP {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notifyDeduplication.ForgetIfCurrent(safeMessage);
            _logger?.LogDebug(ex, "ES notify setting skipped: EmulationStation API unavailable.");
        }
    }

    private async Task PostMessageBoxRawAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(message.Trim(), Encoding.UTF8, "text/plain");
            using var response = await _esHttpClient.PostAsync("/messagebox", content, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                _logger?.LogDebug("ES messagebox warning returned HTTP {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "ES messagebox warning skipped: EmulationStation API unavailable.");
        }
    }

    private static bool IsEnabledValue(string key, string value)
    {
        var normalized = NormalizeBoolLike(value);
        if (normalized.Length == 0)
        {
            // Empty means auto/default (ES switchauto and switchon both save it),
            // never an explicit user choice.
            return ApiExposeAppsettingsSyncService.TryGetShippedBoolDefault(key, out var shippedDefault) && shippedDefault;
        }

        return normalized == "1";
    }

    private static bool IsDisabledValue(string key, string value)
    {
        var normalized = NormalizeBoolLike(value);
        if (normalized.Length == 0)
        {
            return ApiExposeAppsettingsSyncService.TryGetShippedBoolDefault(key, out var shippedDefault) && !shippedDefault;
        }

        return normalized == "0";
    }

    private static string NormalizeBoolLike(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => "1",
            "0" or "false" or "no" or "off" => "0",
            var other => other
        };
    }

}

