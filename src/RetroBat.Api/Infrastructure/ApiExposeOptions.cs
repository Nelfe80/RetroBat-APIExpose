namespace RetroBat.Api.Infrastructure;

public class ApiExposeOptions
{
    public bool Enabled { get; set; } = true;
    public TestModeOptions TestMode { get; set; } = new();
    public LocalMediaManagerOptions LocalMediaManager { get; set; } = new();
    public MediaAllocationOptions MediaAllocation { get; set; } = new();
    public ApiSettingsOptions ApiSettings { get; set; } = new();
    public LocalizedGamelistCacheOptions LocalizedGamelistCache { get; set; } = new();
    public ScrapingOptions Scraping { get; set; } = new();
    public DatasThemeExposeOptions DatasThemeExpose { get; set; } = new();
    public MarqueeManagerOptions MarqueeManager { get; set; } = new();
    public GameEventsManagerOptions GameEventsManager { get; set; } = new();
    public ControlManagerOptions ControlManager { get; set; } = new();
    public RomSetManagerOptions RomSetManager { get; set; } = new();
    public CollectionPackManagerOptions CollectionPackManager { get; set; } = new();
    public StartupOverlayOptions StartupOverlay { get; set; } = new();
    public SwaggerOptions Swagger { get; set; } = new();
    public WebSocketOptions WebSocket { get; set; } = new();
    public ToastOptions Toasts { get; set; } = new();
    public ApiNotificationOptions ApiNotifications { get; set; } = new();
    public HiscoreOptions Hiscores { get; set; } = new();
    public ThemeDeploymentOptions ThemeDeployments { get; set; } = new();
    public MediaDeploymentRulesOptions MediaDeploymentRules { get; set; } = new();
    public InstallerDeploymentOptions InstallerDeployment { get; set; } = new();
    public EsFeaturesMenuOptions EsFeaturesMenu { get; set; } = new();
    public EmulationStationLifecycleOptions EmulationStationLifecycle { get; set; } = new();
    public RetroArchWrapperDeploymentOptions RetroArchWrapperDeployment { get; set; } = new();
    public EsControllerOptions EsController { get; set; } = new();
    public TaskProgressOptions TaskProgress { get; set; } = new();
    public ScrapeQueueOverlayOptions ScrapeQueueOverlay { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public TaxonomyOptions Taxonomy { get; set; } = new();

    public class TestModeOptions
    {
        public bool Enabled { get; set; }
        public bool SkipInteractiveStartupWork { get; set; } = true;
    }

    public class ScrapingOptions
    {
        public bool AutoScrapingEnabled { get; set; } = true;
        public string RemoteProvider { get; set; } = "screenscraper";
        public bool ScreenScraperEnabled { get; set; } = true;
        public bool ScrapeQueueEnabled { get; set; } = true;
        public string ScreenScraperBaseUrl { get; set; } = "https://api.screenscraper.fr/api2";
        public string ScreenScraperDevId { get; set; } = string.Empty;
        public string ScreenScraperDevPassword { get; set; } = string.Empty;
        public bool UseBundledScreenScraperDeveloperCredentials { get; set; } = true;
        public bool RemoteAfterLocalOnly { get; set; } = true;
        public bool ExactLocalMediaScrapingEnabled { get; set; } = false;
        public bool RefreshCurrentGameAfterRemoteSuccess { get; set; } = true;
        public bool NotifyHeavyMediaScrapeEnabled { get; set; } = true;
        public bool ResumePendingScrapesOnStartup { get; set; } = false;
        public bool BootstrapDefaultPlaceholdersOnStartup { get; set; } = false;
        public bool LiveEsMetadataPushEnabled { get; set; } = true;
        public bool LiveEsMediaPushEnabled { get; set; } = false;
        public int LiveEsMediaPushDelayMs { get; set; } = 1200;
        public int LiveEsAddGamesMinIntervalMs { get; set; } = 1200;
        public bool TraceLiveAddGamesPayloads { get; set; } = false;
        public int RemoteTextNoChangeCooldownMinutes { get; set; } = 720;
        public string RemoteScrapePerformanceProfile { get; set; } = "auto";
        public int RemoteScrapeConcurrencyCap { get; set; } = 4;
        public int RemoteScrapeFallbackConcurrency { get; set; } = 1;
        public int RemoteScrapeMaxQueueWorkers { get; set; } = 8;
        public int RemoteScrapeRequestsPerMinuteCap { get; set; } = 0;
        public int RemoteScrapeRequestMinuteSafetyMargin { get; set; } = 2;
        public bool DescriptionTranslationEnabled { get; set; } = true;
        public bool DescriptionTranslationInstallMissingModels { get; set; } = true;
        public string DescriptionTranslationSourceLanguage { get; set; } = "en";
        public string DescriptionTranslationPendingPath { get; set; } = "media/aliases/shared/description-translation-pending";
        public int DescriptionTranslationTimeoutSeconds { get; set; } = 300;
        public bool DescriptionTranslationNotifyModelInstall { get; set; } = true;
        public bool DescriptionTranslationNotifyTranslatedCurrentGame { get; set; } = true;
        public string TranslateLocallyToolsPath { get; set; } = "tools/translateLocally";
        public string TranslateLocallyExecutableName { get; set; } = "translateLocally.windows-2019.x86-64.exe";
        public string TranslateLocallyProfilePath { get; set; } = "tools/translateLocally/profile";
        public bool TranslateLocallyPortableModelStoreEnabled { get; set; } = true;
        public string TranslateLocallyModelStorePath { get; set; } = "tools/translateLocally/models";
        public bool ProjectedMediaIndexCacheEnabled { get; set; } = true;
        public bool ScreenScraperRawCacheEnabled { get; set; } = true;
        public bool MarqueeScrapingEnabled { get; set; } = true;
        public bool ScreenMarqueeScrapingEnabled { get; set; } = true;
        public bool ScreenMarqueeSmallScrapingEnabled { get; set; } = true;
        public bool SteamGridScrapingEnabled { get; set; } = true;
        public bool MixScrapingEnabled { get; set; } = true;
        public bool MapScrapingEnabled { get; set; } = false;
        public bool ManualScrapingEnabled { get; set; } = true;
        public bool MagazineScrapingEnabled { get; set; } = true;
        public bool VideoScrapingEnabled { get; set; } = true;
        public bool VideoNormalizedScrapingEnabled { get; set; } = true;
        public bool ThemeHbScrapingEnabled { get; set; } = false;
        public bool BezelScrapingEnabled { get; set; } = true;
        public string BezelAspectRatio { get; set; } = "16-9";
        public string BezelOrientation { get; set; } = "match_cabinet";
    }

    public class TaxonomyOptions
    {
        public List<RegionTaxonomyOptions> Regions { get; set; } = new();
        public List<LanguageTaxonomyOptions> Languages { get; set; } = new();
        public List<string> ScreenScraperRegionOrder { get; set; } = new();
        public List<string> Orientations { get; set; } = new();
    }

    public class RegionTaxonomyOptions
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string RomValue { get; set; } = string.Empty;
        public string ScreenScraperCode { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
    }

    public class LanguageTaxonomyOptions
    {
        public string Key { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DefaultScreenScraperRegion { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
    }

    public class LocalMediaManagerOptions
    {
        public bool Enabled { get; set; } = true;
        public bool PopulateAllGamelistsRequested { get; set; }
        public bool RemoveRomsMediaAfterCanonicalMigration { get; set; } = true;
    }

    public class MediaAllocationOptions
    {
        public string ImageSource { get; set; } = "ss";
        public string LogoSource { get; set; } = "logo";
        public string ThumbSource { get; set; } = "ss";
        public string WheelStyle { get; set; } = "carbon";
        public string MediaRegionMode { get; set; } = "match_rom_region";
        public string LogoRegionMode { get; set; } = "user_language";
        public string UserRegion { get; set; } = "auto";
    }

    public class ApiSettingsOptions
    {
        public string RegionProfile { get; set; } = string.Empty;
        public string LanguageProfile { get; set; } = string.Empty;
        public bool RepairGamelistsOnStartup { get; set; } = true;
        public bool SyncGamelistsWithSystemLanguage { get; set; } = true;
    }

    public class LocalizedGamelistCacheOptions
    {
        public bool Enabled { get; set; } = true;
        public bool PrebuildOnStartup { get; set; } = true;
        public string RootPath { get; set; } = "resources/gamelist/localized";
        public List<string> ActiveLanguages { get; set; } = new();
    }

    public class DatasThemeExposeOptions
    {
        public bool Enabled { get; set; } = true;
        public bool HighScoreExposeEnabled { get; set; } = true;
        public bool LegacyHiscoreThemeExportEnabled { get; set; } = false;
        public bool CpoControlPanelExposeEnabled { get; set; } = true;
        public bool CpoPanelWebSocketPushEnabled { get; set; } = false;
        public string GeneralPanelButtons { get; set; } = "auto";
    }

    public class MarqueeManagerOptions
    {
        public bool Enabled { get; set; } = false;
        public bool WebSocketAssetsEnabled { get; set; } = false;
        public string AutogenProfile { get; set; } = "no";
        public bool SystemMarqueeThemeBackgroundEnabled { get; set; } = true;
        public string DmdAutogenProfile { get; set; } = "no";
        public bool AutogenNotifyEnabled { get; set; } = true;
    }

    public class GameEventsManagerOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RetroArchWrapperEnabled { get; set; } = true;
        public bool ConsoleHighScoreCaptureEnabled { get; set; } = true;
        public bool MameOutputsEnabled { get; set; } = true;
        public bool MameLuaIngameEnabled { get; set; } = true;
        public int MameLuaIngamePort { get; set; } = 12347;
        public bool MameLuaIngamePluginDeploymentEnabled { get; set; } = true;
        public bool MameLuaIngameMirrorToEmulatorPlugins { get; set; } = true;
        public bool ExportScoresOnGameEndEnabled { get; set; } = true;
        public int MaxHighScores { get; set; } = 10;
    }

    public class RomSetManagerOptions
    {
        public bool Enabled { get; set; } = false;
        public string GroupsRootPath { get; set; } = "resources/gamelist/systems";
        public bool NeverHideFavorites { get; set; } = true;
        public string Profile { get; set; } = "gamer";
        public string RetroAchievementsMode { get; set; } = "no_filter";
        public string LanguageMode { get; set; } = "show_all";
        public string RegionMode { get; set; } = "show_all";
        public string RomVersionMode { get; set; } = "stable";
        public string OfficialGamesMode { get; set; } = "show";
        public string ClonesMode { get; set; } = "hide";
        public string PrototypesMode { get; set; } = "hide";
        public string DemosMode { get; set; } = "show";
        public string BetaAlphaMode { get; set; } = "show";
        public string LocationTestsMode { get; set; } = "hide";
        public string UsefulPatchesMode { get; set; } = "hide";
        public string HacksModsMode { get; set; } = "show";
        public string CheatsTrainersMode { get; set; } = "hide";
        public string BootlegsPiratesMode { get; set; } = "hide";
        public string UnlicensedMode { get; set; } = "show";
        public string HomebrewsAftermarketMode { get; set; } = "show";
        public string AdultMode { get; set; } = "hide";
        public string CasinoMode { get; set; } = "hide";
        public string MahjongMode { get; set; } = "hide";
        public string QuizMode { get; set; } = "hide";
        public string NonGamesMode { get; set; } = "show";
        public string UnknownRomsMode { get; set; } = "show";
        public string ArcadeDiagnosticsMode { get; set; } = "show";
        public bool OnlyRetroAchievements { get; set; } = false;
        public bool ShowClones { get; set; } = true;
        public bool ShowPrototypes { get; set; } = true;
        public bool ShowBootlegsAndHacks { get; set; } = true;
        public bool ShowAdult { get; set; } = true;
        public bool ShowCasino { get; set; } = true;
        public bool ShowMahjong { get; set; } = true;
        public bool ShowNonGames { get; set; } = true;
        public bool ShowNonArcade { get; set; } = true;
        public bool ShowHorizontal { get; set; } = true;
        public bool ShowVertical { get; set; } = true;
        public string ScreenOrientation { get; set; } = "auto";
        public string CocktailGames { get; set; } = "auto";
        public string MultiScreenGames { get; set; } = "auto";
        public string FunctionalSecondScreen { get; set; } = "auto";
        public string WideSurroundDisplay { get; set; } = "auto";
        public string PortableLinkGameplay { get; set; } = "auto";
        public string CabinetControlsCompatibility { get; set; } = "auto";
        public string PlayerCount { get; set; } = "auto";
        public string ButtonCompatibility { get; set; } = "auto";
        public string VariantMode { get; set; } = "display_only";
        public string RegionProfile { get; set; } = string.Empty;
        public string LanguageProfile { get; set; } = string.Empty;
        public string Translations { get; set; } = "prefer_if_language_match";
        public string ArcadeHandling { get; set; } = "parent_clone_group";
        public string OutputMode { get; set; } = "gamelist_hidden";
        public bool DebugReport { get; set; } = false;
        public bool ReloadGamesAfterApply { get; set; } = true;
        public bool RomPackInstallerEnabled { get; set; } = false;
        public bool RomPackInstallerUnzipRoms { get; set; } = false;
        public bool OnTheFlyRomInstallerEnabled { get; set; } = false;
        public string OnTheFlyRomExtractionTrigger { get; set; } = "game_start";
        public bool OnTheFlyRomResetAfterGameEndEnabled { get; set; } = false;
        public int OnTheFlyRomResetAfterGameEndDelayMs { get; set; } = 12000;
    }

    public class ControlManagerOptions
    {
        public string CabinetProfile { get; set; } = "generic_arcade";
        public int PlayerCount { get; set; } = 2;
        public int ButtonsPerPlayer { get; set; } = 6;
        public bool ArcadeJoystick { get; set; } = true;
        public bool AnalogJoystick { get; set; } = false;
        public bool RotaryJoystick { get; set; } = false;
        public string Spinner { get; set; } = "none";
        public string Trackball { get; set; } = "none";
        public string Wheel { get; set; } = "none";
        public string Pedals { get; set; } = "none";
        public string Shifter { get; set; } = "none";
        public string Lightgun { get; set; } = "none";
        public string DanceMat { get; set; } = "none";
        public string Guitar { get; set; } = "none";
        public string Drums { get; set; } = "none";
        public string Turntable { get; set; } = "none";
        public bool Microphone { get; set; } = false;
        public bool Keyboard { get; set; } = false;
        public bool Mouse { get; set; } = false;
        public bool Touchscreen { get; set; } = false;
        public bool MotionController { get; set; } = false;
    }

    public class CollectionPackManagerOptions
    {
        public bool Enabled { get; set; } = false;
        public bool CollectionPackInstallerEnabled { get; set; } = false;
        public bool DynamicCollectionsEnabled { get; set; } = true;
        public bool StaticCollectionsEnabled { get; set; } = false;
        public bool ApplyCollectionThemeToGamesEnabled { get; set; } = false;
        public string PackageRootPath { get; set; } = "package-installer/collections";
        public List<CollectionPackThemeInstallationOptions> ThemeInstallations { get; set; } =
        [
            new CollectionPackThemeInstallationOptions
            {
                Name = "HyperBat",
                Enabled = true,
                ActiveThemeMatcher = "*hyperbat*",
                ThemeDirectorySearchPattern = "*hyperbat*",
                CollectionInstallTargets =
                [
                    new CollectionPackInstallTargetOptions
                    {
                        Kind = "hyperspin",
                        Path = "{EmulationStationThemesRoot}/{themeSet}/_systemmedia/videosyst/hyperspinxp/{collection}"
                    },
                    new CollectionPackInstallTargetOptions
                    {
                        Kind = "hyperbat",
                        Path = "{EmulationStationThemesRoot}/{themeSet}/_gametheme/{collection}/hyperbat/{collection}"
                    }
                ],
                GameThemePath = "{EmulationStationThemesRoot}/{themeSet}/_gametheme/{frontendSystem}/hyperbat/{rom}",
                CanonicalThemePath = "{EmulationStationThemesRoot}/{themeSet}/_gametheme/_canonical/hyperbat/{frontendSystem}/{rom}"
            }
        ];
    }

    public class CollectionPackThemeInstallationOptions
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string ActiveThemeMatcher { get; set; } = string.Empty;
        public string ThemeDirectorySearchPattern { get; set; } = string.Empty;
        public List<CollectionPackInstallTargetOptions> CollectionInstallTargets { get; set; } = new();
        public string GameThemePath { get; set; } = string.Empty;
        public string CanonicalThemePath { get; set; } = string.Empty;
    }

    public class CollectionPackInstallTargetOptions
    {
        public bool Enabled { get; set; } = true;
        public string Kind { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public class StartupOverlayOptions
    {
        public bool Enabled { get; set; } = true;
        public string Message { get; set; } = "APIExpose prepare la mediatheque...";
        public string Title { get; set; } = "RetroBat APIExpose";
        public double Opacity { get; set; } = 0.92d;
        public string MessagesFilePath { get; set; } = "resources/startup-overlay/messages.json";
        public string SplashImagePath { get; set; } = "resources/startup-overlay/splashscreen.png";
        public int MinimumVisibleMilliseconds { get; set; } = 3000;
    }

    public class SwaggerOptions
    {
        public bool Enabled { get; set; } = true;
    }

    public class WebSocketOptions
    {
        public bool Enabled { get; set; } = true;
        public string Endpoint { get; set; } = "/ws";
    }

    public class ToastOptions
    {
        public bool Enabled { get; set; } = true;
        public double Opacity { get; set; } = 0.96d;
    }

    public class ApiNotificationOptions
    {
        public bool Enabled { get; set; } = true;
    }

    public class HiscoreOptions
    {
        public int MaxHiscore { get; set; } = 10;
    }

    public class ThemeDeploymentOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RefreshCurrentGameAfterInstallSuccess { get; set; } = true;
        public List<ThemeDeploymentRuleOptions> Rules { get; set; } =
        [
            new ThemeDeploymentRuleOptions
            {
                Name = "HyperBat",
                Enabled = true,
                MediaKind = "themehb",
                ScreenScraperMediaTypes = ["themehb"],
                ActiveThemeMatcher = "*hyperbat*",
                ThemeDirectorySearchPattern = "*hyperbat*",
                NotifyLocalScrape = true,
                RefreshMode = "f5",
                InstallTargets =
                [
                    new DeploymentTargetOptions
                    {
                        Type = "ExtractArchive",
                        Path = "{EmulationStationThemesRoot}/{themeSet}/_gametheme/{frontendSystem}/hyperbat/{rom}",
                        Overwrite = true
                    }
                ]
            }
        ];
    }

    public class ThemeDeploymentRuleOptions
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string MediaKind { get; set; } = string.Empty;
        public List<string> ScreenScraperMediaTypes { get; set; } = new();
        public string ActiveThemeMatcher { get; set; } = string.Empty;
        public string ThemeDirectorySearchPattern { get; set; } = string.Empty;
        public bool NotifyLocalScrape { get; set; } = true;
        public string RefreshMode { get; set; } = "none";
        public List<DeploymentTargetOptions> InstallTargets { get; set; } = new();
    }

    public class MediaDeploymentRulesOptions
    {
        public bool Enabled { get; set; } = true;
        public List<MediaDeploymentRuleOptions> Rules { get; set; } = new();
    }

    public class MediaDeploymentRuleOptions
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string System { get; set; } = string.Empty;
        public string FrontendSystem { get; set; } = string.Empty;
        public string MediaKind { get; set; } = string.Empty;
        public string Source { get; set; } = "{source}";
        public List<DeploymentTargetOptions> Targets { get; set; } = new();
    }

    public class DeploymentTargetOptions
    {
        public bool Enabled { get; set; } = true;
        public string Type { get; set; } = "CopyFile";
        public string Path { get; set; } = string.Empty;
        public bool Overwrite { get; set; } = true;
    }

    public class InstallerDeploymentOptions
    {
        public bool Enabled { get; set; } = true;
        public string InstallerRootPath { get; set; } = ".installer";
        public string LogFilePath { get; set; } = "logs/installer-deployment.jsonl";
        public bool DryRunOnStartup { get; set; } = false;
        public bool SyncThemesOnStartup { get; set; } = true;
        public bool SyncScriptsOnStartup { get; set; } = true;
        public bool SyncGameInfosOnStartup { get; set; } = true;
        public string GameInfosSourcePath { get; set; } = "resources/theme/gameinfos";
        public string GameInfosTargetPath { get; set; } = string.Empty;
        public string HashManifestPath { get; set; } = "logs/installer-deployment-hashes.json";
        public bool OverwriteMediasFiles { get; set; } = true;
        public bool OverwriteScriptFiles { get; set; } = true;
        public bool OverwriteGameInfosFiles { get; set; } = true;
    }

    public class EsFeaturesMenuOptions
    {
        public bool Enabled { get; set; } = true;
        public bool InstallOnStartup { get; set; } = true;
        public bool DryRunOnStartup { get; set; } = false;
        public string FeaturesPath { get; set; } = string.Empty;
        public string SourceFragmentPath { get; set; } = "resources/config-ESmenus/apiexpose_es_features_blocks_to_add.cfg";
        public bool LocaleDeploymentEnabled { get; set; } = true;
        public string LocaleSourceRootPath { get; set; } = "resources/config-ESmenus/locales";
        public string LocaleTargetRootPath { get; set; } = string.Empty;
        public string BackupPath { get; set; } = "logs/es-features-menu/backups";
        public bool BackupEnabled { get; set; } = true;
        public int BackupRetentionCount { get; set; } = 4;
        public string LogFilePath { get; set; } = "logs/es-features-menu/deployment.jsonl";
        public bool LogEnabled { get; set; } = false;
        public bool ResetLogOnStartup { get; set; } = true;
    }

    public class EmulationStationLifecycleOptions
    {
        public bool Enabled { get; set; } = true;
        public bool StopApiWhenEmulationStationStops { get; set; } = true;
        public bool RemoveEsFeaturesOnShutdown { get; set; } = true;
        public int PollIntervalMilliseconds { get; set; } = 1000;
        public int ShutdownGraceMilliseconds { get; set; } = 3500;
        public bool SendF5AfterEsApiReady { get; set; } = true;
        public int F5AfterEsApiReadyDelayMilliseconds { get; set; } = 5000;
        public int F5AfterEsApiReadyHoldMilliseconds { get; set; } = 80;
    }

    public class RetroArchWrapperDeploymentOptions
    {
        public bool Enabled { get; set; } = false;
        public string WrapperDllPath { get; set; } = "wrapper/wrapper.dll";
        public string CoresPath { get; set; } = "../../emulators/retroarch/cores";
        public string RealCoresPath { get; set; } = "../../emulators/retroarch/cores_real";
        public string BackupPath { get; set; } = "logs/wrapper-deployment/backups";
        public string LogFilePath { get; set; } = "logs/wrapper-deployment.jsonl";
        public bool WrapAllCores { get; set; } = true;
        public List<string> TargetCores { get; set; } = new();
        public bool SkipIfRetroArchRunning { get; set; } = true;
        public bool DryRunOnStartup { get; set; } = false;
    }

    public class EsControllerOptions
    {
        public bool Enabled { get; set; } = true;
        public string Backend { get; set; } = "dry-run";
        public bool RequireEmulationStationForeground { get; set; } = true;
        public bool FocusEmulationStationBeforeInput { get; set; } = true;
        public bool ClickEmulationStationIfFocusFails { get; set; } = false;
        public bool RightClickWarningEnabled { get; set; } = true;
        public bool FocusWarningEnabled { get; set; } = true;
        public int FocusWarningDurationMs { get; set; } = 1200;
        public bool RestoreSelectionAfterReloadGames { get; set; } = true;
        public int RestoreSelectionDelayMs { get; set; } = 3500;
        public string GameNavigationForwardInput { get; set; } = "right";
        public string GameNavigationBackwardInput { get; set; } = "left";
        public bool GameNavigationPageInputsEnabled { get; set; } = true;
        public string GameNavigationPageForwardInput { get; set; } = "pagedown";
        public string GameNavigationPageBackwardInput { get; set; } = "pageup";
        public int GameNavigationPageSize { get; set; } = 10;
        public int EventsObservationMinDelayMs { get; set; } = 250;
        public int EventsObservationMaxDelayMs { get; set; } = 2500;
        public int EventsObservationSettleMs { get; set; } = 180;
        public int EventsObservationPollMs { get; set; } = 50;
    }

    public class TaskProgressOptions
    {
        public bool Enabled { get; set; } = true;
        public double Opacity { get; set; } = 0.96d;
        public int MinimumVisibleMilliseconds { get; set; } = 2500;
        public Dictionary<string, bool> ShowTasks { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bootstrap-default-placeholders"] = false,
            ["refresh-system-selections"] = true,
            ["es-settings-reallocation"] = true,
            ["gamelist-generation"] = true,
            ["force-local-resync"] = true,
            ["rom-set-manager"] = true,
            ["rom-pack-installer"] = true,
            ["rom-pack-on-the-fly-extraction"] = true,
            ["description-translation-model"] = true,
            ["datas-theme-expose"] = false,
            ["reloadgames"] = true
        };
    }

    public class ScrapeQueueOverlayOptions
    {
        public bool Enabled { get; set; } = true;
        public double Opacity { get; set; } = 0.9d;
        public int RefreshIntervalMs { get; set; } = 250;
    }

    public class LoggingOptions
    {
        public bool ConsoleEnabled { get; set; } = true;
        public bool ResetRuntimeLogsOnStartup { get; set; } = true;
        public string[] RuntimeLogFilesToReset { get; set; } =
        {
            "logs/refresh-tracking.jsonl",
            "logs/media-update-audit.jsonl",
            "logs/local-gamelist-update.jsonl",
            "logs/gamelist-display-name-normalization.jsonl"
        };
        public GameSessionLogOptions GameSessionLogs { get; set; } = new();
        public EsFlowLogOptions EsFlowLogs { get; set; } = new();
    }

    public class GameSessionLogOptions
    {
        public bool Enabled { get; set; } = true;
        public string DirectoryPath { get; set; } = "logs/game-sessions";
        public bool ResetOnStartup { get; set; } = true;
    }

    public class EsFlowLogOptions
    {
        public bool Enabled { get; set; } = false;
        public string FilePath { get; set; } = "logs/es-flow.jsonl";
        public bool ResetOnStartup { get; set; } = true;
    }
}
