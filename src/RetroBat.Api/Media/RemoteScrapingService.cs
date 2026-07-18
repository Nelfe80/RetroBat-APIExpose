using Microsoft.Extensions.Options;
using RetroBat.Domain.Interfaces;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;
using System.Collections.Concurrent;
using System.Text.Json;

namespace RetroBat.Api.Media;

public sealed class RemoteScrapingService
{
    private static readonly ConcurrentDictionary<string, RemoteTextNoChangeCacheEntry> RemoteTextNoChangeCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> RemoteMediaNoChangeCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RemoteExactLocalNoRetryCacheEntry> RemoteExactLocalNoRetryEntries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RemoteMediaNoChangeCooldown = TimeSpan.FromHours(12);
    private static readonly object RemoteTextNoChangeCacheLock = new();
    private static readonly object RemoteExactLocalNoRetryCacheLock = new();
    private static readonly JsonSerializerOptions RemoteTextNoChangeCacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static bool RemoteTextNoChangeCacheLoaded;
    private static bool RemoteExactLocalNoRetryCacheLoaded;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ScreenScraperConnectionService _screenScraperConnectionService;
    private readonly ScreenScraperCapabilityService _screenScraperCapabilityService;
    private readonly ScreenScraperRemoteProvider _screenScraperRemoteProvider;
    private readonly MarqueeAutogenService _marqueeAutogenService;
    private readonly RemoteScrapeQueueService _scrapeQueueService;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly LocalMediaIndexService _localMediaIndexService;
    private readonly MediaLocalizationResolver _mediaLocalizationResolver;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ILogger<RemoteScrapingService>? _logger;

    public RemoteScrapingService(
        IOptionsMonitor<ApiExposeOptions> options,
        EmulationStationSettingsService settingsService,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ScreenScraperConnectionService screenScraperConnectionService,
        ScreenScraperCapabilityService screenScraperCapabilityService,
        ScreenScraperRemoteProvider screenScraperRemoteProvider,
        MarqueeAutogenService marqueeAutogenService,
        RemoteScrapeQueueService scrapeQueueService,
        GamelistUpdateService gamelistUpdateService,
        SystemIdNormalizer systemIdNormalizer,
        LocalMediaIndexService localMediaIndexService,
        MediaLocalizationResolver mediaLocalizationResolver,
        MediaRuntimeState runtimeState,
        IEmulationStationNotificationService notificationService,
        InterfaceTextService interfaceTextService,
        ILogger<RemoteScrapingService>? logger = null)
    {
        _options = options;
        _settingsService = settingsService;
        _runtimeOptions = runtimeOptions;
        _screenScraperConnectionService = screenScraperConnectionService;
        _screenScraperCapabilityService = screenScraperCapabilityService;
        _screenScraperRemoteProvider = screenScraperRemoteProvider;
        _marqueeAutogenService = marqueeAutogenService;
        _scrapeQueueService = scrapeQueueService;
        _gamelistUpdateService = gamelistUpdateService;
        _systemIdNormalizer = systemIdNormalizer;
        _localMediaIndexService = localMediaIndexService;
        _mediaLocalizationResolver = mediaLocalizationResolver;
        _runtimeState = runtimeState;
        _notificationService = notificationService;
        _interfaceTextService = interfaceTextService;
        _logger = logger;
    }

    public async Task<RemoteScrapeDecision> EvaluateAfterLocalAsync(
        MediaProjectionPlan plan,
        bool forceRemoteScrape,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;
        RemoteExactLocalNoRetryTtlDays = options.Scraping.RemoteExactLocalNoRetryTtlDays;
        var scraping = BuildRuntimeScrapingOptions(options.Scraping);
        var settings = _settingsService.GetScrapingSettings();
        var screenScraperConnection = _screenScraperConnectionService.Resolve();
        var policy = ResolveRemotePolicy(plan, scraping);
        var skippedAlreadyTriedKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missingKinds = policy.AllowedMissingKinds;
        var liveMissingKinds = ResolveLiveRelevantMissingKinds(plan, missingKinds, settings.IsHyperBatThemeActive);
        var requestedMissingKinds = forceRemoteScrape || !scraping.RemoteAfterLocalOnly
            ? missingKinds
            : liveMissingKinds;
        requestedMissingKinds = PrioritizeThemeHbWhenHyperBatIsActive(requestedMissingKinds, settings.IsHyperBatThemeActive);
        if (!forceRemoteScrape && !plan.IgnoreRemoteScrapeCooldown)
        {
            requestedMissingKinds = FilterAlreadyTriedRemoteKinds(
                plan,
                requestedMissingKinds,
                policy.ExactLocalMissingKinds,
                skippedAlreadyTriedKinds);
        }

        var effectiveMissingKinds = missingKinds
            .Except(skippedAlreadyTriedKinds.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var provider = string.IsNullOrWhiteSpace(scraping.RemoteProvider)
            ? "screenscraper"
            : scraping.RemoteProvider.Trim().ToLowerInvariant();
        var hasHyperBatThemeRequest = requestedMissingKinds.Contains(MediaKinds.ThemeHb, StringComparer.OrdinalIgnoreCase) &&
            scraping.ThemeHbScrapingEnabled;

        var decision = new RemoteScrapeDecision
        {
            Enabled = scraping.AutoScrapingEnabled || forceRemoteScrape || hasHyperBatThemeRequest,
            Provider = provider,
            SystemId = plan.SystemId,
            FrontendSystemId = plan.FrontendSystemId,
            GameSlug = plan.GameSlug,
            GamePath = plan.GamePath,
            DisplayName = plan.DisplayName,
            MissingKinds = requestedMissingKinds,
            ExactLocalMissingKinds = policy.ExactLocalMissingKinds,
            ExactLocalOnly = IsExactLocalOnlyRemoteCheck(plan, policy, requestedMissingKinds),
            DeferredMissingKinds = effectiveMissingKinds
                .Except(requestedMissingKinds, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            WorkflowMode = ResolveWorkflowMode(plan, requestedMissingKinds),
            ExcludedKinds = policy.ExcludedKinds
                .Concat(skippedAlreadyTriedKinds)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            BezelAspectRatio = NormalizeBezelAspectRatio(scraping.BezelAspectRatio),
            BezelOrientation = NormalizeBezelOrientation(scraping.BezelOrientation),
            NeedsDescription = plan.NeedsDescriptionScrape,
            LocalFirst = scraping.RemoteAfterLocalOnly,
            RefreshCurrentGameAfterSuccess = hasHyperBatThemeRequest && settings.IsHyperBatThemeActive
                ? scraping.RefreshCurrentGameAfterRemoteSuccess || _runtimeOptions.ShouldRefreshCurrentAfterHyperBatInstallSuccess()
                : scraping.RefreshCurrentGameAfterRemoteSuccess
        };

        if (!decision.Enabled)
        {
            decision.Status = "disabled";
            decision.Message = "Auto scraping is disabled.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        if (scraping.RemoteAfterLocalOnly && effectiveMissingKinds.Count == 0 && !plan.NeedsDescriptionScrape)
        {
            decision.Status = skippedAlreadyTriedKinds.Count > 0
                ? "remote-media-already-tried"
                : "local-satisfied";
            decision.Message = skippedAlreadyTriedKinds.Count > 0
                ? "Remote media were already checked for this game/kind and returned no usable update."
                : "Local scraping satisfied all currently requested media/text needs.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        if (!forceRemoteScrape && scraping.RemoteAfterLocalOnly && requestedMissingKinds.Count == 0 && !plan.NeedsDescriptionScrape)
        {
            EnqueueDeferredMissingKinds(plan, decision, "live-satisfied");
            decision.Status = skippedAlreadyTriedKinds.Count > 0
                ? "remote-media-already-tried"
                : "live-satisfied";
            decision.Message = skippedAlreadyTriedKinds.Count > 0
                ? "Remote live media were already checked for this game/kind and returned no usable update."
                : "Current game card is already satisfied; remaining missing media are not live scraping triggers.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        if (!string.Equals(provider, "screenscraper", StringComparison.OrdinalIgnoreCase))
        {
            decision.Status = "provider-unsupported";
            decision.Message = $"Remote scraping provider '{provider}' is not registered.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        if (!scraping.ScreenScraperEnabled)
        {
            decision.Status = "provider-disabled";
            decision.Message = "ScreenScraper provider is disabled.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        decision.ScreenScraperSystemId = _systemIdNormalizer.ResolveScreenScraperSystemId(plan.FrontendSystemId);
        decision.HasScreenScraperCredentials = screenScraperConnection.HasUserCredentials;
        decision.HasScreenScraperDeveloperCredentials = screenScraperConnection.HasDeveloperCredentials;
        decision.ScreenScraperDeveloperCredentialSource = screenScraperConnection.DeveloperCredentialSource;
        decision.ScreenScraperBaseUrl = screenScraperConnection.BaseUrl;
        decision.ScreenScraperSoftName = screenScraperConnection.SoftName;
        decision.Region = string.Empty;
        decision.Language = settings.Language;

        if (!decision.HasScreenScraperCredentials)
        {
            decision.Status = "missing-user-credentials";
            decision.Message = "ScreenScraper user credentials are missing in EmulationStation scraper settings.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        if (!decision.HasScreenScraperDeveloperCredentials)
        {
            decision.Status = "missing-developer-credentials";
            decision.Message = "ScreenScraper developer credentials are missing.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        if (string.IsNullOrWhiteSpace(decision.ScreenScraperSystemId))
        {
            decision.Status = "missing-system-mapping";
            decision.Message = "No ScreenScraper system id mapping is available for this frontend system.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            return decision;
        }

        if (!forceRemoteScrape &&
            decision.ExactLocalOnly &&
            TryGetRemoteExactLocalNoRetry(plan, requestedMissingKinds, out var exactLocalNoRetryEntry))
        {
            decision.Status = "exact-local-no-retry";
            decision.Message = "Exact local media were already checked and no exact regional update was available.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-exact-local-no-retry",
                "media",
                "skipped",
                new
                {
                    kinds = requestedMissingKinds,
                    exactLocalNoRetryEntry.LastCheckedAtUtc,
                    exactLocalNoRetryEntry.Status
                },
                cancellationToken);
            return decision;
        }

        if (!forceRemoteScrape &&
            IsTextOnlyRemoteMetadataCheck(plan, requestedMissingKinds) &&
            TryGetRemoteTextNoChangeCooldown(plan, settings.Language, out var cooldownEntry))
        {
            decision.Status = "text-no-change-cooldown";
            decision.Message = "Remote metadata was already checked recently and returned no usable update.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-text-cooldown",
                "metadata",
                "skipped",
                new
                {
                    language = settings.Language,
                    cooldownEntry.LastCheckedAtUtc,
                    cooldownEntry.ExpiresAtUtc,
                    cooldownEntry.Status
                },
                cancellationToken);
            return decision;
        }

        if (!forceRemoteScrape &&
            IsMediaOnlyRemoteCheck(plan, requestedMissingKinds) &&
            TryGetRemoteMediaNoChangeCooldown(plan, requestedMissingKinds, out var mediaCooldownUntilUtc))
        {
            decision.Status = "media-no-change-cooldown";
            decision.Message = "Remote media were already checked recently and returned no usable update.";
            await AuditDecisionAsync(plan, decision, cancellationToken);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-media-cooldown",
                "media",
                "skipped",
                new
                {
                    kinds = requestedMissingKinds,
                    expiresAtUtc = mediaCooldownUntilUtc
                },
                cancellationToken);
            return decision;
        }

        EnqueueDeferredMissingKinds(plan, decision, "deferred-after-live-decision");

        var jobKey = $"screenscraper:{plan.FrontendSystemId}:{plan.GameSlug}:{string.Join(',', requestedMissingKinds)}";
        decision.JobKey = jobKey;
        decision.Status = "running";
        decision.Message = "ScreenScraper provider is fetching missing remote data.";

        var scrapeInvalidation = _runtimeState.GetRemoteScrapeInvalidationSnapshot();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scrapeInvalidation.CancellationToken);
        var scrapeCancellationToken = linkedCts.Token;
        var blocksReload = !decision.ExactLocalOnly;
        _runtimeState.TrackScrapeQueued(
            jobKey,
            plan.FrontendSystemId,
            plan.GameSlug,
            string.IsNullOrWhiteSpace(plan.DisplayName) ? plan.GameSlug : plan.DisplayName,
            requestedMissingKinds.Count == 0 ? "metadata" : string.Join(",", requestedMissingKinds),
            blocksReload: blocksReload);
        var activityStarted = false;
        try
        {
            _runtimeState.TrackScrapeStarted(jobKey);
            _runtimeState.BeginScrapeActivity(blocksReload: blocksReload);
            activityStarted = true;
            var selectionToken = _runtimeState.CaptureCurrentGameSelection();
            var selectionStillCurrent = IsCapturedSelectionStillCurrent(selectionToken, plan);
            await AuditDecisionAsync(plan, decision, scrapeCancellationToken);
            if (!decision.ExactLocalOnly &&
                !IsThemeHbOnlyScrape(decision) &&
                selectionStillCurrent)
            {
                await NotifyRemoteScrapeStartedAsync(decision, scrapeCancellationToken);
            }

            scrapeCancellationToken.ThrowIfCancellationRequested();
            _settingsService.Invalidate();
            var currentScrapeSettings = _settingsService.GetScrapingSettings();
            decision.Language = currentScrapeSettings.Language;

            var result = await _screenScraperRemoteProvider.ScrapeAsync(
                plan,
                requestedMissingKinds,
                decision.ScreenScraperSystemId,
                decision.Language,
                decision.BezelAspectRatio,
                decision.BezelOrientation,
                decision.ExactLocalOnly ? false : decision.RefreshCurrentGameAfterSuccess,
                scrapeCancellationToken);

            decision.Status = result.Status;
            decision.Message = result.Message;
            decision.ScreenScraperGameId = result.ScreenScraperGameId;
            decision.DownloadedMediaCount = result.DownloadedMediaCount;
            decision.ImportedMediaCount = result.ImportedMediaCount;
            decision.ImportedKinds = result.ImportedKinds
                .Select(MediaKinds.Normalize)
                .Where(kind => !string.IsNullOrWhiteSpace(kind))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            decision.TextUpdated = result.TextUpdated;
            decision.GamelistChanged = result.GamelistChanged;
            decision.MediaContentChanged = result.MediaContentChanged;
            decision.MetadataChanged = result.MetadataChanged;
            decision.LivePushed = result.LivePushed;
            decision.ReloadRequested = result.ReloadRequested;
            if (IsTextOnlyRemoteMetadataCheck(plan, requestedMissingKinds))
            {
                if (IsRemoteTextNoChangeCacheableResult(result))
                {
                    var cooldown = ResolveRemoteTextNoChangeCooldown(scraping);
                    var cacheEntry = RememberRemoteTextNoChange(plan, decision.Language, result.Status, cooldown);
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-text-cooldown",
                        "metadata",
                        "remembered",
                        new
                        {
                            language = decision.Language,
                            cacheEntry.LastCheckedAtUtc,
                            cacheEntry.ExpiresAtUtc,
                            cacheEntry.Status
                        },
                        scrapeCancellationToken);
                }
                else
                {
                    ForgetRemoteTextNoChange(plan, decision.Language);
                }
            }

            var requestedExactLocalKinds = requestedMissingKinds
                .Where(kind => policy.ExactLocalMissingKinds.Contains(MediaKinds.Normalize(kind), StringComparer.OrdinalIgnoreCase))
                .Select(MediaKinds.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requestedExactLocalKinds.Count > 0)
            {
                var unresolvedExactKinds = ResolveExactLocalMissingKinds(plan, ResolveSelectedDisplayKinds(plan))
                    .Intersect(requestedExactLocalKinds, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                ForgetRemoteExactLocalNoRetry(plan, requestedExactLocalKinds);
                if (unresolvedExactKinds.Count > 0 &&
                    IsRemoteExactLocalNoRetryCacheableResult(result))
                {
                    foreach (var unresolvedKind in unresolvedExactKinds)
                    {
                        RememberRemoteExactLocalNoRetry(plan, [unresolvedKind], result.Status);
                    }

                    var cacheEntry = RememberRemoteExactLocalNoRetry(plan, unresolvedExactKinds, result.Status);
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-exact-local-no-retry",
                        "media",
                        "remembered",
                        new
                        {
                            requestedKinds = requestedMissingKinds,
                            unresolvedExactKinds,
                            cacheEntry.LastCheckedAtUtc,
                            cacheEntry.Status,
                            result.ImportedMediaCount,
                            mixedDecision = !decision.ExactLocalOnly
                        },
                        scrapeCancellationToken);
                }
            }

            if (IsMediaOnlyRemoteCheck(plan, requestedMissingKinds))
            {
                if (IsRemoteMediaNoChangeCacheableResult(result))
                {
                    var expiresAtUtc = RememberRemoteMediaNoChange(plan, requestedMissingKinds);
                    foreach (var kind in requestedMissingKinds)
                    {
                        RememberRemoteMediaNoChange(plan, [kind]);
                    }

                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-media-cooldown",
                        "media",
                        "remembered",
                        new
                        {
                            kinds = requestedMissingKinds,
                            expiresAtUtc,
                            result.Status
                        },
                        scrapeCancellationToken);
                }
                else
                {
                    ForgetRemoteMediaNoChange(plan, requestedMissingKinds);
                    foreach (var kind in requestedMissingKinds)
                    {
                        ForgetRemoteMediaNoChange(plan, [kind]);
                    }
                }
            }

            selectionStillCurrent = IsCapturedSelectionStillCurrent(selectionToken, plan);
            var isCurrentGame = selectionStillCurrent;
            var marqueeAutogenResult = await _marqueeAutogenService.GenerateAfterRemoteScrapeAsync(
                plan,
                decision,
                isCurrentGame,
                scrapeCancellationToken);
            if (marqueeAutogenResult.WasGenerated)
            {
                result.Status = "completed";
                result.Message = "ScreenScraper data imported and generated marquee fallback created.";
                result.MediaContentChanged = true;

                decision.Status = result.Status;
                decision.Message = result.Message;
                if (!decision.LivePushed &&
                    !decision.ExactLocalOnly &&
                    decision.RefreshCurrentGameAfterSuccess &&
                    selectionStillCurrent)
                {
                    var autogenLivePushed = await _gamelistUpdateService.PushLiveGameUpdateToEsAsync(
                        plan,
                        scrapeCancellationToken,
                        LiveGameUpdateNotificationKind.RemoteScrape);
                    result.LivePushed = result.LivePushed || autogenLivePushed;
                    decision.LivePushed = result.LivePushed;
                }
            }

            await AuditDecisionAsync(plan, decision, scrapeCancellationToken);
            var heavyMediaNotificationSent = false;
            if (ShouldNotifyHeavyMediaScrape(decision, isCurrentGame))
            {
                await NotifyHeavyMediaScrapeCompletedAsync(decision, scrapeCancellationToken);
                heavyMediaNotificationSent = true;
            }

            if (isCurrentGame && ShouldNotifyThemeHbUnavailable(decision))
            {
                await NotifyThemeHbUnavailableAsync(decision, scrapeCancellationToken);
            }
            else if (isCurrentGame && string.Equals(decision.Status, "provider-error", StringComparison.OrdinalIgnoreCase))
            {
                if (!decision.ExactLocalOnly)
                {
                    await NotifyRemoteScrapeFailedAsync(decision, scrapeCancellationToken);
                }
            }
            else if (isCurrentGame &&
                decision.ExactLocalOnly &&
                decision.ImportedMediaCount > 0 &&
                !heavyMediaNotificationSent &&
                string.Equals(decision.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                await NotifyRemoteScrapeSucceededAsync(decision, scrapeCancellationToken);
            }
            else if (isCurrentGame &&
                !decision.LivePushed &&
                !heavyMediaNotificationSent &&
                string.Equals(decision.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (!decision.ExactLocalOnly || decision.ImportedMediaCount > 0)
                {
                    await NotifyRemoteScrapeSucceededAsync(decision, scrapeCancellationToken);
                }
            }
            else if (isCurrentGame &&
                decision.LivePushed &&
                !heavyMediaNotificationSent &&
                string.Equals(decision.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-notification",
                    "notification",
                    "skipped-after-live-addgames",
                    new
                    {
                        reason = "live-addgames-already-refreshes-current-card",
                        decision.ImportedMediaCount,
                        decision.ImportedKinds
                    },
                    scrapeCancellationToken);
            }

            if (result.RequiresGamelistPersistence)
            {
                if (plan.SuppressImmediateGamelistUpdates)
                {
                    _scrapeQueueService.MarkPendingGamelistPersistence(plan);
                    _gamelistUpdateService.MarkLiveGamelistDirty(plan);
                    var pendingUpdate = await _gamelistUpdateService.StageExtendedEntriesAsync(plan, scrapeCancellationToken);
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-gamelist-persist",
                        "gamelist",
                        pendingUpdate.Changed ? "staged-extended-game-selected" : "staged-extended-unchanged",
                        new
                        {
                            result.MediaContentChanged,
                            result.MetadataChanged,
                            pendingMediaContentChanged = pendingUpdate.MediaContentChanged,
                            pendingMetadataChanged = pendingUpdate.MetadataChanged,
                            afterFinalNotify = true
                        },
                        scrapeCancellationToken);
                }
                else
                {
                    var gamelistUpdate = await _gamelistUpdateService.StageExtendedEntriesAsync(plan, scrapeCancellationToken);
                    decision.GamelistChanged = gamelistUpdate.Changed;
                    decision.MediaContentChanged = decision.MediaContentChanged || gamelistUpdate.MediaContentChanged;
                    decision.MetadataChanged = decision.MetadataChanged || gamelistUpdate.MetadataChanged;
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-gamelist-persist",
                        "gamelist",
                        gamelistUpdate.Changed ? "staged-extended" : "staged-extended-unchanged",
                        new
                        {
                            gamelistUpdate.MediaContentChanged,
                            gamelistUpdate.MetadataChanged,
                            afterFinalNotify = true
                        },
                        scrapeCancellationToken);
                }
            }

            if (result.TextUpdated || result.RequiresGamelistPersistence)
            {
                _runtimeState.RequestLocalizedGamelistCacheRefreshForGame(plan);
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-localized-cache",
                    "gamelist-cache",
                    "queued-entry-refresh",
                    new
                    {
                        plan.FrontendSystemId,
                        plan.GamePath,
                        plan.GameSlug,
                        result.TextUpdated,
                        result.RequiresGamelistPersistence,
                        selectionStillCurrent
                    },
                    scrapeCancellationToken);
            }

            _logger?.LogInformation(
                "Remote scrape completed: provider={Provider}, status={Status}, system={SystemId}, game={GameSlug}, missing={MissingKinds}, downloaded={DownloadedMediaCount}, imported={ImportedMediaCount}, textUpdated={TextUpdated}, livePushed={LivePushed}",
                decision.Provider,
                decision.Status,
                decision.FrontendSystemId,
                decision.GameSlug,
                string.Join(",", decision.MissingKinds),
                decision.DownloadedMediaCount,
                decision.ImportedMediaCount,
                decision.TextUpdated,
                decision.LivePushed);
        }
        catch (OperationCanceledException) when (scrapeInvalidation.CancellationToken.IsCancellationRequested ||
            _runtimeState.IsRemoteScrapeInvalidated(scrapeInvalidation.Version))
        {
            decision.Status = "cancelled-stale-language-sync";
            decision.Message = "Remote scraping was cancelled because a language gamelist synchronization invalidated in-flight scrape results.";
            await AuditDecisionAsync(plan, decision, CancellationToken.None);
            _logger?.LogDebug(
                "Remote scrape cancelled by language gamelist sync: provider={Provider}, system={SystemId}, game={GameSlug}",
                decision.Provider,
                decision.FrontendSystemId,
                decision.GameSlug);
        }
        catch (OperationCanceledException)
        {
            _scrapeQueueService.Enqueue(plan, decision.MissingKinds, "cancelled-live-selection-changed");
            decision.Status = "cancelled";
            decision.Message = "Remote scraping was cancelled because a newer game-selected has priority.";
            await AuditDecisionAsync(plan, decision, CancellationToken.None);
            _logger?.LogDebug(
                "Remote scrape cancelled because a newer game-selected has priority: provider={Provider}, system={SystemId}, game={GameSlug}",
                decision.Provider,
                decision.FrontendSystemId,
                decision.GameSlug);
        }
        finally
        {
            if (activityStarted)
            {
                _runtimeState.EndScrapeActivity(blocksReload: blocksReload);
            }

            _runtimeState.TrackScrapeCompleted(jobKey);
        }

        return decision;
    }

    private void EnqueueDeferredMissingKinds(MediaProjectionPlan plan, RemoteScrapeDecision decision, string reason)
    {
        if (decision.DeferredMissingKinds.Count == 0)
        {
            return;
        }

        _scrapeQueueService.Enqueue(plan, decision.DeferredMissingKinds, reason);
    }

    private bool IsCapturedSelectionStillCurrent(CurrentGameSelectionToken selectionToken, MediaProjectionPlan plan)
    {
        return (_runtimeState.IsCurrentGameSelectionToken(selectionToken, plan.FrontendSystemId, plan.GamePath) ||
                _runtimeState.IsCurrentGameSelectionToken(selectionToken, plan.SystemId, plan.GamePath)) &&
            _gamelistUpdateService.IsCurrentlySelectedGame(plan);
    }

    private async Task NotifyRemoteScrapeStartedAsync(RemoteScrapeDecision decision, CancellationToken cancellationToken)
    {
        await _notificationService.NotifyAsync(ResolveScrapeStartedMessage(decision), cancellationToken);
    }

    private async Task NotifyRemoteScrapeSucceededAsync(RemoteScrapeDecision decision, CancellationToken cancellationToken)
    {
        if (IsThemeHbOnlyScrape(decision) && decision.ImportedMediaCount > 0)
        {
            await _notificationService.NotifyAsync(ResolveThemeHbSucceededMessage(decision), cancellationToken);
            return;
        }

        var mediaPart = decision.ImportedMediaCount > 0
            ? $"{decision.ImportedMediaCount} {_interfaceTextService.Text(decision.ImportedMediaCount > 1 ? "term.media.many" : "term.media.one", decision.Language)}"
            : _interfaceTextService.Text("term.texts", decision.Language);
        await _notificationService.NotifyAsync(ResolveScrapeSucceededMessage(decision, mediaPart), cancellationToken);
    }

    private async Task NotifyRemoteScrapeFailedAsync(RemoteScrapeDecision decision, CancellationToken cancellationToken)
    {
        await _notificationService.NotifyAsync(ResolveScrapeFailedMessage(decision), cancellationToken);
    }

    private async Task NotifyHeavyMediaScrapeCompletedAsync(RemoteScrapeDecision decision, CancellationToken cancellationToken)
    {
        await _notificationService.NotifyAsync(ResolveHeavyMediaScrapeCompletedMessage(decision), cancellationToken);
    }

    private async Task NotifyThemeHbUnavailableAsync(RemoteScrapeDecision decision, CancellationToken cancellationToken)
    {
        await _notificationService.NotifyAsync(ResolveThemeHbUnavailableMessage(decision), cancellationToken);
    }

    private string ResolveScrapeStartedMessage(RemoteScrapeDecision decision)
    {
        var gameName = ResolveDisplayName(decision);
        if (IsThemeHbOnlyScrape(decision))
        {
            return _interfaceTextService.Format(
                "notification.scrape.theme_started",
                decision.Language,
                ("game", gameName));
        }

        var mode = string.IsNullOrWhiteSpace(decision.WorkflowMode) ? "FD" : decision.WorkflowMode;
        var details = ResolveScrapeRequestedDetails(decision);
        return _interfaceTextService.Format(
            "notification.scrape.started",
            decision.Language,
            ("mode", mode),
            ("details", details),
            ("game", gameName));
    }

    private string ResolveScrapeSucceededMessage(RemoteScrapeDecision decision, string mediaPart)
    {
        var gameName = ResolveDisplayName(decision);
        var mode = string.IsNullOrWhiteSpace(decision.WorkflowMode) ? "FD" : decision.WorkflowMode;
        return _interfaceTextService.Format(
            "notification.scrape.completed",
            decision.Language,
            ("mode", mode),
            ("game", gameName),
            ("details", mediaPart));
    }

    private string ResolveScrapeFailedMessage(RemoteScrapeDecision decision)
    {
        var gameName = ResolveDisplayName(decision);
        return _interfaceTextService.Format(
            "notification.scrape.provider_error",
            decision.Language,
            ("game", gameName));
    }

    private string ResolveHeavyMediaScrapeCompletedMessage(RemoteScrapeDecision decision)
    {
        var gameName = ResolveDisplayName(decision);
        var mediaPart = string.Join(", ", ResolveHeavyImportedKindLabels(decision));
        if (string.IsNullOrWhiteSpace(mediaPart))
        {
            mediaPart = _interfaceTextService.Text("term.media.one", decision.Language);
        }

        return _interfaceTextService.Format(
            "notification.scrape.heavy_completed",
            decision.Language,
            ("game", gameName),
            ("details", mediaPart));
    }

    private bool ShouldNotifyHeavyMediaScrape(RemoteScrapeDecision decision, bool isCurrentGame)
    {
        if (!_runtimeOptions.ShouldNotifyHeavyMediaScrape() ||
            !ShouldNotifyHeavyMediaScrapeStatic(decision) ||
            !(isCurrentGame || decision.ImportedKinds.Any(IsVideoKind)))
        {
            return false;
        }

        return !HasLiveVideoHeavyMediaNotificationDuplicate(decision);
    }

    private static bool ShouldNotifyHeavyMediaScrapeStatic(RemoteScrapeDecision decision)
    {
        return string.Equals(decision.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
            decision.ImportedKinds.Any(IsHeavyMediaKind);
    }

    private static bool HasLiveVideoHeavyMediaNotificationDuplicate(RemoteScrapeDecision decision)
    {
        if (!decision.LivePushed || !decision.ImportedKinds.Any(IsVideoKind))
        {
            return false;
        }

        var heavyKinds = decision.ImportedKinds
            .Where(IsHeavyMediaKind)
            .Select(MediaKinds.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return heavyKinds.Count > 0 && heavyKinds.All(IsVideoKind);
    }

    private IEnumerable<string> ResolveHeavyImportedKindLabels(RemoteScrapeDecision decision)
    {
        return decision.ImportedKinds
            .Where(IsHeavyMediaKind)
            .Select(kind => ResolveHeavyMediaKindLabel(kind, decision.Language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase);
    }

    private string ResolveHeavyMediaKindLabel(string kind, string? language)
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
            : _interfaceTextService.Text(key, language);
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

    private string ResolveScrapeRequestedDetails(RemoteScrapeDecision decision)
    {
        var labels = decision.MissingKinds
            .Select(kind => ResolveMediaKindLabel(kind, decision.Language))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (decision.NeedsDescription)
        {
            labels.Add(_interfaceTextService.Text("term.texts", decision.Language));
        }

        return labels.Count == 0
            ? _interfaceTextService.Text("term.texts", decision.Language)
            : string.Join(", ", labels.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private string ResolveMediaKindLabel(string kind, string? language)
    {
        var key = MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Image => "label.media.image",
            MediaKinds.Thumbnail => "label.media.thumbnail",
            MediaKinds.Logo or MediaKinds.Wheel or MediaKinds.WheelCarbon or MediaKinds.WheelSteel => "label.media.logo",
            MediaKinds.Marquee => "label.media.marquee",
            MediaKinds.BoxFront => "label.media.boxfront",
            MediaKinds.Box3d => "label.media.box3d",
            MediaKinds.BoxBack => "label.media.boxback",
            MediaKinds.BoxSide => "label.media.boxside",
            MediaKinds.BoxTexture => "label.media.boxtexture",
            MediaKinds.Cartridge => "label.media.cartridge",
            MediaKinds.Label => "label.media.label",
            MediaKinds.Fanart => "label.media.fanart",
            MediaKinds.Flyer => "label.media.flyer",
            MediaKinds.Figurine => "label.media.figurine",
            MediaKinds.Bezel => "label.media.bezel",
            MediaKinds.Map => "label.media.map",
            MediaKinds.Manual => "label.media.manual",
            MediaKinds.Magazine => "label.media.magazine",
            MediaKinds.Video => "label.media.video",
            MediaKinds.VideoNormalized => "label.media.video_normalized",
            MediaKinds.ThemeHb => "label.media.themehb",
            MediaKinds.ScreenMarquee => "label.media.screenmarquee",
            MediaKinds.ScreenMarqueeSmall => "label.media.screenmarqueesmall",
            MediaKinds.SteamGrid => "label.media.steamgrid",
            MediaKinds.MixRbv1 => "label.media.mixrbv1",
            MediaKinds.MixRbv2 => "label.media.mixrbv2",
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(key)
            ? MediaKinds.Normalize(kind)
            : _interfaceTextService.Text(key, language);
    }

    private string ResolveThemeHbSucceededMessage(RemoteScrapeDecision decision)
    {
        var gameName = ResolveDisplayName(decision);
        return _interfaceTextService.Format(
            "notification.scrape.theme_completed",
            decision.Language,
            ("game", gameName));
    }

    private string ResolveThemeHbUnavailableMessage(RemoteScrapeDecision decision)
    {
        var gameName = ResolveDisplayName(decision);
        return _interfaceTextService.Format(
            "notification.scrape.theme_unavailable",
            decision.Language,
            ("game", gameName));
    }

    private static bool ShouldNotifyThemeHbUnavailable(RemoteScrapeDecision decision)
    {
        return IsThemeHbOnlyScrape(decision) &&
            decision.DownloadedMediaCount == 0 &&
            decision.ImportedMediaCount == 0 &&
            !string.Equals(decision.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsThemeHbOnlyScrape(RemoteScrapeDecision decision)
    {
        return decision.MissingKinds.Count > 0 &&
            decision.MissingKinds.All(kind =>
                string.Equals(MediaKinds.Normalize(kind), MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveDisplayName(RemoteScrapeDecision decision)
    {
        var displayName = !string.IsNullOrWhiteSpace(decision.DisplayName)
            ? decision.DisplayName
            : decision.GameSlug;
        return EsNotificationText.ShortGameName(displayName);
    }

    private static string ResolveWorkflowMode(MediaProjectionPlan plan, IReadOnlyList<string> requestedMissingKinds)
    {
        if (requestedMissingKinds.Count == 0)
        {
            return "FL";
        }

        var hasLocalContribution = plan.Needs.Any(need =>
            need.WasImported ||
            need.WasProjected ||
            need.WasContentChanged ||
            !string.IsNullOrWhiteSpace(need.ExistingPath) ||
            !string.IsNullOrWhiteSpace(need.ProjectedPath));
        return hasLocalContribution ? "H" : "FD";
    }

    private static async Task AuditDecisionAsync(
        MediaProjectionPlan plan,
        RemoteScrapeDecision decision,
        CancellationToken cancellationToken)
    {
        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "remote-scrape-decision",
            decision.Provider,
            decision.Status,
            new
            {
            decision.Message,
            decision.Enabled,
            decision.MissingKinds,
            decision.ExactLocalMissingKinds,
            decision.ExactLocalOnly,
            decision.DeferredMissingKinds,
            decision.ExcludedKinds,
                decision.NeedsDescription,
                decision.ScreenScraperSystemId,
                decision.ScreenScraperGameId,
                decision.DownloadedMediaCount,
                decision.ImportedMediaCount,
                decision.TextUpdated,
                decision.GamelistChanged,
                decision.MediaContentChanged,
                decision.MetadataChanged,
                decision.LivePushed,
                decision.ReloadRequested
            },
            cancellationToken);
    }

    public RemoteScrapingStatus GetStatus()
    {
        var options = _options.CurrentValue;
        var scraping = BuildRuntimeScrapingOptions(options.Scraping);
        var settings = _settingsService.GetScrapingSettings();
        var screenScraperConnection = _screenScraperConnectionService.Resolve();
        return new RemoteScrapingStatus
        {
            AutoScrapingEnabled = scraping.AutoScrapingEnabled,
            Provider = string.IsNullOrWhiteSpace(scraping.RemoteProvider)
                ? "screenscraper"
                : scraping.RemoteProvider,
            ScreenScraperEnabled = scraping.ScreenScraperEnabled,
            ScrapeQueueEnabled = scraping.ScrapeQueueEnabled,
            LocalFirst = scraping.RemoteAfterLocalOnly,
            RefreshCurrentGameAfterSuccess = scraping.RefreshCurrentGameAfterRemoteSuccess,
            MarqueeScrapingEnabled = scraping.MarqueeScrapingEnabled,
            ScreenMarqueeScrapingEnabled = scraping.ScreenMarqueeScrapingEnabled,
            ScreenMarqueeSmallScrapingEnabled = scraping.ScreenMarqueeSmallScrapingEnabled,
            SteamGridScrapingEnabled = scraping.SteamGridScrapingEnabled,
            MixScrapingEnabled = scraping.MixScrapingEnabled,
            MapScrapingEnabled = scraping.MapScrapingEnabled,
            ManualScrapingEnabled = scraping.ManualScrapingEnabled,
            MagazineScrapingEnabled = scraping.MagazineScrapingEnabled,
            VideoScrapingEnabled = scraping.VideoScrapingEnabled,
            VideoNormalizedScrapingEnabled = scraping.VideoNormalizedScrapingEnabled,
            BezelScrapingEnabled = scraping.BezelScrapingEnabled,
            BezelAspectRatio = NormalizeBezelAspectRatio(scraping.BezelAspectRatio),
            BezelOrientation = NormalizeBezelOrientation(scraping.BezelOrientation),
            HasScreenScraperCredentials =
                !string.IsNullOrWhiteSpace(settings.ScreenScraperUser) &&
                !string.IsNullOrWhiteSpace(settings.ScreenScraperPassword),
            HasScreenScraperDeveloperCredentials = screenScraperConnection.HasDeveloperCredentials,
            ScreenScraperDeveloperCredentialSource = screenScraperConnection.DeveloperCredentialSource,
            ScreenScraperBaseUrl = screenScraperConnection.BaseUrl,
            ScreenScraperSoftName = screenScraperConnection.SoftName,
            ScreenScraperCatalogEndpoint = "jeuInfos.php",
            ScreenScraperImageEndpoint = "mediaJeu.php",
            ScreenScraperVideoEndpoint = "mediaVideoJeu.php",
            ScreenScraperManualEndpoint = "mediaManuelJeu.php",
            ScreenScraperCapabilities = _screenScraperCapabilityService.GetSnapshot(),
            Workflow = "local-media -> missing media/text audit -> remote provider -> canonical media base -> addgames live with canonical relative links and rating normalized as 0.0 -> extended gamelist staged -> exposed at startup/reloadgames"
        };
    }

    private ApiExposeOptions.ScrapingOptions BuildRuntimeScrapingOptions(ApiExposeOptions.ScrapingOptions fallback)
    {
        return new ApiExposeOptions.ScrapingOptions
        {
            AutoScrapingEnabled = _runtimeOptions.IsAutoScrapingEnabled(),
            RemoteProvider = _runtimeOptions.GetRemoteScrapingProvider(),
            ScreenScraperEnabled = _runtimeOptions.IsScreenScraperProviderEnabled(),
            ScrapeQueueEnabled = _runtimeOptions.IsRemoteScrapeQueueEnabled(),
            RemoteAfterLocalOnly = _runtimeOptions.IsRemoteScrapingAfterLocalOnly(),
            ExactLocalMediaScrapingEnabled = _runtimeOptions.IsExactLocalMediaScrapingEnabled(),
            RefreshCurrentGameAfterRemoteSuccess = _runtimeOptions.ShouldRefreshCurrentAfterRemoteScrapeSuccess(),
            MapScrapingEnabled = _runtimeOptions.IsRemoteMapScrapingEnabled(),
            ManualScrapingEnabled = _runtimeOptions.IsRemoteManualScrapingEnabled(),
            MagazineScrapingEnabled = _runtimeOptions.IsRemoteMagazineScrapingEnabled(),
            VideoScrapingEnabled = _runtimeOptions.IsRemoteVideoScrapingEnabled(),
            VideoNormalizedScrapingEnabled = _runtimeOptions.IsRemoteVideoNormalizedScrapingEnabled(),
            BezelScrapingEnabled = _runtimeOptions.IsRemoteBezelScrapingEnabled(),
            BezelAspectRatio = _runtimeOptions.GetRemoteBezelAspectRatio(),
            BezelOrientation = _runtimeOptions.GetRemoteBezelOrientation(),
            ResumePendingScrapesOnStartup = fallback.ResumePendingScrapesOnStartup,
            BootstrapDefaultPlaceholdersOnStartup = fallback.BootstrapDefaultPlaceholdersOnStartup,
            LiveEsMetadataPushEnabled = fallback.LiveEsMetadataPushEnabled,
            LiveEsMediaPushEnabled = fallback.LiveEsMediaPushEnabled,
            LiveEsMediaPushDelayMs = fallback.LiveEsMediaPushDelayMs,
            RemoteTextNoChangeCooldownMinutes = fallback.RemoteTextNoChangeCooldownMinutes,
            ProjectedMediaIndexCacheEnabled = fallback.ProjectedMediaIndexCacheEnabled,
            MarqueeScrapingEnabled = _runtimeOptions.IsRemoteMarqueeScrapingEnabled(),
            ScreenMarqueeScrapingEnabled = _runtimeOptions.IsRemoteScreenMarqueeScrapingEnabled(),
            ScreenMarqueeSmallScrapingEnabled = _runtimeOptions.IsRemoteScreenMarqueeSmallScrapingEnabled(),
            SteamGridScrapingEnabled = _runtimeOptions.IsRemoteSteamGridScrapingEnabled(),
            MixScrapingEnabled = _runtimeOptions.IsRemoteMixScrapingEnabled(),
            ThemeHbScrapingEnabled = _runtimeOptions.IsHyperBatThemeInstallEnabled()
        };
    }

    private RemoteScrapePolicy ResolveRemotePolicy(
        MediaProjectionPlan plan,
        ApiExposeOptions.ScrapingOptions scraping)
    {
        var missingKinds = plan.Needs
            .Where(need => need.IsMissing)
            .Where(need => string.IsNullOrWhiteSpace(need.ExistingPath))
            .Where(need => string.IsNullOrWhiteSpace(need.ProjectedPath) || !File.Exists(need.ProjectedPath))
            .Select(need => MediaKinds.Normalize(need.Kind))
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedDisplayKinds = ResolveSelectedDisplayKinds(plan);
        var exactLocalMissingKinds = scraping.ExactLocalMediaScrapingEnabled
            ? ResolveExactLocalMissingKinds(plan, selectedDisplayKinds)
            : [];
        var requestedKinds = missingKinds
            .Concat(exactLocalMissingKinds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowed = new List<string>();
        var excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in requestedKinds.OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase))
        {
            var reason = GetRemoteExclusionReason(kind, scraping, selectedDisplayKinds);
            if (string.IsNullOrWhiteSpace(reason))
            {
                allowed.Add(kind);
            }
            else
            {
                excluded[kind] = reason;
            }
        }

        return new RemoteScrapePolicy(allowed, excluded, exactLocalMissingKinds);
    }

    private List<string> ResolveExactLocalMissingKinds(
        MediaProjectionPlan plan,
        IReadOnlySet<string> selectedDisplayKinds)
    {
        if (selectedDisplayKinds.Count == 0)
        {
            return [];
        }

        var mediaIndex = _localMediaIndexService.Build([plan.SystemId]);
        var exactMissing = new List<string>();
        foreach (var kind in selectedDisplayKinds.OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedKind = MediaKinds.Normalize(kind);
            var need = plan.Needs.FirstOrDefault(entry =>
                string.Equals(MediaKinds.Normalize(entry.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase));
            if (need == null ||
                (need.IsMissing && string.IsNullOrWhiteSpace(need.ExistingPath) && string.IsNullOrWhiteSpace(need.ImportedPath)))
            {
                continue;
            }

            var preferredRegion = _mediaLocalizationResolver
                .BuildMediaRegionPriority(plan, normalizedKind)
                .FirstOrDefault(region => !string.IsNullOrWhiteSpace(region) &&
                    !string.Equals(region, "wor", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(preferredRegion))
            {
                continue;
            }

            if (!HasExactRegionalCandidate(mediaIndex, plan, ResolveLocalIndexKind(normalizedKind), preferredRegion))
            {
                exactMissing.Add(normalizedKind);
            }
        }

        return exactMissing
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasExactRegionalCandidate(
        LocalMediaIndex mediaIndex,
        MediaProjectionPlan plan,
        string kind,
        string preferredRegion)
    {
        var gameSlug = NormalizeSlug(plan.GameSlug);
        var familySlug = NormalizeSlug(plan.ProjectionBaseName);
        if (string.IsNullOrWhiteSpace(familySlug))
        {
            familySlug = gameSlug;
        }

        return mediaIndex.GetCandidates(plan.SystemId, kind)
            .Any(candidate =>
                string.Equals(candidate.Region, preferredRegion, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(candidate.GameSlug, gameSlug, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.FamilySlug, familySlug, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ResolveLocalIndexKind(string kind)
    {
        var normalized = MediaKinds.Normalize(kind);
        return string.Equals(normalized, MediaKinds.Logo, StringComparison.OrdinalIgnoreCase)
            ? MediaKinds.Wheel
            : normalized;
    }

    private static string NormalizeSlug(string? value)
    {
        var fileName = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        var chars = fileName
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return string.Join(
                " ",
                new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Replace(' ', '_');
    }

    private static string GetRemoteExclusionReason(
        string kind,
        ApiExposeOptions.ScrapingOptions scraping,
        IReadOnlySet<string> selectedDisplayKinds)
    {
        var normalizedKind = MediaKinds.Normalize(kind);
        var isSelectedDisplayKind = selectedDisplayKinds.Contains(normalizedKind);
        return MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Marquee when !scraping.MarqueeScrapingEnabled && !isSelectedDisplayKind =>
                "marquees are disabled by APIExpose remote policy",
            MediaKinds.ScreenMarquee when !scraping.ScreenMarqueeScrapingEnabled && !isSelectedDisplayKind =>
                "screen marquees are disabled by APIExpose remote policy",
            MediaKinds.ScreenMarqueeSmall when !scraping.ScreenMarqueeSmallScrapingEnabled && !isSelectedDisplayKind =>
                "small screen marquees are disabled by APIExpose remote policy",
            MediaKinds.SteamGrid when !scraping.SteamGridScrapingEnabled && !isSelectedDisplayKind =>
                "steamgrid is disabled by APIExpose remote policy",
            MediaKinds.MixRbv1 or MediaKinds.MixRbv2 when !scraping.MixScrapingEnabled && !isSelectedDisplayKind =>
                "mix medias are disabled by APIExpose remote policy",
            MediaKinds.Map when !scraping.MapScrapingEnabled =>
                "maps are disabled by APIExpose remote policy because they are optional document files",
            MediaKinds.Manual when !scraping.ManualScrapingEnabled =>
                "manuals are disabled by APIExpose remote policy because they are large files",
            MediaKinds.Magazine when !scraping.MagazineScrapingEnabled =>
                "magazines are disabled by APIExpose remote policy because they are optional document files",
            MediaKinds.Video when !scraping.VideoScrapingEnabled =>
                "videos are disabled by APIExpose remote policy because they are large files",
            MediaKinds.VideoNormalized when !scraping.VideoNormalizedScrapingEnabled =>
                "normalized videos are disabled by APIExpose remote policy because they are large files",
            MediaKinds.Bezel when !scraping.BezelScrapingEnabled =>
                "bezels are disabled by APIExpose remote policy",
            MediaKinds.ThemeHb when !scraping.ThemeHbScrapingEnabled =>
                "themehb is disabled by Themes Manager",
            _ => string.Empty
        };
    }

    private static HashSet<string> ResolveSelectedDisplayKinds(MediaProjectionPlan plan)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSelectionKind(selected, plan.PreferredImageSource, MediaSelectionTarget.Image);
        AddSelectionKind(selected, plan.PreferredLogoSource, MediaSelectionTarget.Logo);
        AddSelectionKind(selected, plan.PreferredThumbnailSource, MediaSelectionTarget.Thumbnail);
        return selected;
    }

    private static string NormalizeBezelAspectRatio(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "4:3" or "4-3" or "43" => "4-3",
            "16:9" or "16-9" or "169" or "" => "16-9",
            _ => "16-9"
        };
    }

    private static string NormalizeBezelOrientation(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "vertical" or "v" => "vertical",
            "cocktail" or "table" => "cocktail",
            "match-cabinet" or "auto" or "from-cabinet" => "match_cabinet",
            "horizontal" or "h" or "" => "horizontal",
            _ => "horizontal"
        };
    }

    private static List<string> ResolveLiveRelevantMissingKinds(MediaProjectionPlan plan, IReadOnlyList<string> missingKinds, bool isHyperBatThemeActive)
    {
        var liveKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MediaKinds.Image,
            MediaKinds.Logo,
            MediaKinds.Thumbnail,
            MediaKinds.Fanart,
            MediaKinds.WheelCarbon,
            MediaKinds.WheelSteel
        };

        if (isHyperBatThemeActive)
        {
            liveKinds.Add(MediaKinds.ThemeHb);
        }

        AddSelectionKind(liveKinds, plan.PreferredImageSource, MediaSelectionTarget.Image);
        AddSelectionKind(liveKinds, plan.PreferredLogoSource, MediaSelectionTarget.Logo);
        AddSelectionKind(liveKinds, plan.PreferredThumbnailSource, MediaSelectionTarget.Thumbnail);

        return missingKinds
            .Select(MediaKinds.Normalize)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Where(kind => liveKinds.Contains(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> PrioritizeThemeHbWhenHyperBatIsActive(IReadOnlyList<string> kinds, bool isHyperBatThemeActive)
    {
        if (!isHyperBatThemeActive ||
            !kinds.Contains(MediaKinds.ThemeHb, StringComparer.OrdinalIgnoreCase))
        {
            return kinds.ToList();
        }

        return kinds
            .OrderBy(kind => string.Equals(MediaKinds.Normalize(kind), MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }

    private static List<string> FilterAlreadyTriedRemoteKinds(
        MediaProjectionPlan plan,
        IReadOnlyList<string> requestedKinds,
        IReadOnlyList<string> exactLocalMissingKinds,
        IDictionary<string, string> skippedKinds)
    {
        var filtered = new List<string>();
        foreach (var kind in requestedKinds
            .Select(MediaKinds.Normalize)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (exactLocalMissingKinds.Contains(kind, StringComparer.OrdinalIgnoreCase) &&
                TryGetRemoteExactLocalNoRetryForKind(plan, kind, out _))
            {
                skippedKinds[kind] = "exact local media already checked without a usable regional update";
                continue;
            }

            if (TryGetRemoteMediaNoChangeCooldown(plan, [kind], out _))
            {
                skippedKinds[kind] = "remote media already checked recently without a usable update";
                continue;
            }

            filtered.Add(kind);
        }

        return filtered;
    }

    private static void AddSelectionKind(ISet<string> kinds, string source, MediaSelectionTarget target)
    {
        var kind = ResolveSelectionSourceToKind(source, target);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            kinds.Add(kind);
        }
    }

    private static string ResolveSelectionSourceToKind(string source, MediaSelectionTarget target)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "sstitle" or "title" => MediaKinds.Image,
            "ss" or "screenshot" or "thumb" or "thumbnail" => MediaKinds.Thumbnail,
            "logo" => MediaKinds.Logo,
            "wheel-hd" => target == MediaSelectionTarget.Logo ? MediaKinds.WheelCarbon : MediaKinds.WheelCarbon,
            "wheel" => MediaKinds.Wheel,
            "wheel-carbon" or "wheelcarbon" => MediaKinds.WheelCarbon,
            "wheel-steel" or "wheelsteel" => MediaKinds.WheelSteel,
            "marquee" => MediaKinds.Marquee,
            "screenmarquee" or "screen-marquee" => MediaKinds.ScreenMarquee,
            "screenmarqueesmall" or "screen-marquee-small" => MediaKinds.ScreenMarqueeSmall,
            "boxback" or "box-back" => MediaKinds.BoxBack,
            "box-2d" or "box2d" => MediaKinds.BoxFront,
            "box-3d" or "box3d" => MediaKinds.Box3d,
            "cartridge" or "cart" or "support-2d" or "support2d" => MediaKinds.Cartridge,
            "label" or "support-texture" or "supporttexture" => MediaKinds.Label,
            "figurine" => MediaKinds.Figurine,
            "fanart" => MediaKinds.Fanart,
            "steamgrid" => MediaKinds.SteamGrid,
            "mix" or "mixrbv2" => MediaKinds.MixRbv2,
            "mixrbv1" => MediaKinds.MixRbv1,
            _ => string.Empty
        };
    }

    private static bool IsTextOnlyRemoteMetadataCheck(MediaProjectionPlan plan, IReadOnlyList<string> requestedMissingKinds)
    {
        return plan.NeedsDescriptionScrape && requestedMissingKinds.Count == 0;
    }

    private static bool IsMediaOnlyRemoteCheck(MediaProjectionPlan plan, IReadOnlyList<string> requestedMissingKinds)
    {
        return !plan.NeedsDescriptionScrape && requestedMissingKinds.Count > 0;
    }

    private static bool IsExactLocalOnlyRemoteCheck(
        MediaProjectionPlan plan,
        RemoteScrapePolicy policy,
        IReadOnlyList<string> requestedMissingKinds)
    {
        return !plan.NeedsDescriptionScrape &&
            requestedMissingKinds.Count > 0 &&
            requestedMissingKinds.All(kind =>
                policy.ExactLocalMissingKinds.Contains(MediaKinds.Normalize(kind), StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsRemoteTextNoChangeCacheableResult(ScreenScraperRemoteScrapeResult result)
    {
        return !result.TextUpdated &&
            result.ImportedMediaCount == 0 &&
            result.Status is "no-change" or "not-found";
    }

    private static bool IsRemoteMediaNoChangeCacheableResult(ScreenScraperRemoteScrapeResult result)
    {
        return !result.TextUpdated &&
            result.ImportedMediaCount == 0 &&
            result.Status is "no-change" or "not-found";
    }

    private static bool IsRemoteExactLocalNoRetryCacheableResult(ScreenScraperRemoteScrapeResult result)
    {
        return result.Status is "completed" or "no-change" or "not-found";
    }

    private static TimeSpan ResolveRemoteTextNoChangeCooldown(ApiExposeOptions.ScrapingOptions scraping)
    {
        var minutes = scraping.RemoteTextNoChangeCooldownMinutes <= 0
            ? 720
            : scraping.RemoteTextNoChangeCooldownMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 10080));
    }

    private static bool TryGetRemoteTextNoChangeCooldown(
        MediaProjectionPlan plan,
        string language,
        out RemoteTextNoChangeCacheEntry entry)
    {
        EnsureRemoteTextNoChangeCacheLoaded();
        var key = BuildRemoteTextNoChangeCacheKey(plan, language);
        if (RemoteTextNoChangeCooldowns.TryGetValue(key, out var cachedEntry) &&
            cachedEntry.ExpiresAtUtc > DateTime.UtcNow)
        {
            entry = cachedEntry;
            return true;
        }

        if (cachedEntry != null)
        {
            RemoteTextNoChangeCooldowns.TryRemove(key, out _);
            SaveRemoteTextNoChangeCache();
        }

        entry = null!;
        return false;
    }

    private static RemoteTextNoChangeCacheEntry RememberRemoteTextNoChange(
        MediaProjectionPlan plan,
        string language,
        string status,
        TimeSpan cooldown)
    {
        EnsureRemoteTextNoChangeCacheLoaded();
        var now = DateTime.UtcNow;
        var entry = new RemoteTextNoChangeCacheEntry(
            BuildRemoteTextNoChangeCacheKey(plan, language),
            plan.SystemId,
            plan.FrontendSystemId,
            plan.GameSlug,
            string.IsNullOrWhiteSpace(plan.TextSourceGameSlug) ? plan.GameSlug : plan.TextSourceGameSlug,
            NormalizeRemoteTextLanguage(language),
            now,
            now.Add(cooldown),
            string.IsNullOrWhiteSpace(status) ? "no-change" : status.Trim());
        RemoteTextNoChangeCooldowns[entry.Key] = entry;
        SaveRemoteTextNoChangeCache();
        return entry;
    }

    private static void ForgetRemoteTextNoChange(MediaProjectionPlan plan, string language)
    {
        EnsureRemoteTextNoChangeCacheLoaded();
        if (RemoteTextNoChangeCooldowns.TryRemove(BuildRemoteTextNoChangeCacheKey(plan, language), out _))
        {
            SaveRemoteTextNoChangeCache();
        }
    }

    private static bool TryGetRemoteMediaNoChangeCooldown(
        MediaProjectionPlan plan,
        IReadOnlyList<string> kinds,
        out DateTime expiresAtUtc)
    {
        var key = BuildRemoteMediaNoChangeCacheKey(plan, kinds);
        if (RemoteMediaNoChangeCooldowns.TryGetValue(key, out var cachedExpiresAtUtc) &&
            cachedExpiresAtUtc > DateTime.UtcNow)
        {
            expiresAtUtc = cachedExpiresAtUtc;
            return true;
        }

        if (cachedExpiresAtUtc != default)
        {
            RemoteMediaNoChangeCooldowns.TryRemove(key, out _);
        }

        expiresAtUtc = default;
        return false;
    }

    private static DateTime RememberRemoteMediaNoChange(MediaProjectionPlan plan, IReadOnlyList<string> kinds)
    {
        var expiresAtUtc = DateTime.UtcNow.Add(RemoteMediaNoChangeCooldown);
        RemoteMediaNoChangeCooldowns[BuildRemoteMediaNoChangeCacheKey(plan, kinds)] = expiresAtUtc;
        return expiresAtUtc;
    }

    private static void ForgetRemoteMediaNoChange(MediaProjectionPlan plan, IReadOnlyList<string> kinds)
    {
        RemoteMediaNoChangeCooldowns.TryRemove(BuildRemoteMediaNoChangeCacheKey(plan, kinds), out _);
    }

    private static string BuildRemoteMediaNoChangeCacheKey(MediaProjectionPlan plan, IReadOnlyList<string> kinds)
    {
        return string.Join(
            "|",
            NormalizeCacheToken(plan.FrontendSystemId),
            NormalizeCacheToken(plan.GamePath),
            string.Join(",", kinds
                .Select(MediaKinds.Normalize)
                .Where(kind => !string.IsNullOrWhiteSpace(kind))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)));
    }

    // The exact-local no-retry cache historically never expired: media published
    // later on ScreenScraper could never be retried. Entries older than the TTL
    // are treated as absent and purged (TTL <= 0 restores the historical
    // never-expire behavior). Synced from options on every remote evaluation.
    private static volatile int RemoteExactLocalNoRetryTtlDays = 14;

    private static bool IsExpiredRemoteExactLocalNoRetryEntry(RemoteExactLocalNoRetryCacheEntry entry)
    {
        return RemoteExactLocalNoRetryTtlDays > 0 &&
            DateTime.UtcNow - entry.LastCheckedAtUtc > TimeSpan.FromDays(RemoteExactLocalNoRetryTtlDays);
    }

    private static bool TryGetRemoteExactLocalNoRetry(
        MediaProjectionPlan plan,
        IReadOnlyList<string> kinds,
        out RemoteExactLocalNoRetryCacheEntry entry)
    {
        EnsureRemoteExactLocalNoRetryCacheLoaded();
        if (RemoteExactLocalNoRetryEntries.TryGetValue(BuildRemoteMediaNoChangeCacheKey(plan, kinds), out entry!))
        {
            if (!IsExpiredRemoteExactLocalNoRetryEntry(entry))
            {
                return true;
            }

            RemoteExactLocalNoRetryEntries.TryRemove(entry.Key, out _);
            SaveRemoteExactLocalNoRetryCache();
        }

        entry = null!;
        return false;
    }

    private static bool TryGetRemoteExactLocalNoRetryForKind(
        MediaProjectionPlan plan,
        string kind,
        out RemoteExactLocalNoRetryCacheEntry entry)
    {
        EnsureRemoteExactLocalNoRetryCacheLoaded();
        var normalizedKind = MediaKinds.Normalize(kind);
        if (RemoteExactLocalNoRetryEntries.TryGetValue(BuildRemoteMediaNoChangeCacheKey(plan, [normalizedKind]), out entry!))
        {
            if (!IsExpiredRemoteExactLocalNoRetryEntry(entry))
            {
                return true;
            }

            RemoteExactLocalNoRetryEntries.TryRemove(entry.Key, out _);
            SaveRemoteExactLocalNoRetryCache();
        }

        var frontendSystemId = NormalizeCacheToken(plan.FrontendSystemId);
        var gamePath = NormalizeCacheToken(plan.GamePath);
        entry = RemoteExactLocalNoRetryEntries.Values.FirstOrDefault(candidate =>
            string.Equals(NormalizeCacheToken(candidate.FrontendSystemId), frontendSystemId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeCacheToken(candidate.GamePath), gamePath, StringComparison.OrdinalIgnoreCase) &&
            candidate.Kinds.Contains(normalizedKind, StringComparer.OrdinalIgnoreCase) &&
            !IsExpiredRemoteExactLocalNoRetryEntry(candidate))!;
        return entry != null;
    }

    private static RemoteExactLocalNoRetryCacheEntry RememberRemoteExactLocalNoRetry(
        MediaProjectionPlan plan,
        IReadOnlyList<string> kinds,
        string status)
    {
        EnsureRemoteExactLocalNoRetryCacheLoaded();
        var now = DateTime.UtcNow;
        var normalizedKinds = kinds
            .Select(MediaKinds.Normalize)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var entry = new RemoteExactLocalNoRetryCacheEntry(
            BuildRemoteMediaNoChangeCacheKey(plan, normalizedKinds),
            plan.SystemId,
            plan.FrontendSystemId,
            plan.GameSlug,
            plan.GamePath,
            normalizedKinds,
            now,
            string.IsNullOrWhiteSpace(status) ? "no-change" : status.Trim());
        RemoteExactLocalNoRetryEntries[entry.Key] = entry;
        SaveRemoteExactLocalNoRetryCache();
        return entry;
    }

    private static void ForgetRemoteExactLocalNoRetry(MediaProjectionPlan plan, IReadOnlyList<string> kinds)
    {
        EnsureRemoteExactLocalNoRetryCacheLoaded();
        var changed = RemoteExactLocalNoRetryEntries.TryRemove(BuildRemoteMediaNoChangeCacheKey(plan, kinds), out _);
        foreach (var kind in kinds
            .Select(MediaKinds.Normalize)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            changed = RemoteExactLocalNoRetryEntries.TryRemove(BuildRemoteMediaNoChangeCacheKey(plan, [kind]), out _) || changed;
        }

        if (changed)
        {
            SaveRemoteExactLocalNoRetryCache();
        }
    }

    private static string BuildRemoteTextNoChangeCacheKey(MediaProjectionPlan plan, string language)
    {
        return string.Join(
            "|",
            NormalizeCacheToken(plan.FrontendSystemId),
            NormalizeCacheToken(string.IsNullOrWhiteSpace(plan.TextSourceGameSlug) ? plan.GameSlug : plan.TextSourceGameSlug),
            NormalizeRemoteTextLanguage(language));
    }

    private static string NormalizeRemoteTextLanguage(string language)
    {
        return string.IsNullOrWhiteSpace(language) ? "default" : language.Trim().ToLowerInvariant();
    }

    private static string NormalizeCacheToken(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string ResolveRemoteTextNoChangeCachePath()
    {
        return Path.Combine(RetroBatPaths.RuntimeLogRoot, "remote-text-nochange-cache.json");
    }

    private static string ResolveRemoteExactLocalNoRetryCachePath()
    {
        return Path.Combine(RetroBatPaths.RuntimeLogRoot, "remote-exact-local-noretry-cache.json");
    }

    private static void EnsureRemoteTextNoChangeCacheLoaded()
    {
        lock (RemoteTextNoChangeCacheLock)
        {
            if (RemoteTextNoChangeCacheLoaded)
            {
                return;
            }

            RemoteTextNoChangeCooldowns.Clear();
            var path = ResolveRemoteTextNoChangeCachePath();
            if (File.Exists(path))
            {
                try
                {
                    var entries = JsonSerializer.Deserialize<List<RemoteTextNoChangeCacheEntry>>(
                        File.ReadAllText(path),
                        RemoteTextNoChangeCacheJsonOptions) ?? new List<RemoteTextNoChangeCacheEntry>();
                    var now = DateTime.UtcNow;
                    foreach (var entry in entries.Where(entry =>
                        !string.IsNullOrWhiteSpace(entry.Key) &&
                        entry.ExpiresAtUtc > now))
                    {
                        RemoteTextNoChangeCooldowns[entry.Key] = entry;
                    }
                }
                catch
                {
                    RemoteTextNoChangeCooldowns.Clear();
                }
            }

            RemoteTextNoChangeCacheLoaded = true;
        }
    }

    private static void SaveRemoteTextNoChangeCache()
    {
        lock (RemoteTextNoChangeCacheLock)
        {
            var path = ResolveRemoteTextNoChangeCachePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var now = DateTime.UtcNow;
            var entries = RemoteTextNoChangeCooldowns.Values
                .Where(entry => entry.ExpiresAtUtc > now)
                .OrderBy(entry => entry.FrontendSystemId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.TextSlug, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Language, StringComparer.OrdinalIgnoreCase)
                .ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(entries, RemoteTextNoChangeCacheJsonOptions));
        }
    }

    private static void EnsureRemoteExactLocalNoRetryCacheLoaded()
    {
        lock (RemoteExactLocalNoRetryCacheLock)
        {
            if (RemoteExactLocalNoRetryCacheLoaded)
            {
                return;
            }

            RemoteExactLocalNoRetryEntries.Clear();
            var path = ResolveRemoteExactLocalNoRetryCachePath();
            if (File.Exists(path))
            {
                try
                {
                    var entries = JsonSerializer.Deserialize<List<RemoteExactLocalNoRetryCacheEntry>>(
                        File.ReadAllText(path),
                        RemoteTextNoChangeCacheJsonOptions) ?? new List<RemoteExactLocalNoRetryCacheEntry>();
                    foreach (var entry in entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Key)))
                    {
                        RemoteExactLocalNoRetryEntries[entry.Key] = entry;
                    }
                }
                catch
                {
                    RemoteExactLocalNoRetryEntries.Clear();
                }
            }

            RemoteExactLocalNoRetryCacheLoaded = true;
        }
    }

    private static void SaveRemoteExactLocalNoRetryCache()
    {
        lock (RemoteExactLocalNoRetryCacheLock)
        {
            var path = ResolveRemoteExactLocalNoRetryCachePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entries = RemoteExactLocalNoRetryEntries.Values
                .OrderBy(entry => entry.FrontendSystemId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.GameSlug, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => string.Join(",", entry.Kinds), StringComparer.OrdinalIgnoreCase)
                .ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(entries, RemoteTextNoChangeCacheJsonOptions));
        }
    }

    private sealed record RemoteScrapePolicy(
        List<string> AllowedMissingKinds,
        Dictionary<string, string> ExcludedKinds,
        List<string> ExactLocalMissingKinds);

    private sealed record RemoteTextNoChangeCacheEntry(
        string Key,
        string SystemId,
        string FrontendSystemId,
        string GameSlug,
        string TextSlug,
        string Language,
        DateTime LastCheckedAtUtc,
        DateTime ExpiresAtUtc,
        string Status);

    private sealed record RemoteExactLocalNoRetryCacheEntry(
        string Key,
        string SystemId,
        string FrontendSystemId,
        string GameSlug,
        string GamePath,
        List<string> Kinds,
        DateTime LastCheckedAtUtc,
        string Status);

    private enum MediaSelectionTarget
    {
        Image,
        Logo,
        Thumbnail
    }
}

public sealed class RemoteScrapeDecision
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string FrontendSystemId { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string WorkflowMode { get; set; } = string.Empty;
    public List<string> MissingKinds { get; set; } = new();
    public List<string> ExactLocalMissingKinds { get; set; } = new();
    public bool ExactLocalOnly { get; set; }
    public List<string> DeferredMissingKinds { get; set; } = new();
    public Dictionary<string, string> ExcludedKinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool NeedsDescription { get; set; }
    public bool LocalFirst { get; set; }
    public bool RefreshCurrentGameAfterSuccess { get; set; }
    public string ScreenScraperSystemId { get; set; } = string.Empty;
    public string ScreenScraperGameId { get; set; } = string.Empty;
    public bool HasScreenScraperCredentials { get; set; }
    public bool HasScreenScraperDeveloperCredentials { get; set; }
    public string ScreenScraperDeveloperCredentialSource { get; set; } = string.Empty;
    public string ScreenScraperBaseUrl { get; set; } = string.Empty;
    public string ScreenScraperSoftName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string BezelAspectRatio { get; set; } = string.Empty;
    public string BezelOrientation { get; set; } = string.Empty;
    public int DownloadedMediaCount { get; set; }
    public int ImportedMediaCount { get; set; }
    public List<string> ImportedKinds { get; set; } = new();
    public bool TextUpdated { get; set; }
    public bool GamelistChanged { get; set; }
    public bool MediaContentChanged { get; set; }
    public bool MetadataChanged { get; set; }
    public bool LivePushed { get; set; }
    public bool ReloadRequested { get; set; }
}

public sealed class RemoteScrapingStatus
{
    public bool AutoScrapingEnabled { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool ScreenScraperEnabled { get; set; }
    public bool ScrapeQueueEnabled { get; set; }
    public bool LocalFirst { get; set; }
    public bool RefreshCurrentGameAfterSuccess { get; set; }
    public bool MarqueeScrapingEnabled { get; set; }
    public bool ScreenMarqueeScrapingEnabled { get; set; }
    public bool ScreenMarqueeSmallScrapingEnabled { get; set; }
    public bool SteamGridScrapingEnabled { get; set; }
    public bool MixScrapingEnabled { get; set; }
    public bool MapScrapingEnabled { get; set; }
    public bool HasScreenScraperCredentials { get; set; }
    public bool HasScreenScraperDeveloperCredentials { get; set; }
    public string ScreenScraperDeveloperCredentialSource { get; set; } = string.Empty;
    public string ScreenScraperBaseUrl { get; set; } = string.Empty;
    public string ScreenScraperSoftName { get; set; } = string.Empty;
    public string ScreenScraperCatalogEndpoint { get; set; } = string.Empty;
    public string ScreenScraperImageEndpoint { get; set; } = string.Empty;
    public string ScreenScraperVideoEndpoint { get; set; } = string.Empty;
    public string ScreenScraperManualEndpoint { get; set; } = string.Empty;
    public bool ManualScrapingEnabled { get; set; }
    public bool MagazineScrapingEnabled { get; set; }
    public bool VideoScrapingEnabled { get; set; }
    public bool VideoNormalizedScrapingEnabled { get; set; }
    public bool BezelScrapingEnabled { get; set; }
    public string BezelAspectRatio { get; set; } = string.Empty;
    public string BezelOrientation { get; set; } = string.Empty;
    public ScreenScraperCapabilitySnapshot ScreenScraperCapabilities { get; set; } = ScreenScraperCapabilitySnapshot.Unknown;
    public string Workflow { get; set; } = string.Empty;
}
