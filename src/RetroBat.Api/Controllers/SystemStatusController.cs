using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Hubs;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Providers.MameOutputs;
using RetroBat.Providers.RetroArchWrapper;

namespace RetroBat.Api.Controllers;

/// <summary>
/// One-call service dashboard: APIExpose, WebSocket stream, EmulationStation
/// and the state of every manager gate. Start here when something looks off.
/// </summary>
[ApiController]
[Tags("System & Health")]
[Route("api/v1/status")]
public sealed class SystemStatusController : ControllerBase
{
    private static readonly HttpClient EsProbeClient = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly WebSocketConnectionManager _webSocketManager;

    public SystemStatusController(
        ApiExposeRuntimeOptionsService runtimeOptions,
        WebSocketConnectionManager webSocketManager)
    {
        _runtimeOptions = runtimeOptions;
        _webSocketManager = webSocketManager;
    }

    /// <summary>
    /// Live status of every service: API version, WebSocket stream and client
    /// counts, EmulationStation reachability (API port 1234 + process), and
    /// each manager's effective ON/OFF gate.
    /// </summary>
    /// <remarks>
    /// A manager reported OFF here explains why its whole feature branch is
    /// inactive even when child switches look ON in the EmulationStation menu.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(SystemStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemStatusResponse>> GetStatus(CancellationToken cancellationToken)
    {
        var (wsClients, wsByStream) = _webSocketManager.GetConnectionSnapshot();
        var esProcessRunning = Process.GetProcessesByName("emulationstation").Length > 0;
        var esApiReachable = await ProbeEmulationStationApiAsync(cancellationToken);

        return Ok(new SystemStatusResponse
        {
            ApiExpose = new ApiExposeStatus
            {
                Version = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                Enabled = _runtimeOptions.IsApiExposeEnabled()
            },
            WebSocket = new WebSocketStatus
            {
                Enabled = _runtimeOptions.IsWebSocketEnabled(),
                ConnectedClients = wsClients,
                ClientsByStream = wsByStream
            },
            EmulationStation = new EmulationStationStatus
            {
                ProcessRunning = esProcessRunning,
                ApiReachable = esApiReachable
            },
            Managers = new ManagersStatus
            {
                LocalMediaManager = _runtimeOptions.IsLocalMediaManagerEnabled(),
                AutoScrapingManager = _runtimeOptions.IsAutoScrapingEnabled(),
                RomsManager = _runtimeOptions.IsRomSetManagerEnabled(),
                MarqueeManager = _runtimeOptions.IsMarqueeManagerEnabled(),
                GameEventsManager = _runtimeOptions.IsRetroArchWrapperEnabled() || _runtimeOptions.IsMameOutputsEnabled(),
                ThemesManager = _runtimeOptions.IsDatasThemeExposeEnabled(),
                CollectionsPackManager = _runtimeOptions.IsCollectionPackManagerEnabled(),
                Swagger = _runtimeOptions.IsSwaggerEnabled(),
                ToastNotifications = _runtimeOptions.IsTaskProgressEnabled()
            }
        });
    }

    private static async Task<bool> ProbeEmulationStationApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await EsProbeClient.GetAsync("http://127.0.0.1:1234/caps", cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Capability negotiation for API consumers: which data sources this install
/// can actually provide, resolved from the live runtime (not hard-coded).
/// </summary>
[ApiController]
[Tags("System & Health")]
[Route("api/v1/capabilities")]
public sealed class CapabilitiesController : ControllerBase
{
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEnumerable<IProvider> _providers;

    public CapabilitiesController(
        ApiExposeRuntimeOptionsService runtimeOptions,
        MediaRuntimeState runtimeState,
        IEnumerable<IProvider> providers)
    {
        _runtimeOptions = runtimeOptions;
        _runtimeState = runtimeState;
        _providers = providers;
    }

    /// <summary>
    /// Effective capabilities of this install: registered event providers,
    /// live /addgames support of the running EmulationStation build, and the
    /// manager gates. Consumers should feature-detect here instead of
    /// hard-coding assumptions.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiCapabilitiesResponse), StatusCodes.Status200OK)]
    public ActionResult<ApiCapabilitiesResponse> GetCapabilities()
    {
        return Ok(new ApiCapabilitiesResponse
        {
            ApiVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            AddGamesSupported = _runtimeState.AddGamesSupported,
            WebSocket = _runtimeOptions.IsWebSocketEnabled(),
            ArcadeOutputs = _providers.OfType<MameOutputsProvider>().Any() && _runtimeOptions.IsMameOutputsEnabled(),
            MemoryEvents = _providers.OfType<RetroArchWrapperProvider>().Any() && _runtimeOptions.IsRetroArchWrapperEnabled(),
            MameLuaIngame = _runtimeOptions.IsMameLuaIngameEnabled(),
            Hiscores = _runtimeOptions.IsConsoleHighScoreCaptureEnabled(),
            DynPanels = Directory.Exists(RetroBatPaths.DynPanelsRoot),
            AutoScraping = _runtimeOptions.IsAutoScrapingEnabled(),
            RomsManager = _runtimeOptions.IsRomSetManagerEnabled(),
            MarqueeManager = _runtimeOptions.IsMarqueeManagerEnabled()
        });
    }
}

/// <summary>Feature-detection payload for API consumers.</summary>
public sealed class ApiCapabilitiesResponse
{
    /// <example>1.3.5+20260719.020000.abcdef12</example>
    public string ApiVersion { get; set; } = string.Empty;
    /// <summary>null = not probed yet this session; false = degraded mode (merge on ES exit).</summary>
    public bool? AddGamesSupported { get; set; }
    /// <example>true</example>
    public bool WebSocket { get; set; }
    /// <summary>MAME network outputs (lamps) are captured and streamed.</summary>
    /// <example>true</example>
    public bool ArcadeOutputs { get; set; }
    /// <summary>RetroArch wrapper memory events (.MEM signals) are captured.</summary>
    /// <example>true</example>
    public bool MemoryEvents { get; set; }
    /// <example>true</example>
    public bool MameLuaIngame { get; set; }
    /// <example>true</example>
    public bool Hiscores { get; set; }
    /// <summary>The DynPanels data pack is installed (control panel definitions).</summary>
    /// <example>true</example>
    public bool DynPanels { get; set; }
    /// <example>true</example>
    public bool AutoScraping { get; set; }
    /// <example>true</example>
    public bool RomsManager { get; set; }
    /// <example>true</example>
    public bool MarqueeManager { get; set; }
}

/// <summary>Aggregated service status.</summary>
public sealed class SystemStatusResponse
{
    public ApiExposeStatus ApiExpose { get; set; } = new();
    public WebSocketStatus WebSocket { get; set; } = new();
    public EmulationStationStatus EmulationStation { get; set; } = new();
    public ManagersStatus Managers { get; set; } = new();
}

public sealed class ApiExposeStatus
{
    /// <example>1.3.3+20260718.204458.6eb45758</example>
    public string Version { get; set; } = string.Empty;
    /// <summary>Master switch (ENABLE API EXPOSE). OFF disables every automation.</summary>
    /// <example>true</example>
    public bool Enabled { get; set; }
}

public sealed class WebSocketStatus
{
    /// <example>true</example>
    public bool Enabled { get; set; }
    /// <example>3</example>
    public int ConnectedClients { get; set; }
    /// <summary>Connected clients per stream ("(global)" = unfiltered /ws).</summary>
    public IReadOnlyDictionary<string, int> ClientsByStream { get; set; } = new Dictionary<string, int>();
}

public sealed class EmulationStationStatus
{
    /// <summary>An emulationstation process is running on this machine.</summary>
    /// <example>true</example>
    public bool ProcessRunning { get; set; }
    /// <summary>The EmulationStation HTTP API answers on http://127.0.0.1:1234.</summary>
    /// <example>true</example>
    public bool ApiReachable { get; set; }
}

/// <summary>Effective ON/OFF gate of each manager (parents win over children).</summary>
public sealed class ManagersStatus
{
    /// <example>true</example>
    public bool LocalMediaManager { get; set; }
    /// <example>true</example>
    public bool AutoScrapingManager { get; set; }
    /// <example>true</example>
    public bool RomsManager { get; set; }
    /// <example>true</example>
    public bool MarqueeManager { get; set; }
    /// <example>true</example>
    public bool GameEventsManager { get; set; }
    /// <example>true</example>
    public bool ThemesManager { get; set; }
    /// <example>true</example>
    public bool CollectionsPackManager { get; set; }
    /// <example>true</example>
    public bool Swagger { get; set; }
    /// <example>true</example>
    public bool ToastNotifications { get; set; }
}
