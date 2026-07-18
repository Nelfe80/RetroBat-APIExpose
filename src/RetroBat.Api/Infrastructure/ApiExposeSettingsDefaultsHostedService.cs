using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class ApiExposeSettingsDefaultsHostedService : IHostedService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly IEsSettingsStore _settingsStore;
    private readonly ILogger<ApiExposeSettingsDefaultsHostedService>? _logger;
    private IDisposable? _optionsReloadRegistration;

    public ApiExposeSettingsDefaultsHostedService(
        IOptionsMonitor<ApiExposeOptions> options,
        IEsSettingsStore settingsStore,
        ILogger<ApiExposeSettingsDefaultsHostedService>? logger = null)
    {
        _options = options;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureDefaults(_options.CurrentValue, cancellationToken);
            _optionsReloadRegistration = _options.OnChange(options =>
            {
                try
                {
                    EnsureDefaults(options, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
                {
                    _logger?.LogWarning(ex, "Unable to synchronize APIExpose menu defaults after appsettings reload.");
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Unable to initialize APIExpose menu defaults in es_settings.cfg.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _optionsReloadRegistration?.Dispose();
        return Task.CompletedTask;
    }

    private void EnsureDefaults(ApiExposeOptions options, CancellationToken cancellationToken)
    {
        var changed = _settingsStore.Update(document =>
        {
            var root = document.Root ?? throw new InvalidOperationException("es_settings.cfg root is missing.");
            var hasRomSetVisibilityMarker = HasSetting(root, "global.apiexpose.romset.defaults_initialized");
            var hasRomSetVisibilitySetting = root.Elements()
                .Any(element => (element.Attribute("name")?.Value ?? string.Empty)
                    .StartsWith("global.apiexpose.romset.show_", StringComparison.OrdinalIgnoreCase));
            var includeRomSetVisibilityDefaults = !hasRomSetVisibilityMarker && !hasRomSetVisibilitySetting;
            var defaults = BuildDefaults(options, includeRomSetVisibilityDefaults);
            var updated = false;
            foreach (var (key, value) in defaults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = root.Elements()
                    .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (ShouldSynchronizeFromAppsettings(key) &&
                        !string.Equals(existing.Attribute("value")?.Value ?? string.Empty, value, StringComparison.Ordinal))
                    {
                        existing.SetAttributeValue("value", value);
                        updated = true;
                    }

                    continue;
                }

                root.Add(new XText(Environment.NewLine + "  "));
                root.Add(new XElement("string", new XAttribute("name", key), new XAttribute("value", value)));
                updated = true;
            }

            updated |= SynchronizeLegacyScraperMediaSettings(root, options.MediaAllocation);
            updated |= NormalizeRomSetNoAutoSettings(root, options.RomSetManager);
            if (updated)
            {
                root.Add(new XText(Environment.NewLine));
            }

            return updated;
        }, cancellationToken);

        if (changed)
        {
            _logger?.LogInformation("APIExpose menu defaults initialized in es_settings.cfg.");
        }
    }

    private static bool HasSetting(XElement root, string key)
    {
        return root.Elements()
            .Any(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
    }

    private static List<(string Key, string Value)> BuildDefaults(ApiExposeOptions options, bool includeRomSetVisibilityDefaults)
    {
        var defaults = new List<(string Key, string Value)>
        {
            ("global.apiexpose.enabled", ToEsBool(options.Enabled)),
            ("global.apiexpose.local_media_manager.enabled", ToEsBool(options.LocalMediaManager.Enabled)),
            ("global.apiexpose.local_media_manager.populate_all_requested", ToEsBool(options.LocalMediaManager.PopulateAllGamelistsRequested)),
            ("global.apiexpose.local_media_manager.remove_roms_media_after_canonical_migration", ToEsBool(options.LocalMediaManager.RemoveRomsMediaAfterCanonicalMigration)),
            ("global.apiexpose.media_allocation.image_source", options.MediaAllocation.ImageSource),
            ("global.apiexpose.media_allocation.logo_source", options.MediaAllocation.LogoSource),
            ("global.apiexpose.media_allocation.thumb_source", options.MediaAllocation.ThumbSource),
            ("global.apiexpose.media_allocation.wheel_style", options.MediaAllocation.WheelStyle),
            ("global.apiexpose.media_allocation.region_mode", options.MediaAllocation.MediaRegionMode),
            ("global.apiexpose.media_allocation.logo_region_mode", options.MediaAllocation.LogoRegionMode),
            ("global.apiexpose.media_allocation.user_region", options.MediaAllocation.UserRegion),
            ("global.apiexpose.api.region_profile", options.ApiSettings.RegionProfile),
            ("global.apiexpose.api.language_profile", options.ApiSettings.LanguageProfile),
            ("global.apiexpose.api.repair_gamelists_on_startup", ToEsBool(options.ApiSettings.RepairGamelistsOnStartup)),
            ("global.apiexpose.api.sync_gamelists_with_system_language", ToEsBool(options.ApiSettings.SyncGamelistsWithSystemLanguage)),
            ("global.apiexpose.scraping.auto_enabled", ToEsBool(options.Scraping.AutoScrapingEnabled)),
            ("global.apiexpose.scraping.screenscraper.enabled", ToEsBool(options.Scraping.ScreenScraperEnabled)),
            ("global.apiexpose.scraping.description_translation.enabled", ToEsBool(options.Scraping.DescriptionTranslationEnabled)),
            ("global.apiexpose.scraping.queue.enabled", ToEsBool(options.Scraping.ScrapeQueueEnabled)),
            ("global.apiexpose.scraping.remote_after_local_only", ToEsBool(options.Scraping.RemoteAfterLocalOnly)),
            ("global.apiexpose.scraping.exact_local_media.enabled", ToEsBool(options.Scraping.ExactLocalMediaScrapingEnabled)),
            ("global.apiexpose.scraping.refresh_current_after_success", ToEsBool(options.Scraping.RefreshCurrentGameAfterRemoteSuccess)),
            ("global.apiexpose.scraping.notify_media.enabled", ToEsBool(options.Scraping.NotifyHeavyMediaScrapeEnabled)),
            ("global.apiexpose.scraping.marquee.enabled", ToEsBool(options.Scraping.MarqueeScrapingEnabled)),
            ("global.apiexpose.scraping.screen_marquee.enabled", ToEsBool(options.Scraping.ScreenMarqueeScrapingEnabled)),
            ("global.apiexpose.scraping.screen_marquee_small.enabled", ToEsBool(options.Scraping.ScreenMarqueeSmallScrapingEnabled)),
            ("global.apiexpose.scraping.steamgrid.enabled", ToEsBool(options.Scraping.SteamGridScrapingEnabled)),
            ("global.apiexpose.scraping.mix.enabled", ToEsBool(options.Scraping.MixScrapingEnabled)),
            ("global.apiexpose.scraping.maps.enabled", ToEsBool(options.Scraping.MapScrapingEnabled)),
            ("global.apiexpose.scraping.manuals.enabled", ToEsBool(options.Scraping.ManualScrapingEnabled)),
            ("global.apiexpose.scraping.magazines.enabled", ToEsBool(options.Scraping.MagazineScrapingEnabled)),
            ("global.apiexpose.scraping.videos.enabled", ToEsBool(options.Scraping.VideoScrapingEnabled)),
            ("global.apiexpose.scraping.video_normalized.enabled", ToEsBool(options.Scraping.VideoNormalizedScrapingEnabled)),
            ("global.apiexpose.scraping.bezels.enabled", ToEsBool(options.Scraping.BezelScrapingEnabled)),
            ("global.apiexpose.scraping.bezel_aspect", options.Scraping.BezelAspectRatio),
            ("global.apiexpose.scraping.bezel_orientation", options.Scraping.BezelOrientation),
            ("global.apiexpose.collections_auto_installer.hyperbat_theme.enabled", ToEsBool(options.ThemeDeployments.Enabled)),
            ("global.apiexpose.collections_auto_installer.refresh_current_after_success", ToEsBool(options.ThemeDeployments.RefreshCurrentGameAfterInstallSuccess)),
            ("global.apiexpose.datas_theme_expose.enabled", ToEsBool(options.DatasThemeExpose.Enabled)),
            ("global.apiexpose.datas_theme_expose.high_score.enabled", ToEsBool(options.DatasThemeExpose.HighScoreExposeEnabled)),
            ("global.apiexpose.datas_theme_expose.legacy_hiscore_theme.enabled", ToEsBool(options.DatasThemeExpose.LegacyHiscoreThemeExportEnabled)),
            ("global.apiexpose.datas_theme_expose.cpo_control_panel.enabled", ToEsBool(options.DatasThemeExpose.CpoControlPanelExposeEnabled)),
            ("global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled", ToEsBool(options.DatasThemeExpose.CpoPanelWebSocketPushEnabled)),
            ("global.apiexpose.datas_theme_expose.cpo.general_panel_buttons", options.DatasThemeExpose.GeneralPanelButtons),
            ("global.apiexpose.marquee_manager.enabled", ToEsBool(options.MarqueeManager.Enabled)),
            ("global.apiexpose.marquee_manager.websocket_assets.enabled", ToEsBool(options.MarqueeManager.WebSocketAssetsEnabled)),
            ("global.apiexpose.marquee_manager.autogen_profile", options.MarqueeManager.AutogenProfile),
            ("global.apiexpose.marquee_manager.system_marquee_theme_background.enabled", ToEsBool(options.MarqueeManager.SystemMarqueeThemeBackgroundEnabled)),
            ("global.apiexpose.marquee_manager.dmd_autogen_profile", options.MarqueeManager.DmdAutogenProfile),
            ("global.apiexpose.marquee_manager.autogen_notify.enabled", ToEsBool(options.MarqueeManager.AutogenNotifyEnabled)),
            ("global.apiexpose.rom_set_manager.enabled", ToEsBool(options.RomSetManager.Enabled)),
            ("global.apiexpose.game_events_manager.enabled", ToEsBool(options.GameEventsManager.Enabled)),
            ("global.apiexpose.game_events.retroarch_wrapper.enabled", ToEsBool(options.GameEventsManager.RetroArchWrapperEnabled)),
            ("global.apiexpose.game_events.console_high_score_capture.enabled", ToEsBool(options.GameEventsManager.ConsoleHighScoreCaptureEnabled)),
            ("global.apiexpose.game_events.mame_lua_ingame.enabled", ToEsBool(options.GameEventsManager.MameLuaIngameEnabled)),
            ("global.apiexpose.game_events.mame_outputs.enabled", ToEsBool(options.GameEventsManager.MameOutputsEnabled)),
            ("global.apiexpose.game_events.export_scores_on_game_end.enabled", ToEsBool(options.GameEventsManager.ExportScoresOnGameEndEnabled)),
            ("global.apiexpose.game_events.max_high_scores", Math.Clamp(options.GameEventsManager.MaxHighScores, 1, 100).ToString()),
            ("global.apiexpose.romset.defaults_initialized", "1"),
            ("global.apiexpose.romset.never_hide_favorites", ToEsBool(options.RomSetManager.NeverHideFavorites)),
            ("global.apiexpose.romset.profile", options.RomSetManager.Profile),
            ("global.apiexpose.romset.ra_mode", options.RomSetManager.RetroAchievementsMode),
            ("global.apiexpose.romset.language_mode", options.RomSetManager.LanguageMode),
            ("global.apiexpose.romset.region_mode", options.RomSetManager.RegionMode),
            ("global.apiexpose.romset.rom_version", options.RomSetManager.RomVersionMode),
            ("global.apiexpose.romset.official_games_mode", options.RomSetManager.OfficialGamesMode),
            ("global.apiexpose.romset.clones_mode", options.RomSetManager.ClonesMode),
            ("global.apiexpose.romset.prototypes_mode", options.RomSetManager.PrototypesMode),
            ("global.apiexpose.romset.demos_mode", options.RomSetManager.DemosMode),
            ("global.apiexpose.romset.beta_alpha_mode", options.RomSetManager.BetaAlphaMode),
            ("global.apiexpose.romset.location_tests_mode", options.RomSetManager.LocationTestsMode),
            ("global.apiexpose.romset.useful_patches_mode", options.RomSetManager.UsefulPatchesMode),
            ("global.apiexpose.romset.hacks_mods_mode", options.RomSetManager.HacksModsMode),
            ("global.apiexpose.romset.cheats_trainers_mode", options.RomSetManager.CheatsTrainersMode),
            ("global.apiexpose.romset.bootlegs_pirates_mode", options.RomSetManager.BootlegsPiratesMode),
            ("global.apiexpose.romset.unlicensed_mode", options.RomSetManager.UnlicensedMode),
            ("global.apiexpose.romset.homebrews_aftermarket_mode", options.RomSetManager.HomebrewsAftermarketMode),
            ("global.apiexpose.romset.adult_mode", options.RomSetManager.AdultMode),
            ("global.apiexpose.romset.casino_mode", options.RomSetManager.CasinoMode),
            ("global.apiexpose.romset.mahjong_mode", options.RomSetManager.MahjongMode),
            ("global.apiexpose.romset.quiz_mode", options.RomSetManager.QuizMode),
            ("global.apiexpose.romset.non_games_mode", options.RomSetManager.NonGamesMode),
            ("global.apiexpose.romset.unknown_roms_mode", options.RomSetManager.UnknownRomsMode),
            ("global.apiexpose.romset.arcade_diagnostics_mode", options.RomSetManager.ArcadeDiagnosticsMode),
            ("global.apiexpose.romset.variant_mode", options.RomSetManager.VariantMode),
            ("global.apiexpose.romset.translations", options.RomSetManager.Translations),
            ("global.apiexpose.romset.arcade_handling", options.RomSetManager.ArcadeHandling),
            ("global.apiexpose.romset.output_mode", options.RomSetManager.OutputMode),
            ("global.apiexpose.romset.debug_report", ToEsBool(options.RomSetManager.DebugReport)),
            ("global.apiexpose.romset.screen_orientation", options.RomSetManager.ScreenOrientation),
            ("global.apiexpose.romset.cocktail_games", options.RomSetManager.CocktailGames),
            ("global.apiexpose.romset.multi_screen_games", options.RomSetManager.MultiScreenGames),
            ("global.apiexpose.romset.functional_second_screen", options.RomSetManager.FunctionalSecondScreen),
            ("global.apiexpose.romset.wide_surround_display", options.RomSetManager.WideSurroundDisplay),
            ("global.apiexpose.romset.portable_link_gameplay", options.RomSetManager.PortableLinkGameplay),
            ("global.apiexpose.romset.cabinet_controls_compatibility", options.RomSetManager.CabinetControlsCompatibility),
            ("global.apiexpose.romset.player_count", options.RomSetManager.PlayerCount),
            ("global.apiexpose.romset.button_compatibility", options.RomSetManager.ButtonCompatibility),
            ("global.apiexpose.control_manager.cabinet_profile", options.ControlManager.CabinetProfile),
            ("global.apiexpose.control_manager.player_count", Math.Clamp(options.ControlManager.PlayerCount, 1, 8).ToString()),
            ("global.apiexpose.control_manager.buttons_per_player", Math.Clamp(options.ControlManager.ButtonsPerPlayer, 0, 12).ToString()),
            ("global.apiexpose.control_manager.arcade_joystick", ToEsBool(options.ControlManager.ArcadeJoystick)),
            ("global.apiexpose.control_manager.analog_joystick", ToEsBool(options.ControlManager.AnalogJoystick)),
            ("global.apiexpose.control_manager.rotary_joystick", ToEsBool(options.ControlManager.RotaryJoystick)),
            ("global.apiexpose.control_manager.spinner", options.ControlManager.Spinner),
            ("global.apiexpose.control_manager.trackball", options.ControlManager.Trackball),
            ("global.apiexpose.control_manager.wheel", options.ControlManager.Wheel),
            ("global.apiexpose.control_manager.pedals", options.ControlManager.Pedals),
            ("global.apiexpose.control_manager.shifter", options.ControlManager.Shifter),
            ("global.apiexpose.control_manager.lightgun", options.ControlManager.Lightgun),
            ("global.apiexpose.control_manager.dance_mat", options.ControlManager.DanceMat),
            ("global.apiexpose.control_manager.guitar", options.ControlManager.Guitar),
            ("global.apiexpose.control_manager.drums", options.ControlManager.Drums),
            ("global.apiexpose.control_manager.turntable", options.ControlManager.Turntable),
            ("global.apiexpose.control_manager.microphone", ToEsBool(options.ControlManager.Microphone)),
            ("global.apiexpose.control_manager.keyboard", ToEsBool(options.ControlManager.Keyboard)),
            ("global.apiexpose.control_manager.mouse", ToEsBool(options.ControlManager.Mouse)),
            ("global.apiexpose.control_manager.touchscreen", ToEsBool(options.ControlManager.Touchscreen)),
            ("global.apiexpose.control_manager.motion_controller", ToEsBool(options.ControlManager.MotionController)),
            ("global.apiexpose.romset.pack_installer.enabled", ToEsBool(options.RomSetManager.RomPackInstallerEnabled)),
            ("global.apiexpose.romset.pack_installer.unzip_roms", ToEsBool(options.RomSetManager.RomPackInstallerUnzipRoms)),
            ("global.apiexpose.romset.pack_installer.on_the_fly.trigger", NormalizeOnTheFlyTrigger(
                options.RomSetManager.OnTheFlyRomInstallerEnabled
                    ? options.RomSetManager.OnTheFlyRomExtractionTrigger
                    : "never")),
            ("global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end", ToEsBool(options.RomSetManager.OnTheFlyRomResetAfterGameEndEnabled)),
            ("global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end_delay_ms", Math.Clamp(options.RomSetManager.OnTheFlyRomResetAfterGameEndDelayMs, 0, 120000).ToString()),
            ("global.apiexpose.collections_pack_manager.enabled", ToEsBool(options.CollectionPackManager.Enabled)),
            ("global.apiexpose.collections_pack_manager.pack_installer.enabled", ToEsBool(options.CollectionPackManager.CollectionPackInstallerEnabled)),
            ("global.apiexpose.collections_pack_manager.dynamic_collections.enabled", ToEsBool(options.CollectionPackManager.DynamicCollectionsEnabled)),
            ("global.apiexpose.collections_pack_manager.static_collections.enabled", ToEsBool(options.CollectionPackManager.StaticCollectionsEnabled)),
            ("global.apiexpose.collections_pack_manager.apply_collection_theme_to_games.enabled", ToEsBool(options.CollectionPackManager.ApplyCollectionThemeToGamesEnabled)),
            ("global.apiexpose.startup_overlay.enabled", ToEsBool(options.StartupOverlay.Enabled)),
            ("global.apiexpose.toast_notifications.enabled", ToEsBool(options.Toasts.Enabled)),
            ("global.apiexpose.api_notifications.enabled", ToEsBool(options.ApiNotifications.Enabled)),
            ("global.apiexpose.task_progress.enabled", ToEsBool(options.TaskProgress.Enabled)),
            ("global.apiexpose.swagger.enabled", ToEsBool(options.Swagger.Enabled)),
            ("global.apiexpose.websocket.enabled", ToEsBool(options.WebSocket.Enabled))
        };

        if (includeRomSetVisibilityDefaults)
        {
            // legacy show_* keys superseded by the *_mode choices are no longer
            // seeded (audit-esmenu); only the three still read by the filter are
            defaults.InsertRange(7, new[]
            {
                ("global.apiexpose.romset.show_non_arcade", ToEsBool(options.RomSetManager.ShowNonArcade)),
                ("global.apiexpose.romset.show_horizontal", ToEsBool(options.RomSetManager.ShowHorizontal)),
                ("global.apiexpose.romset.show_vertical", ToEsBool(options.RomSetManager.ShowVertical))
            });
        }

        return defaults;
    }

    private static bool ShouldSynchronizeFromAppsettings(string key)
    {
        return key.StartsWith("global.apiexpose.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SynchronizeLegacyScraperMediaSettings(
        XElement root,
        ApiExposeOptions.MediaAllocationOptions mediaAllocation)
    {
        var changed = false;
        changed |= SetStringSetting(root, "ScrapperImageSrc", mediaAllocation.ImageSource);
        changed |= SetStringSetting(root, "ScrapperLogoSrc", mediaAllocation.LogoSource);
        changed |= SetStringSetting(root, "ScrapperThumbSrc", NormalizeLegacyThumbSource(mediaAllocation.ThumbSource));
        changed |= SetStringSetting(root, "WheelStyle", mediaAllocation.WheelStyle);
        return changed;
    }

    private static bool NormalizeRomSetNoAutoSettings(
        XElement root,
        ApiExposeOptions.RomSetManagerOptions romSetManager)
    {
        var changed = false;
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.ra_mode", romSetManager.RetroAchievementsMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.language_mode", romSetManager.LanguageMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.region_mode", romSetManager.RegionMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.rom_version", romSetManager.RomVersionMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.official_games_mode", romSetManager.OfficialGamesMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.clones_mode", romSetManager.ClonesMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.prototypes_mode", romSetManager.PrototypesMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.demos_mode", romSetManager.DemosMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.beta_alpha_mode", romSetManager.BetaAlphaMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.location_tests_mode", romSetManager.LocationTestsMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.useful_patches_mode", romSetManager.UsefulPatchesMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.hacks_mods_mode", romSetManager.HacksModsMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.cheats_trainers_mode", romSetManager.CheatsTrainersMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.bootlegs_pirates_mode", romSetManager.BootlegsPiratesMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.unlicensed_mode", romSetManager.UnlicensedMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.homebrews_aftermarket_mode", romSetManager.HomebrewsAftermarketMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.adult_mode", romSetManager.AdultMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.casino_mode", romSetManager.CasinoMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.mahjong_mode", romSetManager.MahjongMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.quiz_mode", romSetManager.QuizMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.non_games_mode", romSetManager.NonGamesMode);
        changed |= ReplaceAutoSetting(root, "global.apiexpose.romset.arcade_diagnostics_mode", romSetManager.ArcadeDiagnosticsMode);
        return changed;
    }

    private static bool ReplaceAutoSetting(XElement root, string key, string fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(fallbackValue) ||
            string.Equals(fallbackValue, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existing = root.Elements()
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return false;
        }

        var current = existing.Attribute("value")?.Value ?? string.Empty;
        if (!string.Equals(current, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        existing.SetAttributeValue("value", fallbackValue.Trim());
        return true;
    }

    private static string NormalizeLegacyThumbSource(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Equals("thumbnail", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("thumb", StringComparison.OrdinalIgnoreCase)
            ? "ss"
            : normalized;
    }

    private static bool SetStringSetting(XElement root, string key, string? value)
    {
        var normalizedValue = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return false;
        }

        var existing = root.Elements()
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var current = existing.Attribute("value")?.Value ?? string.Empty;
            if (string.Equals(current, normalizedValue, StringComparison.Ordinal))
            {
                return false;
            }

            existing.SetAttributeValue("value", normalizedValue);
            return true;
        }

        root.Add(new XText(Environment.NewLine + "  "));
        root.Add(new XElement("string", new XAttribute("name", key), new XAttribute("value", normalizedValue)));
        return true;
    }

    private static string? FirstExistingValue(XElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = root.Elements()
                .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")
                ?.Value
                ?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ToEsBool(bool value)
    {
        return value ? "1" : "0";
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
}
