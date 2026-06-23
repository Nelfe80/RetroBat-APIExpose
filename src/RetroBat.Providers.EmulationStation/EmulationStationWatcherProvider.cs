using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Providers.EmulationStation;

public class EmulationStationWatcherProvider : IProvider
{
    private const int EventsIniReadRetryCount = 4;
    private const int EventsIniReadRetryDelayMs = 5;
    private const int EventsIniPollIntervalMs = 20;
    private const int EventsIniSettleDelayMs = 25;
    private const int EventsIniStableProbeDelayMs = 8;
    private const int GamelistReadRetryCount = 5;
    private const string EsSettingsReallocationProgressTaskId = "es-settings-reallocation";
    private static readonly TimeSpan VisibleMediaReallocationReloadDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan GamelistReadRetryDelay = TimeSpan.FromMilliseconds(120);
    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IHiscoreService _hiscoreService;
    private readonly IHiscoreThemeWriter _hiscoreThemeWriter;
    private readonly IMediaPrefetchService _mediaPrefetchService;
    private readonly IGamelistSelectionSyncService _gamelistSelectionSyncService;
    private readonly MediaRuntimeState _mediaRuntimeState;
    private readonly IStartupOverlayService _startupOverlayService;
    private readonly ITaskProgressService _taskProgressService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly IOptionsMonitor<EmulationStationWatcherOptions> _options;
    private readonly ILogger<EmulationStationWatcherProvider>? _logger;
    private readonly object _cacheLock = new();
    private readonly object _eventDedupLock = new();
    private readonly object _gameSelectedScrapeLock = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _eventsIniPollCts;
    private Task? _eventsIniPollTask;
    private readonly object _eventsIniProcessLock = new();
    private CancellationTokenSource? _eventsIniProcessCts;
    private IDisposable? _settingsSubscription;
    private readonly string _eventsIniPath = RetroBatPaths.EventsIniPath;
    private readonly string _eventsIniDir;
    private readonly HttpClient _httpClient;
    private static readonly TimeSpan EsApiRetryDelay = TimeSpan.FromMilliseconds(250);
    private string? _lastEventSignature;
    private DateTime _lastEventsIniSeenWriteUtc = DateTime.MinValue;
    private long _lastEventsIniSeenLength = -1;
    private long _eventsIniSignalSequence;
    private string _lastMediaAllocationSettingsSignature = string.Empty;
    private string _lastMediaSelectionSignature = string.Empty;
    private long _gameSelectedScrapeSequence;
    private long _frontendEventSequence;
    private CancellationTokenSource? _gameSelectedScrapeCts;
    private EmulationStationScrapingSettings? _lastScrapingSettings;
    private DateTime _lastEventAt = DateTime.MinValue;
    private readonly HashSet<string> _invalidGamelistWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _invalidGamelistWarningsLock = new();
    private readonly Dictionary<string, CachedItem<SystemDetails>> _systemDetailsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedGames> _gamesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedGamelist> _gamelistCache = new(StringComparer.OrdinalIgnoreCase);
    private CachedEsSystems? _esSystemsCache;
    private CachedEsSettings? _esSettingsCache;

    public EmulationStationWatcherProvider(IEventBus eventBus, ApiContext context, IHiscoreService hiscoreService, IHiscoreThemeWriter hiscoreThemeWriter, IMediaPrefetchService mediaPrefetchService, IGamelistSelectionSyncService gamelistSelectionSyncService, MediaRuntimeState mediaRuntimeState, IStartupOverlayService startupOverlayService, ITaskProgressService taskProgressService, EmulationStationSettingsService settingsService, IEsSettingsStore settingsStore, IEsSettingsChangeBus settingsChangeBus, IOptionsMonitor<EmulationStationWatcherOptions> options, ILogger<EmulationStationWatcherProvider>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _hiscoreService = hiscoreService;
        _hiscoreThemeWriter = hiscoreThemeWriter;
        _mediaPrefetchService = mediaPrefetchService;
        _gamelistSelectionSyncService = gamelistSelectionSyncService;
        _mediaRuntimeState = mediaRuntimeState;
        _startupOverlayService = startupOverlayService;
        _taskProgressService = taskProgressService;
        _settingsService = settingsService;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _options = options;
        _logger = logger;
        _eventsIniDir = Path.GetDirectoryName(_eventsIniPath)!;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:1234"),
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_eventsIniDir))
        {
            Directory.CreateDirectory(_eventsIniDir);
        }

        if (!File.Exists(_eventsIniPath))
        {
            File.WriteAllText(_eventsIniPath, "");
        }

        _watcher = new FileSystemWatcher(_eventsIniDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = Path.GetFileName(_eventsIniPath),
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        MarkEventsIniSeen();
        _eventsIniPollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _eventsIniPollTask = Task.Run(() => PollEventsIniAsync(_eventsIniPollCts.Token), CancellationToken.None);

        _settingsSubscription = _settingsChangeBus.Subscribe((_, token) => HandleSettingsChangedAsync(token));
        
        _logger?.LogInformation($"EmulationStationWatcherProvider watching {_eventsIniPath}");
        VerifyPublicWebAccessSetting();
        LogScrapingSettingsSummary();
        _lastScrapingSettings = _settingsService.GetScrapingSettings();
        _lastMediaAllocationSettingsSignature = BuildMediaAllocationSettingsSignature(_settingsService.GetAllSettings());
        _lastMediaSelectionSignature = BuildMediaSelectionSignature(_lastScrapingSettings);
        SynchronizeLegacyScraperMediaSettings(_lastScrapingSettings);
        _ = Task.Run(() => RunStartupGamelistMaintenanceAsync(_eventsIniPollCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunStartupGamelistMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var syncedGameIds = await _gamelistSelectionSyncService.SyncEsGameIdsForAllSystemsAsync(cancellationToken);
            _logger?.LogInformation("ES gameid startup sync completed: updatedEntries={UpdatedEntries}.", syncedGameIds);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Echec de la synchronisation startup des gameid ES dans les gamelists.");
        }

        try
        {
            var updatedEntries = await _gamelistSelectionSyncService.EnsureDefaultPlaceholdersForAllSystemsAsync(cancellationToken);
            _logger?.LogInformation("Default placeholder images ensured for {EntryCount} gamelist entries.", updatedEntries);
            _logger?.LogInformation(
                "Startup gamelist media allocation normalization is handled by StartupGamelistMediaNormalizationHostedService; provider startup skips the duplicate pass.");
            if (updatedEntries > 0)
            {
                _logger?.LogInformation(
                    "Aucun reloadgames APIExpose demande apres initialisation startup de {EntryCount} placeholders par defaut; ES recharge deja ses gamelists au demarrage.",
                    updatedEntries);
                _startupOverlayService.MarkStartupBootstrapCompleted(awaitingFirstReload: false);
            }
            else
            {
                _logger?.LogInformation("Aucun placeholder par defaut supplementaire a initialiser au demarrage.");
                _startupOverlayService.MarkStartupBootstrapCompleted(awaitingFirstReload: false);
            }
        }
        catch (Exception ex)
        {
            _startupOverlayService.MarkStartupBootstrapCompleted(awaitingFirstReload: false);
            _logger?.LogWarning(ex, "Echec de l'initialisation des placeholders par defaut dans les gamelists.");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType != WatcherChangeTypes.Changed) return;

        ScheduleEventsIniProcessing();
    }

    private async Task PollEventsIniAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (HasEventsIniChangedSinceLastSeen())
                {
                    ScheduleEventsIniProcessing();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "events.ini poll skipped during transient file access failure.");
            }

            try
            {
                await Task.Delay(EventsIniPollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private bool HasEventsIniChangedSinceLastSeen()
    {
        var info = new FileInfo(_eventsIniPath);
        if (!info.Exists)
        {
            return false;
        }

        if (info.LastWriteTimeUtc == _lastEventsIniSeenWriteUtc &&
            info.Length == _lastEventsIniSeenLength)
        {
            return false;
        }

        _lastEventsIniSeenWriteUtc = info.LastWriteTimeUtc;
        _lastEventsIniSeenLength = info.Length;
        return true;
    }

    private void MarkEventsIniSeen()
    {
        try
        {
            var info = new FileInfo(_eventsIniPath);
            if (!info.Exists)
            {
                return;
            }

            _lastEventsIniSeenWriteUtc = info.LastWriteTimeUtc;
            _lastEventsIniSeenLength = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Unable to prime events.ini poll state.");
        }
    }

    private void HandleEventsIniChanged()
    {
        ScheduleEventsIniProcessing();
    }

    private void ScheduleEventsIniProcessing()
    {
        var signalSequence = Interlocked.Increment(ref _eventsIniSignalSequence);
        CancellationTokenSource cts;
        lock (_eventsIniProcessLock)
        {
            _eventsIniProcessCts?.Cancel();
            _eventsIniProcessCts?.Dispose();
            _eventsIniProcessCts = new CancellationTokenSource();
            cts = _eventsIniProcessCts;
        }

        _ = Task.Run(() => ProcessEventsIniAfterSettleAsync(signalSequence, cts.Token), CancellationToken.None);
    }

    private async Task ProcessEventsIniAfterSettleAsync(long signalSequence, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(EventsIniSettleDelayMs, cancellationToken);
            if (signalSequence != Volatile.Read(ref _eventsIniSignalSequence))
            {
                return;
            }

            HandleEventsIniChangedNow(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer events.ini signal superseded this one.
        }
    }

    private void HandleEventsIniChangedNow(CancellationToken cancellationToken)
    {
        try
        {
            var lines = ReadEventsIniLines(cancellationToken);
            if (lines.Length == 0) return;

            var evt = lines[0].Trim();
            if (evt.StartsWith("event="))
            {
                var eventName = evt.Substring(6).Trim();
                var sequence = Interlocked.Increment(ref _frontendEventSequence);
                // Process on thread pool to not block watcher
                _ = Task.Run(() => ProcessEventAsync(sequence, eventName, lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()));
            }
        }
        catch (IOException)
        {
            // Ignore temporary file lock
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing events.ini");
        }
    }

    private string[] ReadEventsIniLines(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= EventsIniReadRetryCount; attempt++)
        {
            try
            {
                WaitForStableEventsIni(cancellationToken);
                var before = ReadEventsIniSnapshot();
                var lines = File.ReadAllLines(_eventsIniPath);
                var after = ReadEventsIniSnapshot();
                if (before == after)
                {
                    return lines;
                }
            }
            catch (IOException) when (attempt < EventsIniReadRetryCount)
            {
                Thread.Sleep(EventsIniReadRetryDelayMs);
            }
        }

        return File.ReadAllLines(_eventsIniPath);
    }

    private void WaitForStableEventsIni(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= EventsIniReadRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = ReadEventsIniSnapshot();
            Thread.Sleep(EventsIniStableProbeDelayMs);
            cancellationToken.ThrowIfCancellationRequested();
            var after = ReadEventsIniSnapshot();
            if (before == after)
            {
                return;
            }
        }
    }

    private (DateTime LastWriteUtc, long Length) ReadEventsIniSnapshot()
    {
        var info = new FileInfo(_eventsIniPath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, 0);
    }

    private async Task ProcessEventAsync(long sequence, string eventName, string[] args)
    {
        if (ShouldSkipDuplicateEvent(eventName, args))
        {
            _logger?.LogDebug("ES duplicate event ignored: {EventName}", eventName);
            return;
        }

        _logger?.LogInformation($"ES Event received: {eventName}");
        _mediaRuntimeState.SetLastFrontendEvent(eventName);
        var eventType = "ui.event";
        var eventPublished = false;

        if (eventName == "game-selected")
        {
            var perf = Stopwatch.StartNew();
            var options = _options.CurrentValue;
            var bypassCache = IsFullDetailsMode(options);
            var detailsEnabled = !IsContextOnlyMode(options);
            eventType = "ui.game.selected";
            _context.Ui.State = "browsing";
            
            if (args.Length > 0)
            {
                ParseGameSelected(args[0], out string systemId, out string path, out string name);
                if (ShouldIgnorePostLiveAddGamesFirstGamelistSelection(systemId, path))
                {
                    LogGameSelectedPerfIfNeeded(perf.Elapsed, systemId, path, options, detailsEnabled, bypassCache);
                    return;
                }

                _context.Ui.SelectedSystem = new SystemDetails { Name = systemId };
                _context.Ui.Selected = new GameReference
                {
                    SystemId = systemId,
                    GamePath = path,
                    GameName = name
                };
                await PublishRawFrontendEventAsync(
                    "ui.game.selected.raw",
                    eventName,
                    args,
                    new
                    {
                        SystemId = systemId,
                        GamePath = path,
                        GameName = name
                    },
                    sequence,
                    perf);
                await PublishFrontendEventAsync(eventType, eventName, args);
                eventPublished = true;

                _mediaRuntimeState.RecordGameSelectedSelection(systemId, path);
                _mediaRuntimeState.ClearLiveAddGamesSuppressionOnGameSelected(systemId, path, name);
                var localProjectionOnGameSelected = options.LocalProjectionOnGameSelected || options.PrefetchOnGameSelected;
                var (scrapeSequence, scrapeCancellationToken) = BeginLatestGameSelectedScrape();

                if ((localProjectionOnGameSelected || options.QueueRemoteScrapeOnGameSelected) &&
                    _mediaRuntimeState.ShouldSuppressGameSelectedScrape(systemId, path, out var suppressReason))
                {
                    _logger?.LogInformation(
                        "game-selected ignored during API live refresh suppression window: reason={Reason}, system={SystemId}, path={Path}",
                        suppressReason,
                        systemId,
                        path);
                    LogGameSelectedPerfIfNeeded(perf.Elapsed, systemId, path, options, detailsEnabled, bypassCache);
                    return;
                }
                
                var systemDetails = detailsEnabled ? await FetchSystemDetailsAsync(systemId, bypassCache) : null;
                if (!IsLatestFrontendEvent(sequence))
                {
                    return;
                }
                _context.Ui.SelectedSystem = systemDetails ?? new SystemDetails { Name = systemId };
                
                var gameDetails = detailsEnabled ? await FetchGameDetailsAsync(systemId, path, bypassCache) : null;
                if (!IsLatestFrontendEvent(sequence))
                {
                    return;
                }
                var gameId = gameDetails?.Id ?? gameDetails?.Md5 ?? "";
                
                _context.Ui.Selected = new GameReference 
                { 
                    SystemId = systemId, 
                    GamePath = path, 
                    GameName = name,
                    GameId = gameId,
                    Details = gameDetails
                };

                var selectedGame = _context.Ui.Selected;
                if (localProjectionOnGameSelected || options.QueueRemoteScrapeOnGameSelected)
                {
                    try
                    {
                        if (!await WaitForStableGameSelectedAsync(options, scrapeSequence, systemId, path, scrapeCancellationToken))
                        {
                            _logger?.LogDebug(
                                "game-selected scrape skipped after debounce because selection changed: system={SystemId}, path={Path}",
                                systemId,
                                path);
                            LogGameSelectedPerfIfNeeded(perf.Elapsed, systemId, path, options, detailsEnabled, bypassCache);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (!IsLatestGameSelectedScrape(scrapeSequence))
                    {
                        _logger?.LogDebug(
                            "game-selected scrape skipped during debounce because selection changed: system={SystemId}, path={Path}",
                            systemId,
                            path);
                        LogGameSelectedPerfIfNeeded(perf.Elapsed, systemId, path, options, detailsEnabled, bypassCache);
                        return;
                    }

                    if (!IsLatestGameSelectedScrape(scrapeSequence))
                    {
                        _logger?.LogDebug(
                            "game-selected scrape skipped after debounce because selection changed: system={SystemId}, path={Path}",
                            systemId,
                            path);
                        LogGameSelectedPerfIfNeeded(perf.Elapsed, systemId, path, options, detailsEnabled, bypassCache);
                        return;
                    }
                }

                if (localProjectionOnGameSelected)
                {
                    if (!IsLatestGameSelectedScrape(scrapeSequence))
                    {
                        _logger?.LogDebug("Stale game-selected local projection skipped before disk work for system={SystemId}, path={Path}", systemId, path);
                        LogGameSelectedPerfIfNeeded(perf.Elapsed, systemId, path, options, detailsEnabled, bypassCache);
                        return;
                    }

                    MediaPrefetchResult prefetchResult;
                    try
                    {
                        prefetchResult = await _mediaPrefetchService.PrefetchForSelectionAsync(
                            selectedGame,
                            allowRemoteScrape: options.QueueRemoteScrapeOnGameSelected,
                            forceRemoteScrape: false,
                            createUserVariantGuide: options.CreateUserVariantGuidesOnGameSelected,
                            suppressImmediateGamelistUpdates: true,
                            scrapeCancellationToken);
                    }
                    catch (OperationCanceledException) when (!IsLatestGameSelectedScrape(scrapeSequence))
                    {
                        _logger?.LogDebug("Stale game-selected prefetch cancelled for system={SystemId}, path={Path}", systemId, path);
                        await DemoteStaleSelectionToBackgroundQueueAsync(selectedGame, "cancelled-prefetch");
                        return;
                    }

                    if (!IsLatestGameSelectedScrape(scrapeSequence))
                    {
                        _logger?.LogDebug("Stale game-selected prefetch result ignored for system={SystemId}, path={Path}", systemId, path);
                        await DemoteStaleSelectionToBackgroundQueueAsync(selectedGame, "stale-prefetch-result");
                        return;
                    }

                    _logger?.LogInformation(
                        "Local media projection prepared for game-selected: system={SystemId}, game={GameSlug}, queuedRemote={QueuedRemote}, missing={MissingCount}, arcadeLike={ArcadeLike}, folderSystem={FolderSystem}, skipCrc={SkipCrc}, biosFiltered={BiosFiltered}",
                        prefetchResult.SystemId,
                        prefetchResult.GameSlug,
                        prefetchResult.QueuedRemoteScrape,
                        prefetchResult.Needs.Count(n => n.IsMissing),
                        prefetchResult.IsArcadeLike,
                        prefetchResult.IsFolderBasedSystem,
                        prefetchResult.SkipCrcComputation,
                        prefetchResult.IsFilteredArcadeBiosCandidate);
                }
                else
                {
                    _logger?.LogDebug("Media prefetch skipped on game-selected; live priority scrape may still refresh the ES game card.");
                    if (options.QueueRemoteScrapeOnGameSelected)
                    {
                        MediaPrefetchResult remoteQueueResult;
                        try
                        {
                            remoteQueueResult = await _mediaPrefetchService.PrefetchForSelectionAsync(
                                selectedGame,
                                allowRemoteScrape: true,
                                forceRemoteScrape: false,
                                createUserVariantGuide: options.CreateUserVariantGuidesOnGameSelected,
                                suppressImmediateGamelistUpdates: true,
                                scrapeCancellationToken);
                        }
                        catch (OperationCanceledException) when (!IsLatestGameSelectedScrape(scrapeSequence))
                        {
                            _logger?.LogDebug("Stale live priority scrape cancelled for system={SystemId}, path={Path}", systemId, path);
                            await DemoteStaleSelectionToBackgroundQueueAsync(selectedGame, "cancelled-live-priority");
                            return;
                        }

                        if (!IsLatestGameSelectedScrape(scrapeSequence))
                        {
                            _logger?.LogDebug("Stale live priority scrape result ignored for system={SystemId}, path={Path}", systemId, path);
                            await DemoteStaleSelectionToBackgroundQueueAsync(selectedGame, "stale-live-priority-result");
                            return;
                        }

                        _logger?.LogInformation(
                            "Live priority scrape checked for game-selected: system={SystemId}, game={GameSlug}, queuedRemote={QueuedRemote}, missing={MissingCount}, gamePathExists={GamePathExists}, gamelistMd5={GamelistMd5}",
                            remoteQueueResult.SystemId,
                            remoteQueueResult.GameSlug,
                            remoteQueueResult.QueuedRemoteScrape,
                            remoteQueueResult.Needs.Count(n => n.IsMissing),
                            remoteQueueResult.GamePathExists,
                            string.IsNullOrWhiteSpace(remoteQueueResult.GamelistMd5) ? "<none>" : remoteQueueResult.GamelistMd5);
                        await RefreshSelectedGameDetailsAfterLiveScrapeAsync(systemId, path);
                    }
                }

                CompleteLatestGameSelectedScrape(scrapeSequence);
                LogGameSelectedPerfIfNeeded(perf.Elapsed, systemId, path, options, detailsEnabled, bypassCache);
            }
        }
        else if (eventName == "system-selected")
        {
            eventType = "ui.system.selected";
            _context.Ui.State = "browsing";
            if (_mediaRuntimeState.HasMediaChangesPending())
            {
                _mediaRuntimeState.RequestReloadGames(TimeSpan.FromSeconds(2), requestedByScrape: true);
            }
            if (args.Length > 0)
            {
                var sysId = args[0].Trim();
                _mediaRuntimeState.ClearPostLiveAddGamesFirstGamelistSelectionGuardForSystemChange(sysId);
                _context.Ui.SelectedSystem = new SystemDetails { Name = sysId };
                _context.Ui.Selected = null;
                await PublishRawFrontendEventAsync(
                    "ui.system.selected.raw",
                    eventName,
                    args,
                    new
                    {
                        SystemId = sysId
                    },
                    sequence);
                await PublishFrontendEventAsync(eventType, eventName, args);
                eventPublished = true;
                var systemDetails = await FetchSystemDetailsAsync(sysId);
                if (!IsLatestFrontendEvent(sequence))
                {
                    return;
                }
                _context.Ui.SelectedSystem = systemDetails ?? new SystemDetails { Name = sysId };
                _context.Ui.Selected = null;
            }
            else
            {
                _mediaRuntimeState.ClearPostLiveAddGamesFirstGamelistSelectionGuard();
            }
        }
        else if (eventName == "game-start")
        {
            eventType = "ui.game.started";
            _context.Ui.State = "playing";
            
            if (args.Length > 0)
            {
                ParseGameStart(args[0], out string path, out string longName, out string shortName);
                
                var sysId = _context.Ui.SelectedSystem?.Name ?? "unknown";
                _context.Ui.Running = new GameReference
                {
                    SystemId = sysId,
                    GamePath = path,
                    GameName = shortName
                };
                await PublishRawFrontendEventAsync(
                    "ui.game.started.raw",
                    eventName,
                    args,
                    new
                    {
                        SystemId = sysId,
                        GamePath = path,
                        GameName = shortName,
                        LongName = longName
                    },
                    sequence);
                var launch = await TryReadLaunchDetailsAsync(path);
                if (string.Equals(sysId, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    sysId = InferSystemIdFromLaunchOrPath(launch, path);
                }

                var gameDetails = await FetchGameDetailsAsync(sysId, path);
                var gameId = gameDetails?.Id ?? gameDetails?.Md5 ?? "";

                _context.Ui.Running = new GameReference 
                { 
                    SystemId = sysId, 
                    GamePath = path, 
                    GameName = shortName,
                    GameId = gameId,
                    Details = gameDetails
                };

                if (launch != null)
                {
                    _context.Ui.Running.Launch = launch;
                    _logger?.LogInformation(
                        "Launch command captured from emulatorLauncher.log for {RomPath}",
                        launch.RomPath);
                }
            }
        }
        else if (eventName == "game-end")
        {
            var finishedGame = _context.Ui.Running ?? _context.Ui.Selected;
            eventType = "ui.game.ended";
            await PublishRawFrontendEventAsync(
                "ui.game.ended.raw",
                eventName,
                args,
                new
                {
                    SystemId = finishedGame?.SystemId ?? _context.Ui.SelectedSystem?.Name ?? string.Empty,
                    GamePath = finishedGame?.GamePath ?? string.Empty,
                    GameName = finishedGame?.GameName ?? string.Empty
                },
                sequence);
            _context.Ui.State = "browsing";
            _context.Ui.Running = null;

            if (finishedGame != null)
            {
                try
                {
                    if (IsArcadeLikeHiscoreSystem(finishedGame))
                    {
                        var hiscoreResult = await _hiscoreService.ExtractAsync(finishedGame);
                        _logger?.LogInformation(
                            "Hiscore extraction on game-end for {RomName}: status={Status}, scores={ScoreCount}, source={SourceFile}",
                            hiscoreResult.RomName,
                            hiscoreResult.Status,
                            hiscoreResult.Scores.Count,
                            string.IsNullOrWhiteSpace(hiscoreResult.SourceFile) ? "<none>" : hiscoreResult.SourceFile);

                        await _hiscoreThemeWriter.WriteAsync(finishedGame, hiscoreResult);
                        _logger?.LogInformation(
                            "Hiscore theme XML written for system={SystemId}, rom={RomName}",
                            finishedGame.SystemId,
                            hiscoreResult.RomName);

                        await _eventBus.PublishAsync(new EventEnvelope
                        {
                            Type = "hiscore.updated",
                            Payload = hiscoreResult
                        });
                    }
                    else
                    {
                        _logger?.LogDebug(
                            "Hi2Txt arcade hiscore skipped on console game-end for system={SystemId}, game={GameName}",
                            finishedGame.SystemId,
                            finishedGame.GameName);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error extracting hiscore on game-end");
                }
            }
        }
        
        var envelope = new EventEnvelope
        {
            Type = eventType,
            Payload = new { EventName = eventName, RawArgs = args, Context = _context.Ui }
        };
        
        if (!eventPublished)
        {
            await _eventBus.PublishAsync(envelope);
        }
    }

    private async Task<bool> WaitForStableGameSelectedAsync(
        EmulationStationWatcherOptions options,
        long scrapeSequence,
        string systemId,
        string path,
        CancellationToken cancellationToken)
    {
        var debounceMs = Math.Clamp(options.GameSelectedLocalProjectionDebounceMs, 0, 10000);
        if (debounceMs <= 0)
        {
            return true;
        }

        await Task.Delay(debounceMs, cancellationToken);
        if (!IsLatestGameSelectedScrape(scrapeSequence))
        {
            return false;
        }

        var selected = _context.Ui.Selected;
        return selected != null &&
            string.Equals(selected.SystemId, systemId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizePath(selected.GamePath), NormalizePath(path), StringComparison.OrdinalIgnoreCase);
    }

    private async Task PublishFrontendEventAsync(string eventType, string eventName, string[] args)
    {
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = eventType,
            Payload = new { EventName = eventName, RawArgs = args, Context = _context.Ui }
        });
    }

    private bool IsLatestFrontendEvent(long sequence)
        => sequence == Volatile.Read(ref _frontendEventSequence);

    private async Task PublishRawFrontendEventAsync(
        string eventType,
        string eventName,
        string[] args,
        object selection,
        long sequence,
        Stopwatch? stopwatch = null)
    {
        var elapsedMs = stopwatch == null ? 0 : (int)stopwatch.Elapsed.TotalMilliseconds;
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = eventType,
            Payload = new
            {
                EventName = eventName,
                Sequence = sequence,
                RawArgs = args,
                Selection = selection,
                ReceivedAtUtc = DateTime.UtcNow,
                Latency = new
                {
                    Source = "events.ini",
                    ElapsedMs = elapsedMs
                },
                Context = _context.Ui
            }
        });

        _logger?.LogInformation(
            "frontend raw event published: type={EventType}, event={EventName}, sequence={Sequence}, elapsedMs={ElapsedMs}",
            eventType,
            eventName,
            sequence,
            elapsedMs);
    }

    private (long Sequence, CancellationToken CancellationToken) BeginLatestGameSelectedScrape()
    {
        lock (_gameSelectedScrapeLock)
        {
            _gameSelectedScrapeCts?.Cancel();
            _gameSelectedScrapeCts?.Dispose();
            _gameSelectedScrapeCts = new CancellationTokenSource();
            _gameSelectedScrapeSequence++;
            return (_gameSelectedScrapeSequence, _gameSelectedScrapeCts.Token);
        }
    }

    private bool IsLatestGameSelectedScrape(long sequence)
    {
        lock (_gameSelectedScrapeLock)
        {
            return sequence == _gameSelectedScrapeSequence;
        }
    }

    private void CompleteLatestGameSelectedScrape(long sequence)
    {
        lock (_gameSelectedScrapeLock)
        {
            if (sequence == _gameSelectedScrapeSequence)
            {
                _gameSelectedScrapeCts?.Cancel();
            }
        }
    }

    private async Task DemoteStaleSelectionToBackgroundQueueAsync(GameReference game, string reason)
    {
        await Task.CompletedTask;
        _logger?.LogDebug(
            "Stale game-selected selection ignored without background demotion because remote scraping is archived: reason={Reason}, system={SystemId}, path={Path}",
            reason,
            game.SystemId,
            game.GamePath);
    }

    private static bool IsArcadeLikeHiscoreSystem(GameReference game)
    {
        var systemId = game.SystemId ?? string.Empty;
        if (systemId.Equals("mame", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("fbneo", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("fba", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("hbmame", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("arcade", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var core = (game.Launch?.Core ?? game.Launch?.Emulator ?? string.Empty)
            .Replace("_libretro.dll", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_libretro", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        return core.StartsWith("mame", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleSettingsChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _settingsStore.WaitForStableFileAsync(cancellationToken);
            ClearEsSettingsDependentCaches("es_settings.cfg changed");
            _settingsService.Invalidate();
            var rawSettings = _settingsService.GetAllSettings();
            var settings = _settingsService.GetScrapingSettings();
            var signature = BuildMediaAllocationSettingsSignature(rawSettings);
            var mediaSelectionSignature = BuildMediaSelectionSignature(settings);
            SynchronizeLegacyScraperMediaSettings(settings);
            if (string.Equals(signature, _lastMediaAllocationSettingsSignature, StringComparison.Ordinal))
            {
                return;
            }

            var previousSignature = _lastMediaAllocationSettingsSignature;
            var previousMediaSelectionSignature = _lastMediaSelectionSignature;
            var previousSettings = _lastScrapingSettings;
            _lastMediaAllocationSettingsSignature = signature;
            _lastMediaSelectionSignature = mediaSelectionSignature;
            _lastScrapingSettings = settings;

            _logger?.LogInformation(
                "APIExpose media allocation settings changed: previous={PreviousSignature}, current={CurrentSignature}",
                previousSignature,
                signature);

            VerifyPublicWebAccessSetting();
            LogScrapingSettingsSummary();

            if (string.Equals(previousMediaSelectionSignature, mediaSelectionSignature, StringComparison.Ordinal))
            {
                _logger?.LogInformation("Aucune normalisation gamelist: les sources image/logo/thumbnail/wheel-style n'ont pas change.");
                return;
            }

            _taskProgressService.Report(
                EsSettingsReallocationProgressTaskId,
                "Normalisation medias ES",
                0,
                2,
                "settings ES");
            _mediaRuntimeState.RequestVisibleMediaReallocationWorkflow(
                VisibleMediaReallocationReloadDelay,
                new VisibleMediaReallocationRequest("all", string.Empty, "es-settings"));
            _taskProgressService.Report(
                EsSettingsReallocationProgressTaskId,
                "Normalisation medias ES",
                1,
                2,
                "workflow planifie");

            _taskProgressService.Report(
                EsSettingsReallocationProgressTaskId,
                "Normalisation medias ES",
                2,
                2,
                "refresh ES differe");
            _taskProgressService.Complete(EsSettingsReallocationProgressTaskId);
            _logger?.LogInformation("Workflow reallocation media planifie apres changement des settings ES.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer es_settings.cfg write or service stopping.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Echec du traitement de changement de es_settings.cfg.");
        }
    }

    private void SynchronizeLegacyScraperMediaSettings(EmulationStationScrapingSettings settings)
    {
        try
        {
            if (_settingsStore.Update(document =>
                {
                    var root = document.Root ?? throw new InvalidOperationException("es_settings.cfg root is missing.");
                    var changed = false;
                    changed |= SetEsStringSetting(root, "ScrapperImageSrc", settings.ImageSource);
                    changed |= SetEsStringSetting(root, "ScrapperLogoSrc", settings.LogoSource);
                    changed |= SetEsStringSetting(root, "ScrapperThumbSrc", settings.ThumbSource);
                    changed |= SetEsStringSetting(root, "WheelStyle", settings.WheelStyle);
                    return changed;
                }))
            {
                ClearEsSettingsDependentCaches("legacy scraper media settings synchronized");
                _settingsService.Invalidate();
                _logger?.LogInformation(
                    "Legacy ES scraper media settings synchronized from APIExpose allocation: image={ImageSource}, logo={LogoSource}, thumb={ThumbSource}, wheelStyle={WheelStyle}",
                    settings.ImageSource,
                    settings.LogoSource,
                    settings.ThumbSource,
                    settings.WheelStyle);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Impossible de synchroniser les options scraper ES depuis les preferences media APIExpose.");
        }
    }

    private static bool SetEsStringSetting(XElement root, string key, string value)
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

    private bool ShouldSkipDuplicateEvent(string eventName, string[] args)
    {
        var signature = eventName + "|" + string.Join("|", args);

        lock (_eventDedupLock)
        {
            if (string.Equals(_lastEventSignature, signature, StringComparison.Ordinal))
            {
                return true;
            }

            _lastEventSignature = signature;
            _lastEventAt = DateTime.UtcNow;
            return false;
        }
    }

    private void VerifyPublicWebAccessSetting()
    {
        var settings = _settingsService.GetScrapingSettings();
        if (!settings.PublicWebAccessEnabled)
        {
            _logger?.LogWarning("PublicWebAccess n'est pas a true dans es_settings.cfg. L'API ES locale peut ne pas etre exploitable correctement.");
            return;
        }

        _logger?.LogInformation("PublicWebAccess=true confirme dans es_settings.cfg.");
    }

    private void LogScrapingSettingsSummary()
    {
        var settings = _settingsService.GetScrapingSettings();
        _logger?.LogInformation(
            "ES/APIExpose media settings: language={Language}, imageSource={ImageSource}, logoSource={LogoSource}, thumbSource={ThumbSource}, wheelStyle={WheelStyle}, manual={ScrapeManual}, videos={ScrapeVideos}, fanart={ScrapeFanart}, bezel={ScrapeBezel}, boxBack={ScrapeBoxBack}, map={ScrapeMap}, showManualIcon={ShowManualIcon}",
            settings.Language,
            settings.ImageSource,
            settings.LogoSource,
            settings.ThumbSource,
            settings.WheelStyle,
            settings.ScrapeManual,
            settings.ScrapeVideos,
            settings.ScrapeFanart,
            settings.ScrapeBezel,
            settings.ScrapeBoxBack,
            settings.ScrapeMap,
            settings.ShowManualIcon);
    }

    private async Task TryReloadGamesIfNeededAsync()
    {
        if (!_mediaRuntimeState.TryConsumeReloadGamesReady(TimeSpan.FromSeconds(5), out _))
        {
            return;
        }

        try
        {
            var response = await _httpClient.GetAsync("/reloadgames");
            if (response.IsSuccessStatusCode)
            {
                ClearEsRuntimeCaches("reloadgames completed");
                _logger?.LogInformation("reloadgames appele avec succes apres des scraps recents.");
            }
            else
            {
                _logger?.LogWarning("reloadgames a retourne HTTP {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Echec de l'appel /reloadgames apres scraps.");
        }
    }

    private static string BuildMediaAllocationSettingsSignature(IReadOnlyDictionary<string, string> settings)
    {
        return string.Join("|",
            FirstRawSetting(settings, "global.apiexpose.media_allocation.image_source", "ScrapperImageSrc", "ScraperImageSrc", "ImageSource"),
            FirstRawSetting(settings, "global.apiexpose.media_allocation.logo_source", "ScrapperLogoSrc", "ScraperLogoSrc", "LogoSource"),
            FirstRawSetting(settings, "global.apiexpose.media_allocation.thumb_source", "ScrapperThumbSrc", "ScraperThumbSrc", "ThumbSource"),
            FirstRawSetting(settings, "global.apiexpose.media_allocation.wheel_style", "global.apiexpose.scraping.wheel_style"),
            FirstRawSetting(settings, "global.apiexpose.media_allocation.region_mode"),
            FirstRawSetting(settings, "global.apiexpose.media_allocation.logo_region_mode"),
            FirstRawSetting(settings, "global.apiexpose.media_allocation.user_region"));
    }

    private static string BuildMediaSelectionSignature(EmulationStationScrapingSettings settings)
    {
        return string.Join("|",
            settings.ImageSource.Trim().ToLowerInvariant(),
            settings.LogoSource.Trim().ToLowerInvariant(),
            settings.ThumbSource.Trim().ToLowerInvariant(),
            settings.WheelStyle.Trim().ToLowerInvariant(),
            settings.MediaRegionMode.Trim().ToLowerInvariant(),
            settings.LogoRegionMode.Trim().ToLowerInvariant(),
            settings.UserRegion.Trim().ToLowerInvariant());
    }

    private static string FirstRawSetting(IReadOnlyDictionary<string, string> settings, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = TryGetRawSetting(settings, key);
            if (!string.Equals(value, "<absent>", StringComparison.Ordinal))
            {
                return value;
            }
        }

        return "<absent>";
    }

    private static string TryGetRawSetting(IReadOnlyDictionary<string, string> settings, string key)
    {
        return settings.TryGetValue(key, out var value) ? value ?? string.Empty : "<absent>";
    }
    
    // Enrichissement depuis l'API locale ES (127.0.0.1:1234)
    private async Task<SystemDetails?> FetchSystemDetailsAsync(string systemId, bool bypassCache = false)
    {
        var cacheKey = NormalizeCacheKey(systemId);
        if (ShouldUseCache(bypassCache) && TryGetFreshCacheItem(_systemDetailsCache, cacheKey, out var cachedSystem))
        {
            return CloneSystemDetails(cachedSystem);
        }

        try
        {
            var url = $"/systems/{systemId}";
            _logger?.LogDebug("[FetchSystemDetailsAsync] API Request to {Url}", url);
            var response = await GetEsApiWithRetryAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var details = JsonSerializer.Deserialize<SystemDetails>(content, options);
                
                if (details != null)
                {
                    ConsolidateWithEsConfigs(details);
                    SetCacheItem(_systemDetailsCache, cacheKey, CloneSystemDetails(details));
                    return details;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to fetch system details for {systemId}: {ex.Message}");
        }
        return null;
    }

    private async Task<GameDetails?> FetchGameDetailsAsync(string systemId, string path, bool bypassCache = false)
    {
        try
        {
            var cachedGames = await FetchGamesForSystemAsync(systemId, bypassCache);
            if (cachedGames != null)
            {
                var normalizedPath = NormalizePath(path);
                var cachedMatch = cachedGames.Games.FirstOrDefault(g => NormalizePath(g.Path) == normalizedPath);
                if (cachedMatch != null)
                {
                    var cloned = CloneGameDetails(cachedMatch);
                    if (string.IsNullOrEmpty(cloned.Md5) && !string.IsNullOrEmpty(cloned.Id))
                        cloned.Md5 = cloned.Id;

                    ConsolidateWithGamelist(cloned, systemId);
                    return cloned;
                }
            }

            var url = $"/systems/{systemId}/games";
            _logger?.LogDebug("[FetchGameDetailsAsync] API Request to {Url}", url);
            var response = await GetEsApiWithRetryAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var games = JsonSerializer.Deserialize<List<EsGameApiData>>(content, options);
                
                if (games != null)
                {
                    // Normalize slashes for comparison
                    var normalizedPath = path.Replace("\\", "/").ToLowerInvariant();
                    // Match path
                    var match = games.FirstOrDefault(g => (g.Path ?? "").Replace("\\", "/").ToLowerInvariant() == normalizedPath);
                    if (match != null)
                    {
                        // Some endpoints map the API "id" differently than Md5, let's make sure Md5 is exposed
                        if (string.IsNullOrEmpty(match.Md5) && !string.IsNullOrEmpty(match.Id))
                            match.Md5 = match.Id;
                            
                        // Enrichir avec infos du gamelist.xml local pour compléter d'éventuels champs (scrap name, ScrapDate, emulator, etc.)
                        ConsolidateWithGamelist(match, systemId);

                        return match;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to fetch game details for {path}: {ex.Message}");
        }
        return null;
    }

    private async Task RefreshSelectedGameDetailsAfterLiveScrapeAsync(string systemId, string path)
    {
        var selected = _context.Ui.Selected;
        if (selected == null ||
            !string.Equals(selected.SystemId, systemId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizePath(selected.GamePath), NormalizePath(path), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var refreshed = await FetchGameDetailsAsync(systemId, path, bypassCache: true);
        if (refreshed == null)
        {
            return;
        }

        selected.Details = refreshed;
        selected.GameId = refreshed.Id;
        if (string.IsNullOrWhiteSpace(selected.GameId))
        {
            selected.GameId = refreshed.Md5;
        }
    }

    private async Task<CachedGames?> FetchGamesForSystemAsync(string systemId, bool bypassCache)
    {
        var cacheKey = NormalizeCacheKey(systemId);
        if (ShouldUseCache(bypassCache))
        {
            lock (_cacheLock)
            {
                if (_gamesCache.TryGetValue(cacheKey, out var cached) && IsFresh(cached.CachedAtUtc))
                {
                    return cached;
                }
            }
        }

        var url = $"/systems/{systemId}/games";
        _logger?.LogDebug("[FetchGameDetailsAsync] API Request to {Url}", url);
        var response = await GetEsApiWithRetryAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var games = JsonSerializer.Deserialize<List<EsGameApiData>>(content, jsonOptions);
        if (games == null)
        {
            return null;
        }

        var cachedGames = new CachedGames(DateTime.UtcNow, games);
        lock (_cacheLock)
        {
            _gamesCache[cacheKey] = cachedGames;
        }

        return cachedGames;
    }

    private async Task<HttpResponseMessage> GetEsApiWithRetryAsync(string url)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await _httpClient.GetAsync(url);
            }
            catch (Exception ex) when (attempt < 3)
            {
                lastException = ex;
                _logger?.LogDebug(ex, "Retry ES API {Attempt}/3 pour {Url}.", attempt, url);
                await Task.Delay(EsApiRetryDelay);
            }
        }

        throw lastException ?? new HttpRequestException($"ES API request failed for {url}.");
    }

    // Helper classes matching the API structure for internal deserialization from ES API
    private class EsGameApiData : GameDetails 
    {
        public string Path { get; set; } = string.Empty;
    }

    private void ConsolidateWithGamelist(EsGameApiData details, string systemId)
    {
        var gamelistPath = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
        _logger?.LogDebug("[ConsolidateWithGamelist] Reading data from {GamelistPath}", gamelistPath);
        if (!File.Exists(gamelistPath))
            return;

        try
        {
            var targetMd5 = details.Md5;
            var targetPath = Path.GetFileName(details.Path); 
            var xDoc = LoadCachedGamelistDocument(systemId, gamelistPath);
            if (xDoc.Root == null)
            {
                return;
            }

            var gameNodes = xDoc.Descendants("game").ToList();
            var targetGame = gameNodes.FirstOrDefault(g => 
                (g.Element("md5")?.Value == targetMd5 && !string.IsNullOrEmpty(targetMd5)) || 
                (g.Element("path")?.Value ?? "").EndsWith(targetPath)
            );

            if (targetGame != null)
            {
                // Consolidate Fields only if not already populated or if we want to overwrite 
                // E.g. we fetch all possible fields into 'details'
                
                // Extract possible custom tags
                var scrapElem = targetGame.Element("scrap");
                if (scrapElem != null)
                {
                    details.ScrapName = scrapElem.Attribute("name")?.Value ?? details.ScrapName;
                    details.ScrapDate = scrapElem.Attribute("date")?.Value ?? details.ScrapDate;
                }

                if (string.IsNullOrEmpty(details.SystemName)) details.SystemName = systemId;
                if (string.IsNullOrEmpty(details.Desc)) details.Desc = targetGame.Element("desc")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Emulator)) details.Emulator = targetGame.Element("emulator")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Family)) details.Family = targetGame.Element("family")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Arcadesystemname)) details.Arcadesystemname = targetGame.Element("arcadesystemname")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Players)) details.Players = targetGame.Element("players")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Favorite)) details.Favorite = targetGame.Element("favorite")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Hidden)) details.Hidden = targetGame.Element("hidden")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Kidgame)) details.Kidgame = targetGame.Element("kidgame")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Playcount)) details.Playcount = targetGame.Element("playcount")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Lastplayed)) details.Lastplayed = targetGame.Element("lastplayed")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Gametime)) details.Gametime = targetGame.Element("gametime")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Lang)) details.Lang = targetGame.Element("lang")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Region)) details.Region = targetGame.Element("region")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Releasedate)) details.Releasedate = targetGame.Element("releasedate")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Genres)) details.Genres = targetGame.Element("genres")?.Value ?? targetGame.Element("genreId")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Manual)) details.Manual = targetGame.Element("manual")?.Value ?? "";
                
                // Mettre à jour / enrichir les médias de gamelist si jamais ceux de l'api étaient vides
                if (string.IsNullOrEmpty(details.Boxback)) details.Boxback = targetGame.Element("boxback")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Bezel)) details.Bezel = targetGame.Element("bezel")?.Value ?? "";
                if (string.IsNullOrEmpty(details.Fanart)) details.Fanart = targetGame.Element("fanart")?.Value ?? "";
                
                // Consolidation générique pour tous les autres tags potentiels rajoutés manuellement ou par des thèmes/scrappeurs (non exhaustifs)
                if (details.Extras == null) details.Extras = new Dictionary<string, string>();
                
                var knownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "id", "path", "name", "desc", "image", "video", "marquee", "thumbnail",
                    "developer", "publisher", "genre", "rating", "md5", "emulator", "fanart", 
                    "bezel", "boxback", "manual", "releasedate", "family", "genres", "genreid", 
                    "arcadesystemname", "players", "favorite", "hidden", "kidgame", "playcount", 
                    "lastplayed", "gametime", "lang", "region", "scrap", "core"
                };

                foreach (var elem in targetGame.Elements())
                {
                    var name = elem.Name.LocalName;
                    var val = elem.Value;
                    
                    if (!string.IsNullOrEmpty(name) && !knownTags.Contains(name))
                    {
                        // On stocke la valeur principale du noeud
                        if (!string.IsNullOrEmpty(val))
                        {
                            // On la met dans le dico Extras
                            details.Extras[name] = val;
                        }

                        // Et si le noeud a des attributs exotiques
                        if (elem.HasAttributes)
                        {
                            foreach (var attr in elem.Attributes())
                            {
                                details.Extras[$"{name}_{attr.Name.LocalName}"] = attr.Value;
                            }
                        }
                    }
                }
            }
        }
        catch (XmlException ex) when (!IsMissingRootXml(ex))
        {
            LogInvalidGamelistOnce(systemId, gamelistPath, ex);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to consolidate gamelist.xml for {systemId}: {ex.Message}");
        }
    }

    private XDocument LoadGamelistDocumentOrEmpty(string gamelistPath)
    {
        for (var attempt = 1; attempt <= GamelistReadRetryCount; attempt++)
        {
            try
            {
                using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException ex) when (IsMissingRootXml(ex))
            {
                _logger?.LogInformation("[ConsolidateWithGamelist] gamelist vide detecte pour {GamelistPath}, utilisation d'une structure vide.", gamelistPath);
                return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XElement("gameList"));
            }
            catch (IOException) when (attempt < GamelistReadRetryCount)
            {
                Thread.Sleep(GamelistReadRetryDelay);
            }
        }

        using var finalStream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return XDocument.Load(finalStream, LoadOptions.PreserveWhitespace);
    }

    private XDocument LoadCachedGamelistDocument(string systemId, string gamelistPath)
    {
        if (!_options.CurrentValue.EsCacheEnabled)
        {
            return LoadGamelistDocumentOrEmpty(gamelistPath);
        }

        var fileInfo = new FileInfo(gamelistPath);
        var cacheKey = NormalizeCacheKey(systemId);
        lock (_cacheLock)
        {
            if (_gamelistCache.TryGetValue(cacheKey, out var cached) &&
                string.Equals(cached.Path, gamelistPath, StringComparison.OrdinalIgnoreCase) &&
                cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc &&
                cached.Length == fileInfo.Length)
            {
                return cached.Document;
            }
        }

        var document = LoadGamelistDocumentOrEmpty(gamelistPath);
        lock (_cacheLock)
        {
            _gamelistCache[cacheKey] = new CachedGamelist(
                gamelistPath,
                fileInfo.LastWriteTimeUtc,
                fileInfo.Length,
                document);
        }

        return document;
    }

    private void LogInvalidGamelistOnce(string systemId, string gamelistPath, XmlException ex)
    {
        bool shouldWarn;
        lock (_invalidGamelistWarningsLock)
        {
            shouldWarn = _invalidGamelistWarnings.Add(gamelistPath);
        }

        if (shouldWarn)
        {
            _logger?.LogWarning(
                "gamelist.xml invalide pour {SystemId}, consolidation ignoree: {GamelistPath}; {Message}",
                systemId,
                gamelistPath,
                ex.Message);
            return;
        }

        _logger?.LogDebug(
            "gamelist.xml invalide deja signale pour {SystemId}, consolidation ignoree: {GamelistPath}",
            systemId,
            gamelistPath);
    }

    private static bool IsMissingRootXml(XmlException ex)
    {
        return ex.Message.Contains("Root element is missing", StringComparison.OrdinalIgnoreCase);
    }

    private void ConsolidateWithEsConfigs(SystemDetails details)
    {
        var systemName = details.Name;
        if (string.IsNullOrEmpty(systemName)) return;

        // Parse es_systems.cfg
        var esSystemsPath = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_systems.cfg");
        if (File.Exists(esSystemsPath))
        {
            try
            {
                var xDoc = LoadCachedEsSystemsDocument(esSystemsPath);
                var systemNodes = xDoc.Descendants("system").ToList();
                var targetSystem = systemNodes.FirstOrDefault(s => s.Element("name")?.Value == systemName);
                
                if (targetSystem != null)
                {
                    details.Fullname = targetSystem.Element("fullname")?.Value ?? details.Fullname;
                    details.Manufacturer = targetSystem.Element("manufacturer")?.Value ?? details.Manufacturer;
                    details.Release = targetSystem.Element("release")?.Value ?? details.Release;
                    details.Hardware = targetSystem.Element("hardware")?.Value ?? details.Hardware;
                    details.Path = targetSystem.Element("path")?.Value ?? details.Path;
                    details.Extension = targetSystem.Element("extension")?.Value ?? details.Extension;
                    details.Command = targetSystem.Element("command")?.Value ?? details.Command;
                    details.Platform = targetSystem.Element("platform")?.Value ?? details.Platform;
                    details.Theme = targetSystem.Element("theme")?.Value ?? details.Theme;
                    details.Group = targetSystem.Element("group")?.Value ?? details.Group;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to consolidate es_systems.cfg for {systemName}: {ex.Message}");
            }
        }

        try
        {
            foreach (var pair in _settingsStore.ReadAllSettings())
            {
                if (pair.Key.StartsWith($"{systemName}.", StringComparison.OrdinalIgnoreCase))
                {
                    var key = pair.Key.Substring(systemName.Length + 1);
                    details.Settings[key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to consolidate es_settings.cfg for {systemName}: {ex.Message}");
        }
    }

    private XDocument LoadCachedEsSystemsDocument(string path)
    {
        if (!_options.CurrentValue.EsCacheEnabled)
        {
            return XDocument.Load(path);
        }

        var fileInfo = new FileInfo(path);
        lock (_cacheLock)
        {
            if (_esSystemsCache != null &&
                string.Equals(_esSystemsCache.Path, path, StringComparison.OrdinalIgnoreCase) &&
                _esSystemsCache.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc &&
                _esSystemsCache.Length == fileInfo.Length)
            {
                return _esSystemsCache.Document;
            }
        }

        var document = XDocument.Load(path);
        lock (_cacheLock)
        {
            _esSystemsCache = new CachedEsSystems(path, fileInfo.LastWriteTimeUtc, fileInfo.Length, document);
        }

        return document;
    }

    private void ParseGameSelected(string line, out string systemId, out string path, out string name)
    {
        systemId = "";
        path = "";
        name = "";
        
        var args = ParseArguments(line);
        if (args.Count > 0) systemId = args[0];
        if (args.Count > 1) path = args[1];
        if (args.Count > 2) name = args[2];
        else if (args.Count == 2) name = Path.GetFileNameWithoutExtension(path);
    }

    private bool ShouldIgnorePostLiveAddGamesFirstGamelistSelection(string systemId, string path)
    {
        var isFirstGamelistEntry = IsFirstGamelistGame(systemId, path, out var firstGamelistPath);
        if (!_mediaRuntimeState.ShouldIgnorePostLiveAddGamesFirstGamelistSelection(
                systemId,
                path,
                isFirstGamelistEntry,
                out var reason))
        {
            return false;
        }

        _logger?.LogInformation(
            "game-selected ignored after live /addgames because ES emitted the first gamelist entry before restoring the current card: reason={Reason}, system={SystemId}, path={Path}, firstGamelistPath={FirstGamelistPath}",
            reason,
            systemId,
            path,
            firstGamelistPath);
        return true;
    }

    private bool IsFirstGamelistGame(string systemId, string path, out string firstGamelistPath)
    {
        firstGamelistPath = string.Empty;
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var gamelistPath = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return false;
        }

        try
        {
            var xDoc = LoadCachedGamelistDocument(systemId, gamelistPath);
            var firstPath = xDoc.Root?
                .Elements("game")
                .Select(game => game.Element("path")?.Value?.Trim() ?? string.Empty)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(firstPath))
            {
                return false;
            }

            firstGamelistPath = ResolveGamelistGamePath(systemId, firstPath);
            var selectedPath = ResolveGamelistGamePath(systemId, path);
            return string.Equals(NormalizePath(firstGamelistPath), NormalizePath(selectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unable to test first gamelist entry for system={SystemId}, path={Path}.", systemId, path);
            return false;
        }
    }

    private static string ResolveGamelistGamePath(string systemId, string gamelistEntryPath)
    {
        if (string.IsNullOrWhiteSpace(gamelistEntryPath))
        {
            return string.Empty;
        }

        try
        {
            var normalizedEntryPath = gamelistEntryPath.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedEntryPath))
            {
                return Path.GetFullPath(normalizedEntryPath);
            }

            return Path.GetFullPath(Path.Combine(RetroBatPaths.RomsRoot, systemId, normalizedEntryPath));
        }
        catch
        {
            return gamelistEntryPath;
        }
    }

    private void ParseGameStart(string line, out string path, out string longName, out string shortName)
    {
        path = "";
        longName = "";
        shortName = "";
        
        var args = ParseArguments(line);
        if (args.Count > 0) path = args[0];
        if (args.Count > 1) longName = args[1];
        if (args.Count > 2) shortName = args[2];
        else if (args.Count == 1) shortName = Path.GetFileNameWithoutExtension(path);
    }

    private List<string> ParseArguments(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new System.Text.StringBuilder();
        bool inQuotes = false;
        
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }
            }
            else
            {
                currentArg.Append(c);
            }
        }
        
        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }
        
        return args;
    }

    private async Task<LaunchDetails?> TryReadLaunchDetailsAsync(string romPath)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var launch = TryReadLaunchDetailsFromLog(romPath);
            if (launch != null)
            {
                return launch;
            }

            await Task.Delay(150);
        }

        _logger?.LogInformation(
            "No matching emulatorLauncher.log entry found yet for {RomPath}",
            romPath);

        return null;
    }

    private LaunchDetails? TryReadLaunchDetailsFromLog(string romPath)
    {
        var logPath = RetroBatPaths.EmulatorLauncherLogPath;
        if (!File.Exists(logPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(logPath);
        }
        catch (IOException)
        {
            return null;
        }

        var normalizedRomPath = NormalizePath(romPath);

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("[Startup]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!line.Contains("-rom", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var startupCommand = ExtractMessageContent(line, "[Startup]");
            if (string.IsNullOrWhiteSpace(startupCommand))
            {
                continue;
            }

            var startupRomPath = ExtractArgumentValue(startupCommand, "-rom");
            if (!string.Equals(NormalizePath(startupRomPath), normalizedRomPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var runningCommand = string.Empty;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Contains("--------------------------------------------------------------", StringComparison.Ordinal))
                {
                    break;
                }

                if (lines[j].Contains("[Running]", StringComparison.OrdinalIgnoreCase))
                {
                    runningCommand = ExtractMessageContent(lines[j], "[Running]");
                    break;
                }
            }

            return new LaunchDetails
            {
                SourceLog = logPath,
                Timestamp = ExtractTimestamp(line),
                StartupCommand = startupCommand,
                RunningCommand = runningCommand,
                System = ExtractArgumentValue(startupCommand, "-system"),
                Emulator = ExtractArgumentValue(startupCommand, "-emulator"),
                Core = ExtractArgumentValue(startupCommand, "-core"),
                RomPath = startupRomPath
            };
        }

        return null;
    }

    private static DateTime? ExtractTimestamp(string line)
    {
        if (line.Length < 23)
        {
            return null;
        }

        var raw = line[..23];
        if (DateTime.TryParse(raw, out var timestamp))
        {
            return timestamp;
        }

        return null;
    }

    private static string ExtractMessageContent(string line, string marker)
    {
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        return line[(markerIndex + marker.Length)..].Trim();
    }

    private static string ExtractArgumentValue(string commandLine, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var pattern = $@"{Regex.Escape(argumentName)}\s+(?:""(?<value>[^""]*)""|(?<value>\S+))";
        var match = Regex.Match(commandLine, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static string NormalizePath(string path)
    {
        return (path ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }

    private static string InferSystemIdFromLaunchOrPath(LaunchDetails? launch, string romPath)
    {
        if (!string.IsNullOrWhiteSpace(launch?.System))
        {
            return launch.System.Trim();
        }

        try
        {
            var romsRoot = Path.GetFullPath(RetroBatPaths.RomsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullGamePath = Path.GetFullPath(romPath);
            if (fullGamePath.StartsWith(romsRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullGamePath[romsRoot.Length..];
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length > 1)
                {
                    return parts[0];
                }
            }
        }
        catch
        {
            // Ignore inference failure and fall back to unknown.
        }

        return "unknown";
    }

    public object GetCacheSnapshot()
    {
        lock (_cacheLock)
        {
            return new
            {
                Options = _options.CurrentValue,
                SystemDetails = _systemDetailsCache.Keys.OrderBy(k => k).ToArray(),
                Games = _gamesCache
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new { SystemId = pair.Key, Count = pair.Value.Games.Count, CachedAtUtc = pair.Value.CachedAtUtc })
                    .ToArray(),
                Gamelists = _gamelistCache
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new { SystemId = pair.Key, pair.Value.Path, pair.Value.Length, pair.Value.LastWriteTimeUtc })
                    .ToArray(),
                EsSystemsCached = _esSystemsCache != null,
                EsSettingsCached = _esSettingsCache != null
            };
        }
    }

    public object ClearCaches(string reason = "manual")
    {
        ClearAllCaches(reason);
        return GetCacheSnapshot();
    }

    private void ClearAllCaches(string reason)
    {
        lock (_cacheLock)
        {
            _systemDetailsCache.Clear();
            _gamesCache.Clear();
            _gamelistCache.Clear();
            _esSystemsCache = null;
            _esSettingsCache = null;
        }

        _logger?.LogInformation("EmulationStation watcher caches cleared: {Reason}", reason);
    }

    private void ClearEsSettingsDependentCaches(string reason)
    {
        lock (_cacheLock)
        {
            _systemDetailsCache.Clear();
            _esSettingsCache = null;
        }

        _logger?.LogDebug("EmulationStation settings-dependent caches cleared: {Reason}", reason);
    }

    private void ClearEsRuntimeCaches(string reason)
    {
        lock (_cacheLock)
        {
            _systemDetailsCache.Clear();
            _gamesCache.Clear();
        }

        _logger?.LogDebug("EmulationStation runtime API caches cleared: {Reason}", reason);
    }

    private bool ShouldUseCache(bool bypassCache)
    {
        return _options.CurrentValue.EsCacheEnabled && !bypassCache;
    }

    private bool IsFresh(DateTime cachedAtUtc)
    {
        var ttlSeconds = Math.Max(1, _options.CurrentValue.GamesCacheTtlSeconds);
        return DateTime.UtcNow - cachedAtUtc <= TimeSpan.FromSeconds(ttlSeconds);
    }

    private bool TryGetFreshCacheItem<T>(Dictionary<string, CachedItem<T>> cache, string key, out T value)
    {
        lock (_cacheLock)
        {
            if (cache.TryGetValue(key, out var cached) && IsFresh(cached.CachedAtUtc))
            {
                value = cached.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    private void SetCacheItem<T>(Dictionary<string, CachedItem<T>> cache, string key, T value)
    {
        if (!_options.CurrentValue.EsCacheEnabled)
        {
            return;
        }

        lock (_cacheLock)
        {
            cache[key] = new CachedItem<T>(DateTime.UtcNow, value);
        }
    }

    private void LogGameSelectedPerfIfNeeded(
        TimeSpan elapsed,
        string systemId,
        string path,
        EmulationStationWatcherOptions options,
        bool detailsEnabled,
        bool bypassCache)
    {
        var threshold = Math.Max(0, options.GameSelectedPerfLogThresholdMs);
        if (elapsed.TotalMilliseconds < threshold)
        {
            return;
        }

        _logger?.LogInformation(
            "game-selected processed in {ElapsedMs}ms: system={SystemId}, rom={RomPath}, dataDepth={DataDepth}, detailsEnabled={DetailsEnabled}, cache={CacheMode}, prefetch={PrefetchMode}",
            (int)elapsed.TotalMilliseconds,
            systemId,
            path,
            options.GameSelectedDataDepth,
            detailsEnabled,
            bypassCache || !options.EsCacheEnabled ? "bypass" : "enabled",
            options.LocalProjectionOnGameSelected
                ? "local-projection"
                : options.PrefetchOnGameSelected
                    ? "prefetch"
                    : "disabled");
    }

    private static bool IsContextOnlyMode(EmulationStationWatcherOptions options)
    {
        return string.Equals(options.GameSelectedDataDepth, "ContextOnly", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFullDetailsMode(EmulationStationWatcherOptions options)
    {
        return string.Equals(options.GameSelectedDataDepth, "FullDetails", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCacheKey(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static SystemDetails CloneSystemDetails(SystemDetails source)
    {
        return new SystemDetails
        {
            Name = source.Name,
            Fullname = source.Fullname,
            Theme = source.Theme,
            Manufacturer = source.Manufacturer,
            Logo = source.Logo,
            Release = source.Release,
            Hardware = source.Hardware,
            Path = source.Path,
            Extension = source.Extension,
            Command = source.Command,
            Platform = source.Platform,
            Group = source.Group,
            Settings = new Dictionary<string, string>(source.Settings, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static EsGameApiData CloneGameDetails(EsGameApiData source)
    {
        return new EsGameApiData
        {
            Path = source.Path,
            Id = source.Id,
            Name = source.Name,
            Desc = source.Desc,
            Image = source.Image,
            Video = source.Video,
            Marquee = source.Marquee,
            Thumbnail = source.Thumbnail,
            Developer = source.Developer,
            Publisher = source.Publisher,
            Genre = source.Genre,
            Rating = source.Rating,
            Md5 = source.Md5,
            SystemName = source.SystemName,
            Emulator = source.Emulator,
            Fanart = source.Fanart,
            Bezel = source.Bezel,
            Boxback = source.Boxback,
            Map = source.Map,
            Manual = source.Manual,
            Releasedate = source.Releasedate,
            Family = source.Family,
            Genres = source.Genres,
            Arcadesystemname = source.Arcadesystemname,
            Players = source.Players,
            Favorite = source.Favorite,
            Hidden = source.Hidden,
            Kidgame = source.Kidgame,
            Playcount = source.Playcount,
            Lastplayed = source.Lastplayed,
            Gametime = source.Gametime,
            Lang = source.Lang,
            Region = source.Region,
            ScraperId = source.ScraperId,
            ScrapName = source.ScrapName,
            ScrapDate = source.ScrapDate,
            Extras = new Dictionary<string, string>(source.Extras, StringComparer.OrdinalIgnoreCase)
        };
    }

    private sealed record CachedItem<T>(DateTime CachedAtUtc, T Value);
    private sealed record CachedGames(DateTime CachedAtUtc, List<EsGameApiData> Games);
    private sealed record CachedGamelist(string Path, DateTime LastWriteTimeUtc, long Length, XDocument Document);
    private sealed record CachedEsSystems(string Path, DateTime LastWriteTimeUtc, long Length, XDocument Document);
    private sealed record CachedEsSettings(string Path, DateTime LastWriteTimeUtc, long Length, XDocument Document);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _eventsIniPollCts?.Cancel();
        try
        {
            _eventsIniPollTask?.Wait(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Shutdown best effort; the poller observes cancellation and has no durable state.
        }
        _eventsIniPollCts?.Dispose();
        lock (_eventsIniProcessLock)
        {
            _eventsIniProcessCts?.Cancel();
            _eventsIniProcessCts?.Dispose();
            _eventsIniProcessCts = null;
        }

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
        }
        _settingsSubscription?.Dispose();
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    public bool IsHealthy() => true;
}
