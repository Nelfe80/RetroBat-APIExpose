namespace RetroBat.Api.Infrastructure;

public class ApiExposeOptions
{
    public bool Enabled { get; set; } = true;
    public TestModeOptions TestMode { get; set; } = new();
    public LocalMediaManagerOptions LocalMediaManager { get; set; } = new();
    public MediaMigrationOptions MediaMigration { get; set; } = new();
    public MediaAllocationOptions MediaAllocation { get; set; } = new();
    public MediaDiscoveryOptions MediaDiscovery { get; set; } = new();
    public ApiSettingsOptions ApiSettings { get; set; } = new();
    public LocalizedGamelistCacheOptions LocalizedGamelistCache { get; set; } = new();
    public ScrapingOptions Scraping { get; set; } = new();
    public DatasThemeExposeOptions DatasThemeExpose { get; set; } = new();
    public MarqueeManagerOptions MarqueeManager { get; set; } = new();
    public GameEventsManagerOptions GameEventsManager { get; set; } = new();
    public MonitorSyncOptions MonitorSync { get; set; } = new();
    public ControlManagerOptions ControlManager { get; set; } = new();
    public RomSetManagerOptions RomSetManager { get; set; } = new();
    public CollectionPackManagerOptions CollectionPackManager { get; set; } = new();
    public StartupOverlayOptions StartupOverlay { get; set; } = new();
    public CabinetBadgeOverlayOptions CabinetBadgeOverlay { get; set; } = new();
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
    public RetroAchievementsOptions RetroAchievements { get; set; } = new();
    public CommunityRamOptions CommunityRam { get; set; } = new();
    public DataPackOptions DataPack { get; set; } = new();
    public SelfUpdateOptions SelfUpdate { get; set; } = new();

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
        public bool ExactLocalMediaScrapingEnabled { get; set; } = true;
        public bool RefreshCurrentGameAfterRemoteSuccess { get; set; } = true;
        public bool NotifyHeavyMediaScrapeEnabled { get; set; } = true;
        public bool ResumePendingScrapesOnStartup { get; set; } = false;
        public bool BootstrapDefaultPlaceholdersOnStartup { get; set; } = false;
        public bool LiveEsMetadataPushEnabled { get; set; } = true;
        public bool LiveEsMediaPushEnabled { get; set; } = false;

        /// <summary>Recent EmulationStation builds reject the raw /addgames body
        /// (upstream file-guard regression): every push answers 204 and the live
        /// card refresh is a silent no-op. When enabled, two consecutive qualified
        /// 204s mark the ES build as unsupported: pushes stop, the user gets one
        /// toast per session, and scraped data reaches gamelist.xml through the
        /// pending-extended merge instead. false disables detection entirely.</summary>
        public bool DetectAddGamesSupport { get; set; } = true;

        /// <summary>Merge pending extended gamelists into gamelist.xml when
        /// EmulationStation exits - the only moment ES cannot overwrite the file
        /// from its own memory. Complements the existing api-startup merge.</summary>
        public bool MergePendingOnEsExit { get; set; } = true;

        /// <summary>Only POST a live addgames when the fragment changes something
        /// ES actually renders: a visible media PATH, a fresh video, or a first
        /// description. Same-path file replacements never repaint (ES texture
        /// cache has no mtime check) and later text updates ride the next
        /// mutualized push - pushing for those only rebuilds the ES view for
        /// nothing. false restores the historical delta filter alone.</summary>
        public bool RequireRenderDeltaForLivePush { get; set; } = true;

        /// <summary>
        /// Ask ScreenScraper only for media its own catalogue lists for the game.
        /// jeuInfos.php answers with what the site HAS; when a media type is absent
        /// from that answer, building a URL for it anyway just spends a round-trip to
        /// be told no. Measured over 63 attempts: the 47 speculative ones ALL failed,
        /// the 16 catalogue-backed ones ALL succeeded - the catalogue is authoritative.
        /// false restores the historical speculative fallback.
        /// </summary>
        public bool SkipMediaNotInCatalog { get; set; } = true;

        /// <summary>Suppress the generic "card updated" toast when APIExpose has
        /// no visible-change label to announce; scraping activity notifications
        /// keep telling the user something is happening. false restores the
        /// historical generic fallback toast.</summary>
        public bool HonestNotifications { get; set; } = true;

        /// <summary>F3 (Super Mario World bug): ES's addgames ingestion clears a
        /// game's unknown XML elements, wiping the Roms Manager ownership tags
        /// while &lt;hidden&gt; survives - the entry becomes an orphan. Carrying
        /// the existing apiexpose_romset_* tags inside the fragment lets ES
        /// reload them instead. Dedicated rollback: set to false at the first
        /// sign of addgames instability.</summary>
        public bool IncludeRomsetTagsInLivePayload { get; set; } = true;
        public int LiveEsMediaPushDelayMs { get; set; } = 1200;
        public int LiveEsAddGamesMinIntervalMs { get; set; } = 1200;
        public bool TraceLiveAddGamesPayloads { get; set; } = false;
        public int RemoteTextNoChangeCooldownMinutes { get; set; } = 720;

        /// <summary>TTL of the exact-local no-retry cache (media that exists
        /// locally but not in the exact region): after this many days the remote
        /// check is allowed again, so media published later on ScreenScraper
        /// becomes reachable. 0 or less restores the historical never-expire.</summary>
        public int RemoteExactLocalNoRetryTtlDays { get; set; } = 14;
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
        // The engine is no longer bundled in the installer (to keep it slim). When it is missing and
        // this URL is set, the API downloads the portable engine zip once, into TranslateLocallyToolsPath,
        // then proceeds. Empty (default) => the translation feature degrades gracefully ("missing-tool").
        public string TranslateLocallyEngineDownloadUrl { get; set; } = "";
        public string TranslateLocallyEngineDownloadSha256 { get; set; } = "";
        public bool ProjectedMediaIndexCacheEnabled { get; set; } = true;
        public bool ScreenScraperRawCacheEnabled { get; set; } = true;
        // defaults aligned with appsettings.json (the shipped source of truth):
        // a missing appsettings key must not silently flip a media kind on
        public bool MarqueeScrapingEnabled { get; set; } = true;
        public bool ScreenMarqueeScrapingEnabled { get; set; } = false;
        public bool ScreenMarqueeSmallScrapingEnabled { get; set; } = false;
        public bool SteamGridScrapingEnabled { get; set; } = false;
        public bool MixScrapingEnabled { get; set; } = false;
        public bool MapScrapingEnabled { get; set; } = false;
        public bool ManualScrapingEnabled { get; set; } = false;
        public bool MagazineScrapingEnabled { get; set; } = false;
        public bool VideoScrapingEnabled { get; set; } = true;
        public bool VideoNormalizedScrapingEnabled { get; set; } = false;
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

        /// <summary>DÉPRÉCIÉ (LOT 1) - la décision « supprimer les copies roms/ après migration »
        /// est désormais portée par <see cref="MediaMigrationOptions.Mode"/> (`move` = supprime,
        /// `copy` = garde). Conservé pour ne pas casser les configs existantes ; il n'est plus lu
        /// pour décider de l'autorun (qui n'a plus lieu par défaut). Sera retiré au LOT 9.</summary>
        public bool RemoveRomsMediaAfterCanonicalMigration { get; set; } = true;
    }

    /// <summary>
    /// LOT 1 - migration des médias déjà présents sous roms/ vers le store canonique, DÉCOUPLÉE
    /// du reste du sous-système média. Par défaut <c>none</c> : la migration ne se lance PLUS au
    /// démarrage, et l'auto-scraping (qui écrit les nouveaux médias dans le store canonique)
    /// continue de fonctionner normalement - c'est le mode hybride voulu. La migration devient un
    /// acte explicite, choisi par l'opérateur.
    /// </summary>
    public class MediaMigrationOptions
    {
        /// <summary>
        /// <c>none</c> (défaut) : aucune migration, aucun autorun.
        /// <c>copy</c> : migrer les médias roms/ vers le store canonique, GARDER les copies roms/.
        /// <c>move</c> : migrer puis SUPPRIMER les copies roms/ (ancien comportement
        /// RemoveRomsMediaAfterCanonicalMigration=true).
        /// </summary>
        public string Mode { get; set; } = "none";
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

        /// <summary>LOT 5 (§10.1) - how a resolved medium is written into a generic gamelist slot.
        /// "fill_missing" (default) fills empty slots and updates slots APIExpose still owns, but
        /// never overwrites a user binding; "managed" behaves the same for the write decision;
        /// "force" reallocates unconditionally (explicit user action only).</summary>
        public string WritePolicy { get; set; } = "fill_missing";

        /// <summary>LOT 5 (2/2) - route gamelist media writes through <see cref="WritePolicy"/> and the
        /// ownership sidecar. OFF by default: writes keep their legacy overwrite behavior until a
        /// cabinet canary validates that user bindings survive a re-scrape.</summary>
        public bool WritePolicyEnabled { get; set; } = false;
    }

    /// <summary>
    /// HP3 media-discovery tuning. The directory-listing cache is ON by default since 1.6.4:
    /// a real-cabinet canary removed ~97% of directory enumerations on navigation with no media
    /// regression (validated by the directory mtime + a short safety TTL). Set
    /// DirectoryCacheEnabled=false to fall back to the HP1/HP2 per-publication listing.
    /// </summary>
    public class MediaDiscoveryOptions
    {
        /// <summary>Reuse a directory's file listing between publications (validated by the
        /// directory mtime, backstopped by <see cref="SafetyTtlSeconds"/>).</summary>
        public bool DirectoryCacheEnabled { get; set; } = true;

        /// <summary>Upper bound on how long a positive listing is trusted even if the directory
        /// mtime never moved - the guard for filesystems that don't update it.</summary>
        public int SafetyTtlSeconds { get; set; } = 5;

        /// <summary>How long an absent directory is remembered as absent (it has no mtime to
        /// trust); a writer creating it invalidates explicitly for immediacy.</summary>
        public int NegativeTtlSeconds { get; set; } = 1;

        /// <summary>LRU ceiling on cached directories.</summary>
        public int MaxCachedDirectories { get; set; } = 4096;

        /// <summary>LOT 7 - let the game snapshots FILL a missing media kind from the user gamelist:
        /// a roms/ file the gamelist references but that was never migrated into the canonical store
        /// becomes visible. OFF by default and ADDITIVE - it only adds a kind the canonical store
        /// lacks, never overrides a canonical asset - so it ships dark and is turned on by the canary.</summary>
        public bool GamelistMediaEnabled { get; set; } = false;

        /// <summary>HP5 - stamp each stream asset with PathRoot ("apiexpose" / "retrobat" /
        /// "external-local"), the root its relative Path is resolved against. OFF by default and
        /// additive (the field is omitted from the JSON when off, SnapshotVersion stays 2): an
        /// older MarqueeManager never sees it, a newer one uses it to resolve deterministically
        /// instead of guessing plugin-then-RetroBat. Turn on once the consumers understand it.</summary>
        public bool EmitPathRoot { get; set; } = false;

        /// <summary>LOT 6 (2/2) - let the resolver-driven <see cref="RetroBat.Api.Media.MediaScrapePlanner"/>
        /// SUPPRESS a scrape need when the canonical store already satisfies the kind (local-first,
        /// no redundant re-scrape). OFF by default: the scrape-need decision stays purely slot-based.
        /// The planner may only remove a need, never add one, so a genuinely missing kind is never
        /// skipped. Validate with a scrape canary before turning on.</summary>
        public bool ScrapePlannerEnabled { get; set; } = false;
    }

    public class ApiSettingsOptions
    {
        public string RegionProfile { get; set; } = string.Empty;
        public string LanguageProfile { get; set; } = string.Empty;
        public bool RepairGamelistsOnStartup { get; set; } = false;
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

    public class MonitorSyncOptions
    {
        // Quand true, APIExpose écrit <système>.MonitorIndex dans es_settings.cfg pour
        // que les émulateurs standalone (MAME/FBNeo…) ciblent l'écran RetroBat. La
        // valeur = le NUMÉRO N du device name GDI \\.\DISPLAYN de cet écran (ce que
        // emulatorLauncher concatène en "\\.\DISPLAY" + MonitorIndex), recalculé au
        // démarrage et à chaque changement d'écran dans Marquee Manager (le dropdown
        // ES plafonne à 10 et ne peut pas exprimer un device number élevé comme 24).
        public bool Enabled { get; set; } = true;
        public string[] Systems { get; set; } = new[]
        {
            "mame", "fbneo", "fba", "hbmame", "neogeo", "neogeo64", "neogeocd"
        };
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

    public class RetroAchievementsOptions
    {
        public bool Enabled { get; set; } = true;
        public RetroAchievementsProxyOptions Proxy { get; set; } = new();
        public RetroAchievementsApiOptions Api { get; set; } = new();
        public RetroAchievementsCacheOptions Cache { get; set; } = new();
        public RetroAchievementsSecurityOptions Security { get; set; } = new();
    }

    public class RetroAchievementsProxyOptions
    {
        public bool Enabled { get; set; } = true;
        public string TargetHost { get; set; } = "https://retroachievements.org";
        public string Route { get; set; } = "/dorequest.php";
        public int TimeoutMs { get; set; } = 10000;
        public bool EmitRequestEvents { get; set; } = true;
        public bool EmitResponseEvents { get; set; } = true;
    }

    public class RetroAchievementsApiOptions
    {
        public bool Enabled { get; set; } = true;
        public string BaseUrl { get; set; } = "https://retroachievements.org/API";
        public string Username { get; set; } = string.Empty;
        public string WebApiKey { get; set; } = string.Empty;
        public int TimeoutMs { get; set; } = 10000;
        public bool DownloadBadges { get; set; } = true;
        public bool DownloadGameImages { get; set; } = true;
        public bool DownloadUserImages { get; set; } = true;
    }

    public class RetroAchievementsCacheOptions
    {
        public bool Enabled { get; set; } = true;
        public string RootPath { get; set; } = "media/retroachievements";
    }

    public class RetroAchievementsSecurityOptions
    {
        public bool PublishSensitiveValues { get; set; } = false;
        public bool StoreRawRequests { get; set; } = false;
    }

    public class RomSetManagerOptions
    {
        // defaults aligned with appsettings.json (the shipped source of truth):
        // a missing appsettings key must not silently change filter behavior
        public bool Enabled { get; set; } = true;
        public string GroupsRootPath { get; set; } = "resources/gamelist/systems";
        public bool NeverHideFavorites { get; set; } = true;
        public string Profile { get; set; } = "";
        public string RetroAchievementsMode { get; set; } = "always_show";
        public string LanguageMode { get; set; } = "show_all";
        public string RegionMode { get; set; } = "show_all";
        public string RomVersionMode { get; set; } = "auto";
        public string OfficialGamesMode { get; set; } = "auto";
        public string ClonesMode { get; set; } = "";
        public string PrototypesMode { get; set; } = "";
        public string DemosMode { get; set; } = "";
        public string BetaAlphaMode { get; set; } = "";
        public string LocationTestsMode { get; set; } = "hide";
        public string UsefulPatchesMode { get; set; } = "";
        public string HacksModsMode { get; set; } = "";
        public string CheatsTrainersMode { get; set; } = "";
        public string BootlegsPiratesMode { get; set; } = "";
        public string UnlicensedMode { get; set; } = "";
        public string HomebrewsAftermarketMode { get; set; } = "";
        public string AdultMode { get; set; } = "hide";
        public string CasinoMode { get; set; } = "hide";
        public string MahjongMode { get; set; } = "hide";
        public string QuizMode { get; set; } = "hide";
        public string NonGamesMode { get; set; } = "hide";
        public string UnknownRomsMode { get; set; } = "show";
        public string ArcadeDiagnosticsMode { get; set; } = "show";
        public bool OnlyRetroAchievements { get; set; } = false;
        public bool ShowClones { get; set; } = false;
        public bool ShowPrototypes { get; set; } = false;
        public bool ShowBootlegsAndHacks { get; set; } = false;
        public bool ShowAdult { get; set; } = false;
        public bool ShowCasino { get; set; } = false;
        public bool ShowMahjong { get; set; } = false;
        public bool ShowNonGames { get; set; } = true;
        public bool ShowNonArcade { get; set; } = false;
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
        public string VariantMode { get; set; } = "hide_variants";
        public string RegionProfile { get; set; } = string.Empty;
        public string LanguageProfile { get; set; } = string.Empty;
        public string Translations { get; set; } = "prefer_if_language_match";
        public string ArcadeHandling { get; set; } = "disabled";
        public string OutputMode { get; set; } = "gamelist_hidden";
        public bool DebugReport { get; set; } = false;
        public bool ReloadGamesAfterApply { get; set; } = true;
        public bool RomPackInstallerEnabled { get; set; } = true;
        public bool RomPackInstallerUnzipRoms { get; set; } = false;
        public bool OnTheFlyRomInstallerEnabled { get; set; } = true;
        public string OnTheFlyRomExtractionTrigger { get; set; } = "game_start";
        public bool OnTheFlyRomResetAfterGameEndEnabled { get; set; } = true;
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

    /// <summary>Cabinet badge overlay (check-in QR + cabinet number over the
    /// RetroBat screen, driven by the fleet hub). Venue opt-in.</summary>
    public class CabinetBadgeOverlayOptions
    {
        public bool Enabled { get; set; }
        public double Opacity { get; set; } = 0.92d;

        /// <summary>Écran cible : « auto » = l'écran d'EmulationStation ;
        /// sinon l'index d'un écran (0, 1, 2…) pour viser un autre affichage
        /// de la borne (marquee, topper…).</summary>
        public string Screen { get; set; } = "auto";

        /// <summary>Accroche au-dessus du QR, dans la langue de la salle
        /// (« Scan to play » s'affiche toujours en dessous).</summary>
        public string Title { get; set; } = "Scannez pour jouer";
    }

    public class StartupOverlayOptions
    {
        public bool Enabled { get; set; } = false;
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
        public bool VisualAdaptiveThrottleEnabled { get; set; } = true;
        public bool VisualStaleDropEnabled { get; set; } = true;
        public int VisualMinIntervalFloorMs { get; set; } = 30;
        public int VisualMinIntervalCeilingMs { get; set; } = 50;
        public int VisualTargetAgeMs { get; set; } = 90;
        public int VisualFinalFlushMs { get; set; } = 50;
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
        public string LogFilePath { get; set; } = ".log/installer-deployment.jsonl";
        public bool DryRunOnStartup { get; set; } = false;
        public bool SyncThemesOnStartup { get; set; } = true;
        public bool SyncScriptsOnStartup { get; set; } = true;
        public bool SyncGameInfosOnStartup { get; set; } = true;
        public string GameInfosSourcePath { get; set; } = "resources/theme/gameinfos";
        public string GameInfosTargetPath { get; set; } = string.Empty;
        public string HashManifestPath { get; set; } = ".log/installer-deployment-hashes.json";
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
        public string BackupPath { get; set; } = ".log/es-features-menu/backups";
        public bool BackupEnabled { get; set; } = true;
        public int BackupRetentionCount { get; set; } = 4;
        public string LogFilePath { get; set; } = ".log/es-features-menu/deployment.jsonl";
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

    /// <summary>
    /// La mise a jour automatique d'APIExpose (voir <see cref="SelfUpdateService"/>).
    /// Section de configuration : <c>ApiExpose:SelfUpdate</c>.
    /// </summary>
    public class SelfUpdateOptions
    {
        /// <summary>Prendre la derniere version publiee au lancement. Vrai par defaut.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Le depot dont on lit les releases.</summary>
        public string Repository { get; set; } = "Nelfe80/RetroBat-APIExpose";

        /// <summary>Attente apres le demarrage : le watcher et les fournisseurs d'abord.</summary>
        public int StartupDelaySeconds { get; set; } = 90;

        /// <summary>
        /// On regarde de nouveau toutes les N heures, parce qu'une borne peut rester allumee des
        /// jours et parce qu'elle etait peut-etre occupee la fois d'avant. 0 = une seule fois.
        /// </summary>
        public int IntervalHours { get; set; } = 6;
    }

    public class RetroArchWrapperDeploymentOptions
    {
        /// <summary>
        /// Deployer le wrapper sur les cores au demarrage (et le rafraichir quand le build de
        /// reference change). Vrai par defaut : c'est ce qui fait qu'une borne fraichement
        /// installee lit ses jeux RetroArch sans qu'on y touche. L'ancienne clef `Enabled`
        /// (fausse par defaut, et qui bloquait jusqu'au POST explicite) n'est plus lue :
        /// les appsettings de la flotte qui la portent tombent sur ce defaut.
        /// </summary>
        public bool AutoDeploy { get; set; } = true;
        public string WrapperDllPath { get; set; } = "wrapper/wrapper.dll";
        public string CoresPath { get; set; } = "../../emulators/retroarch/cores";
        public string RealCoresPath { get; set; } = "../../emulators/retroarch/cores_real";
        public string BackupPath { get; set; } = ".log/wrapper-deployment/backups";
        public string LogFilePath { get; set; } = ".log/wrapper-deployment.jsonl";
        /// <summary>Ce que l'audit sait de chaque core (taille, date, empreinte) : il ne relit que ce qui a bouge.</summary>
        public string CachePath { get; set; } = ".log/wrapper-deployment-cache.json";
        public bool WrapAllCores { get; set; } = true;
        public List<string> TargetCores { get; set; } = new();
        public bool SkipIfRetroArchRunning { get; set; } = true;
        public bool DryRunOnStartup { get; set; } = false;
        /// <summary>Quand RetroArch tourne au demarrage, on reessaie a cet intervalle.</summary>
        public int RetryIntervalSeconds { get; set; } = 60;
        public int MaxRetries { get; set; } = 30;
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
            ".log/refresh-tracking.jsonl",
            ".log/media-update-audit.jsonl",
            ".log/local-gamelist-update.jsonl",
            ".log/gamelist-display-name-normalization.jsonl"
        };
        public GameSessionLogOptions GameSessionLogs { get; set; } = new();
        public EsFlowLogOptions EsFlowLogs { get; set; } = new();
    }

    public class GameSessionLogOptions
    {
        public bool Enabled { get; set; } = true;
        public string DirectoryPath { get; set; } = ".log/game-sessions";
        public bool ResetOnStartup { get; set; } = true;
    }

    public class EsFlowLogOptions
    {
        public bool Enabled { get; set; } = false;
        public string FilePath { get; set; } = ".log/es-flow.jsonl";
        public bool ResetOnStartup { get; set; } = true;
    }
}
