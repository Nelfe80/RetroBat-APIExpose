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

var testModeRequested = args.Any(arg =>
    string.Equals(arg, "--test-mode", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(arg, "/test-mode", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(arg, "test-mode", StringComparison.OrdinalIgnoreCase));
var hostArgs = args
    .Where(arg =>
        !string.Equals(arg, "--test-mode", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(arg, "/test-mode", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(arg, "test-mode", StringComparison.OrdinalIgnoreCase))
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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }
});

// Core Services
var eventBus = new SimpleEventBus();
builder.Services.AddSingleton<IEventBus>(eventBus);

var wsManager = new WebSocketConnectionManager();
builder.Services.AddSingleton(wsManager);
builder.Services.AddSingleton<MediaRuntimeState>();
builder.Services.AddSingleton<StartupOverlayService>();
builder.Services.AddSingleton<IStartupOverlayService>(sp => sp.GetRequiredService<StartupOverlayService>());
builder.Services.AddSingleton<ToastOverlayService>();
builder.Services.AddSingleton<IToastNotificationService>(sp => sp.GetRequiredService<ToastOverlayService>());
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
builder.Services.AddSingleton<EmulationStationWatcherProvider>();
builder.Services.AddSingleton<IProvider>(sp => sp.GetRequiredService<EmulationStationWatcherProvider>());
builder.Services.AddSingleton<IProvider, Hi2TxtProvider>();
builder.Services.AddSingleton<IProvider, RetroArchWrapperProvider>();
builder.Services.AddSingleton<IProvider, RetroArchConsoleHiscoreProvider>();

var app = builder.Build();

// Setup internal event subscriber to broadcast via WebSockets
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
app.UseSwaggerUI();

app.UseWebSockets();
app.UseRouting();

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

app.Run("http://127.0.0.1:12345");

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
    private readonly List<IProvider> _startedProviders = new();
    
    public ProviderHostedService(
        IEnumerable<IProvider> providers,
        ApiExposeRuntimeOptionsService runtimeOptions,
        StartupReadinessState startupReadiness)
    {
        _providers = providers;
        _runtimeOptions = runtimeOptions;
        _startupReadiness = startupReadiness;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            if (provider is RetroArchWrapperProvider && !_runtimeOptions.IsRetroArchWrapperEnabled())
            {
                continue;
            }

            if (provider is RetroArchConsoleHiscoreProvider && !_runtimeOptions.IsConsoleHighScoreCaptureEnabled())
            {
                continue;
            }

            if (provider is MameOutputsProvider && !_runtimeOptions.IsMameOutputsEnabled())
            {
                continue;
            }

            if (provider is MameLuaIngameProvider && !_runtimeOptions.IsMameLuaIngameEnabled())
            {
                continue;
            }

            await provider.StartAsync(cancellationToken);
            _startedProviders.Add(provider);
        }

        _startupReadiness.MarkReady();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _startedProviders)
        {
            await provider.StopAsync(cancellationToken);
        }
    }

}
