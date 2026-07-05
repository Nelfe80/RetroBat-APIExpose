using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class RemoteScrapeQueueService : BackgroundService
{
    private static readonly TimeSpan IdleQuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WorkerCapacityPollDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteMediaNoChangeCooldown = TimeSpan.FromHours(12);
    private readonly object _lock = new();
    private readonly Dictionary<string, RemoteScrapeQueueWorkItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaProjectionPlan> _pendingGamelistPersistence = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _noChangeCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _lifo = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ScreenScraperConnectionService _connectionService;
    private readonly ScreenScraperCapabilityService _capabilityService;
    private readonly ScreenScraperRemoteProvider _provider;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<RemoteScrapeQueueService>? _logger;

    public RemoteScrapeQueueService(
        ApiExposeRuntimeOptionsService runtimeOptions,
        ScreenScraperConnectionService connectionService,
        ScreenScraperCapabilityService capabilityService,
        ScreenScraperRemoteProvider provider,
        GamelistUpdateService gamelistUpdateService,
        SystemIdNormalizer systemIdNormalizer,
        EmulationStationSettingsService settingsService,
        MediaRuntimeState runtimeState,
        IEmulationStationNotificationService notificationService,
        InterfaceTextService interfaceTextService,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<RemoteScrapeQueueService>? logger = null)
    {
        _runtimeOptions = runtimeOptions;
        _connectionService = connectionService;
        _capabilityService = capabilityService;
        _provider = provider;
        _gamelistUpdateService = gamelistUpdateService;
        _systemIdNormalizer = systemIdNormalizer;
        _settingsService = settingsService;
        _runtimeState = runtimeState;
        _notificationService = notificationService;
        _interfaceTextService = interfaceTextService;
        _options = options;
        _logger = logger;
    }

    public void Enqueue(MediaProjectionPlan plan, IReadOnlyList<string> kinds, string reason)
    {
        var normalizedKinds = kinds
            .Select(MediaKinds.Normalize)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedKinds.Count == 0)
        {
            return;
        }

        var key = BuildKey(plan, normalizedKinds);
        if (IsNoChangeCooldownActive(key))
        {
            _logger?.LogDebug(
                "Remote scrape queue skipped by no-change cooldown: system={SystemId}, game={GameSlug}, kinds={Kinds}, reason={Reason}.",
                plan.FrontendSystemId,
                plan.GameSlug,
                string.Join(",", normalizedKinds),
                reason);
            return;
        }

        var scrapeInvalidation = _runtimeState.GetRemoteScrapeInvalidationSnapshot();
        var item = new RemoteScrapeQueueWorkItem(
            key,
            ClonePlan(plan),
            normalizedKinds,
            string.IsNullOrWhiteSpace(reason) ? "queued" : reason.Trim(),
            DateTime.UtcNow,
            scrapeInvalidation.Version);

        lock (_lock)
        {
            _items[item.Key] = item;
            _lifo.Push(item.Key);
        }

        _signal.Release();
        _logger?.LogDebug(
            "Remote scrape queue enqueued: system={SystemId}, game={GameSlug}, kinds={Kinds}, reason={Reason}.",
            item.Plan.FrontendSystemId,
            item.Plan.GameSlug,
            string.Join(",", item.Kinds),
            item.Reason);
    }

    public IReadOnlyList<MediaProjectionPlan> DrainPendingGamelistPersistence(string frontendSystemId, string currentGamePath, int maxItems = 12)
    {
        var normalizedSystem = NormalizeKeyPart(frontendSystemId);
        var normalizedCurrentPath = NormalizePath(currentGamePath);
        var drained = new List<MediaProjectionPlan>();
        lock (_lock)
        {
            var keys = _pendingGamelistPersistence
                .Values
                .Where(plan => string.Equals(NormalizeKeyPart(plan.FrontendSystemId), normalizedSystem, StringComparison.OrdinalIgnoreCase))
                .Where(plan => !string.Equals(NormalizePath(plan.GamePath), normalizedCurrentPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(plan => BuildPendingPersistenceKey(plan), StringComparer.OrdinalIgnoreCase)
                .Select(BuildPendingPersistenceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maxItems))
                .ToList();

            foreach (var key in keys)
            {
                if (_pendingGamelistPersistence.Remove(key, out var plan))
                {
                    drained.Add(ClonePlan(plan));
                }
            }
        }

        return drained;
    }

    public RemoteScrapeQueueDiscardResult DiscardPendingForLanguageSwitch(string reason)
    {
        lock (_lock)
        {
            var queued = _items.Count;
            var pendingPersistence = _pendingGamelistPersistence.Count;
            var cooldowns = _noChangeCooldowns.Count;
            _items.Clear();
            _pendingGamelistPersistence.Clear();
            _noChangeCooldowns.Clear();
            while (_lifo.Count > 0)
            {
                _lifo.Pop();
            }

            _logger?.LogInformation(
                "Remote scrape queue discarded for language switch: reason={Reason}, queued={Queued}, pendingPersistence={PendingPersistence}, cooldowns={Cooldowns}.",
                reason,
                queued,
                pendingPersistence,
                cooldowns);
            return new RemoteScrapeQueueDiscardResult(queued, pendingPersistence, cooldowns);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = _capabilityService.ResolveMaxQueueWorkers();
        var workers = Enumerable
            .Range(0, workerCount)
            .Select(workerIndex => ExecuteWorkerAsync(workerIndex, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task ExecuteWorkerAsync(int workerIndex, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (workerIndex >= _capabilityService.ResolveQueueConcurrency())
                {
                    await Task.Delay(WorkerCapacityPollDelay, stoppingToken);
                    continue;
                }

                var item = TryPopLatest();
                if (item == null)
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (!await WaitUntilQueueCanRunAsync(stoppingToken))
                {
                    if (IsStaleAfterRemoteScrapeInvalidation(item))
                    {
                        await AuditQueueAsync(item, "discarded-stale-language-sync", CancellationToken.None);
                    }
                    else
                    {
                        Requeue(item, "gate-closed-before-start");
                    }
                    continue;
                }

                if (!IsQueueEnabled())
                {
                    if (IsStaleAfterRemoteScrapeInvalidation(item))
                    {
                        await AuditQueueAsync(item, "discarded-stale-language-sync", CancellationToken.None);
                    }
                    else
                    {
                        Requeue(item, "queue-disabled");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                await ProcessItemAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Remote scrape queue loop failed; queue will continue.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ProcessItemAsync(RemoteScrapeQueueWorkItem item, CancellationToken stoppingToken)
    {
        var jobKey = $"screenscraper-queue:{item.Plan.FrontendSystemId}:{item.Plan.GameSlug}:{string.Join(',', item.Kinds)}";
        var scrapeInvalidation = _runtimeState.GetRemoteScrapeInvalidationSnapshot();
        if (item.RemoteScrapeInvalidationVersion != scrapeInvalidation.Version)
        {
            await AuditQueueAsync(
                item,
                "discarded-stale-language-sync",
                CancellationToken.None,
                new
                {
                    item.RemoteScrapeInvalidationVersion,
                    currentRemoteScrapeInvalidationVersion = scrapeInvalidation.Version,
                    scrapeInvalidation.Reason,
                    scrapeInvalidation.InvalidatedAtUtc
                });
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, scrapeInvalidation.CancellationToken);
        var monitor = CancelWhenLiveGateClosesAsync(linkedCts, stoppingToken);
        _runtimeState.TrackScrapeQueued(
            jobKey,
            item.Plan.FrontendSystemId,
            item.Plan.GameSlug,
            string.IsNullOrWhiteSpace(item.Plan.DisplayName) ? item.Plan.GameSlug : item.Plan.DisplayName,
            string.Join(",", item.Kinds),
            blocksReload: false);
        _runtimeState.TrackScrapeStarted(jobKey);
        _runtimeState.BeginScrapeActivity(blocksReload: false);
        var selectionToken = _runtimeState.CaptureCurrentGameSelection();

        try
        {
            var connection = _connectionService.Resolve();
            if (!connection.HasUserCredentials || !connection.HasDeveloperCredentials)
            {
                await AuditQueueAsync(item, "skipped-credentials", linkedCts.Token);
                return;
            }

            var screenScraperSystemId = _systemIdNormalizer.ResolveScreenScraperSystemId(item.Plan.FrontendSystemId);
            if (string.IsNullOrWhiteSpace(screenScraperSystemId))
            {
                await AuditQueueAsync(item, "skipped-system-mapping", linkedCts.Token);
                return;
            }

            var scrapingOptions = _options.CurrentValue.Scraping;
            _settingsService.Invalidate();
            var currentLanguage = _settingsService.GetScrapingSettings().Language;
            var result = await _provider.ScrapeAsync(
                item.Plan,
                item.Kinds,
                screenScraperSystemId,
                currentLanguage,
                _runtimeOptions.GetRemoteBezelAspectRatio(),
                _runtimeOptions.GetRemoteBezelOrientation(),
                refreshCurrentGameAfterSuccess: false,
                linkedCts.Token);

            var selectionStillCurrent = IsCapturedSelectionStillCurrent(selectionToken, item.Plan);
            if (result.ImportedKinds.Contains(MediaKinds.Video, StringComparer.OrdinalIgnoreCase) &&
                selectionStillCurrent)
            {
                var videoLivePushed = await _gamelistUpdateService.PushLiveGameUpdateToEsAsync(
                    item.Plan,
                    linkedCts.Token,
                    LiveGameUpdateNotificationKind.RemoteVideoScrape,
                    allowCurrentVideoRefresh: true);
                result.LivePushed = result.LivePushed || videoLivePushed;
            }

            if (!result.LivePushed &&
                result.ImportedMediaCount > 0 &&
                selectionStillCurrent)
            {
                var mediaLivePushed = await _gamelistUpdateService.PushLiveGameUpdateToEsAsync(
                    item.Plan,
                    linkedCts.Token,
                    LiveGameUpdateNotificationKind.RemoteScrape);
                result.LivePushed = result.LivePushed || mediaLivePushed;
            }

            if (!result.LivePushed &&
                result.TextUpdated &&
                selectionStillCurrent)
            {
                var textLivePushed = await _gamelistUpdateService.PushLiveGameUpdateToEsAsync(
                    item.Plan,
                    linkedCts.Token,
                    LiveGameUpdateNotificationKind.RemoteScrape,
                    allowLocalizedMetadataRefresh: true);
                result.LivePushed = result.LivePushed || textLivePushed;
            }

            if (result.RequiresGamelistPersistence)
            {
                MarkPendingGamelistPersistence(item.Plan);
                _gamelistUpdateService.MarkLiveGamelistDirty(item.Plan);
                await _gamelistUpdateService.StageExtendedEntriesAsync(item.Plan, linkedCts.Token);
            }

            if (result.TextUpdated || result.RequiresGamelistPersistence)
            {
                _runtimeState.RequestLocalizedGamelistCacheRefreshForGame(item.Plan);
                await AuditQueueAsync(
                    item,
                    "localized-cache-refresh-queued",
                    linkedCts.Token,
                    new
                    {
                        item.Plan.FrontendSystemId,
                        item.Plan.GamePath,
                        item.Plan.GameSlug,
                        result.TextUpdated,
                        result.RequiresGamelistPersistence
                    });
            }

            if (IsNoChangeCooldownResult(result))
            {
                RememberNoChangeCooldown(item.Key);
            }
            else
            {
                ForgetNoChangeCooldown(item.Key);
            }

            if (ShouldNotifyHeavyMediaScrape(result, item.Plan, selectionStillCurrent))
            {
                await _notificationService.NotifyAsync(
                    ResolveHeavyMediaScrapeCompletedMessage(item, result),
                    linkedCts.Token);
            }

            await AuditQueueAsync(
                item,
                result.Status,
                linkedCts.Token,
                new
                {
                    result.DownloadedMediaCount,
                    result.ImportedMediaCount,
                    result.TextUpdated,
                    importedKinds = result.ImportedKinds,
                    result.RequiresGamelistPersistence,
                    selectionStillCurrent,
                    gamelistPersistence = result.RequiresGamelistPersistence ? "pending-next-planned-write" : "not-needed",
                    bezelAspect = scrapingOptions.BezelAspectRatio,
                    bezelOrientation = scrapingOptions.BezelOrientation
                });
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested &&
            (scrapeInvalidation.CancellationToken.IsCancellationRequested ||
                _runtimeState.IsRemoteScrapeInvalidated(scrapeInvalidation.Version)))
        {
            await AuditQueueAsync(
                item,
                "cancelled-stale-language-sync",
                CancellationToken.None,
                new
                {
                    item.RemoteScrapeInvalidationVersion,
                    scrapeInvalidation.Reason,
                    scrapeInvalidation.InvalidatedAtUtc
                });
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            Requeue(item, "cancelled-by-live");
            await AuditQueueAsync(item, "paused-by-live", CancellationToken.None);
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await monitor;
            }
            catch (OperationCanceledException)
            {
            }

            _runtimeState.EndScrapeActivity(blocksReload: false);
            _runtimeState.TrackScrapeCompleted(jobKey);
        }
    }

    private async Task<bool> WaitUntilQueueCanRunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsQueueEnabled())
            {
                return false;
            }

            if (_runtimeState.IsRemoteScrapeQueueAllowed(IdleQuietPeriod, out _))
            {
                return true;
            }

            await Task.Delay(IdlePollDelay, cancellationToken);
        }

        return false;
    }

    private async Task CancelWhenLiveGateClosesAsync(CancellationTokenSource jobCts, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !jobCts.IsCancellationRequested)
        {
            if (!_runtimeState.IsRemoteScrapeQueueAllowed(IdleQuietPeriod, out _))
            {
                jobCts.Cancel();
                return;
            }

            await Task.Delay(IdlePollDelay, stoppingToken);
        }
    }

    private RemoteScrapeQueueWorkItem? TryPopLatest()
    {
        lock (_lock)
        {
            while (_lifo.Count > 0)
            {
                var key = _lifo.Pop();
                if (_items.Remove(key, out var item))
                {
                    return item;
                }
            }
        }

        return null;
    }

    private void Requeue(RemoteScrapeQueueWorkItem item, string reason)
    {
        if (IsStaleAfterRemoteScrapeInvalidation(item))
        {
            return;
        }

        var updated = item with { Reason = reason, QueuedAtUtc = DateTime.UtcNow };
        lock (_lock)
        {
            _items[updated.Key] = updated;
            _lifo.Push(updated.Key);
        }

        _signal.Release();
    }

    private bool IsNoChangeCooldownActive(string key)
    {
        lock (_lock)
        {
            if (!_noChangeCooldowns.TryGetValue(key, out var expiresAtUtc))
            {
                return false;
            }

            if (expiresAtUtc > DateTime.UtcNow)
            {
                return true;
            }

            _noChangeCooldowns.Remove(key);
            return false;
        }
    }

    private void RememberNoChangeCooldown(string key)
    {
        lock (_lock)
        {
            _noChangeCooldowns[key] = DateTime.UtcNow.Add(RemoteMediaNoChangeCooldown);
        }
    }

    private void ForgetNoChangeCooldown(string key)
    {
        lock (_lock)
        {
            _noChangeCooldowns.Remove(key);
        }
    }

    public void MarkPendingGamelistPersistence(MediaProjectionPlan plan)
    {
        var key = BuildPendingPersistenceKey(plan);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_lock)
        {
            _pendingGamelistPersistence[key] = ClonePlan(plan);
        }
    }

    private bool IsQueueEnabled()
    {
        return _runtimeOptions.IsAutoScrapingEnabled() &&
            _runtimeOptions.IsScreenScraperProviderEnabled() &&
            _runtimeOptions.IsRemoteScrapeQueueEnabled() &&
            string.Equals(_runtimeOptions.GetRemoteScrapingProvider(), "screenscraper", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildKey(MediaProjectionPlan plan, IReadOnlyList<string> kinds)
    {
        var pathKey = (plan.GamePath ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
        return $"{plan.FrontendSystemId}:{pathKey}:{string.Join(',', kinds)}";
    }

    private static string BuildPendingPersistenceKey(MediaProjectionPlan plan)
    {
        return $"{NormalizeKeyPart(plan.FrontendSystemId)}:{NormalizePath(plan.GamePath)}";
    }

    private static string NormalizeKeyPart(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizePath(string? value)
    {
        return (value ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private static MediaProjectionPlan ClonePlan(MediaProjectionPlan source)
    {
        return new MediaProjectionPlan
        {
            SystemId = source.SystemId,
            FrontendSystemId = source.FrontendSystemId,
            GameSlug = source.GameSlug,
            TextSourceGameSlug = source.TextSourceGameSlug,
            DisplayName = source.DisplayName,
            GamePath = source.GamePath,
            ProjectionBaseName = source.ProjectionBaseName,
            PreferredImageSource = source.PreferredImageSource,
            PreferredLogoSource = source.PreferredLogoSource,
            PreferredThumbnailSource = source.PreferredThumbnailSource,
            IsArcadeLike = source.IsArcadeLike,
            IsFolderBasedSystem = source.IsFolderBasedSystem,
            SkipCrcComputation = source.SkipCrcComputation,
            IsFilteredArcadeBiosCandidate = source.IsFilteredArcadeBiosCandidate,
            NeedsDescriptionScrape = source.NeedsDescriptionScrape,
            IgnoreRemoteScrapeCooldown = source.IgnoreRemoteScrapeCooldown,
            GamePathExists = source.GamePathExists,
            GamelistMd5 = source.GamelistMd5,
            GamelistCrc32 = source.GamelistCrc32,
            GamelistPath = source.GamelistPath,
            EsGameId = source.EsGameId,
            ScreenScraperGameId = source.ScreenScraperGameId,
            RomRegions = source.RomRegions.ToList(),
            RomLanguages = source.RomLanguages.ToList(),
            SuppressImmediateGamelistUpdates = source.SuppressImmediateGamelistUpdates,
            Needs = source.Needs
                .Select(need => new MediaNeed
                {
                    Kind = need.Kind,
                    IsMissing = need.IsMissing,
                    InitialExistingPath = need.InitialExistingPath,
                    ExistingPath = need.ExistingPath,
                    TargetRelativePath = need.TargetRelativePath,
                    ImportedPath = need.ImportedPath,
                    ProjectedPath = need.ProjectedPath,
                    WasImported = need.WasImported,
                    WasProjected = need.WasProjected,
                    WasContentChanged = need.WasContentChanged
                })
                .ToList()
        };
    }

    private static Task AuditQueueAsync(
        RemoteScrapeQueueWorkItem item,
        string status,
        CancellationToken cancellationToken,
        object? details = null)
    {
        return MediaUpdateAuditLog.AppendAsync(
            item.Plan,
            "remote-scrape-queue",
            "screenscraper",
            status,
            new
            {
                item.Reason,
                item.Kinds,
                details
            },
            cancellationToken);
    }

    private static bool IsNoChangeCooldownResult(ScreenScraperRemoteScrapeResult result)
    {
        return !result.RequiresGamelistPersistence &&
            !result.TextUpdated &&
            result.ImportedMediaCount == 0 &&
            result.DownloadedMediaCount == 0 &&
            result.Status is "no-change" or "not-found";
    }

    private bool ShouldNotifyHeavyMediaScrape(
        ScreenScraperRemoteScrapeResult result,
        MediaProjectionPlan plan,
        bool selectionStillCurrent)
    {
        return _runtimeOptions.ShouldNotifyHeavyMediaScrape() &&
            string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
            result.ImportedKinds.Any(IsHeavyMediaKind) &&
            (selectionStillCurrent || result.ImportedKinds.Any(IsVideoKind));
    }

    private bool IsCapturedSelectionStillCurrent(CurrentGameSelectionToken selectionToken, MediaProjectionPlan plan)
    {
        return (_runtimeState.IsCurrentGameSelectionToken(selectionToken, plan.FrontendSystemId, plan.GamePath) ||
                _runtimeState.IsCurrentGameSelectionToken(selectionToken, plan.SystemId, plan.GamePath)) &&
            _gamelistUpdateService.IsCurrentlySelectedGame(plan);
    }

    private string ResolveHeavyMediaScrapeCompletedMessage(RemoteScrapeQueueWorkItem item, ScreenScraperRemoteScrapeResult result)
    {
        var gameName = EsNotificationText.ShortGameName(!string.IsNullOrWhiteSpace(item.Plan.DisplayName)
            ? item.Plan.DisplayName
            : item.Plan.GameSlug);
        var mediaPart = string.Join(", ", result.ImportedKinds
            .Where(IsHeavyMediaKind)
            .Select(ResolveHeavyMediaKindLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(mediaPart))
        {
            mediaPart = _interfaceTextService.Text("term.media.one", _settingsService.GetScrapingSettings().Language);
        }

        var language = _settingsService.GetScrapingSettings().Language;
        return _interfaceTextService.Format(
            "notification.scrape.heavy_completed",
            language,
            ("game", gameName),
            ("details", mediaPart));
    }

    private string ResolveHeavyMediaKindLabel(string kind)
    {
        var key = MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Manual => "label.media.manual",
            MediaKinds.Magazine => "label.media.magazine",
            MediaKinds.Video => "label.media.video",
            MediaKinds.VideoNormalized => "label.media.video_normalized",
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(key)
            ? kind
            : _interfaceTextService.Text(key, _settingsService.GetScrapingSettings().Language);
    }

    private static bool IsHeavyMediaKind(string kind)
    {
        return MediaKinds.Normalize(kind) is
            MediaKinds.Manual or
            MediaKinds.Magazine or
            MediaKinds.Video or
            MediaKinds.VideoNormalized;
    }

    private static bool IsVideoKind(string kind)
    {
        return MediaKinds.Normalize(kind) is MediaKinds.Video or MediaKinds.VideoNormalized;
    }

    private bool IsStaleAfterRemoteScrapeInvalidation(RemoteScrapeQueueWorkItem item)
    {
        return _runtimeState.IsRemoteScrapeInvalidated(item.RemoteScrapeInvalidationVersion);
    }

    private sealed record RemoteScrapeQueueWorkItem(
        string Key,
        MediaProjectionPlan Plan,
        List<string> Kinds,
        string Reason,
        DateTime QueuedAtUtc,
        long RemoteScrapeInvalidationVersion);
}

public sealed record RemoteScrapeQueueDiscardResult(
    int QueuedItems,
    int PendingGamelistPersistence,
    int NoChangeCooldowns);
