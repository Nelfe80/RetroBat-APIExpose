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

var consoleLoggingEnabled = builder.Configuration.GetValue("ApiExpose:Logging:ConsoleEnabled", true);
if (!consoleLoggingEnabled)
{
    builder.Logging.ClearProviders();
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
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var apiVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "APIExpose — RetroBat Local API",
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
builder.Services.AddSingleton<MediaRuntimeState>();
builder.Services.AddSingleton<StartupOverlayService>();
builder.Services.AddSingleton<LiveContestOverlayService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<LiveContestClientService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveContestClientService>());
builder.Services.AddSingleton<IStartupOverlayService>(sp => sp.GetRequiredService<StartupOverlayService>());
builder.Services.AddSingleton<ToastOverlayService>();
builder.Services.AddSingleton<IToastNotificationService>(sp => sp.GetRequiredService<ToastOverlayService>());
builder.Services.AddSingleton<RetroBat.Api.Infrastructure.CheevosSessionService>();
builder.Services.AddSingleton<CabinetBadgeOverlayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CabinetBadgeOverlayService>());
builder.Services.AddSingleton<ChallengeAnnounceOverlayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChallengeAnnounceOverlayService>());
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
builder.Services.AddSingleton<IGamelistStore, GamelistStore>();
builder.Services.AddSingleton(typeof(IRuntimeConfigSnapshotProvider<>), typeof(RuntimeConfigSnapshotProvider<>));
builder.Services.AddSingleton<EmulationStationSettingsService>();
builder.Services.AddSingleton<EmulationStationSystemConfigService>();
builder.Services.AddSingleton<InterfaceTextService>();
builder.Services.AddSingleton<ApiExposeTaxonomyService>();
builder.Services.AddSingleton<RomMetadataResolver>();
builder.Services.AddSingleton<SystemIdNormalizer>();
builder.Services.AddSingleton<GameNameNormalizer>();
builder.Services.AddSingleton<MediaSystemRules>();
builder.Services.AddSingleton<MediaLocalizationResolver>();
builder.Services.AddSingleton<LocalMediaIndexService>();
builder.Services.AddSingleton<MediaNeedEvaluator>();
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
}
builder.Services.AddHostedService<CpoPanelWebSocketProjectionService>();
builder.Services.AddHostedService<RetroAchievementsRuntimeProjectionService>();
builder.Services.AddHostedService<RetroAchievementsLeaderboardInferenceService>();
builder.Services.AddHostedService<RetroArchLogMonitorService>();
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
builder.Services.AddSingleton<IProvider, MameLuaIngameProvider>();
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
    options.DocumentTitle = "APIExpose — RetroBat Local API";
});

app.UseWebSockets();
app.UseRouting();

// X-04 — cle API de la borne : facultative (vide = LAN de confiance, defaut
// historique). Quand Security:ApiKey est renseignee, toute requete NON
// loopback vers /api ou /ws doit presenter X-Api-Key (le hub la pose sur tous
// ses appels via Hub:CabinetApiKey). Obligatoire avant toute exposition hors
// LAN.
var cabinetApiKey = app.Configuration["Security:ApiKey"] ?? string.Empty;
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
            if (!string.Equals(provided, cabinetApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Cle API requise (en-tete X-Api-Key)." });
                return;
            }
        }

        await next();
    });
}

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
// appsettings pour que le hub joigne la borne par le LAN — et active alors
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
