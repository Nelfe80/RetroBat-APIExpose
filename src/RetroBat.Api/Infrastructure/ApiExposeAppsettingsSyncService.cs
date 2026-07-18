using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class ApiExposeAppsettingsSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object SyncLock = new();
    private static readonly IReadOnlyDictionary<string, AppsettingsMapping> Mappings =
        new Dictionary<string, AppsettingsMapping>(StringComparer.OrdinalIgnoreCase)
        {
            ["global.apiexpose.enabled"] = Bool("Enabled"),
            ["global.apiexpose.local_media_manager.enabled"] = Bool("LocalMediaManager", "Enabled"),
            ["global.apiexpose.local_media_manager.populate_all_requested"] = Bool("LocalMediaManager", "PopulateAllGamelistsRequested"),
            ["global.apiexpose.local_media_manager.remove_roms_media_after_canonical_migration"] = Bool("LocalMediaManager", "RemoveRomsMediaAfterCanonicalMigration"),
            ["global.apiexpose.media_allocation.image_source"] = String("MediaAllocation", "ImageSource"),
            ["global.apiexpose.media_allocation.logo_source"] = String("MediaAllocation", "LogoSource"),
            ["global.apiexpose.media_allocation.thumb_source"] = String("MediaAllocation", "ThumbSource"),
            ["global.apiexpose.media_allocation.wheel_style"] = String("MediaAllocation", "WheelStyle"),
            ["global.apiexpose.media_allocation.region_mode"] = String("MediaAllocation", "MediaRegionMode"),
            ["global.apiexpose.media_allocation.logo_region_mode"] = String("MediaAllocation", "LogoRegionMode"),
            ["global.apiexpose.media_allocation.user_region"] = String("MediaAllocation", "UserRegion"),
            ["global.apiexpose.api.region_profile"] = String("ApiSettings", "RegionProfile"),
            ["global.apiexpose.api.language_profile"] = String("ApiSettings", "LanguageProfile"),
            ["global.apiexpose.api.repair_gamelists_on_startup"] = Bool("ApiSettings", "RepairGamelistsOnStartup"),
            ["global.apiexpose.api.sync_gamelists_with_system_language"] = Bool("ApiSettings", "SyncGamelistsWithSystemLanguage"),
            ["global.apiexpose.scraping.auto_enabled"] = Bool("Scraping", "AutoScrapingEnabled"),
            ["global.apiexpose.scraping.remote_provider"] = String("Scraping", "RemoteProvider"),
            ["global.apiexpose.scraping.screenscraper.enabled"] = Bool("Scraping", "ScreenScraperEnabled"),
            ["global.apiexpose.scraping.description_translation.enabled"] = Bool("Scraping", "DescriptionTranslationEnabled"),
            ["global.apiexpose.scraping.queue.enabled"] = Bool("Scraping", "ScrapeQueueEnabled"),
            ["global.apiexpose.scraping.remote_after_local_only"] = Bool("Scraping", "RemoteAfterLocalOnly"),
            ["global.apiexpose.scraping.exact_local_media.enabled"] = Bool("Scraping", "ExactLocalMediaScrapingEnabled"),
            ["global.apiexpose.scraping.refresh_current_after_success"] = Bool("Scraping", "RefreshCurrentGameAfterRemoteSuccess"),
            ["global.apiexpose.scraping.notify_media.enabled"] = Bool("Scraping", "NotifyHeavyMediaScrapeEnabled"),
            ["global.apiexpose.scraping.marquee.enabled"] = Bool("Scraping", "MarqueeScrapingEnabled"),
            ["global.apiexpose.scraping.screen_marquee.enabled"] = Bool("Scraping", "ScreenMarqueeScrapingEnabled"),
            ["global.apiexpose.scraping.screen_marquee_small.enabled"] = Bool("Scraping", "ScreenMarqueeSmallScrapingEnabled"),
            ["global.apiexpose.scraping.steamgrid.enabled"] = Bool("Scraping", "SteamGridScrapingEnabled"),
            ["global.apiexpose.scraping.mix.enabled"] = Bool("Scraping", "MixScrapingEnabled"),
            ["global.apiexpose.scraping.maps.enabled"] = Bool("Scraping", "MapScrapingEnabled"),
            ["global.apiexpose.scraping.manuals.enabled"] = Bool("Scraping", "ManualScrapingEnabled"),
            ["global.apiexpose.scraping.magazines.enabled"] = Bool("Scraping", "MagazineScrapingEnabled"),
            ["global.apiexpose.scraping.videos.enabled"] = Bool("Scraping", "VideoScrapingEnabled"),
            ["global.apiexpose.scraping.video_normalized.enabled"] = Bool("Scraping", "VideoNormalizedScrapingEnabled"),
            ["global.apiexpose.scraping.bezels.enabled"] = Bool("Scraping", "BezelScrapingEnabled"),
            ["global.apiexpose.scraping.bezel_aspect"] = String("Scraping", "BezelAspectRatio"),
            ["global.apiexpose.scraping.bezel_orientation"] = String("Scraping", "BezelOrientation"),
            ["global.apiexpose.collections_auto_installer.enabled"] = Bool("ThemeDeployments", "Enabled"),
            ["global.apiexpose.collections_auto_installer.hyperbat_theme.enabled"] = Bool("ThemeDeployments", "Enabled"),
            ["global.apiexpose.collections_auto_installer.refresh_current_after_success"] = Bool("ThemeDeployments", "RefreshCurrentGameAfterInstallSuccess"),
            ["global.apiexpose.datas_theme_expose.enabled"] = Bool("DatasThemeExpose", "Enabled"),
            ["global.apiexpose.high_score_theme_extractor.enabled"] = Bool("DatasThemeExpose", "Enabled"),
            ["global.apiexpose.datas_theme_expose.high_score.enabled"] = Bool("DatasThemeExpose", "HighScoreExposeEnabled"),
            ["global.apiexpose.datas_theme_expose.legacy_hiscore_theme.enabled"] = Bool("DatasThemeExpose", "LegacyHiscoreThemeExportEnabled"),
            ["global.apiexpose.datas_theme_expose.cpo_control_panel.enabled"] = Bool("DatasThemeExpose", "CpoControlPanelExposeEnabled"),
            ["global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled"] = Bool("DatasThemeExpose", "CpoPanelWebSocketPushEnabled"),
            ["global.apiexpose.datas_theme_expose.cpo.general_panel_buttons"] = String("DatasThemeExpose", "GeneralPanelButtons"),
            ["global.apiexpose.marquee_manager.enabled"] = Bool("MarqueeManager", "Enabled"),
            ["global.apiexpose.marquee_manager.websocket_assets.enabled"] = Bool("MarqueeManager", "WebSocketAssetsEnabled"),
            ["global.apiexpose.marquee_manager.autogen_profile"] = String("MarqueeManager", "AutogenProfile"),
            ["global.apiexpose.marquee_manager.system_marquee_theme_background.enabled"] = Bool("MarqueeManager", "SystemMarqueeThemeBackgroundEnabled"),
            ["global.apiexpose.marquee_manager.dmd_autogen_profile"] = String("MarqueeManager", "DmdAutogenProfile"),
            ["global.apiexpose.marquee_manager.system_dmd_autogen_profile"] = String("MarqueeManager", "DmdAutogenProfile"),
            ["global.apiexpose.marquee_manager.autogen_notify.enabled"] = Bool("MarqueeManager", "AutogenNotifyEnabled"),
            ["global.apiexpose.rom_set_manager.enabled"] = Bool("RomSetManager", "Enabled"),
            ["global.apiexpose.romset.never_hide_favorites"] = Bool("RomSetManager", "NeverHideFavorites"),
            ["global.apiexpose.romset.profile"] = String("RomSetManager", "Profile"),
            ["global.apiexpose.romset.ra_mode"] = String("RomSetManager", "RetroAchievementsMode"),
            ["global.apiexpose.romset.language_mode"] = String("RomSetManager", "LanguageMode"),
            ["global.apiexpose.romset.region_mode"] = String("RomSetManager", "RegionMode"),
            ["global.apiexpose.romset.rom_version"] = String("RomSetManager", "RomVersionMode"),
            ["global.apiexpose.romset.official_games_mode"] = String("RomSetManager", "OfficialGamesMode"),
            ["global.apiexpose.romset.clones_mode"] = String("RomSetManager", "ClonesMode"),
            ["global.apiexpose.romset.prototypes_mode"] = String("RomSetManager", "PrototypesMode"),
            ["global.apiexpose.romset.demos_mode"] = String("RomSetManager", "DemosMode"),
            ["global.apiexpose.romset.beta_alpha_mode"] = String("RomSetManager", "BetaAlphaMode"),
            ["global.apiexpose.romset.location_tests_mode"] = String("RomSetManager", "LocationTestsMode"),
            ["global.apiexpose.romset.useful_patches_mode"] = String("RomSetManager", "UsefulPatchesMode"),
            ["global.apiexpose.romset.hacks_mods_mode"] = String("RomSetManager", "HacksModsMode"),
            ["global.apiexpose.romset.cheats_trainers_mode"] = String("RomSetManager", "CheatsTrainersMode"),
            ["global.apiexpose.romset.bootlegs_pirates_mode"] = String("RomSetManager", "BootlegsPiratesMode"),
            ["global.apiexpose.romset.unlicensed_mode"] = String("RomSetManager", "UnlicensedMode"),
            ["global.apiexpose.romset.homebrews_aftermarket_mode"] = String("RomSetManager", "HomebrewsAftermarketMode"),
            ["global.apiexpose.romset.adult_mode"] = String("RomSetManager", "AdultMode"),
            ["global.apiexpose.romset.casino_mode"] = String("RomSetManager", "CasinoMode"),
            ["global.apiexpose.romset.mahjong_mode"] = String("RomSetManager", "MahjongMode"),
            ["global.apiexpose.romset.quiz_mode"] = String("RomSetManager", "QuizMode"),
            ["global.apiexpose.romset.non_games_mode"] = String("RomSetManager", "NonGamesMode"),
            ["global.apiexpose.romset.unknown_roms_mode"] = String("RomSetManager", "UnknownRomsMode"),
            ["global.apiexpose.romset.arcade_diagnostics_mode"] = String("RomSetManager", "ArcadeDiagnosticsMode"),
            // legacy show_* keys superseded by the *_mode choices were removed
            // (audit-esmenu); only the three still read by the filter remain
            ["global.apiexpose.romset.show_non_arcade"] = Bool("RomSetManager", "ShowNonArcade"),
            ["global.apiexpose.romset.show_horizontal"] = Bool("RomSetManager", "ShowHorizontal"),
            ["global.apiexpose.romset.show_vertical"] = Bool("RomSetManager", "ShowVertical"),
            ["global.apiexpose.romset.screen_orientation"] = String("RomSetManager", "ScreenOrientation"),
            ["global.apiexpose.romset.cocktail_games"] = String("RomSetManager", "CocktailGames"),
            ["global.apiexpose.romset.multi_screen_games"] = String("RomSetManager", "MultiScreenGames"),
            ["global.apiexpose.romset.functional_second_screen"] = String("RomSetManager", "FunctionalSecondScreen"),
            ["global.apiexpose.romset.wide_surround_display"] = String("RomSetManager", "WideSurroundDisplay"),
            ["global.apiexpose.romset.portable_link_gameplay"] = String("RomSetManager", "PortableLinkGameplay"),
            ["global.apiexpose.romset.cabinet_controls_compatibility"] = String("RomSetManager", "CabinetControlsCompatibility"),
            ["global.apiexpose.romset.player_count"] = String("RomSetManager", "PlayerCount"),
            ["global.apiexpose.romset.button_compatibility"] = String("RomSetManager", "ButtonCompatibility"),
            ["global.apiexpose.control_manager.cabinet_profile"] = String("ControlManager", "CabinetProfile"),
            ["global.apiexpose.control_manager.player_count"] = Int("ControlManager", "PlayerCount"),
            ["global.apiexpose.control_manager.buttons_per_player"] = Int("ControlManager", "ButtonsPerPlayer"),
            ["global.apiexpose.control_manager.arcade_joystick"] = Bool("ControlManager", "ArcadeJoystick"),
            ["global.apiexpose.control_manager.analog_joystick"] = Bool("ControlManager", "AnalogJoystick"),
            ["global.apiexpose.control_manager.rotary_joystick"] = Bool("ControlManager", "RotaryJoystick"),
            ["global.apiexpose.control_manager.spinner"] = String("ControlManager", "Spinner"),
            ["global.apiexpose.control_manager.trackball"] = String("ControlManager", "Trackball"),
            ["global.apiexpose.control_manager.wheel"] = String("ControlManager", "Wheel"),
            ["global.apiexpose.control_manager.pedals"] = String("ControlManager", "Pedals"),
            ["global.apiexpose.control_manager.shifter"] = String("ControlManager", "Shifter"),
            ["global.apiexpose.control_manager.lightgun"] = String("ControlManager", "Lightgun"),
            ["global.apiexpose.control_manager.dance_mat"] = String("ControlManager", "DanceMat"),
            ["global.apiexpose.control_manager.guitar"] = String("ControlManager", "Guitar"),
            ["global.apiexpose.control_manager.drums"] = String("ControlManager", "Drums"),
            ["global.apiexpose.control_manager.turntable"] = String("ControlManager", "Turntable"),
            ["global.apiexpose.control_manager.microphone"] = Bool("ControlManager", "Microphone"),
            ["global.apiexpose.control_manager.keyboard"] = Bool("ControlManager", "Keyboard"),
            ["global.apiexpose.control_manager.mouse"] = Bool("ControlManager", "Mouse"),
            ["global.apiexpose.control_manager.touchscreen"] = Bool("ControlManager", "Touchscreen"),
            ["global.apiexpose.control_manager.motion_controller"] = Bool("ControlManager", "MotionController"),
            ["global.apiexpose.romset.variant_mode"] = String("RomSetManager", "VariantMode"),
            ["global.apiexpose.romset.region_profile"] = String("ApiSettings", "RegionProfile"),
            ["global.apiexpose.romset.language_profile"] = String("ApiSettings", "LanguageProfile"),
            ["global.apiexpose.romset.translations"] = String("RomSetManager", "Translations"),
            ["global.apiexpose.romset.arcade_handling"] = String("RomSetManager", "ArcadeHandling"),
            ["global.apiexpose.romset.output_mode"] = String("RomSetManager", "OutputMode"),
            ["global.apiexpose.romset.debug_report"] = Bool("RomSetManager", "DebugReport"),
            ["global.apiexpose.romset.pack_installer.enabled"] = Bool("RomSetManager", "RomPackInstallerEnabled"),
            ["global.apiexpose.romset.pack_installer.unzip_roms"] = Bool("RomSetManager", "RomPackInstallerUnzipRoms"),
            ["global.apiexpose.romset.pack_installer.on_the_fly.enabled"] = Bool("RomSetManager", "OnTheFlyRomInstallerEnabled"),
            ["global.apiexpose.romset.pack_installer.on_the_fly.trigger"] = String("RomSetManager", "OnTheFlyRomExtractionTrigger"),
            ["global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end"] = Bool("RomSetManager", "OnTheFlyRomResetAfterGameEndEnabled"),
            ["global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end_delay_ms"] = Int("RomSetManager", "OnTheFlyRomResetAfterGameEndDelayMs"),
            ["global.apiexpose.collections_pack_manager.enabled"] = Bool("CollectionPackManager", "Enabled"),
            ["global.apiexpose.collections_pack_manager.pack_installer.enabled"] = Bool("CollectionPackManager", "CollectionPackInstallerEnabled"),
            ["global.apiexpose.collections_pack_manager.dynamic_collections.enabled"] = Bool("CollectionPackManager", "DynamicCollectionsEnabled"),
            ["global.apiexpose.collections_pack_manager.static_collections.enabled"] = Bool("CollectionPackManager", "StaticCollectionsEnabled"),
            ["global.apiexpose.collections_pack_manager.apply_collection_theme_to_games.enabled"] = Bool("CollectionPackManager", "ApplyCollectionThemeToGamesEnabled"),
            ["global.apiexpose.game_events_manager.enabled"] = Bool("GameEventsManager", "Enabled"),
            ["global.apiexpose.game_events.retroarch_wrapper.enabled"] = Bool("GameEventsManager", "RetroArchWrapperEnabled"),
            ["global.apiexpose.game_events.console_high_score_capture.enabled"] = Bool("GameEventsManager", "ConsoleHighScoreCaptureEnabled"),
            ["global.apiexpose.game_events.mame_outputs.enabled"] = Bool("GameEventsManager", "MameOutputsEnabled"),
            ["global.apiexpose.game_events.export_scores_on_game_end.enabled"] = Bool("GameEventsManager", "ExportScoresOnGameEndEnabled"),
            ["global.apiexpose.game_events.max_high_scores"] = Int("GameEventsManager", "MaxHighScores"),
            ["global.apiexpose.startup_overlay.enabled"] = Bool("StartupOverlay", "Enabled"),
            ["global.apiexpose.swagger.enabled"] = Bool("Swagger", "Enabled"),
            ["global.apiexpose.websocket.enabled"] = Bool("WebSocket", "Enabled"),
            ["global.apiexpose.toast_notifications.enabled"] = Bool("Toasts", "Enabled"),
            ["global.apiexpose.api_notifications.enabled"] = Bool("ApiNotifications", "Enabled"),
            ["global.apiexpose.task_progress.enabled"] = Bool("TaskProgress", "Enabled")
        };

    private readonly ILogger<ApiExposeAppsettingsSyncService>? _logger;

    public ApiExposeAppsettingsSyncService(ILogger<ApiExposeAppsettingsSyncService>? logger = null)
    {
        _logger = logger;
    }

    public int ApplyEsSettingsChanges(IReadOnlyList<ApiExposeSettingChange> changes)
    {
        var applicable = changes
            .Where(change => Mappings.ContainsKey(change.Key))
            .ToList();
        if (applicable.Count == 0)
        {
            return 0;
        }

        lock (SyncLock)
        {
            var path = ResolveAppsettingsPath();
            var root = LoadRoot(path);
            var changed = 0;

            foreach (var change in applicable)
            {
                if (!Mappings.TryGetValue(change.Key, out var mapping) ||
                    !TryBuildJsonValue(mapping.Type, NormalizeSettingValue(change.Key, change.NewValue), out var value))
                {
                    continue;
                }

                changed += SetValue(root, mapping.Path, value) ? 1 : 0;
                if (string.Equals(
                    change.Key,
                    "global.apiexpose.romset.pack_installer.on_the_fly.trigger",
                    StringComparison.OrdinalIgnoreCase))
                {
                    var enabled = !string.Equals(
                        NormalizeOnTheFlyTrigger(change.NewValue),
                        "never",
                        StringComparison.OrdinalIgnoreCase);
                    changed += SetValue(
                        root,
                        WithApiExposeRoot(["RomSetManager", "OnTheFlyRomInstallerEnabled"]),
                        JsonValue.Create(enabled)!) ? 1 : 0;
                }
            }

            if (changed == 0)
            {
                return 0;
            }

            SaveRoot(path, root);
            _logger?.LogInformation("Synchronized {Count} ES interface setting(s) into appsettings.json.", changed);
            return changed;
        }
    }

    private static string ResolveAppsettingsPath()
    {
        return Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json");
    }

    private static JsonObject LoadRoot(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject { ["ApiExpose"] = new JsonObject() };
        }

        using var stream = File.OpenRead(path);
        return JsonNode.Parse(stream) as JsonObject ?? new JsonObject { ["ApiExpose"] = new JsonObject() };
    }

    private static void SaveRoot(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, root.ToJsonString(JsonOptions), new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);
    }

    private static bool SetValue(JsonObject root, IReadOnlyList<string> path, JsonNode value)
    {
        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }

            current = child;
        }

        var leaf = path[^1];
        if (JsonNode.DeepEquals(current[leaf], value))
        {
            return false;
        }

        current[leaf] = value;
        return true;
    }

    private static bool TryBuildJsonValue(AppsettingsValueType type, string rawValue, out JsonNode value)
    {
        value = null!;
        switch (type)
        {
            case AppsettingsValueType.Bool:
                if (!TryParseBool(rawValue, out var boolValue))
                {
                    return false;
                }

                value = JsonValue.Create(boolValue)!;
                return true;
            case AppsettingsValueType.Int:
                if (!int.TryParse((rawValue ?? string.Empty).Trim(), out var intValue))
                {
                    return false;
                }

                value = JsonValue.Create(intValue)!;
                return true;
            case AppsettingsValueType.String:
                value = JsonValue.Create((rawValue ?? string.Empty).Trim())!;
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeSettingValue(string key, string? rawValue)
    {
        var value = (rawValue ?? string.Empty).Trim();
        if (!IsAutoNeutralStringSetting(key))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(value) ||
            value.Equals("ignore", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : value;
    }

    private static bool IsAutoNeutralStringSetting(string key)
    {
        return key.Equals("global.apiexpose.romset.ra_mode", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.language_mode", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.region_mode", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.rom_version", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.official_games_mode", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.screen_orientation", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.cocktail_games", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.multi_screen_games", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.functional_second_screen", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.wide_surround_display", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.portable_link_gameplay", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.cabinet_controls_compatibility", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.player_count", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("global.apiexpose.romset.button_compatibility", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBool(string? value, out bool result)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
            case "":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static string NormalizeOnTheFlyTrigger(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "never" or "disabled" or "off" or "none" or "0" => "never",
            "game_selected" or "selected" => "game_selected",
            _ => "game_start"
        };
    }

    private static AppsettingsMapping Bool(params string[] path)
    {
        return new AppsettingsMapping(AppsettingsValueType.Bool, WithApiExposeRoot(path));
    }

    private static AppsettingsMapping Int(params string[] path)
    {
        return new AppsettingsMapping(AppsettingsValueType.Int, WithApiExposeRoot(path));
    }

    private static AppsettingsMapping String(params string[] path)
    {
        return new AppsettingsMapping(AppsettingsValueType.String, WithApiExposeRoot(path));
    }

    private static string[] WithApiExposeRoot(string[] path)
    {
        return ["ApiExpose", .. path];
    }

    private sealed record AppsettingsMapping(
        AppsettingsValueType Type,
        IReadOnlyList<string> Path);

    private enum AppsettingsValueType
    {
        Bool,
        Int,
        String
    }
}

public sealed record ApiExposeSettingChange(
    string Key,
    string PreviousValue,
    string NewValue);
