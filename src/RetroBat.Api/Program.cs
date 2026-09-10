using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Hubs;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Events;
using RetroBat.Providers.MameOutputs;
using RetroBat.Providers.EmulationStation;
using RetroBat.Providers.Hi2Txt;
using RetroBat.Providers.RetroArchWrapper;
using RetroBat.MediaStore;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;
using System.Reflection;
using System.Text.Json.Serialization;
using RetroBat.Api.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

// --hide-console: the ES start hook launches the exe directly and lets it hide
// its own window (PowerShell -WindowStyle Hidden tripped antivirus ClickFix
// heuristics, and start /MIN alone leaves a visible window behind ES).
if (args.Any(arg => string.Equals(arg, "--hide-console", StringComparison.OrdinalIgnoreCase)))
{
    RetroBat.Api.Infrastructure.ConsoleWindowNative.Hide();
}

var testModeRequested = args.Any(arg =>
    string.Equals(arg, "--test-mode", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(arg, "/test-mode", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(arg, "test-mode", StringComparison.OrdinalIgnoreCase));
var hostArgs = args
    .Where(arg =>
        !string.Equals(arg, "--test-mode", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(arg, "/test-mode", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(arg, "test-mode", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(arg, "--hide-console", StringComparison.OrdinalIgnoreCase))
    .ToArray();

var builder = WebApplication.CreateBuilder(hostArgs);

builder.Configuration
    .AddJsonFile(Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json"), optional: true, reloadOnChange: true);

if (testModeRequested)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ApiExpose:TestMode:Enabled"] = "true"
    });
}

var logConfig = builder.Configuration.GetSection("ApiExpose:Logging");
var consoleLoggingEnabled = logConfig.GetValue("ConsoleEnabled", true);
if (!consoleLoggingEnabled)
{
    builder.Logging.ClearProviders();
}

// General runtime file log: APIExpose runs hidden (--hide-console) so console output
// is lost. This captures every existing _logger.LogXxx into .log/apiexpose-runtime.log
// so incidents like a black marquee after a media migration are diagnosable.
if (logConfig.GetValue("FileEnabled", true))
{
    var minLevel = Enum.TryParse<LogLevel>(logConfig.GetValue("MinimumLevel", "Information"), ignoreCase: true, out var parsed)
        ? parsed
        : LogLevel.Information;
    var filePathRaw = logConfig.GetValue("FilePath", ".log/apiexpose-runtime.log")!;
    var filePath = Path.IsPathRooted(filePathRaw) ? filePathRaw : Path.Combine(RetroBatPaths.PluginRoot, filePathRaw);
    var resetOnStartup = logConfig.GetValue("ResetRuntimeLogsOnStartup", true);

    // let our own logs (down to the configured level) reach the file; keep the
    // framework's own chatter at Warning so the runtime log stays readable.
    builder.Logging.SetMinimumLevel(minLevel);
    builder.Logging.AddFilter("RetroBat", minLevel);
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Logging.AddFilter("System", LogLevel.Warning);
    builder.Logging.AddProvider(new RuntimeFileLoggerProvider(filePath, minLevel, resetOnStartup));
}

builder.Services.Configure<ApiExposeOptions>(builder.Configuration.GetSection("ApiExpose"));
builder.Services.Configure<EmulationStationWatcherOptions>(builder.Configuration.GetSection("ApiExpose:EmulationStationWatcher"));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
    });
// Overlays and SDK pages (OBS Browser Source, file:// or local http origins) consume
// this loopback-only API cross-origin; without CORS every browser fetch is blocked.
// Origines acceptees.
//
// « N'importe laquelle » etait le reglage d'une API qu'on croyait cantonnee a
// la machine. Elle ne l'est pas : tout onglet ouvert sur le poste peut la
// joindre. On accepte donc la boucle locale - d'ou viennent les overlays et les
// outils - la plateforme, et ce que la configuration ajoute explicitement.
//
// Cette liste ne protege PAS a elle seule : un navigateur envoie quand meme une
// requete « simple » et n'en cache que la reponse. C'est la garde d'ecriture,
// plus bas, qui fait le travail. Celle-ci evite seulement qu'un site lise ce
// qui se passe sur la machine du joueur.
var allowedOrigins = (builder.Configuration["Security:AllowedOrigins"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (parsed.IsLoopback)
            {
                return true;
            }

            if (parsed.Scheme == Uri.UriSchemeHttps
                && (parsed.Host.Equals("nelfetech.com", StringComparison.OrdinalIgnoreCase)
                    || parsed.Host.EndsWith(".nelfetech.com", StringComparison.OrdinalIgnoreCase)
                    || parsed.Host.Equals("nelfeplay.com", StringComparison.OrdinalIgnoreCase)
                    || parsed.Host.EndsWith(".nelfeplay.com", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
        })
        .AllowAnyHeader()
        .AllowAnyMethod());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var apiVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "APIExpose - RetroBat Local API",
        Version = apiVersion,
        Description =
            "Local API of the APIExpose plugin for RetroBat / EmulationStation.\n\n" +
            "**Service status: `GET /api/v1/status`** (API, WebSocket, EmulationStation, manager gates).\n" +
            "**Real-time streams: `GET /api/v1/ws/streams`** then `ws://127.0.0.1:12345/ws[/{stream}]`.\n\n" +
            "Contract policy: additive JSON only (no field is ever removed or renamed); the " +
            "/addgames payload sent to EmulationStation is frozen; /reloadgames is never used as an " +
            "automatic refresh. The groups below follow the manager logic of the EmulationStation " +
            "menu: a manager switched OFF disables every feature of its branch.\n\n" +
            "Full documentation: https://nelfe80.github.io/RetroBat-APIExpose/"
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }

    // Nested DTOs reuse short names across services (MameCfgDeployService.Report
    // vs FbneoRmpDeployService.Report): the default schemaId (simple name)
    // collides and the whole swagger.json generation returns 500.
    options.CustomSchemaIds(type => type.IsNested && type.DeclaringType is not null
        ? $"{type.DeclaringType.Name}{type.Name}"
        : type.Name);

    options.DocumentFilter<RetroBat.Api.Infrastructure.SwaggerTagOrderDocumentFilter>();
    options.OperationFilter<RetroBat.Api.Infrastructure.SwaggerParameterExamplesOperationFilter>();
});

// Core Services
var eventBus = new SimpleEventBus();
builder.Services.AddSingleton<IEventBus>(eventBus);

builder.Services.AddSingleton<WebSocketConnectionManager>();
// Netplay. Le choix du relais garde sa mesure une journee, d'ou le singleton.
builder.Services.AddSingleton<RetroBat.Api.Netplay.NetplayRelayPicker>();
builder.Services.AddSingleton<RetroBat.Api.Netplay.NetplayLobbyClient>();
builder.Services.AddSingleton<RetroBat.Api.Netplay.NetplayHostService>();
builder.Services.AddSingleton<RetroBat.Api.Netplay.NetplayGuestService>();
// « Je regarde le direct X » : un seul etat pour toute l'application, sinon la facade et
// l'overlay pourraient viser deux seances differentes.
builder.Services.AddSingleton<RetroBat.Api.Netplay.LiveSpectateState>();
builder.Services.AddSingleton<RetroBat.Api.Netplay.LiveReactionUploader>();
builder.Services.AddSingleton<MediaRuntimeState>();
builder.Services.AddSingleton<StartupOverlayService>();
builder.Services.AddSingleton<LiveContestOverlayService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<LiveContestClientService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveContestClientService>());
// Agent Nelfe Play : la borne va chercher les jeux acquis par le joueur.
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.NelfePlayDeviceStore>();
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.NelfePlayAgentService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Infrastructure.NelfePlayAgentService>());
// Canal RAM communautaire : pull périodique des .MEM publiés sur RetroBat-RAM-Community
// vers le dossier RAM local (lecture seule, n'écrase pas l'officiel). Réglages : ApiExpose:CommunityRam.
builder.Services.AddHostedService<RetroBat.Api.Infrastructure.CommunityRamSyncService>();
// Releve d'audience : ce qui est joue et combien de temps, jamais par qui.
//
// L'interrupteur est reel : une mesure qu'on ne peut pas eteindre n'est pas une
// mesure, c'est une surveillance. Il se coupe dans appsettings sous
// ApiExpose:NelfePlay:PlayReportingEnabled.
RetroBat.Api.Infrastructure.NelfePlayPlayReporter.Enabled =
    builder.Configuration.GetValue("ApiExpose:NelfePlay:PlayReportingEnabled", true);
var nelfePlayBaseUrl = builder.Configuration.GetValue<string>("ApiExpose:NelfePlay:BaseUrl");
if (!string.IsNullOrWhiteSpace(nelfePlayBaseUrl))
{
    RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl = nelfePlayBaseUrl;
}
// Connexion d'une machine a un compte, sans code a recopier : la machine
// demande, une personne connectee accorde.
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.NelfePlayLinkService>();
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.NelfePlayPlayReporter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Infrastructure.NelfePlayPlayReporter>());
// Joueur de session (Lot 6 / Station) : code joueur RGPC posé au check-in par le hub,
// lu par le reporter pour attribuer le record certifié au joueur + tag salle.
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.NelfePlayScoringSessionService>();
// Scoring certifié - enrôlement de la clé d'appareil, ticket de session, capture
// attestation + score (soumission du passeport = étape 3c).
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.NelfePlayScoringReporter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Infrastructure.NelfePlayScoringReporter>());
// Déchiffrement au lancement puis effacement : rien de clair ne survit à la partie.
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.NelfePlayLaunchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Infrastructure.NelfePlayLaunchService>());
builder.Services.AddSingleton<IStartupOverlayService>(sp => sp.GetRequiredService<StartupOverlayService>());
builder.Services.AddSingleton<ToastOverlayService>();
builder.Services.AddSingleton<IToastNotificationService>(sp => sp.GetRequiredService<ToastOverlayService>());
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.CheevosSessionService>();
builder.Services.AddSingleton<CabinetBadgeOverlayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CabinetBadgeOverlayService>());
builder.Services.AddSingleton<ChallengeAnnounceOverlayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChallengeAnnounceOverlayService>());
builder.Services.AddSingleton<CabinetLockOverlayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CabinetLockOverlayService>());
// « Réclame ton record ! » - surimpression de fin de partie pour rattacher un
// score anonyme certifié à un compte (déclenchée par le scoring reporter).
builder.Services.AddSingleton<ClaimOverlayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClaimOverlayService>());
builder.Services.AddSingleton<EsNotifyDeduplicationService>();
builder.Services.AddSingleton<IEmulationStationNotificationService, EmulationStationNotificationService>();
builder.Services.AddSingleton<GameListImpactWarningService>();
builder.Services.AddSingleton<TaskProgressOverlayService>();
builder.Services.AddSingleton<ITaskProgressService>(sp => sp.GetRequiredService<TaskProgressOverlayService>());

builder.Services.AddSingleton<IMediaStore, BasicMediaStore>();
builder.Services.AddSingleton<IMediaAliasStore, JsonMediaAliasStore>();
builder.Services.AddSingleton<ILocalizedTextStore, LocalizedTextStore>();
builder.Services.AddSingleton<MediaReferenceCatalog>();
builder.Services.AddSingleton<IEsSettingsStore, EsSettingsStore>();
builder.Services.AddSingleton<EsSettingsChangeBus>();
builder.Services.AddSingleton<IEsSettingsChangeBus>(sp => sp.GetRequiredService<EsSettingsChangeBus>());
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.MonitorIndexSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Infrastructure.MonitorIndexSyncService>());
builder.Services.AddSingleton<IGamelistStore, GamelistStore>();
builder.Services.AddSingleton(typeof(IRuntimeConfigSnapshotProvider<>), typeof(RuntimeConfigSnapshotProvider<>));
builder.Services.AddSingleton<EmulationStationSettingsService>();
builder.Services.AddSingleton<EmulationStationSystemConfigService>();
builder.Services.AddSingleton<InterfaceTextService>();
builder.Services.AddSingleton<ApiExposeTaxonomyService>();
builder.Services.AddSingleton<RomMetadataResolver>();
builder.Services.AddSingleton<RomCanonicalResolver>();
builder.Services.AddSingleton<SystemIdNormalizer>();
builder.Services.AddSingleton<GameNameNormalizer>();
builder.Services.AddSingleton<MediaSystemRules>();
builder.Services.AddSingleton<MediaLocalizationResolver>();
builder.Services.AddSingleton<LocalMediaIndexService>();
builder.Services.AddSingleton<MediaNeedEvaluator>();
builder.Services.AddSingleton<MediaQualificationService>();
builder.Services.AddSingleton<GamelistMediaCatalogReader>();
builder.Services.AddSingleton<ICanonicalMediaCatalogSource, ProjectionCanonicalMediaCatalogSource>();
builder.Services.AddSingleton<GameMediaCatalogService>();
builder.Services.AddSingleton<MediaResolver>();
// LOT 6 - resolver-driven scrape-need planner (not yet wired into the live scrape path).
builder.Services.AddSingleton<MediaScrapePlanner>();
// LOT 8 - secure, allowlist-scoped resolver for serving gamelist media (canonical + user) over HTTP.
builder.Services.AddSingleton(new GamelistMediaAssetResolver(new Dictionary<string, string>
{
    ["media"] = RetroBatPaths.MediaRoot,
    ["roms"] = RetroBatPaths.RomsRoot
}));
// LOT 5 - ownership sidecar, kept out of roms/ under the plugin's resources tree.
builder.Services.AddSingleton(new MediaSidecarStore(
    Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "media-sidecar")));
builder.Services.AddSingleton<IMediaDiscoveryInvalidator, MediaDiscoveryInvalidator>();
builder.Services.AddSingleton<EsProjectionService>();
builder.Services.AddSingleton<MameGamelistGroupIndex>();
builder.Services.AddSingleton<LocalScrapingPreviewService>();
builder.Services.AddSingleton<ScreenScraperConnectionService>();
builder.Services.AddSingleton<ScreenScraperCapabilityService>();
builder.Services.AddSingleton<ScreenScraperRawCacheMetadataService>();
builder.Services.AddSingleton<DescriptionTranslationService>();
builder.Services.AddSingleton<ScreenScraperRemoteProvider>();
builder.Services.AddSingleton<MarqueeAutogenService>();
builder.Services.AddSingleton<RemoteScrapeQueueService>();
builder.Services.AddSingleton<RemoteScrapingService>();
builder.Services.AddSingleton<GamelistUpdateService>();
builder.Services.AddSingleton<LocalizedGamelistCacheService>();
builder.Services.AddSingleton<MediaMaintenanceService>();
builder.Services.AddSingleton<GamelistGenerationService>();
builder.Services.AddSingleton<LocalGamelistUpdateService>();
builder.Services.AddSingleton<RomSetManagerService>();
builder.Services.AddSingleton<RomPackInstallerService>();
builder.Services.AddSingleton<CollectionPackInstallerService>();
builder.Services.AddSingleton<IGamelistSelectionSyncService>(sp => sp.GetRequiredService<GamelistUpdateService>());
builder.Services.AddSingleton<IMediaPrefetchService, MediaPrefetchService>();
builder.Services.AddSingleton<ApiContext>();
builder.Services.AddSingleton<PanelsCatalogService>();
builder.Services.AddSingleton<ControlFilesCatalogService>();
builder.Services.AddSingleton<PanelDefinitionProjectionService>();
builder.Services.AddSingleton<PanelRemapExportService>();
builder.Services.AddSingleton<MameCfgDeployService>();
builder.Services.AddSingleton<FbneoRmpDeployService>();
builder.Services.AddSingleton<DatasThemeExposeService>();
builder.Services.AddSingleton<ApiExposeAppsettingsSyncService>();
builder.Services.AddSingleton<IEsControllerInputBackend, DryRunEsControllerInputBackend>();
builder.Services.AddSingleton<IEsControllerInputBackend, KeyboardEsControllerInputBackend>();
builder.Services.AddSingleton<EsControllerInputBackendProvider>();
builder.Services.AddSingleton<EsControllerService>();
builder.Services.AddSingleton<IHiscoreService, Hi2TxtExtractionService>();
builder.Services.AddSingleton<DatasThemeHiscoreWriter>();
builder.Services.AddSingleton<EmulationStationHiscoreThemeWriter>();
builder.Services.AddSingleton<IHiscoreThemeWriter, CompositeHiscoreThemeWriter>();
builder.Services.AddSingleton<InstallerDeploymentService>();
builder.Services.AddSingleton<EsFeaturesMenuDeploymentService>();
builder.Services.AddSingleton<ApiExposeRuntimeOptionsService>();
builder.Services.AddSingleton<RetroArchWrapperDeploymentService>();
builder.Services.AddSingleton<IIngameSourceArbitrationService, IngameSourceArbitrationService>();
builder.Services.AddSingleton<IngameGameplayStateService>();
builder.Services.AddSingleton<RetroAchievementsService>();
builder.Services.AddSingleton<RetroAchievementsLeaderboardHistoryStore>();
builder.Services.AddSingleton<StartupReadinessState>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EsSettingsChangeBus>());
if (!testModeRequested)
{
    builder.Services.AddHostedService<RuntimeLogMaintenanceHostedService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupOverlayService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ToastOverlayService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskProgressOverlayService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RomPackInstallerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectionPackInstallerService>());
    builder.Services.AddHostedService<PendingExtendedGamelistHostedService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DescriptionTranslationService>());
    builder.Services.AddHostedService<GamelistDisplayNameNormalizationHostedService>();
    builder.Services.AddHostedService<StartupGamelistMediaNormalizationHostedService>();
    builder.Services.AddHostedService<LocalizedGamelistCachePrebuildHostedService>();
    builder.Services.AddHostedService<ReloadGamesHostedService>();
    builder.Services.AddHostedService<EsFeaturesMenuDeploymentHostedService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PanelRemapExportService>());
    builder.Services.AddHostedService<EmulationStationLifecycleHostedService>();
    builder.Services.AddHostedService<ApiExposeSettingsDefaultsHostedService>();
    builder.Services.AddHostedService<RomsMediaCanonicalMigrationHostedService>();
    builder.Services.AddHostedService<ApiExposeSettingsNotificationHostedService>();
    builder.Services.AddHostedService<LocalMediaManagerActionHostedService>();
    builder.Services.AddHostedService<EsLanguageGamelistSyncHostedService>();
    builder.Services.AddHostedService<InstallerDeploymentHostedService>();
    builder.Services.AddHostedService<RetroArchWrapperDeploymentHostedService>();
    builder.Services.AddHostedService<MameLuaIngamePluginDeploymentHostedService>();
    builder.Services.AddHostedService<RomSetManagerSettingsWatcherHostedService>();
    builder.Services.AddHostedService<DatasThemeExposeSettingsWatcherHostedService>();
    builder.Services.AddHostedService<PersoMemFlagSyncHostedService>();
}
builder.Services.AddHostedService<CpoPanelWebSocketProjectionService>();
// presses resolved to panel slots, for the wiring check
builder.Services.AddHostedService<PanelInputWatcherService>();
builder.Services.AddHostedService<RetroAchievementsRuntimeProjectionService>();
builder.Services.AddHostedService<RetroAchievementsLeaderboardInferenceService>();
builder.Services.AddHostedService<RetroArchLogMonitorService>();

// ── Replay (R1 recorder + R2 player) : local RetroArch (CDC_DEV_NELFE_REPLAY) ──
builder.Services.AddSingleton<RetroBat.Api.Replay.Runtime.RetroArchReplayClient>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Runtime.ReplayCoreTimingProbe>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Storage.ReplayStore>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Playback.ReplayLaunchTokenStore>();
// Qui regarde : jeton opaque, jamais une identite de compte cote borne.
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayViewerSession>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Playback.EsSystemsRomPaths>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Playback.ReplayRuntimeResolver>();
// R7 : les rôles (seams NelfeNet) pointent tous vers l'UNIQUE instance locale. Le jour où un
// replay pourra venir d'une autre borne, seule cette ligne-là change — pas le lecteur.
builder.Services.AddSingleton<RetroBat.Api.Replay.Storage.IReplayManifestStore>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Storage.ReplayStore>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Storage.IReplayObjectStore>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Storage.ReplayStore>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Storage.IReplayMetadataStore>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Storage.ReplayStore>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Storage.IReplayIndex>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Storage.ReplayStore>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Playback.IReplayRuntimeResolver>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Playback.ReplayRuntimeResolver>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayPeerStore>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayNetworkStateService>();
// Les portes d'entrée NelfeNet (CDC §53). Toutes actives en même temps : une borne chez un
// particulier n'a ni hub ni administrateur, l'absence d'une porte ne doit pas fermer les autres.
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ManualPeerSource>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.LanPeerSource>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.AnchorPeerSource>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.PlatformPeerSource>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.IReplayPeerSource>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.ManualPeerSource>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.IReplayPeerSource>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.LanPeerSource>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.IReplayPeerSource>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.AnchorPeerSource>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.IReplayPeerSource>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.PlatformPeerSource>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.MirrorPeerSource>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.IReplayPeerSource>(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.MirrorPeerSource>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayPeerDirectory>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayTransitPublisher>();
// File de semis : l'intention de diffuser un record survit a l'extinction de la machine.
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplaySeedQueue>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplaySeedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.ReplaySeedService>());
// Agent de replication : c'est lui qui fait l'ESSAIM. Desactive par defaut, et sans classement
// suivi il ne fait rien : on ne telecharge pas les parties d'inconnus sur le PC de quelqu'un
// sans qu'il l'ait demande.
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayFollowStore>();
// Ce que cette borne joue vraiment : la cible du PRECHARGEMENT. Les classements suivis disent
// quoi conserver pour l'essaim, cette liste dit quoi avoir sous la main pour soi.
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayPlayedGamesStore>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayReplicationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.ReplayReplicationService>());
// Recensement des copies : sans lui, durable et degraded restent des mots.
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayReactionUploader>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.ReplayReactionUploader>());
// R9 : les reactions deviennent des evenements SIGNES, donc distribuables. La borne epingle la
// cle de l'emetteur (une fois, chez la plateforme), puis accepte des evenements de n'importe quel
// pair puisqu'elle sait a quelle empreinte ils doivent repondre.
// La photo du moment du record : prise PENDANT la partie, quand le score franchit le seuil du
// top (a la fermeture il n'y a plus rien a photographier), montee seulement si le score est
// publie. Une partie qui ne fait pas le top ne coute pas une seule capture.
builder.Services.AddHostedService<RetroBat.Api.Infrastructure.ScoreShotService>();
// Refaire l'image d'un record en REJOUANT son replay : reproductible, en definition d'origine,
// et c'est la seule facon de rattraper les records deja publies. Jamais automatique : la
// relecture prend l'ecran.
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.ScoreShotRegenerator>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Social.SocialIssuerPin>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Social.ReplaySocialStore>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Social.ReplaySocialFeedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Replay.Social.ReplaySocialFeedService>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayHoldingsReporter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.ReplayHoldingsReporter>());
builder.Services.AddHostedService<RetroBat.Api.Replay.Sharing.LanPeerResponderService>();
// Le RELAIS (CDC v2.1 52) : le pont pour les bornes qui ne peuvent pas se joindre. Il doit etre
// enregistre AVANT le resolveur, qui s'en sert comme dernier recours.
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplayRelayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Replay.Sharing.ReplayRelayService>());
builder.Services.AddSingleton<RetroBat.Api.Replay.Playback.IReplaySourceResolver, RetroBat.Api.Replay.Sharing.NelfeNetSourceResolver>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Sharing.ReplaySharePolicy>();
builder.Services.AddSingleton<RetroBat.Api.Replay.Playback.ReplayPlaybackService>();
builder.Services.AddHostedService<RetroBat.Api.Replay.Recording.ReplayRecorderService>();
builder.Services.AddHostedService<RetroBat.Api.Replay.Input.ReplayInputRouterService>();
// ReplayReactionService = singleton PARTAGÉ (hosted service + injecté dans le HUD pour GetCharge).
builder.Services.AddSingleton<RetroBat.Api.Replay.Input.ReplayReactionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetroBat.Api.Replay.Input.ReplayReactionService>());
builder.Services.AddHostedService<RetroBat.Api.Replay.Overlay.ReplayOverlayService>();
builder.Services.AddHostedService<RetroBat.Api.Replay.Overlay.ReplayReactionHudService>();

if (!testModeRequested)
{
    builder.Services.AddHostedService<PhysicalMediaWebSocketProjectionService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteScrapeQueueService>());
}

// Providers
if (!testModeRequested)
{
    builder.Services.AddHostedService<MameStartupConfigHostedService>();
}
builder.Services.AddHostedService<ProviderHostedService>();
builder.Services.AddSingleton<IProvider, EsFlowEventLogProvider>();
builder.Services.AddSingleton<IProvider, GameSessionEventLogProvider>();
// Le pont MAME est aussi resolu par son type : la capture du record lui demande un snapshot,
// et passer par la liste des IProvider pour retrouver celui-la serait fragile.
builder.Services.AddSingleton<MameLuaIngameProvider>();
builder.Services.AddSingleton<IProvider>(sp => sp.GetRequiredService<MameLuaIngameProvider>());
builder.Services.AddSingleton<IProvider, MameOutputsProvider>();
builder.Services.AddSingleton<IProvider>(sp => sp.GetRequiredService<IngameGameplayStateService>());
builder.Services.AddSingleton<IProvider>(sp => sp.GetRequiredService<RetroAchievementsService>());
builder.Services.AddSingleton<IProvider, LiveScoreAggregatorProvider>();
builder.Services.AddSingleton<IProvider, LiveTimerAggregatorProvider>();
builder.Services.AddSingleton<EmulationStationWatcherProvider>();
builder.Services.AddSingleton<IProvider>(sp => sp.GetRequiredService<EmulationStationWatcherProvider>());
builder.Services.AddSingleton<IProvider, Hi2TxtProvider>();
builder.Services.AddSingleton<RetroArchWrapperProvider>();
builder.Services.AddSingleton<IProvider>(sp => sp.GetRequiredService<RetroArchWrapperProvider>());
builder.Services.AddSingleton<IProvider, RetroArchConsoleHiscoreProvider>();

var app = builder.Build();

// Setup internal event subscriber to broadcast via WebSockets
var wsManager = app.Services.GetRequiredService<WebSocketConnectionManager>();
eventBus.Subscribe<EventEnvelope>(evt => 
{
    _ = wsManager.BroadcastAsync(evt);
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/swagger") &&
        !context.RequestServices.GetRequiredService<ApiExposeRuntimeOptionsService>().IsSwaggerEnabled())
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
    options.DisplayRequestDuration();
    options.DocumentTitle = "APIExpose - RetroBat Local API";
});

app.UseWebSockets();
app.UseRouting();

// X-04 - cle API de la borne : facultative (vide = LAN de confiance, defaut
// historique). Quand Security:ApiKey est renseignee, toute requete NON
// loopback vers /api ou /ws doit presenter X-Api-Key (le hub la pose sur tous
// ses appels via Hub:CabinetApiKey). Obligatoire avant toute exposition hors
// LAN.
// Empty in appsettings (no secret in the repo) → generate a unique per-cabinet key on
// first run, so non-loopback access stays gated with a key that differs per machine.
var cabinetApiKey = app.Configuration["Security:ApiKey"] ?? string.Empty;
if (cabinetApiKey.Length == 0)
{
    cabinetApiKey = RetroBat.Api.Infrastructure.CabinetApiKeyStore.GetOrCreate();
}
// NelfeNet : clé de PARTAGE, volontairement distincte de celle ci-dessus. La clé de borne
// administre la machine (lancer une lecture, deployer une configuration, tout lire) ; c'est ce
// qu'il faut au hub de flotte, ce n'est pas ce qu'on donne a une borne voisine qui veut recuperer
// un replay public. La cle de partage n'ouvre QUE la surface ci-dessous, et le controleur y
// restreint encore le CONTENU aux replays effectivement partageables.
// Garde EPROUVEE depuis une adresse non-loopback (2026-09-04) : sans cle 401 ; cle de partage =
// objet public 200, objet prive 404, liste reduite aux publics, manifeste public 200 / prive 404 ;
// hors surface (visibilite, play, etat, pairs, share-key) 401 ; cle de borne toujours pleine.
var replayShareKey = RetroBat.Api.Replay.Sharing.ReplayShareKeyStore.GetOrCreate();
// Adresse d'un reseau prive (RFC 1918, lien-local, ULA IPv6). Une adresse publique n'est JAMAIS
// « le reseau local » : la confiance LAN ne doit pas s'etendre a un client venu d'Internet.
static bool IsPrivateAddress(System.Net.IPAddress? address)
{
    if (address is null) return false;
    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
               || (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
    var b = address.GetAddressBytes();
    if (b.Length != 4) return false;
    return b[0] == 10
           || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
           || (b[0] == 192 && b[1] == 168)
           || (b[0] == 169 && b[1] == 254);
}

static bool IsShareSurface(HttpRequest request)
{
    if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
    var path = request.Path;
    if (path.StartsWithSegments("/api/v1/object")) return true;
    if (!path.StartsWithSegments("/api/v1/replays", out var rest)) return false;
    // /replays, /replays/{id}, /replays/{id}/manifest : rien d'autre.
    var segments = rest.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
    return segments.Length switch
    {
        0 => true,
        1 => true,
        2 => string.Equals(segments[1], "manifest", StringComparison.Ordinal),
        _ => false,
    };
}

if (cabinetApiKey.Length > 0)
{
    app.Use(async (context, next) =>
    {
        var remote = context.Connection.RemoteIpAddress;
        var isLoopback = remote is null || System.Net.IPAddress.IsLoopback(remote);
        var guarded = context.Request.Path.StartsWithSegments("/api") ||
                      context.Request.Path.StartsWithSegments("/ws");
        if (guarded && !isLoopback)
        {
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                ?? context.Request.Query["apiKey"].FirstOrDefault();

            var isCabinetKey = string.Equals(provided, cabinetApiKey, StringComparison.Ordinal);
            var isShareKey = replayShareKey.Length > 0
                             && string.Equals(provided, replayShareKey, StringComparison.Ordinal)
                             && IsShareSurface(context.Request);

            // Une machine qui nous administre depuis le reseau est une ANCRE (en pratique, un hub
            // de flotte). La borne ne connait pas l'adresse de son hub : il se presente en nous
            // parlant, et devient une porte d'entree vers les autres bornes.
            if (isCabinetKey) RetroBat.Api.Replay.Sharing.AnchorPeerSource.Remember(remote);

            // LAN de confiance, meme doctrine que la cle d'API historique : chez un particulier
            // avec deux bornes, personne ne recopie une cle d'une machine a l'autre. N'ouvre QUE
            // la surface de partage, et seulement depuis une adresse privee. Defaut : ferme.
            var lanTrusted = app.Configuration.GetValue("Replay:Share:TrustLocalNetwork", false)
                             && IsPrivateAddress(remote)
                             && IsShareSurface(context.Request);

            if (!isCabinetKey && !isShareKey && !lanTrusted)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Cle API requise (en-tete X-Api-Key)." });
                return;
            }
        }

        await next();
    });
}

// Fail LOUD when RetroBat can't be located: APIExpose resolves it as the grandparent
// of its plugin folder, so an install placed outside <RetroBat>\plugins\ points at an
// empty tree - the #1 cause of "empty game/system lists". Surfaced here + in /api/v1/status.
{
    var retroBatRoot = RetroBatPaths.RetroBatRoot;
    var romsFound = Directory.Exists(RetroBatPaths.RomsRoot);
    var esFound = Directory.Exists(RetroBatPaths.EmulationStationConfigRoot);
    if (romsFound && esFound)
    {
        app.Logger.LogInformation("RetroBat detecte : {Root} (roms + emulationstation presents).", retroBatRoot);
    }
    else
    {
        app.Logger.LogError(
            "RetroBat INTROUVABLE a {Root} (roms={Roms}, emulationstation={Es}). Les listes de jeux/systemes " +
            "seront VIDES : installez APIExpose dans <RetroBat>\\plugins\\APIExpose du bon RetroBat.",
            retroBatRoot, romsFound, esFound);
    }
}

// Le jeton local est cree AU DEMARRAGE, pas a la premiere verification : un
// overlay qui se lance en meme temps que nous doit pouvoir le lire tout de
// suite. Le creer paresseusement laissait le fichier absent tant que personne
// n'avait essaye d'ecrire - c'est-a-dire, en pratique, toujours.
app.Logger.LogInformation(
    "Jeton d'ecriture locale pret ({Length} caracteres).",
    LocalWriteToken.Value.Length);

// ── Garde d'ECRITURE : aucun site web ne pilote cette machine ─────────────
//
// La garde d'origine qui suit ne couvrait que deux prefixes. Tout le reste -
// lancer un jeu, deployer une configuration, appairer un compte - restait
// joignable depuis n'importe quelle page. Et deux details rendaient la chose
// pire qu'elle n'en avait l'air :
//
//   - une liste de chemins interdits pourrit : chaque nouvelle route arrive
//     AUTORISEE par defaut, et personne ne s'en apercoit ;
//   - la garde ne s'appliquait que si l'en-tete Origin etait present. Or une
//     image ou un formulaire n'en envoient pas. Un simple
//     <img src="http://127.0.0.1:12345/api/v1/es/controller/goto?..."> passait.
//
// On interdit donc par VERBE : tout ce qui modifie est refuse aux navigateurs,
// et il faut ajouter une route a la liste pour l'OUVRIR, non pour la fermer.
//
// Le navigateur se reconnait a Sec-Fetch-Site, que tous envoient depuis 2020 -
// y compris sur les images et les formulaires, la ou Origin manque. Un client
// natif (Marquee, Led, le hub) ne l'envoie pas et passe comme avant.
//
// Un overlay LOCAL qui doit ecrire presente le jeton du fichier local : une
// page distante ne peut ni le lire ni le deviner.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var writes = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    // Le préflight doit passer : c'est lui qui demande l'autorisation, et le
    // refuser ferait échouer des requêtes qu'on aurait acceptées.
    if (!writes || !context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
    if (fetchSite.Length == 0)
    {
        // Pas un navigateur : client natif, script, hub. Les autres gardes
        // (cle API hors loopback) s'appliquent toujours.
        await next();
        return;
    }

    // Une page servie par APIExpose lui-meme reste chez elle.
    if (string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    // Inscription Live Contest : ecriture cross-site LEGITIME depuis la page de
    // participation de la plateforme (elle remet le playToken a cet APIExpose).
    // Le navigateur envoie Sec-Fetch-Site: cross-site et n'a PAS le jeton local -
    // on ne l'exige donc pas ici. La garde d'ORIGINE juste en dessous valide que
    // l'appelant est bien la plateforme officielle (ou le loopback).
    if (context.Request.Path.StartsWithSegments("/api/v1/livecontest"))
    {
        await next();
        return;
    }

    if (LocalWriteToken.Matches(context.Request.Headers[LocalWriteToken.HeaderName].FirstOrDefault()))
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    await context.Response.WriteAsJsonAsync(new
    {
        error = "Écriture refusée depuis un navigateur : jeton local requis.",
        header = LocalWriteToken.HeaderName
    });
});

// Garde d'origine : les commandes locales (lancement de jeux, RetroArch,
// overlay) ne sont JAMAIS pilotables depuis un site web. Une requete
// navigateur cross-origin est refusee sur ces routes ; l'inscription Live
// Contest n'accepte que la plateforme officielle (et le local).
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin))
    {
        var path = context.Request.Path;
        var isLoopback = Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.IsLoopback;
        var isPlatform = o is not null && o.Scheme == Uri.UriSchemeHttps &&
                         (o.Host.Equals("nelfetech.com", StringComparison.OrdinalIgnoreCase) ||
                          o.Host.EndsWith(".nelfetech.com", StringComparison.OrdinalIgnoreCase));
        var blockedForWeb =
            path.StartsWithSegments("/api/v1/commands") ||
            path.StartsWithSegments("/api/v1/overlay");
        var contestEnroll = path.StartsWithSegments("/api/v1/livecontest");
        if ((blockedForWeb && !isLoopback) ||
            (contestEnroll && !isLoopback && !isPlatform))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    await next();
});

// Private Network Access (Chrome) : une page PUBLIQUE en HTTPS (nelfetech.com)
// qui appelle cette API en LOOPBACK (127.0.0.1) doit recevoir, sur le preflight,
// l'en-tete « Access-Control-Allow-Private-Network: true » - que le CORS ASP.NET
// n'ajoute pas. Sans lui, Chrome bloque le POST (ex. enroll Live Contest) alors
// qu'un simple GET passe. On le pose sur tout preflight qui le demande.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method) &&
        context.Request.Headers.TryGetValue("Access-Control-Request-Private-Network", out var pna) &&
        string.Equals(pna, "true", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    await next();
});

app.UseCors();

app.Map("/ws", async context =>
{
    await HandleWebSocketAsync(context, string.Empty);
});

app.Map("/ws/{stream}", async context =>
{
    var stream = context.Request.RouteValues["stream"]?.ToString() ?? string.Empty;
    await HandleWebSocketAsync(context, stream);
});

app.MapControllers();

// Écoute configurable (X-04) : loopback par défaut (borne solo, domicile) ;
// une SALLE multi-bornes passe "Urls": "http://0.0.0.0:12345" dans
// appsettings pour que le hub joigne la borne par le LAN - et active alors
// Security:ApiKey (les requêtes non-loopback exigent X-Api-Key).
app.Run(app.Configuration["Urls"] ?? "http://127.0.0.1:12345");

static async Task HandleWebSocketAsync(HttpContext context, string stream)
{
    var runtimeOptions = context.RequestServices.GetRequiredService<ApiExposeRuntimeOptionsService>();
    if (!runtimeOptions.IsWebSocketEnabled())
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var wsManager = context.RequestServices.GetRequiredService<WebSocketConnectionManager>();
    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    await wsManager.AddSocketAsync(webSocket, stream);

    var buffer = new byte[1024 * 4];
    var receiveResult = await webSocket.ReceiveAsync(
        new ArraySegment<byte>(buffer), CancellationToken.None);

    while (!receiveResult.CloseStatus.HasValue)
    {
        receiveResult = await webSocket.ReceiveAsync(
            new ArraySegment<byte>(buffer), CancellationToken.None);
    }

    await wsManager.RemoveSocketAsync(webSocket);
    await webSocket.CloseAsync(
        receiveResult.CloseStatus.Value,
        receiveResult.CloseStatusDescription,
        CancellationToken.None);
}

public class ProviderHostedService : IHostedService
{
    private readonly IEnumerable<IProvider> _providers;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly StartupReadinessState _startupReadiness;
    private readonly ILogger<ProviderHostedService> _logger;
    private readonly List<IProvider> _startedProviders = new();
    private readonly object _startedProvidersLock = new();
    
    public ProviderHostedService(
        IEnumerable<IProvider> providers,
        ApiExposeRuntimeOptionsService runtimeOptions,
        StartupReadinessState startupReadiness,
        ILogger<ProviderHostedService> logger)
    {
        _providers = providers;
        _runtimeOptions = runtimeOptions;
        _startupReadiness = startupReadiness;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var providers = _providers.Where(IsProviderEnabled).ToList();
        foreach (var provider in providers.OfType<EmulationStationWatcherProvider>())
        {
            await StartProviderAsync(provider, cancellationToken);
        }

        _startupReadiness.MarkReady();

        foreach (var provider in providers.Where(provider => provider is not EmulationStationWatcherProvider))
        {
            _ = Task.Run(() => StartProviderAsync(provider, cancellationToken), CancellationToken.None);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<IProvider> providers;
        lock (_startedProvidersLock)
        {
            providers = _startedProviders.ToList();
        }

        foreach (var provider in providers)
        {
            await provider.StopAsync(cancellationToken);
        }
    }

    private bool IsProviderEnabled(IProvider provider)
    {
        if (provider is RetroArchWrapperProvider && !_runtimeOptions.IsRetroArchWrapperEnabled())
        {
            return false;
        }

        if (provider is RetroArchConsoleHiscoreProvider && !_runtimeOptions.IsConsoleHighScoreCaptureEnabled())
        {
            return false;
        }

        if (provider is MameOutputsProvider && !_runtimeOptions.IsMameOutputsEnabled())
        {
            return false;
        }

        if (provider is MameLuaIngameProvider && !_runtimeOptions.IsMameLuaIngameEnabled())
        {
            return false;
        }

        return true;
    }

    private async Task StartProviderAsync(IProvider provider, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        await provider.StartAsync(cancellationToken);
        lock (_startedProvidersLock)
        {
            if (!_startedProviders.Contains(provider))
            {
                _startedProviders.Add(provider);
            }
        }

        _logger.LogInformation(
            "Provider started: {ProviderType}, elapsedMs={ElapsedMs}",
            provider.GetType().Name,
            (int)(DateTime.UtcNow - startedAt).TotalMilliseconds);
    }

}
