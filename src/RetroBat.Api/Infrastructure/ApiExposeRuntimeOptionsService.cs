using System.Globalization;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class ApiExposeRuntimeOptionsService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly IEsSettingsStore _settingsStore;
    private readonly ILogger<ApiExposeRuntimeOptionsService> _logger;

    public ApiExposeRuntimeOptionsService(
        IOptionsMonitor<ApiExposeOptions> options,
        IEsSettingsStore settingsStore,
        ILogger<ApiExposeRuntimeOptionsService> logger)
    {
        _options = options;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public ApiExposeLocalOptionsSnapshot GetLocalOptionsSnapshot()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        var romSetVisibilityInitialized = IsRomSetVisibilityInitialized(esSettings);
        var entries = new List<ApiExposeLocalOptionEntry>
        {
            BuildBoolEntry(
                "global.apiexpose.enabled",
                "API EXPOSE",
                appOptions.Enabled,
                esSettings,
                "enforced-master-switch",
                "Coupe-circuit principal: desactive les flux, notifications, overlays et automatisations APIExpose dependantes."),
            BuildBoolEntry(
                "global.apiexpose.local_media_manager.enabled",
                "Local Media Manager",
                appOptions.LocalMediaManager.Enabled,
                esSettings,
                "enforced-circuit-breaker",
                "Coupe-circuit du gestionnaire media local: desactive projection gamelist, prefetch local et scraping automatique dependant."),
            BuildBoolEntry(
                "global.apiexpose.local_media_manager.populate_all_requested",
                "Local Media Manager - mettre a jour les medias",
                appOptions.LocalMediaManager.PopulateAllGamelistsRequested,
                esSettings,
                "action-switch-on-change",
                "Commande momentanee: quand la valeur change dans es_settings.cfg, APIExpose met a jour les gamelists depuis les medias/textes locaux. La valeur n'est pas remise a zero par l'API."),
            BuildBoolEntry(
                "global.apiexpose.local_media_manager.remove_roms_media_after_canonical_migration",
                "Local Media Manager - nettoyer les medias roms",
                appOptions.LocalMediaManager.RemoveRomsMediaAfterCanonicalMigration,
                esSettings,
                "enforced-on-startup",
                "Au demarrage, affiche une confirmation Windows avant migration vers media/. Si refuse, l'option et les modules impactes sont desactives. Si confirme, les copies legacy dans roms sont supprimees seulement apres deplacement canonique ou hash identique."),
            BuildStringEntry(
                "global.apiexpose.media_allocation.image_source",
                "Allocation media - image",
                appOptions.MediaAllocation.ImageSource,
                esSettings,
                "enforced-on-settings-change",
                "Source media utilisee pour alimenter la balise gamelist <image>."),
            BuildStringEntry(
                "global.apiexpose.media_allocation.logo_source",
                "Allocation media - logo/marquee",
                appOptions.MediaAllocation.LogoSource,
                esSettings,
                "enforced-on-settings-change",
                "Source media utilisee pour alimenter la balise gamelist <marquee>."),
            BuildStringEntry(
                "global.apiexpose.media_allocation.thumb_source",
                "Allocation media - vignette",
                appOptions.MediaAllocation.ThumbSource,
                esSettings,
                "enforced-on-settings-change",
                "Source media utilisee pour alimenter la balise gamelist <thumbnail>."),
            BuildStringEntry(
                "global.apiexpose.media_allocation.wheel_style",
                "Allocation media - style wheel",
                appOptions.MediaAllocation.WheelStyle,
                esSettings,
                "enforced-on-settings-change",
                "Style applique quand la source visible est wheel-hd."),
            BuildStringEntry(
                "global.apiexpose.media_allocation.region_mode",
                "Allocation media - region",
                appOptions.MediaAllocation.MediaRegionMode,
                esSettings,
                "enforced-on-settings-change",
                "Localisation des medias: match_rom_region suit la region de la ROM, content_region_profile suit le profil region central, interface_locale suit la langue ES. all reste accepte en compatibilite."),
            BuildStringEntry(
                "global.apiexpose.media_allocation.logo_region_mode",
                "Allocation media - logo/wheel",
                appOptions.MediaAllocation.LogoRegionMode,
                esSettings,
                "enforced-on-settings-change",
                "Localisation des logos/wheels: user_language favorise une version lisible par l'utilisateur; match_rom_region favorise le logo original de la ROM."),
            BuildStringEntry(
                "global.apiexpose.media_allocation.user_region",
                "Allocation media - region utilisateur",
                appOptions.MediaAllocation.UserRegion,
                esSettings,
                "enforced-on-settings-change",
                "Region prioritaire pour les medias localises quand la selection n'est pas automatique."),
            BuildBoolEntry(
                "global.apiexpose.scraping.auto_enabled",
                "Auto Scraping Manager",
                appOptions.Scraping.AutoScrapingEnabled,
                esSettings,
                "enforced-on-game-selected",
                "Active le scraping local puis distant et met a jour la fiche du jeu en temps reel quand un vrai changement est trouve."),
            BuildBoolEntry(
                "global.apiexpose.scraping.screenscraper.enabled",
                "Auto Scraping Manager - ScreenScraper",
                appOptions.Scraping.ScreenScraperEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le provider ScreenScraper pour alimenter la base media canonique."),
            BuildBoolEntry(
                "global.apiexpose.scraping.queue.enabled",
                "Auto Scraping Manager - file basse priorite",
                appOptions.Scraping.ScrapeQueueEnabled,
                esSettings,
                "enforced-when-idle",
                "Active la file de scraping distante basse priorite. Elle se met en pause des qu'un scrap live ou un game-selected arrive."),
            BuildBoolEntry(
                "global.apiexpose.scraping.remote_after_local_only",
                "Auto Scraping Manager - local d'abord",
                appOptions.Scraping.RemoteAfterLocalOnly,
                esSettings,
                "enforced-on-remote-scrape",
                "Le scraping distant ne demarre que si le scraping local laisse encore des medias ou textes manquants."),
            BuildBoolEntry(
                "global.apiexpose.scraping.exact_local_media.enabled",
                "Auto Scraping Manager - media exact",
                appOptions.Scraping.ExactLocalMediaScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise un scraping distant si le slot visible est satisfait par fallback local mais que la variante regionale exacte prioritaire manque."),
            BuildBoolEntry(
                "global.apiexpose.scraping.refresh_current_after_success",
                "Auto Scraping Manager - refresh fiche courante",
                appOptions.Scraping.RefreshCurrentGameAfterRemoteSuccess,
                esSettings,
                "enforced-after-success",
                "Demande un refresh ES du jeu courant apres un scraping distant avec vrai changement."),
            BuildBoolEntry(
                "global.apiexpose.scraping.notify_media.enabled",
                "Auto Scraping Manager - notifier le scrap media",
                appOptions.Scraping.NotifyHeavyMediaScrapeEnabled,
                esSettings,
                "enforced-after-heavy-media",
                "Notifie la fin d'un scraping distant de media lourd : manuel, magazine ou video."),
            BuildBoolEntry(
                "global.apiexpose.scraping.marquee.enabled",
                "Auto Scraping Manager - marquees",
                appOptions.Scraping.MarqueeScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des marquees en enrichissement. Un marquee choisi comme source visible reste prioritaire."),
            BuildBoolEntry(
                "global.apiexpose.scraping.screen_marquee.enabled",
                "Auto Scraping Manager - screen marquees",
                appOptions.Scraping.ScreenMarqueeScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des screen marquees en enrichissement."),
            BuildBoolEntry(
                "global.apiexpose.scraping.screen_marquee_small.enabled",
                "Auto Scraping Manager - small screen marquees",
                appOptions.Scraping.ScreenMarqueeSmallScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des small screen marquees en enrichissement."),
            BuildBoolEntry(
                "global.apiexpose.scraping.steamgrid.enabled",
                "Auto Scraping Manager - steamgrid",
                appOptions.Scraping.SteamGridScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des steamgrid en enrichissement. Un steamgrid choisi comme source visible reste prioritaire."),
            BuildBoolEntry(
                "global.apiexpose.scraping.mix.enabled",
                "Auto Scraping Manager - mix",
                appOptions.Scraping.MixScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des mixrbv1/mixrbv2 en enrichissement. Un mix choisi comme source visible reste prioritaire."),
            BuildBoolEntry(
                "global.apiexpose.scraping.maps.enabled",
                "Auto Scraping Manager - maps",
                appOptions.Scraping.MapScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des maps. Desactive par defaut car ces documents sont optionnels."),
            BuildBoolEntry(
                "global.apiexpose.scraping.manuals.enabled",
                "Auto Scraping Manager - manuels",
                appOptions.Scraping.ManualScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des manuels. Desactive par defaut car les fichiers sont lourds."),
            BuildBoolEntry(
                "global.apiexpose.scraping.videos.enabled",
                "Auto Scraping Manager - videos",
                appOptions.Scraping.VideoScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des videos. Desactive par defaut car les fichiers sont lourds."),
            BuildBoolEntry(
                "global.apiexpose.scraping.video_normalized.enabled",
                "Auto Scraping Manager - videos normalisees",
                appOptions.Scraping.VideoNormalizedScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des videos normalisees. Desactive par defaut."),
            BuildBoolEntry(
                "global.apiexpose.scraping.bezels.enabled",
                "Auto Scraping Manager - bezels",
                appOptions.Scraping.BezelScrapingEnabled,
                esSettings,
                "enforced-on-remote-scrape",
                "Autorise le telechargement distant des bezels selon le format et l'orientation choisis."),
            BuildStringEntry(
                "global.apiexpose.scraping.bezel_aspect",
                "Auto Scraping Manager - bezel aspect",
                appOptions.Scraping.BezelAspectRatio,
                esSettings,
                "enforced-on-remote-scrape",
                "Aspect ratio requested from ScreenScraper for the active bezel slot."),
            BuildStringEntry(
                "global.apiexpose.scraping.bezel_orientation",
                "Auto Scraping Manager - bezel orientation",
                appOptions.Scraping.BezelOrientation,
                esSettings,
                "enforced-on-remote-scrape",
                "Orientation requested from ScreenScraper for the active bezel slot."),
            BuildBoolEntry(
                "global.apiexpose.collections_auto_installer.hyperbat_theme.enabled",
                "Themes Manager - themes deployment",
                appOptions.ThemeDeployments.Enabled,
                esSettings,
                "enforced-on-collection-install",
                "Autorise le scraping, l'installation et le deploiement automatique des themes declares par APIExpose."),
            BuildBoolEntry(
                "global.apiexpose.collections_auto_installer.refresh_current_after_success",
                "Themes Manager - refresh fiche apres install",
                appOptions.ThemeDeployments.RefreshCurrentGameAfterInstallSuccess,
                esSettings,
                "enforced-after-success",
                "Demande un refresh ES du jeu courant apres une installation de theme reussie."),
            BuildBoolEntryWithFallback(
                "global.apiexpose.datas_theme_expose.enabled",
                "global.apiexpose.high_score_theme_extractor.enabled",
                "Themes Manager",
                appOptions.DatasThemeExpose.Enabled,
                esSettings,
                "enforced-on-settings-change",
                "Active les exports theme consolides .gameinfos pour panels CPO et high scores."),
            BuildBoolEntry(
                "global.apiexpose.datas_theme_expose.high_score.enabled",
                "High Score Expose",
                appOptions.DatasThemeExpose.HighScoreExposeEnabled,
                esSettings,
                "enforced-on-hiscore-update",
                "Met a jour uniquement le bloc hiscore dans le XML .gameinfos consolide."),
            BuildBoolEntry(
                "global.apiexpose.datas_theme_expose.legacy_hiscore_theme.enabled",
                "Exporter .Hiscore",
                appOptions.DatasThemeExpose.LegacyHiscoreThemeExportEnabled,
                esSettings,
                "enforced-on-hiscore-update",
                "Ecrit simplement les hiscores dans le dossier attendu par les themes legacy."),
            BuildBoolEntry(
                "global.apiexpose.datas_theme_expose.cpo_control_panel.enabled",
                "Control Panel Display",
                appOptions.DatasThemeExpose.CpoControlPanelExposeEnabled,
                esSettings,
                "enforced-on-settings-change",
                "Publie le layout panel resolu pour les themes et panels LED a partir du profil Control Panel Manager."),
            BuildBoolEntry(
                "global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled",
                "Control Panel Display - pousser panel WS",
                appOptions.DatasThemeExpose.CpoPanelWebSocketPushEnabled,
                esSettings,
                "enforced-on-ui-selection",
                "Pousse dans le flux WebSocket le layout panel resolu quand un systeme ou un jeu arcade est selectionne."),
            BuildBoolEntry(
                "global.apiexpose.marquee_manager.enabled",
                "Marquee Manager",
                appOptions.MarqueeManager.Enabled,
                esSettings,
                "enforced-circuit-breaker",
                "Prepare les flux de donnees utiles aux clients marquee: image marquee canonique, logo, fanart et contexte jeu."),
            BuildBoolEntry(
                "global.apiexpose.marquee_manager.websocket_assets.enabled",
                "Marquee Manager - datas WS",
                appOptions.MarqueeManager.WebSocketAssetsEnabled,
                esSettings,
                "declared",
                "Reserve l'envoi dans le flux WebSocket des medias et donnees necessaires a la generation d'un marquee a la volee."),
            BuildStringEntry(
                "global.apiexpose.marquee_manager.autogen_profile",
                "Marquee Manager - generation marquee",
                appOptions.MarqueeManager.AutogenProfile,
                esSettings,
                "declared",
                "Profil de generation utilise apres scraping quand aucun vrai marquee n'est disponible. 'no' desactive la generation."),
            BuildBoolEntry(
                "global.apiexpose.marquee_manager.system_marquee_theme_background.enabled",
                "Marquee Manager - background systeme",
                appOptions.MarqueeManager.SystemMarqueeThemeBackgroundEnabled,
                esSettings,
                "declared",
                "Utilise le fanart/background du theme pour composer les marquees systeme generes. Si desactive, la composition reste sur fond noir."),
            BuildStringEntry(
                "global.apiexpose.marquee_manager.dmd_autogen_profile",
                "Marquee Manager - generation DMD",
                appOptions.MarqueeManager.DmdAutogenProfile,
                esSettings,
                "declared",
                "Profil de generation DMD pour systemes et jeux quand aucun dmd.png n'est disponible. 'no' desactive la generation."),
            BuildBoolEntry(
                "global.apiexpose.marquee_manager.autogen_notify.enabled",
                "Marquee Manager - notification generation",
                appOptions.MarqueeManager.AutogenNotifyEnabled,
                esSettings,
                "declared",
                "Affiche une notification ES quand un marquee est genere automatiquement."),
            BuildBoolEntry(
                "global.apiexpose.rom_set_manager.enabled",
                "Roms Manager",
                appOptions.RomSetManager.Enabled,
                esSettings,
                "enforced-on-settings-change",
                "Active le moteur de filtres romset; si coupe, les masquages APIExpose sont restaures."),
            BuildBoolEntry(
                "global.apiexpose.romset.never_hide_favorites",
                "Rom Set - ne jamais cacher les favoris",
                appOptions.RomSetManager.NeverHideFavorites,
                esSettings,
                "enforced-on-apply",
                "Si active, les jeux favoris restent visibles meme si un filtre Roms Manager devrait les masquer."),
            BuildStringEntry(
                "global.apiexpose.romset.profile",
                "Roms Manager - profil",
                appOptions.RomSetManager.Profile,
                esSettings,
                "enforced-on-apply",
                "Profil d'intention joueur; les filtres visibles restent explicites dans le menu Roms Manager."),
            BuildStringEntry(
                "global.apiexpose.romset.ra_mode",
                "Roms Manager - RetroAchievements",
                appOptions.RomSetManager.RetroAchievementsMode,
                esSettings,
                "enforced-on-apply",
                "NO FILTER ignore le statut RA; ALWAYS SHOW protege les jeux RA; SHOW ONLY masque les jeux sans cheevosId."),
            BuildStringEntry(
                "global.apiexpose.romset.language_mode",
                "Roms Manager - langue",
                appOptions.RomSetManager.LanguageMode,
                esSettings,
                "enforced-on-apply",
                "SHOW ALL ne filtre pas; SHOW ONLY MY LANGUAGE masque les autres langues quand une version de la langue existe."),
            BuildStringEntry(
                "global.apiexpose.romset.region_mode",
                "Roms Manager - region",
                appOptions.RomSetManager.RegionMode,
                esSettings,
                "enforced-on-apply",
                "SHOW ALL ne filtre pas; SHOW ONLY MY REGION masque les autres regions quand une version de la region existe."),
            BuildStringEntry(
                "global.apiexpose.romset.rom_version",
                "Roms Manager - version ROM",
                appOptions.RomSetManager.RomVersionMode,
                esSettings,
                "enforced-on-apply",
                "Controle le departage des variantes: stable, latest, original ou enhanced."),
            BuildStringEntry(
                "global.apiexpose.romset.official_games_mode",
                "Roms Manager - jeux officiels",
                appOptions.RomSetManager.OfficialGamesMode,
                esSettings,
                "enforced-on-apply",
                "SHOW/HIDE force l'affichage ou le masquage des jeux officiels standards et clones sans tag special."),
            BuildStringEntry(
                "global.apiexpose.romset.clones_mode",
                "Roms Manager - clones",
                appOptions.RomSetManager.ClonesMode,
                esSettings,
                "enforced-on-apply",
                "SHOW/HIDE force l'affichage ou le masquage des clones secondaires."),
            BuildStringEntry(
                "global.apiexpose.romset.prototypes_mode",
                "Roms Manager - prototypes",
                appOptions.RomSetManager.PrototypesMode,
                esSettings,
                "enforced-on-apply",
                "SHOW/HIDE force les prototypes."),
            BuildStringEntry(
                "global.apiexpose.romset.demos_mode",
                "Roms Manager - demos",
                appOptions.RomSetManager.DemosMode,
                esSettings,
                "enforced-on-apply",
                "SHOW/HIDE force les demos."),
            BuildStringEntry(
                "global.apiexpose.romset.beta_alpha_mode",
                "Roms Manager - beta et alpha",
                appOptions.RomSetManager.BetaAlphaMode,
                esSettings,
                "enforced-on-apply",
                "AUTO laisse le profil decider; SHOW/HIDE force les versions beta et alpha."),
            BuildStringEntry(
                "global.apiexpose.romset.location_tests_mode",
                "Roms Manager - location tests (legacy)",
                appOptions.RomSetManager.LocationTestsMode,
                esSettings,
                "enforced-on-apply",
                "Compatibilite: l'option visible ARCADE TESTS AND DIAGNOSTICS pilote aussi les location tests arcade."),
            BuildStringEntry(
                "global.apiexpose.romset.useful_patches_mode",
                "Roms Manager - patches utiles",
                appOptions.RomSetManager.UsefulPatchesMode,
                esSettings,
                "enforced-on-apply",
                "Controle bugfix, QoL, restoration, widescreen, uncensored et patches utiles identifies."),
            BuildStringEntry(
                "global.apiexpose.romset.hacks_mods_mode",
                "Roms Manager - hacks et mods",
                appOptions.RomSetManager.HacksModsMode,
                esSettings,
                "enforced-on-apply",
                "Controle romhacks, randomizers, total conversions et mods identifies."),
            BuildStringEntry(
                "global.apiexpose.romset.cheats_trainers_mode",
                "Roms Manager - cheats et trainers",
                appOptions.RomSetManager.CheatsTrainersMode,
                esSettings,
                "enforced-on-apply",
                "Controle trainers, cheats, infinite lives, debug menus et variantes similaires."),
            BuildStringEntry(
                "global.apiexpose.romset.bootlegs_pirates_mode",
                "Roms Manager - bootlegs et pirates",
                appOptions.RomSetManager.BootlegsPiratesMode,
                esSettings,
                "enforced-on-apply",
                "Controle bootlegs arcade, pirates console et copies non officielles."),
            BuildStringEntry(
                "global.apiexpose.romset.unlicensed_mode",
                "Roms Manager - unlicensed",
                appOptions.RomSetManager.UnlicensedMode,
                esSettings,
                "enforced-on-apply",
                "Controle les jeux non licencies separes des homebrews et des bootlegs."),
            BuildStringEntry(
                "global.apiexpose.romset.homebrews_aftermarket_mode",
                "Roms Manager - homebrews",
                appOptions.RomSetManager.HomebrewsAftermarketMode,
                esSettings,
                "enforced-on-apply",
                "Controle homebrews, aftermarket et sorties retro modernes."),
            BuildStringEntry(
                "global.apiexpose.romset.adult_mode",
                "Roms Manager - adult",
                appOptions.RomSetManager.AdultMode,
                esSettings,
                "enforced-on-apply",
                "Controle les jeux adultes identifies par la base consolidee."),
            BuildStringEntry(
                "global.apiexpose.romset.casino_mode",
                "Roms Manager - casino",
                appOptions.RomSetManager.CasinoMode,
                esSettings,
                "enforced-on-apply",
                "Controle casino, gambling, poker, slot, pachinko et contenus proches."),
            BuildStringEntry(
                "global.apiexpose.romset.mahjong_mode",
                "Roms Manager - mahjong",
                appOptions.RomSetManager.MahjongMode,
                esSettings,
                "enforced-on-apply",
                "Controle les jeux mahjong identifies."),
            BuildStringEntry(
                "global.apiexpose.romset.quiz_mode",
                "Roms Manager - quiz",
                appOptions.RomSetManager.QuizMode,
                esSettings,
                "enforced-on-apply",
                "Controle les jeux de quiz identifies."),
            BuildStringEntry(
                "global.apiexpose.romset.non_games_mode",
                "Roms Manager - non-games",
                appOptions.RomSetManager.NonGamesMode,
                esSettings,
                "enforced-on-apply",
                "Controle BIOS, devices, samples, drivers techniques et entrees non jouables."),
            BuildStringEntry(
                "global.apiexpose.romset.arcade_diagnostics_mode",
                "Roms Manager - diagnostics arcade",
                appOptions.RomSetManager.ArcadeDiagnosticsMode,
                esSettings,
                "enforced-on-apply",
                "Controle location tests, programmes de test, diagnostic, calibration et service arcade."),
            BuildBoolEntry(
                "global.apiexpose.romset.only_retroachievements",
                "Rom Set - uniquement jeux RetroAchievements",
                appOptions.RomSetManager.OnlyRetroAchievements,
                esSettings,
                "enforced-on-apply",
                "Si active, le Roms Manager masque les jeux sans cheevosId valide. Si inactive, tous les jeux restent visibles."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_clones",
                "Rom Set - afficher clones et variantes",
                appOptions.RomSetManager.ShowClones,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans cl."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_prototypes",
                "Rom Set - afficher prototypes",
                appOptions.RomSetManager.ShowPrototypes,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans pr."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_bootlegs_hacks",
                "Rom Set - afficher hacks et bootlegs",
                appOptions.RomSetManager.ShowBootlegsAndHacks,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans bt."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_adult",
                "Rom Set - afficher jeux adultes",
                appOptions.RomSetManager.ShowAdult,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans adu."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_casino",
                "Rom Set - afficher casino",
                appOptions.RomSetManager.ShowCasino,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans cas."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_mahjong",
                "Rom Set - afficher mahjong",
                appOptions.RomSetManager.ShowMahjong,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans mah."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_non_games",
                "Rom Set - afficher non-jeux",
                appOptions.RomSetManager.ShowNonGames,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans ng."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_non_arcade",
                "Rom Set - afficher non-arcade",
                appOptions.RomSetManager.ShowNonArcade,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les ROMs declarees dans np."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_horizontal",
                "Rom Set - afficher jeux horizontaux",
                appOptions.RomSetManager.ShowHorizontal,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les groupes y=1."),
            BuildRomSetShowEntry(
                "global.apiexpose.romset.show_vertical",
                "Rom Set - afficher jeux verticaux",
                appOptions.RomSetManager.ShowVertical,
                esSettings,
                romSetVisibilityInitialized,
                "enforced-on-apply",
                "Si desactive, le Roms Manager masque les groupes t=1."),
            BuildStringEntry(
                "global.apiexpose.romset.screen_orientation",
                "Rom Set - orientation ecran et cocktail",
                appOptions.RomSetManager.ScreenOrientation,
                esSettings,
                "enforced-on-apply",
                "Filtre les jeux horizontaux, verticaux ou cocktail a partir du champ ori et des marqueurs cocktail."),
            BuildStringEntry(
                "global.apiexpose.romset.cocktail_games",
                "Rom Set - bornes cocktail (legacy)",
                appOptions.RomSetManager.CocktailGames,
                esSettings,
                "enforced-on-apply",
                "Compatibilite: l'option visible SCREEN / COCKTAIL ORIENTATION pilote maintenant les jeux cocktail."),
            BuildStringEntry(
                "global.apiexpose.romset.multi_screen_games",
                "Rom Set - multi-ecrans",
                appOptions.RomSetManager.MultiScreenGames,
                esSettings,
                "enforced-on-apply",
                "Filtre les ROMs avec plusieurs ecrans declares."),
            BuildStringEntry(
                "global.apiexpose.romset.functional_second_screen",
                "Rom Set - second ecran gameplay",
                appOptions.RomSetManager.FunctionalSecondScreen,
                esSettings,
                "enforced-on-apply",
                "Filtre les jeux qui exploitent un second ecran dans le gameplay."),
            BuildStringEntry(
                "global.apiexpose.romset.wide_surround_display",
                "Rom Set - affichage etendu",
                appOptions.RomSetManager.WideSurroundDisplay,
                esSettings,
                "enforced-on-apply",
                "Filtre les bornes avec affichage large, surround ou playfield etendu."),
            BuildStringEntry(
                "global.apiexpose.romset.portable_link_gameplay",
                "Rom Set - liens portables",
                appOptions.RomSetManager.PortableLinkGameplay,
                esSettings,
                "enforced-on-apply",
                "Filtre les jeux exploitant VMU, GBA link, DS link ou ecran portable."),
            BuildStringEntry(
                "global.apiexpose.romset.cabinet_controls_compatibility",
                "Rom Set - compatibilite borne",
                appOptions.RomSetManager.CabinetControlsCompatibility,
                esSettings,
                "enforced-on-apply",
                "Utilise le profil Control Panel Manager pour masquer les jeux demandant des controles absents."),
            BuildStringEntry(
                "global.apiexpose.romset.player_count",
                "Rom Set - nombre de joueurs",
                appOptions.RomSetManager.PlayerCount,
                esSettings,
                "enforced-on-apply",
                "Filtre les jeux qui demandent plus de panels joueurs que la borne declaree."),
            BuildStringEntry(
                "global.apiexpose.romset.button_compatibility",
                "Rom Set - nombre de boutons",
                appOptions.RomSetManager.ButtonCompatibility,
                esSettings,
                "enforced-on-apply",
                "Filtre les jeux qui demandent plus de boutons que le panel declare."),
            BuildStringEntry(
                "global.apiexpose.control_manager.cabinet_profile",
                "Control Panel Manager - profil borne",
                appOptions.ControlManager.CabinetProfile,
                esSettings,
                "declared",
                "Profil materiel de la borne utilise comme base de filtrage."),
            BuildStringEntry(
                "global.apiexpose.control_manager.player_count",
                "Control Panel Manager - panels joueurs",
                appOptions.ControlManager.PlayerCount.ToString(),
                esSettings,
                "declared",
                "Nombre de panels joueurs disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.buttons_per_player",
                "Control Panel Manager - boutons par joueur",
                appOptions.ControlManager.ButtonsPerPlayer.ToString(),
                esSettings,
                "declared",
                "Nombre de boutons disponibles par joueur."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.arcade_joystick",
                "Control Panel Manager - joystick arcade",
                appOptions.ControlManager.ArcadeJoystick,
                esSettings,
                "declared",
                "Indique si la borne dispose d'un joystick arcade numerique."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.analog_joystick",
                "Control Panel Manager - joystick analogique",
                appOptions.ControlManager.AnalogJoystick,
                esSettings,
                "declared",
                "Indique si la borne dispose d'un stick analogique, yoke ou controle assimilable."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.rotary_joystick",
                "Control Panel Manager - rotary joystick",
                appOptions.ControlManager.RotaryJoystick,
                esSettings,
                "declared",
                "Indique si la borne dispose d'un joystick rotary."),
            BuildStringEntry(
                "global.apiexpose.control_manager.spinner",
                "Control Panel Manager - spinner",
                appOptions.ControlManager.Spinner,
                esSettings,
                "declared",
                "Nombre de spinners disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.trackball",
                "Control Panel Manager - trackball",
                appOptions.ControlManager.Trackball,
                esSettings,
                "declared",
                "Nombre de trackballs disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.wheel",
                "Control Panel Manager - volant",
                appOptions.ControlManager.Wheel,
                esSettings,
                "declared",
                "Nombre de volants disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.pedals",
                "Control Panel Manager - pedales",
                appOptions.ControlManager.Pedals,
                esSettings,
                "declared",
                "Nombre de jeux de pedales disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.shifter",
                "Control Panel Manager - shifter",
                appOptions.ControlManager.Shifter,
                esSettings,
                "declared",
                "Nombre de shifters disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.lightgun",
                "Control Panel Manager - lightguns",
                appOptions.ControlManager.Lightgun,
                esSettings,
                "declared",
                "Nombre de lightguns disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.dance_mat",
                "Control Panel Manager - tapis de danse",
                appOptions.ControlManager.DanceMat,
                esSettings,
                "declared",
                "Nombre de tapis de danse disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.guitar",
                "Control Panel Manager - guitares",
                appOptions.ControlManager.Guitar,
                esSettings,
                "declared",
                "Nombre de guitares rythme disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.drums",
                "Control Panel Manager - batterie",
                appOptions.ControlManager.Drums,
                esSettings,
                "declared",
                "Nombre de batteries rythme disponibles."),
            BuildStringEntry(
                "global.apiexpose.control_manager.turntable",
                "Control Panel Manager - platine",
                appOptions.ControlManager.Turntable,
                esSettings,
                "declared",
                "Nombre de platines disponibles."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.microphone",
                "Control Panel Manager - microphone",
                appOptions.ControlManager.Microphone,
                esSettings,
                "declared",
                "Indique si un microphone est disponible."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.keyboard",
                "Control Panel Manager - clavier",
                appOptions.ControlManager.Keyboard,
                esSettings,
                "declared",
                "Indique si un clavier est disponible."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.mouse",
                "Control Panel Manager - souris",
                appOptions.ControlManager.Mouse,
                esSettings,
                "declared",
                "Indique si une souris est disponible."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.touchscreen",
                "Control Panel Manager - ecran tactile",
                appOptions.ControlManager.Touchscreen,
                esSettings,
                "declared",
                "Indique si un ecran tactile est disponible."),
            BuildBoolEntry(
                "global.apiexpose.control_manager.motion_controller",
                "Control Panel Manager - motion controller",
                appOptions.ControlManager.MotionController,
                esSettings,
                "declared",
                "Indique si un controleur de mouvement est disponible."),
            BuildStringEntry(
                "global.apiexpose.romset.variant_mode",
                "Rom Set - mode variantes",
                appOptions.RomSetManager.VariantMode,
                esSettings,
                "enforced-on-audit",
                "display_only calcule les variantes preferees sans ecrire; hide_variants masque les variantes non retenues."),
            BuildStringEntry(
                "global.apiexpose.api.region_profile",
                "API Settings - profil region",
                appOptions.ApiSettings.RegionProfile,
                esSettings,
                "enforced-on-audit",
                "Profil region central utilise par Roms Manager, medias localises et services qui doivent convertir une preference utilisateur en region."),
            BuildStringEntry(
                "global.apiexpose.api.language_profile",
                "API Settings - profil langue",
                appOptions.ApiSettings.LanguageProfile,
                esSettings,
                "enforced-on-audit",
                "Profil langue central utilise par Roms Manager, metadata locale et services qui doivent convertir une preference utilisateur en langue."),
            BuildBoolEntry(
                "global.apiexpose.api.repair_gamelists_on_startup",
                "API Settings - reparer gamelists au demarrage",
                appOptions.ApiSettings.RepairGamelistsOnStartup,
                esSettings,
                "enforced-on-next-startup",
                "Active la normalisation globale des slots visibles et textes gamelist pendant le demarrage APIExpose. Si l'option est desactivee, les normalisations explicites restent disponibles."),
            BuildBoolEntry(
                "global.apiexpose.api.sync_gamelists_with_system_language",
                "API Settings - synchroniser gamelists avec langue systeme",
                appOptions.ApiSettings.SyncGamelistsWithSystemLanguage,
                esSettings,
                "enforced-on-es-language-change",
                "Quand la langue systeme EmulationStation change, relance la normalisation locale des gamelists avec la nouvelle langue. Si off, aucune synchronisation automatique n'est effectuee."),
            BuildStringEntry(
                "global.apiexpose.romset.translations",
                "Rom Set - traductions",
                appOptions.RomSetManager.Translations,
                esSettings,
                "enforced-on-audit",
                "Controle si une traduction peut devenir la variante principale."),
            BuildStringEntry(
                "global.apiexpose.romset.arcade_handling",
                "Rom Set - arcade",
                appOptions.RomSetManager.ArcadeHandling,
                esSettings,
                "enforced-on-audit",
                "Controle le traitement parent/clone pour les systemes arcade."),
            BuildStringEntry(
                "global.apiexpose.romset.output_mode",
                "Rom Set - sortie",
                appOptions.RomSetManager.OutputMode,
                esSettings,
                "enforced-on-apply",
                "gamelist_hidden ecrit les balises hidden; report_only, collection et api_filter restent sans ecriture gamelist pour l'instant."),
            BuildBoolEntry(
                "global.apiexpose.romset.debug_report",
                "Rom Set - rapport debug",
                appOptions.RomSetManager.DebugReport,
                esSettings,
                "enforced-on-audit",
                "Genere un rapport JSON des decisions, scores et raisons."),
            BuildBoolEntry(
                "global.apiexpose.romset.pack_installer.enabled",
                "Roms - import pack au demarrage",
                appOptions.RomSetManager.RomPackInstallerEnabled,
                esSettings,
                "enforced-on-startup",
                "Si active, importe durablement au demarrage les ROMs, medias et gamelists presents dans package-installer (.7z, .zip ou .rar), avec index hash pour eviter les reinstallations."),
            BuildBoolEntry(
                "global.apiexpose.romset.pack_installer.unzip_roms",
                "Roms - decompresser les ROMs",
                appOptions.RomSetManager.RomPackInstallerUnzipRoms,
                esSettings,
                "enforced-on-package-install",
                "Si active, le futur installateur extrait les archives ROM internes vers le dossier roms du systeme; par defaut les .zip/.7z ROM restent tels quels."),
            BuildStringEntry(
                "global.apiexpose.romset.pack_installer.on_the_fly.trigger",
                "Roms - extraction ROM a la demande",
                GetConfiguredOnTheFlyExtractionTrigger(
                    appOptions.RomSetManager.OnTheFlyRomInstallerEnabled,
                    appOptions.RomSetManager.OnTheFlyRomExtractionTrigger),
                esSettings,
                "enforced-on-selected-or-start",
                "never desactive l'extraction a la demande; game_start extrait juste avant le lancement via un script ES synchrone; game_selected precharge apres selection avec avertissement."),
            BuildBoolEntry(
                "global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end",
                "Roms - reset apres sortie jeu",
                appOptions.RomSetManager.OnTheFlyRomResetAfterGameEndEnabled,
                esSettings,
                "enforced-on-game-end",
                "Remplace la ROM extraite a la demande par son placeholder apres la sortie du jeu."),
            BuildIntEntry(
                "global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end_delay_ms",
                "Roms - delai reset apres sortie",
                appOptions.RomSetManager.OnTheFlyRomResetAfterGameEndDelayMs,
                esSettings,
                "enforced-on-game-end",
                "Delai avant de remettre le placeholder, pour laisser ES stabiliser son retour liste."),
            BuildBoolEntry(
                "global.apiexpose.collections_pack_manager.enabled",
                "Collections Pack Manager",
                appOptions.CollectionPackManager.Enabled,
                esSettings,
                "enforced-on-startup",
                "Active le gestionnaire d'installation de packs collections pour les themes deployables."),
            BuildBoolEntry(
                "global.apiexpose.collections_pack_manager.pack_installer.enabled",
                "Collections Pack - Collection Pack Installer",
                appOptions.CollectionPackManager.CollectionPackInstallerEnabled,
                esSettings,
                "enforced-on-startup",
                "Installe au demarrage les packs collections presents dans package-installer/collections/theme-name, avec index hash."),
            BuildBoolEntry(
                "global.apiexpose.collections_pack_manager.dynamic_collections.enabled",
                "Collections Pack - collections dynamiques",
                appOptions.CollectionPackManager.DynamicCollectionsEnabled,
                esSettings,
                "enforced-on-startup",
                "Genere des collections dynamiques .xcc basees sur la balise family canonique detectee dans les gamelists."),
            BuildBoolEntry(
                "global.apiexpose.collections_pack_manager.static_collections.enabled",
                "Collections Pack - collections statiques",
                appOptions.CollectionPackManager.StaticCollectionsEnabled,
                esSettings,
                "enforced-on-startup",
                "Genere des collections statiques custom-*.cfg avec les chemins des jeux detectes; utile en mode compatibilite."),
            BuildBoolEntry(
                "global.apiexpose.collections_pack_manager.apply_collection_theme_to_games.enabled",
                "Collections Pack - aux jeux de la collection",
                appOptions.CollectionPackManager.ApplyCollectionThemeToGamesEnabled,
                esSettings,
                "enforced-on-startup",
                "Genere un theme jeu depuis le theme systeme de la collection pour les jeux de la meme family, si aucun theme jeu dedie n'existe."),
            BuildBoolEntryWithFallback(
                "global.apiexpose.game_events_manager.enabled",
                "global.apiexpose.retroarch_wrapper.enabled",
                "Game Events Manager",
                appOptions.GameEventsManager.Enabled,
                esSettings,
                "enforced-on-startup",
                "Active l'ecoute des evenements de jeu, dont les evenements ingame, arcade et la capture high score console."),
            BuildBoolEntryWithFallback(
                "global.apiexpose.game_events.retroarch_wrapper.enabled",
                "global.apiexpose.retroarch_wrapper.enabled",
                "Game Events - ecouteur d'evenements ingame",
                appOptions.GameEventsManager.RetroArchWrapperEnabled,
                esSettings,
                "enforced-on-startup",
                "Active l'ecoute des evenements ingame pour les systemes compatibles."),
            BuildBoolEntry(
                "global.apiexpose.game_events.console_high_score_capture.enabled",
                "Game Events - capture high score console",
                appOptions.GameEventsManager.ConsoleHighScoreCaptureEnabled,
                esSettings,
                "enforced-on-startup",
                "Capture les signaux SCORE ingame pour les consoles compatibles."),
            BuildBoolEntry(
                "global.apiexpose.game_events.mame_lua_ingame.enabled",
                "Game Events - MAME RAM ingame",
                appOptions.GameEventsManager.MameLuaIngameEnabled,
                esSettings,
                "enforced-on-startup",
                "Active le pont Lua MAME standalone pour publier les evenements RAM arcade dans /ws/ingame."),
            BuildBoolEntry(
                "global.apiexpose.game_events.mame_outputs.enabled",
                "Game Events - ecouteur d'evenements arcade",
                appOptions.GameEventsManager.MameOutputsEnabled,
                esSettings,
                "enforced-on-startup",
                "Active l'ecoute des evenements arcade exposes par les emulateurs compatibles."),
            BuildBoolEntry(
                "global.apiexpose.game_events.export_scores_on_game_end.enabled",
                "Game Events - export score au game-end",
                appOptions.GameEventsManager.ExportScoresOnGameEndEnabled,
                esSettings,
                "enforced-on-game-end",
                "Ecrit le score console capture dans .gameinfos au game-end."),
            BuildIntEntry(
                "global.apiexpose.game_events.max_high_scores",
                "Game Events - max high scores",
                appOptions.GameEventsManager.MaxHighScores,
                esSettings,
                "enforced-on-game-end",
                "Nombre maximal de lignes high score conservees dans l'export console."),
            BuildBoolEntry(
                "global.apiexpose.swagger.enabled",
                "Swagger",
                appOptions.Swagger.Enabled,
                esSettings,
                "enforced",
                "Active l'interface Swagger sur http://127.0.0.1:12345/swagger/index.html."),
            BuildBoolEntry(
                "global.apiexpose.websocket.enabled",
                "Flux WebSocket",
                appOptions.WebSocket.Enabled,
                esSettings,
                "enforced",
                "Active le flux temps reel sur ws://127.0.0.1:12345/ws."),
            BuildBoolEntry(
                "global.apiexpose.toast_notifications.enabled",
                "Notifications toast",
                appOptions.Toasts.Enabled,
                esSettings,
                "enforced",
                "Active les notifications toast APIExpose affichees au-dessus d'EmulationStation. Les notifications ES natives via l'API ES :1234 sont un canal distinct."),
            BuildBoolEntry(
                "global.apiexpose.api_notifications.enabled",
                "Notifications API",
                appOptions.ApiNotifications.Enabled,
                esSettings,
                "enforced",
                "Active les notifications natives poussees vers l'API EmulationStation :1234/notify."),
            BuildBoolEntry(
                "global.apiexpose.task_progress.enabled",
                "Barres de progression toast",
                appOptions.TaskProgress.Enabled,
                esSettings,
                "enforced",
                "Option exposee; ces barres sont des overlays toast de progression, distincts des notifications ES natives."),
            BuildBoolEntry(
                "global.apiexpose.startup_overlay.enabled",
                "Splashscreen API",
                appOptions.StartupOverlay.Enabled,
                esSettings,
                "enforced",
                "Active le splash de demarrage APIExpose en tenant compte du coupe-circuit principal.")
        };

        return new ApiExposeLocalOptionsSnapshot
        {
            EsSettingsPath = RetroBatPaths.EmulationStationSettingsPath,
            EsSettingsExists = File.Exists(RetroBatPaths.EmulationStationSettingsPath),
            Entries = entries,
            RawApiExposeSettings = esSettings
                .Where(pair => pair.Key.Contains(".apiexpose.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    public bool IsSwaggerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool("global.apiexpose.swagger.enabled", appOptions.Swagger.Enabled, esSettings);
    }

    public bool IsWebSocketEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool("global.apiexpose.websocket.enabled", appOptions.WebSocket.Enabled, esSettings);
    }

    public bool IsApiExposeEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions);
    }

    public bool IsTestModeEnabled()
    {
        var envValue = Environment.GetEnvironmentVariable("APIEXPOSE_TEST_MODE");
        if (IsTruthy(envValue))
        {
            return true;
        }

        return _options.CurrentValue.TestMode.Enabled;
    }

    public bool ShouldSkipInteractiveStartupWork()
    {
        var appOptions = _options.CurrentValue;
        return IsTestModeEnabled() && appOptions.TestMode.SkipInteractiveStartupWork;
    }

    public bool IsLocalMediaManagerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsLocalMediaManagerEnabled(esSettings, appOptions);
    }

    public bool ShouldRemoveRomsMediaAfterCanonicalMigration()
    {
        if (ShouldSkipInteractiveStartupWork())
        {
            return false;
        }

        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsLocalMediaManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.local_media_manager.remove_roms_media_after_canonical_migration",
            appOptions.LocalMediaManager.RemoveRomsMediaAfterCanonicalMigration,
            esSettings);
    }

    public bool IsAutoScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsLocalMediaManagerEnabled(esSettings, appOptions) &&
            appOptions.Scraping.AutoScrapingEnabled;
    }

    public string GetRemoteScrapingProvider()
    {
        var appOptions = _options.CurrentValue;
        return string.IsNullOrWhiteSpace(appOptions.Scraping.RemoteProvider)
            ? "screenscraper"
            : appOptions.Scraping.RemoteProvider.Trim();
    }

    public bool IsScreenScraperProviderEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.ScreenScraperEnabled;
    }

    public bool IsRemoteScrapeQueueEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.ScrapeQueueEnabled;
    }

    public bool IsRemoteScrapingAfterLocalOnly()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.RemoteAfterLocalOnly;
    }

    public bool IsExactLocalMediaScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.ExactLocalMediaScrapingEnabled;
    }

    public bool ShouldRefreshCurrentAfterRemoteScrapeSuccess()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.RefreshCurrentGameAfterRemoteSuccess;
    }

    public bool ShouldNotifyHeavyMediaScrape()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.scraping.notify_media.enabled",
                appOptions.Scraping.NotifyHeavyMediaScrapeEnabled,
                esSettings);
    }

    public bool IsHyperBatThemeInstallEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            IsDatasThemeExposeEnabled() &&
            ResolveBool(
            "global.apiexpose.collections_auto_installer.hyperbat_theme.enabled",
            appOptions.ThemeDeployments.Enabled,
            esSettings);
    }

    public bool ShouldRefreshCurrentAfterHyperBatInstallSuccess()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsHyperBatThemeInstallEnabled() &&
            ResolveBool(
            "global.apiexpose.collections_auto_installer.refresh_current_after_success",
            appOptions.ThemeDeployments.RefreshCurrentGameAfterInstallSuccess,
            esSettings);
    }

    public bool IsRemoteManualScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.ManualScrapingEnabled;
    }

    public bool IsRemoteMapScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.MapScrapingEnabled;
    }

    public bool IsRemoteMagazineScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.MagazineScrapingEnabled;
    }

    public bool IsRemoteVideoScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.VideoScrapingEnabled;
    }

    public bool IsRemoteVideoNormalizedScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.VideoNormalizedScrapingEnabled;
    }

    public bool IsRemoteMarqueeScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.MarqueeScrapingEnabled;
    }

    public bool IsRemoteScreenMarqueeScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.ScreenMarqueeScrapingEnabled;
    }

    public bool IsRemoteScreenMarqueeSmallScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.ScreenMarqueeSmallScrapingEnabled;
    }

    public bool IsRemoteSteamGridScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.SteamGridScrapingEnabled;
    }

    public bool IsRemoteMixScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.MixScrapingEnabled;
    }

    public bool IsRemoteBezelScrapingEnabled()
    {
        var appOptions = _options.CurrentValue;
        return IsAutoScrapingEnabled() &&
            appOptions.Scraping.BezelScrapingEnabled;
    }

    public bool IsTaskProgressEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool("global.apiexpose.task_progress.enabled", appOptions.TaskProgress.Enabled, esSettings);
    }

    public bool IsStartupOverlayEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool("global.apiexpose.startup_overlay.enabled", appOptions.StartupOverlay.Enabled, esSettings);
    }

    public string GetRemoteBezelAspectRatio()
    {
        var appOptions = _options.CurrentValue;
        return appOptions.Scraping.BezelAspectRatio;
    }

    public string GetRemoteBezelOrientation()
    {
        var appOptions = _options.CurrentValue;
        return appOptions.Scraping.BezelOrientation;
    }

    public bool AreToastNotificationsEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool("global.apiexpose.toast_notifications.enabled", appOptions.Toasts.Enabled, esSettings);
    }

    public bool AreApiNotificationsEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool("global.apiexpose.api_notifications.enabled", appOptions.ApiNotifications.Enabled, esSettings);
    }

    public bool IsRomPackInstallerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsRomSetManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
            "global.apiexpose.romset.pack_installer.enabled",
            appOptions.RomSetManager.RomPackInstallerEnabled,
            esSettings);
    }

    public bool ShouldUnzipRomPackInstallerRoms()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsRomSetManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
            "global.apiexpose.romset.pack_installer.unzip_roms",
            appOptions.RomSetManager.RomPackInstallerUnzipRoms,
            esSettings);
    }

    public bool IsOnTheFlyRomInstallerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        if (!IsRomSetManagerEnabled(esSettings, appOptions))
        {
            return false;
        }

        if (esSettings.TryGetValue("global.apiexpose.romset.pack_installer.on_the_fly.trigger", out var rawTrigger) &&
            !string.IsNullOrWhiteSpace(rawTrigger))
        {
            return !string.Equals(NormalizeOnTheFlyExtractionTrigger(rawTrigger), "never", StringComparison.OrdinalIgnoreCase);
        }

        return ResolveBool(
            "global.apiexpose.romset.pack_installer.on_the_fly.enabled",
            appOptions.RomSetManager.OnTheFlyRomInstallerEnabled,
            esSettings);
    }

    public string GetOnTheFlyRomExtractionTrigger()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        var value = esSettings.TryGetValue("global.apiexpose.romset.pack_installer.on_the_fly.trigger", out var rawValue) &&
            !string.IsNullOrWhiteSpace(rawValue)
                ? rawValue
                : GetConfiguredOnTheFlyExtractionTrigger(
                    appOptions.RomSetManager.OnTheFlyRomInstallerEnabled,
                    appOptions.RomSetManager.OnTheFlyRomExtractionTrigger);
        return NormalizeOnTheFlyExtractionTrigger(value);
    }

    public bool ShouldExtractOnTheFlyRomOnGameSelected()
    {
        return string.Equals(GetOnTheFlyRomExtractionTrigger(), "game_selected", StringComparison.OrdinalIgnoreCase);
    }

    public bool ShouldExtractOnTheFlyRomOnGameStart()
    {
        return string.Equals(GetOnTheFlyRomExtractionTrigger(), "game_start", StringComparison.OrdinalIgnoreCase);
    }

    public bool ShouldResetOnTheFlyRomAfterGameEnd()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsRomSetManagerEnabled(esSettings, appOptions) &&
            IsOnTheFlyRomInstallerEnabled() &&
            ResolveBool(
                "global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end",
                appOptions.RomSetManager.OnTheFlyRomResetAfterGameEndEnabled,
                esSettings);
    }

    public int GetOnTheFlyRomResetAfterGameEndDelayMs()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return ResolveInt(
            "global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end_delay_ms",
            appOptions.RomSetManager.OnTheFlyRomResetAfterGameEndDelayMs,
            esSettings,
            min: 0,
            max: 120000);
    }

    public bool IsCollectionPackInstallerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsCollectionPackManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.collections_pack_manager.pack_installer.enabled",
                appOptions.CollectionPackManager.CollectionPackInstallerEnabled,
                esSettings);
    }

    public bool AreCollectionPackDynamicCollectionsEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsCollectionPackManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.collections_pack_manager.dynamic_collections.enabled",
                appOptions.CollectionPackManager.DynamicCollectionsEnabled,
                esSettings);
    }

    public bool AreCollectionPackStaticCollectionsEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsCollectionPackManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.collections_pack_manager.static_collections.enabled",
                appOptions.CollectionPackManager.StaticCollectionsEnabled,
                esSettings);
    }

    public bool IsCollectionPackManagerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsCollectionPackManagerEnabled(esSettings, appOptions);
    }

    public bool IsCollectionPackApplyCollectionThemeToGamesEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsCollectionPackManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.collections_pack_manager.apply_collection_theme_to_games.enabled",
                appOptions.CollectionPackManager.ApplyCollectionThemeToGamesEnabled,
                esSettings);
    }

    private bool IsCollectionPackManagerEnabled(
        IReadOnlyDictionary<string, string> esSettings,
        ApiExposeOptions appOptions)
    {
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.collections_pack_manager.enabled",
                appOptions.CollectionPackManager.Enabled,
                esSettings);
    }

    public bool IsRetroArchWrapperEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsGameEventsManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.game_events.retroarch_wrapper.enabled",
                "global.apiexpose.retroarch_wrapper.enabled",
                appOptions.GameEventsManager.RetroArchWrapperEnabled,
                esSettings);
    }

    public bool IsConsoleHighScoreCaptureEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsGameEventsManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.game_events.console_high_score_capture.enabled",
                appOptions.GameEventsManager.ConsoleHighScoreCaptureEnabled,
                esSettings);
    }

    public bool IsMameOutputsEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsGameEventsManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.game_events.mame_outputs.enabled",
                appOptions.GameEventsManager.MameOutputsEnabled,
                esSettings);
    }

    public bool IsMameLuaIngameEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsGameEventsManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.game_events.mame_lua_ingame.enabled",
                appOptions.GameEventsManager.MameLuaIngameEnabled,
                esSettings);
    }

    public bool IsExportScoresOnGameEndEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsGameEventsManagerEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.game_events.export_scores_on_game_end.enabled",
                appOptions.GameEventsManager.ExportScoresOnGameEndEnabled,
                esSettings);
    }

    public int GetMaxHighScores()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return ResolveInt(
            "global.apiexpose.game_events.max_high_scores",
            appOptions.GameEventsManager.MaxHighScores > 0
                ? appOptions.GameEventsManager.MaxHighScores
                : appOptions.Hiscores.MaxHiscore,
            esSettings,
            min: 1,
            max: 100);
    }

    public bool IsRomSetManagerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsRomSetManagerEnabled(esSettings, appOptions);
    }

    private bool IsApiExposeEnabled(
        IReadOnlyDictionary<string, string> esSettings,
        ApiExposeOptions appOptions)
    {
        return ResolveBool(
            "global.apiexpose.enabled",
            appOptions.Enabled,
            esSettings);
    }

    private bool IsLocalMediaManagerEnabled(
        IReadOnlyDictionary<string, string> esSettings,
        ApiExposeOptions appOptions)
    {
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.local_media_manager.enabled",
                appOptions.LocalMediaManager.Enabled,
                esSettings);
    }

    private bool IsRomSetManagerEnabled(
        IReadOnlyDictionary<string, string> esSettings,
        ApiExposeOptions appOptions)
    {
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool(
                "global.apiexpose.rom_set_manager.enabled",
                appOptions.RomSetManager.Enabled,
                esSettings);
    }

    private bool IsGameEventsManagerEnabled(
        IReadOnlyDictionary<string, string> esSettings,
        ApiExposeOptions appOptions)
    {
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool(
            "global.apiexpose.game_events_manager.enabled",
            "global.apiexpose.retroarch_wrapper.enabled",
            appOptions.GameEventsManager.Enabled,
            esSettings);
    }

    public bool IsDatasThemeExposeEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool(
            "global.apiexpose.datas_theme_expose.enabled",
            "global.apiexpose.high_score_theme_extractor.enabled",
            appOptions.DatasThemeExpose.Enabled,
            esSettings);
    }

    public bool IsHighScoreExposeEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsDatasThemeExposeEnabled() &&
            ResolveBool(
                "global.apiexpose.datas_theme_expose.high_score.enabled",
                appOptions.DatasThemeExpose.HighScoreExposeEnabled,
                esSettings);
    }

    public bool IsLegacyHiscoreThemeExportEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsHighScoreExposeEnabled() &&
            ResolveBool(
                "global.apiexpose.datas_theme_expose.legacy_hiscore_theme.enabled",
                appOptions.DatasThemeExpose.LegacyHiscoreThemeExportEnabled,
                esSettings);
    }

    public bool IsCpoControlPanelExposeEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsDatasThemeExposeEnabled() &&
            ResolveBool(
                "global.apiexpose.datas_theme_expose.cpo_control_panel.enabled",
                appOptions.DatasThemeExpose.CpoControlPanelExposeEnabled,
                esSettings);
    }

    public bool IsCpoPanelWebSocketPushEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsCpoControlPanelExposeEnabled() &&
            ResolveBool(
                "global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled",
                appOptions.DatasThemeExpose.CpoPanelWebSocketPushEnabled,
                esSettings);
    }

    public bool IsMarqueeManagerEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsApiExposeEnabled(esSettings, appOptions) &&
            ResolveBool("global.apiexpose.marquee_manager.enabled", appOptions.MarqueeManager.Enabled, esSettings);
    }

    public bool IsMarqueeManagerWebSocketAssetsEnabled()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsMarqueeManagerEnabled() &&
            ResolveBool(
                "global.apiexpose.marquee_manager.websocket_assets.enabled",
                appOptions.MarqueeManager.WebSocketAssetsEnabled,
                esSettings);
    }

    public string GetMarqueeManagerAutogenProfile()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsMarqueeManagerEnabled()
            ? NormalizeMarqueeAutogenProfile(ResolveString(
                "global.apiexpose.marquee_manager.autogen_profile",
                appOptions.MarqueeManager.AutogenProfile,
                esSettings))
            : "no";
    }

    public bool ShouldNotifyMarqueeAutogen()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsMarqueeManagerEnabled() &&
            ResolveBool(
                "global.apiexpose.marquee_manager.autogen_notify.enabled",
                appOptions.MarqueeManager.AutogenNotifyEnabled,
                esSettings);
    }

    public bool ShouldUseThemeBackgroundForSystemMarquee()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsMarqueeManagerEnabled() &&
            ResolveBool(
                "global.apiexpose.marquee_manager.system_marquee_theme_background.enabled",
                appOptions.MarqueeManager.SystemMarqueeThemeBackgroundEnabled,
                esSettings);
    }

    public string GetMarqueeManagerDmdAutogenProfile()
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        return IsMarqueeManagerEnabled()
            ? NormalizeMarqueeDmdAutogenProfile(ResolveString(
                "global.apiexpose.marquee_manager.dmd_autogen_profile",
                ResolveString(
                    "global.apiexpose.marquee_manager.system_dmd_autogen_profile",
                    appOptions.MarqueeManager.DmdAutogenProfile,
                    esSettings),
                esSettings))
            : "no";
    }

    public string GetDatasThemeExposePanelLayout(string systemId)
    {
        var appOptions = _options.CurrentValue;
        var esSettings = ReadEsSettings();
        var normalizedSystemId = (systemId ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var key in new[]
        {
            $"{normalizedSystemId}.apiexpose_panel_{normalizedSystemId}",
            $"apiexpose_panel_{normalizedSystemId}",
            $"global.apiexpose.datas_theme_expose.cpo.{normalizedSystemId}.layout",
            $"{normalizedSystemId}.apiexpose_panel",
            "apiexpose_panel"
        })
        {
            if (esSettings.TryGetValue(key, out var rawValue) &&
                !string.IsNullOrWhiteSpace(rawValue) &&
                !string.Equals(rawValue.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                return rawValue.Trim();
            }
        }

        var controlButtons = esSettings.TryGetValue("global.apiexpose.control_manager.buttons_per_player", out var rawControlButtons) &&
            !string.IsNullOrWhiteSpace(rawControlButtons)
            ? rawControlButtons.Trim()
            : appOptions.ControlManager.ButtonsPerPlayer.ToString();
        if (int.TryParse(controlButtons, out var buttonCount) && buttonCount > 0)
        {
            return $"{buttonCount}-Button";
        }

        return string.Empty;
    }

    private ApiExposeLocalOptionEntry BuildBoolEntry(
        string key,
        string label,
        bool appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings,
        string runtimeStatus,
        string notes)
    {
        var hasOverride = esSettings.TryGetValue(key, out var rawValue);
        var parsedOverride = hasOverride && TryParseBool(rawValue, out var parsed)
            ? parsed
            : (bool?)null;
        var effectiveValue = parsedOverride ?? appsettingsValue;

        return new ApiExposeLocalOptionEntry
        {
            Key = key,
            Label = label,
            AppsettingsValue = appsettingsValue,
            EsSettingsValue = hasOverride ? rawValue ?? string.Empty : string.Empty,
            EsSettingsParsedValue = parsedOverride,
            EffectiveValue = effectiveValue,
            Source = parsedOverride.HasValue ? "es_settings" : "appsettings",
            RuntimeStatus = runtimeStatus,
            Notes = notes
        };
    }

    private ApiExposeLocalOptionEntry BuildRomSetShowEntry(
        string key,
        string label,
        bool appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings,
        bool romSetVisibilityInitialized,
        string runtimeStatus,
        string notes)
    {
        var hasOverride = esSettings.TryGetValue(key, out var rawValue);
        var parsedOverride = hasOverride && TryParseBool(rawValue, out var parsed)
            ? parsed
            : (bool?)null;
        var effectiveValue = parsedOverride ?? (romSetVisibilityInitialized ? false : appsettingsValue);

        return new ApiExposeLocalOptionEntry
        {
            Key = key,
            Label = label,
            AppsettingsValue = appsettingsValue,
            EsSettingsValue = hasOverride ? rawValue ?? string.Empty : string.Empty,
            EsSettingsParsedValue = parsedOverride,
            EffectiveValue = effectiveValue,
            Source = parsedOverride.HasValue
                ? "es_settings"
                : romSetVisibilityInitialized ? "es_settings_absent_false" : "appsettings",
            RuntimeStatus = runtimeStatus,
            Notes = notes
        };
    }

    private ApiExposeLocalOptionEntry BuildStringEntry(
        string key,
        string label,
        string appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings,
        string runtimeStatus,
        string notes)
    {
        var hasOverride = esSettings.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue);
        var effectiveValue = hasOverride ? rawValue!.Trim() : appsettingsValue;

        return new ApiExposeLocalOptionEntry
        {
            Key = key,
            Label = label,
            AppsettingsValue = appsettingsValue,
            EsSettingsValue = hasOverride ? rawValue ?? string.Empty : string.Empty,
            EsSettingsParsedValue = null,
            EffectiveValue = effectiveValue,
            Source = hasOverride ? "es_settings" : "appsettings",
            RuntimeStatus = runtimeStatus,
            Notes = notes
        };
    }

    private static string NormalizeOnTheFlyExtractionTrigger(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "never" or "disabled" or "off" or "none" or "0" => "never",
            "game_selected" or "selected" => "game_selected",
            _ => "game_start"
        };
    }

    private static string NormalizeMarqueeAutogenProfile(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "off" or "none" or "disabled" or "0" => "no",
            "xl" or "xl-1920x360" or "1920x360" => "xl-1920x360",
            "l" or "l-1280x400" or "1280x400" => "l-1280x400",
            "m" or "m-920x360" or "920x360" => "m-920x360",
            _ => "no"
        };
    }

    private static string NormalizeMarqueeDmdAutogenProfile(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('x', 'x');
        return normalized switch
        {
            "off" or "none" or "disabled" or "0" => "no",
            "64x32" => "64x32",
            "128x32" => "128x32",
            "128x64" => "128x64",
            "256x64" => "256x64",
            _ => "no"
        };
    }

    private static string GetConfiguredOnTheFlyExtractionTrigger(bool enabled, string? trigger)
    {
        return enabled
            ? NormalizeOnTheFlyExtractionTrigger(trigger)
            : "never";
    }

    private ApiExposeLocalOptionEntry BuildIntEntry(
        string key,
        string label,
        int appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings,
        string runtimeStatus,
        string notes)
    {
        var hasOverride = esSettings.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue);
        var parsedOverride = ParseIntLikeSetting(hasOverride ? rawValue : null);
        var effectiveValue = parsedOverride ?? appsettingsValue;

        return new ApiExposeLocalOptionEntry
        {
            Key = key,
            Label = label,
            AppsettingsValue = appsettingsValue,
            EsSettingsValue = hasOverride ? rawValue ?? string.Empty : string.Empty,
            EsSettingsParsedValue = parsedOverride,
            EffectiveValue = effectiveValue,
            Source = parsedOverride.HasValue ? "es_settings" : "appsettings",
            RuntimeStatus = runtimeStatus,
            Notes = notes
        };
    }

    private static int? ParseIntLikeSetting(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (int.TryParse(rawValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
        {
            return parsedInt;
        }

        return double.TryParse(rawValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble)
            ? (int)Math.Round(parsedDouble)
            : null;
    }

    private ApiExposeLocalOptionEntry BuildBoolEntryWithFallback(
        string key,
        string fallbackKey,
        string label,
        bool appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings,
        string runtimeStatus,
        string notes)
    {
        var hasOverride = esSettings.TryGetValue(key, out var rawValue);
        var sourceKey = key;
        if (!hasOverride && esSettings.TryGetValue(fallbackKey, out rawValue))
        {
            hasOverride = true;
            sourceKey = fallbackKey;
        }

        var parsedOverride = hasOverride && TryParseBool(rawValue, out var parsed)
            ? parsed
            : (bool?)null;
        var effectiveValue = parsedOverride ?? appsettingsValue;

        return new ApiExposeLocalOptionEntry
        {
            Key = key,
            Label = label,
            AppsettingsValue = appsettingsValue,
            EsSettingsValue = hasOverride ? rawValue ?? string.Empty : string.Empty,
            EsSettingsParsedValue = parsedOverride,
            EffectiveValue = effectiveValue,
            Source = parsedOverride.HasValue
                ? sourceKey.Equals(key, StringComparison.OrdinalIgnoreCase) ? "es_settings" : "es_settings_legacy"
                : "appsettings",
            RuntimeStatus = runtimeStatus,
            Notes = notes
        };
    }

    private Dictionary<string, string> ReadEsSettings()
    {
        try
        {
            return new Dictionary<string, string>(_settingsStore.ReadAllSettings(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _logger.LogWarning(ex, "Unable to read EmulationStation settings for APIExpose local options.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsRomSetVisibilityInitialized(IReadOnlyDictionary<string, string> esSettings)
    {
        return esSettings.ContainsKey("global.apiexpose.romset.defaults_initialized")
            || esSettings.Keys.Any(key => key.StartsWith("global.apiexpose.romset.show_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseBool(string? value, out bool result)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
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
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool IsTruthy(string? value)
    {
        return TryParseBool(value, out var parsed) && parsed;
    }

    private static bool ResolveBool(
        string key,
        bool appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings)
    {
        return esSettings.TryGetValue(key, out var rawValue) && TryParseBool(rawValue, out var parsed)
            ? parsed
            : appsettingsValue;
    }

    private static string ResolveString(
        string key,
        string appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings)
    {
        return esSettings.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue)
            ? rawValue.Trim()
            : appsettingsValue;
    }

    private static bool ResolveBool(
        string key,
        string fallbackKey,
        bool appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings)
    {
        if (esSettings.TryGetValue(key, out var rawValue) && TryParseBool(rawValue, out var parsed))
        {
            return parsed;
        }

        return esSettings.TryGetValue(fallbackKey, out var fallbackRawValue) &&
            TryParseBool(fallbackRawValue, out var fallbackParsed)
            ? fallbackParsed
            : appsettingsValue;
    }

    private static int ResolveInt(
        string key,
        int appsettingsValue,
        IReadOnlyDictionary<string, string> esSettings,
        int min,
        int max)
    {
        if (esSettings.TryGetValue(key, out var rawValue) &&
            int.TryParse(rawValue, out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return Math.Clamp(appsettingsValue, min, max);
    }
}

public sealed class ApiExposeLocalOptionsSnapshot
{
    public string EsSettingsPath { get; set; } = string.Empty;
    public bool EsSettingsExists { get; set; }
    public List<ApiExposeLocalOptionEntry> Entries { get; set; } = new();
    public Dictionary<string, string> RawApiExposeSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ApiExposeLocalOptionEntry
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public object? AppsettingsValue { get; set; }
    public string EsSettingsValue { get; set; } = string.Empty;
    public object? EsSettingsParsedValue { get; set; }
    public object? EffectiveValue { get; set; }
    public string Source { get; set; } = string.Empty;
    public string RuntimeStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

