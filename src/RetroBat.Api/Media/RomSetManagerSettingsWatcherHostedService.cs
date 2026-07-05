using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class RomSetManagerSettingsWatcherHostedService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan EmulationStationSettleDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan EmulationStationStartupGrace = TimeSpan.FromSeconds(4);
    private readonly RomSetManagerService _romSetManagerService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly ILogger<RomSetManagerSettingsWatcherHostedService>? _logger;
    private string _lastSignature = string.Empty;
    private string _lastRelevantSettingsSignature = string.Empty;
    private IDisposable? _settingsSubscription;

    public RomSetManagerSettingsWatcherHostedService(
        RomSetManagerService romSetManagerService,
        MediaRuntimeState runtimeState,
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        ILogger<RomSetManagerSettingsWatcherHostedService>? logger = null)
    {
        _romSetManagerService = romSetManagerService;
        _runtimeState = runtimeState;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastSignature = ComputeRomSetSignature();
        _lastRelevantSettingsSignature = ComputeRelevantSettingsSignature();
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
            var relevantSettingsSignature = ComputeRelevantSettingsSignature();
            if (string.Equals(relevantSettingsSignature, _lastRelevantSettingsSignature, StringComparison.Ordinal))
            {
                return;
            }

            var signature = ComputeRomSetSignature();
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                _lastRelevantSettingsSignature = relevantSettingsSignature;
                return;
            }

            await Task.Delay(EmulationStationSettleDelay, cancellationToken);
            await _settingsStore.WaitForStableFileAsync(cancellationToken);
            relevantSettingsSignature = ComputeRelevantSettingsSignature();
            signature = ComputeRomSetSignature();
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                _lastRelevantSettingsSignature = relevantSettingsSignature;
                return;
            }

            var workflowDelay = TimeSpan.FromSeconds(1);
            if (!IsEmulationStationStableForAutoApply(GetSettingsLastWriteTimeUtc(), out var skipReason))
            {
                workflowDelay = TimeSpan.FromSeconds(5);
                _logger?.LogInformation(
                    "Roms Manager auto apply delayed after es_settings.cfg lifecycle write. Reason={Reason}",
                    skipReason);
            }

            _lastSignature = signature;
            _lastRelevantSettingsSignature = relevantSettingsSignature;
            var options = _romSetManagerService.GetOptions();
            var restore = !options.Enabled;
            _runtimeState.RequestRomSetManagerWorkflow(
                workflowDelay,
                new RomSetManagerWorkflowRequest(
                    restore,
                    "all",
                    string.Empty,
                    restore ? "settings-disabled" : "settings-changed"));
            _logger?.LogInformation(
                "Roms Manager workflow queued after settings change. Restore={Restore}, DelayMs={DelayMs}",
                restore,
                Math.Ceiling(workflowDelay.TotalMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer write.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Roms Manager settings watcher failed.");
        }
    }

    private string ComputeRomSetSignature()
    {
        var options = _romSetManagerService.GetOptions();
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = options.Enabled ? "1" : "0"
        };

        if (options.Enabled)
        {
            values["never_hide_favorites"] = options.NeverHideFavorites ? "1" : "0";
            values["profile"] = options.Profile.Trim();
            values["ra_mode"] = options.RetroAchievementsMode.Trim();
            values["language_mode"] = options.LanguageMode.Trim();
            values["region_mode"] = options.RegionMode.Trim();
            values["rom_version"] = options.RomVersionMode.Trim();
            values["only_retroachievements"] = options.OnlyRetroAchievements ? "1" : "0";
            values["show_official_games"] = options.ShowOfficialGames ? "1" : "0";
            values["show_clones"] = options.ShowClones ? "1" : "0";
            values["show_prototypes"] = options.ShowPrototypes ? "1" : "0";
            values["show_demos"] = options.ShowDemos ? "1" : "0";
            values["show_beta_alpha"] = options.ShowBetaAlpha ? "1" : "0";
            values["show_location_tests"] = options.ShowLocationTests ? "1" : "0";
            values["show_useful_patches"] = options.ShowUsefulPatches ? "1" : "0";
            values["show_hacks_mods"] = options.ShowHacksMods ? "1" : "0";
            values["show_cheats_trainers"] = options.ShowCheatsTrainers ? "1" : "0";
            values["show_bootlegs_pirates"] = options.ShowBootlegsPirates ? "1" : "0";
            values["show_unlicensed"] = options.ShowUnlicensed ? "1" : "0";
            values["show_homebrews_aftermarket"] = options.ShowHomebrewsAftermarket ? "1" : "0";
            values["show_bootlegs_hacks"] = options.ShowBootlegsAndHacks ? "1" : "0";
            values["show_adult"] = options.ShowAdult ? "1" : "0";
            values["show_casino"] = options.ShowCasino ? "1" : "0";
            values["show_mahjong"] = options.ShowMahjong ? "1" : "0";
            values["show_quiz"] = options.ShowQuiz ? "1" : "0";
            values["show_non_games"] = options.ShowNonGames ? "1" : "0";
            values["show_unknown_roms"] = options.ShowUnknownRoms ? "1" : "0";
            values["show_arcade_diagnostics"] = options.ShowArcadeDiagnostics ? "1" : "0";
            values["show_non_arcade"] = options.ShowNonArcade ? "1" : "0";
            values["show_horizontal"] = options.ShowHorizontal ? "1" : "0";
            values["show_vertical"] = options.ShowVertical ? "1" : "0";
            values["screen_orientation"] = options.ScreenOrientation.Trim();
            values["cocktail_games"] = options.CocktailGames.Trim();
            values["multi_screen_games"] = options.MultiScreenGames.Trim();
            values["functional_second_screen"] = options.FunctionalSecondScreen.Trim();
            values["wide_surround_display"] = options.WideSurroundDisplay.Trim();
            values["portable_link_gameplay"] = options.PortableLinkGameplay.Trim();
            values["cabinet_controls_compatibility"] = options.CabinetControlsCompatibility.Trim();
            values["player_count"] = options.PlayerCount.Trim();
            values["button_compatibility"] = options.ButtonCompatibility.Trim();
            if (UsesControlManagerForRomFiltering(options))
            {
                values["control_player_count"] = options.ControlPlayerCount.ToString();
                values["control_buttons_per_player"] = options.ControlButtonsPerPlayer.ToString();
                values["control_arcade_joystick"] = options.ControlArcadeJoystick ? "1" : "0";
                values["control_analog_joystick"] = options.ControlAnalogJoystick ? "1" : "0";
                values["control_rotary_joystick"] = options.ControlRotaryJoystick ? "1" : "0";
                values["control_spinner"] = options.ControlSpinner.Trim();
                values["control_trackball"] = options.ControlTrackball.Trim();
                values["control_wheel"] = options.ControlWheel.Trim();
                values["control_pedals"] = options.ControlPedals.Trim();
                values["control_shifter"] = options.ControlShifter.Trim();
                values["control_lightgun"] = options.ControlLightgun.Trim();
                values["control_dance_mat"] = options.ControlDanceMat.Trim();
                values["control_guitar"] = options.ControlGuitar.Trim();
                values["control_drums"] = options.ControlDrums.Trim();
                values["control_turntable"] = options.ControlTurntable.Trim();
                values["control_microphone"] = options.ControlMicrophone ? "1" : "0";
                values["control_keyboard"] = options.ControlKeyboard ? "1" : "0";
                values["control_mouse"] = options.ControlMouse ? "1" : "0";
                values["control_touchscreen"] = options.ControlTouchscreen ? "1" : "0";
                values["control_motion_controller"] = options.ControlMotionController ? "1" : "0";
            }
            values["variant_mode"] = options.VariantMode.Trim();
            values["region_profile"] = options.RegionProfile.Trim();
            values["language_profile"] = options.LanguageProfile.Trim();
            values["translations"] = options.Translations.Trim();
            values["arcade_handling"] = options.ArcadeHandling.Trim();
            values["output_mode"] = options.OutputMode.Trim();
        }

        var joined = string.Join("\n", values.Select(pair => pair.Key + "=" + pair.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static bool UsesControlManagerForRomFiltering(RomSetManagerOptionsSnapshot options)
    {
        return IsMode(options.CabinetControlsCompatibility, "only") ||
            IsMode(options.PlayerCount, "only") ||
            IsMode(options.ButtonCompatibility, "only");
    }

    private static bool IsMode(string? value, string expected)
    {
        return string.Equals((value ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private string ComputeRelevantSettingsSignature()
    {
        var values = _settingsStore.ReadAllSettings()
            .Where(pair => IsRelevantRomSetSetting(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key.ToLowerInvariant() + "=" + pair.Value);
        var joined = string.Join("\n", values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static bool IsRelevantRomSetSetting(string key)
    {
        if (string.Equals(key, "global.apiexpose.rom_set_manager.enabled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (key.StartsWith("global.apiexpose.control_manager.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!key.StartsWith("global.apiexpose.romset.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !key.StartsWith("global.apiexpose.romset.pack_installer.", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(key, "global.apiexpose.romset.defaults_initialized", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? GetSettingsLastWriteTimeUtc()
    {
        var path = RetroBatPaths.EmulationStationSettingsPath;
        return File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : null;
    }

    private static bool IsEmulationStationStableForAutoApply(DateTime? settingsWriteTimeUtc, out string reason)
    {
        var startTimes = GetEmulationStationStartTimesUtc();
        if (startTimes.Count == 0)
        {
            reason = "emulationstation_not_running";
            return false;
        }

        var now = DateTime.UtcNow;
        foreach (var startTimeUtc in startTimes)
        {
            if (settingsWriteTimeUtc.HasValue && startTimeUtc > settingsWriteTimeUtc.Value)
            {
                continue;
            }

            if (now - startTimeUtc < EmulationStationStartupGrace)
            {
                continue;
            }

            reason = "emulationstation_running";
            return true;
        }

        reason = "emulationstation_recent_start_or_restart";
        return false;
    }

    private static List<DateTime> GetEmulationStationStartTimesUtc()
    {
        var startTimes = new List<DateTime>();
        foreach (var process in Process.GetProcessesByName("emulationstation"))
        {
            using (process)
            {
                try
                {
                    startTimes.Add(process.StartTime.ToUniversalTime());
                }
                catch
                {
                    // The process may exit between enumeration and StartTime access.
                }
            }
        }

        return startTimes;
    }

}

