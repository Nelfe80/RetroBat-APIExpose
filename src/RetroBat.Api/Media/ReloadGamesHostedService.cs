using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;
using System.Net.Sockets;

namespace RetroBat.Api.Media;

public class ReloadGamesHostedService : BackgroundService
{
    private const string ReloadProgressTaskId = "reloadgames";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinimumReloadInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LanguageGamelistSyncScrapeQuietDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LanguageGamelistSyncPreflushSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RomSetManagerPreflushSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PendingProgressReportInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupProgressSuppressionWindow = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://127.0.0.1:1234") };
    private readonly MediaRuntimeState _runtimeState;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly LocalizedGamelistCacheService _localizedGamelistCacheService;
    private readonly RemoteScrapeQueueService _remoteScrapeQueueService;
    private readonly RomSetManagerService _romSetManagerService;
    private readonly IStartupOverlayService _startupOverlayService;
    private readonly ITaskProgressService _taskProgressService;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ILogger<ReloadGamesHostedService>? _logger;
    private string _lastBlockedSignature = string.Empty;
    private DateTime _lastPendingProgressReportAtUtc = DateTime.MinValue;
    private VisibleMediaReallocationReloadSummary? _pendingVisibleMediaReallocationCompletionSummary;
    private LanguageGamelistSyncWorkflowReloadSummary? _pendingLanguageGamelistSyncCompletionSummary;
    private RomSetManagerWorkflowReloadSummary? _pendingRomSetManagerCompletionSummary;

    public ReloadGamesHostedService(
        MediaRuntimeState runtimeState,
        GamelistUpdateService gamelistUpdateService,
        LocalizedGamelistCacheService localizedGamelistCacheService,
        RemoteScrapeQueueService remoteScrapeQueueService,
        RomSetManagerService romSetManagerService,
        IStartupOverlayService startupOverlayService,
        ITaskProgressService taskProgressService,
        IEmulationStationNotificationService notificationService,
        EmulationStationSettingsService settingsService,
        InterfaceTextService interfaceTextService,
        ILogger<ReloadGamesHostedService>? logger = null)
    {
        _runtimeState = runtimeState;
        _gamelistUpdateService = gamelistUpdateService;
        _localizedGamelistCacheService = localizedGamelistCacheService;
        _remoteScrapeQueueService = remoteScrapeQueueService;
        _romSetManagerService = romSetManagerService;
        _startupOverlayService = startupOverlayService;
        _taskProgressService = taskProgressService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _interfaceTextService = interfaceTextService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_runtimeState.TryConsumeReloadGamesReady(MinimumReloadInterval, out _, out var requestedByScrape))
                {
                    try
                    {
                        if (requestedByScrape && _runtimeState.HasLanguageGamelistSyncWorkflowPending())
                        {
                            await DiscardPendingStateForLanguageSwitchAsync(
                                "language-gamelist-sync-pending-before-scrape-reload",
                                stoppingToken);
                            _runtimeState.MarkReloadGamesPending(requestedByScrape: false);
                            _lastBlockedSignature = string.Empty;
                            continue;
                        }

                        if (requestedByScrape)
                        {
                            _logger?.LogInformation(
                                "reloadgames automatique ignore car il provient du scrap; le refresh direct ES cible la fiche sans reprendre la navigation.");
                            var scrapeExtendedSystems = await _gamelistUpdateService.ApplyPendingExtendedGamelistsAsync(
                                "requested-by-scrape-before-localized-cache-entry-patch",
                                stoppingToken);
                            if (scrapeExtendedSystems > 0)
                            {
                                _logger?.LogInformation(
                                    "Pending extended gamelists applied for {SystemCount} systems before localized cache entry patch.",
                                    scrapeExtendedSystems);
                            }

                            await PatchLocalizedGamelistCacheEntriesAfterScrapeAsync(
                                "requested-by-scrape-reload-skipped",
                                stoppingToken);
                            await RefreshTrackingLog.AppendAsync(
                                "reloadgames",
                                "skipped-requested-by-scrape",
                                new { reason = "direct-addgames-preferred" },
                                stoppingToken);
                            _lastBlockedSignature = string.Empty;
                            _taskProgressService.Complete(ReloadProgressTaskId);
                            continue;
                        }

                        if (!await RunVisibleMediaReallocationWorkflowIfPendingAsync(stoppingToken))
                        {
                            _lastBlockedSignature = string.Empty;
                            continue;
                        }

                        if (!await RunLanguageGamelistSyncWorkflowIfPendingAsync(stoppingToken))
                        {
                            _lastBlockedSignature = string.Empty;
                            continue;
                        }

                        if (!await RunRomSetManagerWorkflowIfPendingAsync(stoppingToken))
                        {
                            _lastBlockedSignature = string.Empty;
                            continue;
                        }

                        var suppressTaskProgress = ShouldSuppressStartupReloadProgress();
                        if (!suppressTaskProgress)
                        {
                            _taskProgressService.Report(
                                ReloadProgressTaskId,
                                ReloadProgressTitle(),
                                1,
                                1,
                                "reloadgames");
                        }
                        else
                        {
                            _logger?.LogDebug("reloadgames pending during startup; startup overlay progress unchanged.");
                        }
                        var extendedSystems = await _gamelistUpdateService.ApplyPendingExtendedGamelistsAsync(
                            "before-reloadgames",
                            stoppingToken);
                        if (extendedSystems > 0)
                        {
                            _logger?.LogInformation(
                                "Pending extended gamelists applied for {SystemCount} systems before reloadgames.",
                                extendedSystems);
                        }

                        await PatchLocalizedGamelistCacheEntriesAfterScrapeAsync(
                            "before-reloadgames-after-pending-extended",
                            stoppingToken);

                        if (!await ReapplyLanguageGamelistSyncBeforeReloadAsync(extendedSystems, stoppingToken))
                        {
                            _lastBlockedSignature = string.Empty;
                            continue;
                        }

                        if (await TryRequestFrontendReloadGamesAsync(stoppingToken))
                        {
                            _logger?.LogInformation("reloadgames appele avec succes apres aggregation des mises a jour.");
                            await RefreshTrackingLog.AppendAsync(
                                "reloadgames",
                                "success",
                                new { requestedByScrape },
                                stoppingToken);
                            _startupOverlayService.NotifyReloadSucceeded();
                            if (!suppressTaskProgress)
                            {
                                _taskProgressService.Complete(ReloadProgressTaskId);
                            }

                            await NotifyPendingVisibleMediaReallocationCompletionAsync(stoppingToken);
                            await NotifyPendingLanguageGamelistSyncCompletionAsync(stoppingToken);
                            await NotifyPendingRomSetManagerCompletionAsync(stoppingToken);
                        }
                        else
                        {
                            _runtimeState.MarkReloadGamesPending(requestedByScrape);
                            _logger?.LogWarning("reloadgames agrege n'a pas ete accepte par EmulationStation; nouvelle tentative planifiee.");
                            await RefreshTrackingLog.AppendAsync(
                                "reloadgames",
                                "http-failed-requeued",
                                new { requestedByScrape },
                                stoppingToken);
                        }

                        _lastBlockedSignature = string.Empty;
                    }
                    catch (HttpRequestException ex) when (IsFrontendUnavailable(ex))
                    {
                        _runtimeState.MarkReloadGamesPending(requestedByScrape);
                        _logger?.LogInformation(ex, "reloadgames differe: frontend EmulationStation indisponible sur 127.0.0.1:1234.");
                        await RefreshTrackingLog.AppendAsync(
                            "reloadgames",
                            "frontend-unavailable-requeued",
                            new { requestedByScrape, exceptionType = ex.GetType().FullName, ex.Message },
                            CancellationToken.None);
                    }
                }
                else
                {
                    var status = _runtimeState.GetReloadGamesStatus(MinimumReloadInterval);
                    if (status.Pending)
                    {
                        var blockReason = status.ActiveScrapeCount > 0 && !status.ReloadAllowedDuringActiveScrape
                            ? "priority-scrapes"
                            : status.RetryAfter > TimeSpan.FromMilliseconds(750) && !string.Equals(status.LastFrontendEvent, "game-selected", StringComparison.OrdinalIgnoreCase)
                                ? "post-scrape-quiet-period"
                                : string.Equals(status.LastFrontendEvent, "game-selected", StringComparison.OrdinalIgnoreCase)
                                    ? "last-event-game-selected"
                                    : status.RetryAfter > TimeSpan.Zero
                                        ? "debounce-or-min-interval"
                                        : "pending";
                        ReportPendingReloadProgress(status, blockReason);
                        var signature = $"reason={blockReason}|dirty={status.DirtySinceLastReload}|scrapes={status.ActiveScrapeCount}|background={status.BackgroundScrapeCount}|last={status.LastFrontendEvent}";
                        if (!string.Equals(signature, _lastBlockedSignature, StringComparison.Ordinal))
                        {
                            _lastBlockedSignature = signature;
                            _logger?.LogInformation(
                                "reloadgames en attente: reason={Reason}, dirty={Dirty}, activeScrapes={ActiveScrapes}, backgroundScrapes={BackgroundScrapes}, lastFrontendEvent={LastFrontendEvent}, retryAfterMs={RetryAfterMs}",
                                blockReason,
                                status.DirtySinceLastReload,
                                status.ActiveScrapeCount,
                                status.BackgroundScrapeCount,
                                status.LastFrontendEvent,
                                Math.Ceiling(status.RetryAfter.TotalMilliseconds));
                            await RefreshTrackingLog.AppendAsync(
                                "reloadgames",
                                "pending",
                                new
                                {
                                    reason = blockReason,
                                    dirty = status.DirtySinceLastReload,
                                    activeScrapes = status.ActiveScrapeCount,
                                    backgroundScrapes = status.BackgroundScrapeCount,
                                    lastFrontendEvent = status.LastFrontendEvent,
                                    retryAfterMs = Math.Ceiling(status.RetryAfter.TotalMilliseconds),
                                    requestedByScrape = status.RequestedByScrape
                                },
                                stoppingToken);
                        }
                    }
                    else
                    {
                        _lastBlockedSignature = string.Empty;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Echec du scheduler reloadgames.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }

    private async Task<bool> RunVisibleMediaReallocationWorkflowIfPendingAsync(CancellationToken cancellationToken)
    {
        if (!_runtimeState.TryConsumeVisibleMediaReallocationWorkflow(out var request) || request == null)
        {
            return true;
        }

        try
        {
            await NotifyVisibleMediaReallocationStartedAsync(request, cancellationToken);
            _settingsService.Invalidate();
            var settings = _settingsService.GetScrapingSettings();
            var updatedSystems = 0;
            if (string.Equals(request.Scope, "system", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(request.SystemId))
            {
                var updated = await _gamelistUpdateService.RefreshSelectionsForSystemAsync(
                    request.SystemId,
                    settings,
                    cancellationToken);
                updatedSystems = updated ? 1 : 0;
            }
            else
            {
                updatedSystems = await _gamelistUpdateService.RefreshSelectionsForAllSystemsAsync(
                    settings,
                    cancellationToken);
            }

            _pendingVisibleMediaReallocationCompletionSummary = new VisibleMediaReallocationReloadSummary(
                updatedSystems,
                request.Scope,
                request.SystemId);
            await RefreshTrackingLog.AppendAsync(
                "visible-media-reallocation",
                "single-workflow-normalized",
                new
                {
                    request.Scope,
                    request.SystemId,
                    request.Reason,
                    updatedSystems
                },
                cancellationToken);
            _logger?.LogInformation(
                "Workflow reallocation media normalise: scope={Scope}, system={SystemId}, updatedSystems={UpdatedSystems}. reloadgames unique conserve.",
                request.Scope,
                string.IsNullOrWhiteSpace(request.SystemId) ? "(all)" : request.SystemId,
                updatedSystems);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtimeState.RequestVisibleMediaReallocationWorkflow(TimeSpan.FromSeconds(5), request);
            _logger?.LogWarning(ex, "Echec du workflow de reallocation media; nouvelle tentative planifiee.");
            await RefreshTrackingLog.AppendAsync(
                "visible-media-reallocation",
                "single-workflow-normalization-failed",
                new { request.Scope, request.SystemId, request.Reason, exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
            return false;
        }
    }

    private async Task NotifyPendingVisibleMediaReallocationCompletionAsync(CancellationToken cancellationToken)
    {
        if (_pendingVisibleMediaReallocationCompletionSummary == null)
        {
            return;
        }

        var summary = _pendingVisibleMediaReallocationCompletionSummary;
        _pendingVisibleMediaReallocationCompletionSummary = null;
        await NotifyVisibleMediaReallocationCompletedAsync(summary, cancellationToken);
    }

    private async Task<bool> RunLanguageGamelistSyncWorkflowIfPendingAsync(CancellationToken cancellationToken)
    {
        if (!_runtimeState.TryConsumeLanguageGamelistSyncWorkflow(out var request) || request == null)
        {
            return true;
        }

        try
        {
            var reloadStatus = _runtimeState.GetReloadGamesStatus(TimeSpan.Zero);
            if (reloadStatus.ActiveScrapeCount > 0)
            {
                _runtimeState.RequestLanguageGamelistSyncWorkflow(LanguageGamelistSyncScrapeQuietDelay, request);
                _logger?.LogInformation(
                    "Workflow synchro gamelist langue differe: scrapes bloquants actifs={ActiveScrapes}, scrapes arriere-plan ignores={BackgroundScrapes}.",
                    reloadStatus.ActiveScrapeCount,
                    reloadStatus.BackgroundScrapeCount);
                await RefreshTrackingLog.AppendAsync(
                    "language-gamelist-sync",
                    "deferred-blocking-scrapes-active",
                    new
                    {
                        request.PreviousLanguage,
                        request.CurrentLanguage,
                        request.Reason,
                        activeScrapes = reloadStatus.ActiveScrapeCount,
                        backgroundScrapes = reloadStatus.BackgroundScrapeCount,
                        staleBackgroundScrapesIgnored = true,
                        retryAfterMs = Math.Ceiling(LanguageGamelistSyncScrapeQuietDelay.TotalMilliseconds)
                    },
                    cancellationToken);
                return false;
            }

            await DiscardPendingStateForLanguageSwitchAsync("language-gamelist-sync-start", cancellationToken);
            if (!await TryPreflushLanguageGamelistSyncAsync(request, cancellationToken))
            {
                return false;
            }

            await NotifyLanguageGamelistSyncStartedAsync(request, cancellationToken);
            _settingsService.Invalidate();
            var updatedSystems = 0;
            if (_localizedGamelistCacheService.Enabled)
            {
                var switchResult = await _localizedGamelistCacheService.SwitchToLanguageAsync(
                    request.CurrentLanguage,
                    cancellationToken);
                updatedSystems = switchResult.SystemsSwitched;
                await RefreshTrackingLog.AppendAsync(
                    "language-gamelist-sync",
                    "localized-cache-switched",
                    new
                    {
                        request.PreviousLanguage,
                        request.CurrentLanguage,
                        request.Reason,
                        switchResult.Language,
                        switchResult.SystemsSwitched,
                        switchResult.SystemsGenerated,
                        switchResult.SystemsFailed,
                        switchResult.Success,
                        cacheReason = switchResult.Reason
                    },
                    cancellationToken);
            }
            else
            {
                var settings = BuildLanguageGamelistSyncSettings(request.CurrentLanguage);
                updatedSystems = await _gamelistUpdateService.RefreshSelectionsForAllSystemsWithTextMetadataAsync(
                    settings,
                    cancellationToken);
            }

            _pendingLanguageGamelistSyncCompletionSummary = new LanguageGamelistSyncWorkflowReloadSummary(
                request.PreviousLanguage,
                request.CurrentLanguage,
                updatedSystems);

            await RefreshTrackingLog.AppendAsync(
                "language-gamelist-sync",
                "single-workflow-normalized",
                new
                {
                    request.PreviousLanguage,
                    request.CurrentLanguage,
                    request.Reason,
                    updatedSystems
                },
                cancellationToken);
            _logger?.LogInformation(
                "Workflow synchro gamelist langue execute: previous={PreviousLanguage}, current={CurrentLanguage}, updatedSystems={UpdatedSystems}. reloadgames unique conserve.",
                string.IsNullOrWhiteSpace(request.PreviousLanguage) ? "(none)" : request.PreviousLanguage,
                request.CurrentLanguage,
                updatedSystems);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtimeState.RequestLanguageGamelistSyncWorkflow(TimeSpan.FromSeconds(5), request);
            _logger?.LogWarning(ex, "Echec du workflow de synchro gamelist langue; nouvelle tentative planifiee.");
            await RefreshTrackingLog.AppendAsync(
                "language-gamelist-sync",
                "single-workflow-failed",
                new { request.PreviousLanguage, request.CurrentLanguage, request.Reason, exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
            return false;
        }
    }

    private async Task NotifyPendingLanguageGamelistSyncCompletionAsync(CancellationToken cancellationToken)
    {
        if (_pendingLanguageGamelistSyncCompletionSummary == null)
        {
            return;
        }

        var summary = _pendingLanguageGamelistSyncCompletionSummary;
        _pendingLanguageGamelistSyncCompletionSummary = null;
        await NotifyLanguageGamelistSyncCompletedAsync(summary, cancellationToken);
    }

    private async Task<bool> ReapplyLanguageGamelistSyncBeforeReloadAsync(
        int extendedSystems,
        CancellationToken cancellationToken)
    {
        var summary = _pendingLanguageGamelistSyncCompletionSummary;
        if (summary == null)
        {
            return true;
        }

        try
        {
            _settingsService.Invalidate();
            var updatedSystems = 0;
            object details;
            if (_localizedGamelistCacheService.Enabled)
            {
                var switchResult = await _localizedGamelistCacheService.SwitchToLanguageAsync(
                    summary.CurrentLanguage,
                    cancellationToken);
                updatedSystems = switchResult.SystemsSwitched;
                details = new
                {
                    summary.PreviousLanguage,
                    summary.CurrentLanguage,
                    extendedSystems,
                    previousUpdatedSystems = summary.SystemsUpdated,
                    switchResult.Language,
                    switchResult.SystemsSwitched,
                    switchResult.SystemsGenerated,
                    switchResult.SystemsFailed,
                    switchResult.Success,
                    cacheReason = switchResult.Reason
                };
            }
            else
            {
                var settings = BuildLanguageGamelistSyncSettings(summary.CurrentLanguage);
                updatedSystems = await _gamelistUpdateService.RefreshSelectionsForAllSystemsWithTextMetadataAsync(
                    settings,
                    cancellationToken);
                details = new
                {
                    summary.PreviousLanguage,
                    summary.CurrentLanguage,
                    extendedSystems,
                    previousUpdatedSystems = summary.SystemsUpdated,
                    updatedSystems,
                    cacheReason = "cache-disabled"
                };
            }

            if (updatedSystems > summary.SystemsUpdated)
            {
                _pendingLanguageGamelistSyncCompletionSummary = summary with { SystemsUpdated = updatedSystems };
            }

            await RefreshTrackingLog.AppendAsync(
                "language-gamelist-sync",
                "pre-reload-language-reapplied",
                details,
                cancellationToken);
            _logger?.LogInformation(
                "Synchro langue reappliquee juste avant reloadgames: previous={PreviousLanguage}, current={CurrentLanguage}, extendedSystems={ExtendedSystems}, updatedSystems={UpdatedSystems}.",
                string.IsNullOrWhiteSpace(summary.PreviousLanguage) ? "(none)" : summary.PreviousLanguage,
                summary.CurrentLanguage,
                extendedSystems,
                updatedSystems);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtimeState.RequestLanguageGamelistSyncWorkflow(
                TimeSpan.FromSeconds(5),
                new LanguageGamelistSyncWorkflowRequest(
                    summary.PreviousLanguage,
                    summary.CurrentLanguage,
                    "pre-reload-language-reapply-failed"));
            _logger?.LogWarning(ex, "Echec de la reapplique langue juste avant reloadgames; reloadgames differe.");
            await RefreshTrackingLog.AppendAsync(
                "language-gamelist-sync",
                "pre-reload-language-reapply-failed",
                new { summary.PreviousLanguage, summary.CurrentLanguage, extendedSystems, exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
            return false;
        }
    }

    private EmulationStationScrapingSettings BuildLanguageGamelistSyncSettings(string currentLanguage)
    {
        var settings = _settingsService.GetScrapingSettings();
        var language = string.IsNullOrWhiteSpace(currentLanguage)
            ? settings.Language
            : currentLanguage;
        var profiles = ApiExposeProfileResolver.Resolve(language, "auto", "auto");
        settings.Language = language;
        settings.ContentLanguageProfile = profiles.LanguageProfile;
        settings.ContentRegionProfile = profiles.RegionProfile;
        return settings;
    }

    private async Task<bool> RunRomSetManagerWorkflowIfPendingAsync(CancellationToken cancellationToken)
    {
        if (!_runtimeState.TryConsumeRomSetManagerWorkflow(out var request) || request == null)
        {
            return true;
        }

        try
        {
            if (!await TryPreflushRomSetManagerWorkflowAsync(request, cancellationToken))
            {
                return false;
            }

            await NotifyRomSetManagerStartedAsync(request, cancellationToken);
            var applyRequest = new RomSetManagerApplyRequest
            {
                AllSystems = !string.Equals(request.Scope, "system", StringComparison.OrdinalIgnoreCase),
                SystemId = string.Equals(request.Scope, "system", StringComparison.OrdinalIgnoreCase)
                    ? request.SystemId
                    : null,
                DryRun = false,
                ReloadGames = false
            };

            var result = request.Restore
                ? await _romSetManagerService.RestoreAsync(applyRequest, cancellationToken)
                : await _romSetManagerService.ApplyAsync(applyRequest, cancellationToken);

            _pendingRomSetManagerCompletionSummary = new RomSetManagerWorkflowReloadSummary(
                request.Restore,
                request.Scope,
                request.SystemId,
                result.Systems.Count,
                result.GamesScanned,
                result.GamesToHide,
                result.GamesToRestore,
                result.GamesChanged,
                result.Warnings.Count,
                result.Message);

            await RefreshTrackingLog.AppendAsync(
                "rom-set-manager",
                request.Restore ? "single-workflow-restored" : "single-workflow-applied",
                new
                {
                    request.Scope,
                    request.SystemId,
                    request.Reason,
                    result.Enabled,
                    systemsProcessed = result.Systems.Count,
                    result.GamesScanned,
                    result.GamesToHide,
                    result.GamesToRestore,
                    result.GamesChanged,
                    warningCount = result.Warnings.Count
                },
                cancellationToken);
            _logger?.LogInformation(
                "Workflow Roms Manager execute: mode={Mode}, scope={Scope}, system={SystemId}, systems={Systems}, scanned={Scanned}, changed={Changed}. reloadgames unique conserve.",
                request.Restore ? "restore" : "apply",
                request.Scope,
                string.IsNullOrWhiteSpace(request.SystemId) ? "(all)" : request.SystemId,
                result.Systems.Count,
                result.GamesScanned,
                result.GamesChanged);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtimeState.RequestRomSetManagerWorkflow(TimeSpan.FromSeconds(5), request);
            _logger?.LogWarning(ex, "Echec du workflow Roms Manager; nouvelle tentative planifiee.");
            await RefreshTrackingLog.AppendAsync(
                "rom-set-manager",
                "single-workflow-failed",
                new { request.Scope, request.SystemId, request.Reason, request.Restore, exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
            return false;
        }
    }

    private async Task<bool> TryPreflushRomSetManagerWorkflowAsync(
        RomSetManagerWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await TryRequestFrontendReloadGamesAsync(cancellationToken);
        await RefreshTrackingLog.AppendAsync(
            "rom-set-manager",
            accepted ? "preflush-reloadgames-success" : "preflush-reloadgames-failed",
            new
            {
                request.Restore,
                request.Scope,
                request.SystemId,
                request.Reason,
                settleDelayMs = Math.Ceiling(RomSetManagerPreflushSettleDelay.TotalMilliseconds)
            },
            cancellationToken);

        if (!accepted)
        {
            _runtimeState.RequestRomSetManagerWorkflow(TimeSpan.FromSeconds(5), request);
            _logger?.LogWarning(
                "Workflow Roms Manager differe: pre-reloadgames initial refuse par EmulationStation.");
            return false;
        }

        await Task.Delay(RomSetManagerPreflushSettleDelay, cancellationToken);
        return true;
    }

    private async Task NotifyPendingRomSetManagerCompletionAsync(CancellationToken cancellationToken)
    {
        if (_pendingRomSetManagerCompletionSummary == null)
        {
            return;
        }

        var summary = _pendingRomSetManagerCompletionSummary;
        _pendingRomSetManagerCompletionSummary = null;
        await NotifyRomSetManagerCompletedAsync(summary, cancellationToken);
        await RefreshLocalizedGamelistCachesAfterRomSetManagerAsync(summary, cancellationToken);
    }

    private async Task RefreshLocalizedGamelistCachesAfterRomSetManagerAsync(
        RomSetManagerWorkflowReloadSummary summary,
        CancellationToken cancellationToken)
    {
        if (!_localizedGamelistCacheService.Enabled || summary.GamesChanged <= 0)
        {
            return;
        }

        try
        {
            IReadOnlyCollection<string>? systems = null;
            if (string.Equals(summary.Scope, "system", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(summary.SystemId))
            {
                systems = [summary.SystemId];
            }

            var result = await _localizedGamelistCacheService.PrebuildActiveLanguagesAsync(systems, cancellationToken);
            await RefreshTrackingLog.AppendAsync(
                "rom-set-manager",
                "localized-cache-refreshed",
                new
                {
                    summary.Restore,
                    summary.Scope,
                    summary.SystemId,
                    summary.GamesChanged,
                    languages = result.Languages,
                    result.SystemsGenerated,
                    result.SystemsFailed,
                    result.Reason
                },
                cancellationToken);
            _logger?.LogInformation(
                "Caches gamelist localises rafraichis apres Roms Manager: scope={Scope}, system={SystemId}, languages={Languages}, generated={Generated}, failed={Failed}.",
                summary.Scope,
                string.IsNullOrWhiteSpace(summary.SystemId) ? "(all)" : summary.SystemId,
                string.Join(",", result.Languages),
                result.SystemsGenerated,
                result.SystemsFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Echec du rafraichissement silencieux des caches gamelist localises apres Roms Manager.");
            await RefreshTrackingLog.AppendAsync(
                "rom-set-manager",
                "localized-cache-refresh-failed",
                new
                {
                    summary.Restore,
                    summary.Scope,
                    summary.SystemId,
                    summary.GamesChanged,
                    exceptionType = ex.GetType().FullName,
                    ex.Message
                },
                CancellationToken.None);
        }
    }

    private async Task PatchLocalizedGamelistCacheEntriesAfterScrapeAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var entries = _runtimeState.ConsumeLocalizedGamelistCacheRefreshEntries();
        if (entries.Count == 0 || !_localizedGamelistCacheService.Enabled)
        {
            return;
        }

        try
        {
            var result = await _localizedGamelistCacheService.PatchActiveLanguageEntriesAsync(entries, cancellationToken);
            await RefreshTrackingLog.AppendAsync(
                "remote-scrape",
                "localized-cache-entry-patched",
                new
                {
                    reason,
                    entries = entries.Count,
                    systems = entries.Select(entry => entry.FrontendSystemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    languages = result.Languages,
                    result.EntriesRequested,
                    result.EntriesPatched,
                    result.EntriesSkipped,
                    result.FilesSaved,
                    result.FilesFailed,
                    cacheReason = result.Reason
                },
                cancellationToken);
            _logger?.LogInformation(
                "Entrees cache gamelist localisees patchees apres scrape: reason={Reason}, entries={Entries}, languages={Languages}, patched={Patched}, skipped={Skipped}, saved={Saved}, failed={Failed}.",
                reason,
                entries.Count,
                string.Join(",", result.Languages),
                result.EntriesPatched,
                result.EntriesSkipped,
                result.FilesSaved,
                result.FilesFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var entry in entries)
            {
                _runtimeState.RequestLocalizedGamelistCacheRefreshForGame(entry);
            }

            throw;
        }
        catch (Exception ex)
        {
            foreach (var entry in entries)
            {
                _runtimeState.RequestLocalizedGamelistCacheRefreshForGame(entry);
            }

            _logger?.LogWarning(ex, "Echec du patch silencieux des entrees cache gamelist localisees apres scrape.");
            await RefreshTrackingLog.AppendAsync(
                "remote-scrape",
                "localized-cache-entry-patch-failed",
                new
                {
                    reason,
                    entries = entries.Count,
                    systems = entries.Select(entry => entry.FrontendSystemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    exceptionType = ex.GetType().FullName,
                    ex.Message
                },
                CancellationToken.None);
        }
    }

    private async Task DiscardPendingStateForLanguageSwitchAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var discardedExtended = await _gamelistUpdateService.DiscardPendingExtendedGamelistsAsync(reason, cancellationToken);
        var discardedCacheRefreshEntries = _runtimeState.DiscardLocalizedGamelistCacheRefreshEntries();
        var discardedRemoteQueue = _remoteScrapeQueueService.DiscardPendingForLanguageSwitch(reason);
        if (discardedExtended == 0 &&
            discardedCacheRefreshEntries == 0 &&
            discardedRemoteQueue.QueuedItems == 0 &&
            discardedRemoteQueue.PendingGamelistPersistence == 0 &&
            discardedRemoteQueue.NoChangeCooldowns == 0)
        {
            return;
        }

        await RefreshTrackingLog.AppendAsync(
            "language-gamelist-sync",
            "pending-discarded",
            new
            {
                reason,
                pendingExtendedGamelists = discardedExtended,
                localizedCacheRefreshEntries = discardedCacheRefreshEntries,
                remoteScrapeQueueItems = discardedRemoteQueue.QueuedItems,
                remoteScrapePendingGamelistPersistence = discardedRemoteQueue.PendingGamelistPersistence,
                remoteScrapeNoChangeCooldowns = discardedRemoteQueue.NoChangeCooldowns
            },
            cancellationToken);
    }

    private async Task<bool> TryPreflushLanguageGamelistSyncAsync(
        LanguageGamelistSyncWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await TryRequestFrontendReloadGamesAsync(cancellationToken);
        await RefreshTrackingLog.AppendAsync(
            "language-gamelist-sync",
            accepted ? "preflush-reloadgames-success" : "preflush-reloadgames-failed",
            new
            {
                request.PreviousLanguage,
                request.CurrentLanguage,
                request.Reason,
                settleDelayMs = Math.Ceiling(LanguageGamelistSyncPreflushSettleDelay.TotalMilliseconds)
            },
            cancellationToken);

        if (!accepted)
        {
            _runtimeState.RequestLanguageGamelistSyncWorkflow(TimeSpan.FromSeconds(5), request);
            _logger?.LogWarning(
                "Workflow synchro gamelist langue differe: pre-reloadgames initial refuse par EmulationStation.");
            return false;
        }

        await Task.Delay(LanguageGamelistSyncPreflushSettleDelay, cancellationToken);
        return true;
    }

    private async Task NotifyVisibleMediaReallocationStartedAsync(
        VisibleMediaReallocationRequest request,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetScrapingSettings();
        var language = settings.Language;
        var scope = ResolveVisibleMediaReallocationScopeLabel(
            new VisibleMediaReallocationReloadSummary(0, request.Scope, request.SystemId),
            language);
        var message = _interfaceTextService.Format(
            "notification.media_reallocation.started",
            language,
            ("scope", scope));

        await _notificationService.MessageBoxAsync(message, cancellationToken);
    }

    private async Task NotifyLanguageGamelistSyncStartedAsync(
        LanguageGamelistSyncWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetScrapingSettings();
        var language = settings.Language;
        var message = _interfaceTextService.Format(
            "notification.language_gamelist_sync.started",
            language,
            ("previousLanguage", FormatLanguageForMessage(request.PreviousLanguage, language)),
            ("currentLanguage", FormatLanguageForMessage(request.CurrentLanguage, language)));

        await _notificationService.MessageBoxAsync(message, cancellationToken);
    }

    private async Task NotifyRomSetManagerStartedAsync(
        RomSetManagerWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetScrapingSettings();
        var language = settings.Language;
        var scope = ResolveRomSetManagerScopeLabel(
            new RomSetManagerWorkflowReloadSummary(request.Restore, request.Scope, request.SystemId, 0, 0, 0, 0, 0, 0, string.Empty),
            language);
        var modeKey = request.Restore
            ? "notification.romset.workflow.mode_restore"
            : "notification.romset.workflow.mode_apply";
        var message = _interfaceTextService.Format(
            "notification.romset.workflow.started",
            language,
            ("mode", _interfaceTextService.Text(modeKey, language)),
            ("scope", scope));

        await _notificationService.MessageBoxAsync(message, cancellationToken);
    }

    private async Task NotifyVisibleMediaReallocationCompletedAsync(
        VisibleMediaReallocationReloadSummary? summary,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetScrapingSettings();
        var language = settings.Language;
        var scope = ResolveVisibleMediaReallocationScopeLabel(summary, language);
        var systemsUpdated = Math.Max(0, summary?.SystemsUpdated ?? 0);
        var message = _interfaceTextService.Format(
            "notification.media_reallocation.completed",
            language,
            ("systemsUpdated", systemsUpdated),
            ("scope", scope),
            ("image", settings.ImageSource),
            ("logo", settings.LogoSource),
            ("thumbnail", settings.ThumbSource),
            ("wheel", settings.WheelStyle));

        await _notificationService.MessageBoxAsync(message, cancellationToken);
    }

    private async Task NotifyLanguageGamelistSyncCompletedAsync(
        LanguageGamelistSyncWorkflowReloadSummary? summary,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetScrapingSettings();
        var language = settings.Language;
        var message = _interfaceTextService.Format(
            "notification.language_gamelist_sync.completed",
            language,
            ("previousLanguage", FormatLanguageForMessage(summary?.PreviousLanguage, language)),
            ("currentLanguage", FormatLanguageForMessage(summary?.CurrentLanguage, language)),
            ("systemsUpdated", Math.Max(0, summary?.SystemsUpdated ?? 0)));

        await _notificationService.MessageBoxAsync(message, cancellationToken);
    }

    private async Task NotifyRomSetManagerCompletedAsync(
        RomSetManagerWorkflowReloadSummary? summary,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetScrapingSettings();
        var language = settings.Language;
        var scope = ResolveRomSetManagerScopeLabel(summary, language);
        var modeKey = summary?.Restore == true
            ? "notification.romset.workflow.mode_restore"
            : "notification.romset.workflow.mode_apply";
        var message = _interfaceTextService.Format(
            "notification.romset.workflow.completed",
            language,
            ("mode", _interfaceTextService.Text(modeKey, language)),
            ("scope", scope),
            ("systemsProcessed", Math.Max(0, summary?.SystemsProcessed ?? 0)),
            ("gamesScanned", Math.Max(0, summary?.GamesScanned ?? 0)),
            ("gamesToHide", Math.Max(0, summary?.GamesToHide ?? 0)),
            ("gamesToRestore", Math.Max(0, summary?.GamesToRestore ?? 0)),
            ("gamesChanged", Math.Max(0, summary?.GamesChanged ?? 0)),
            ("warningCount", Math.Max(0, summary?.WarningCount ?? 0)),
            ("message", summary?.Message ?? string.Empty));

        await _notificationService.MessageBoxAsync(message, cancellationToken);
    }

    private string ResolveVisibleMediaReallocationScopeLabel(
        VisibleMediaReallocationReloadSummary? summary,
        string language)
    {
        if (summary != null &&
            string.Equals(summary.Scope, "system", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(summary.SystemId))
        {
            return summary.SystemId;
        }

        return _interfaceTextService.Text("notification.media_reallocation.scope_all", language);
    }

    private string ResolveRomSetManagerScopeLabel(
        RomSetManagerWorkflowReloadSummary? summary,
        string language)
    {
        if (summary != null &&
            string.Equals(summary.Scope, "system", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(summary.SystemId))
        {
            return summary.SystemId;
        }

        return _interfaceTextService.Text("notification.romset.workflow.scope_all", language);
    }

    private string FormatLanguageForMessage(string? languageValue, string interfaceLanguage)
    {
        return string.IsNullOrWhiteSpace(languageValue)
            ? _interfaceTextService.Text("setting.value.empty", interfaceLanguage)
            : languageValue;
    }

    private async Task<bool> TryRequestFrontendReloadGamesAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/reloadgames");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger?.LogWarning("reloadgames agrege a retourne HTTP {StatusCode}.", (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("reloadgames envoye a EmulationStation mais la reponse a depasse le timeout court; restauration poursuivie.");
            return true;
        }
        catch (HttpRequestException ex) when (IsFrontendUnavailable(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogInformation(ex, "reloadgames a provoque une reponse HTTP atypique; restauration poursuivie par precaution.");
            return true;
        }
    }

    private void ReportPendingReloadProgress(ReloadGamesStatus status, string blockReason)
    {
        if (status.RequestedByScrape)
        {
            return;
        }

        if (ShouldSuppressStartupReloadProgress())
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastPendingProgressReportAtUtc < PendingProgressReportInterval)
        {
            return;
        }

        _lastPendingProgressReportAtUtc = nowUtc;
        var detail = blockReason switch
        {
            "last-event-game-selected" => _interfaceTextService.Text("progress.reloadgames.waiting_navigation", CurrentLanguage()),
            "debounce-or-min-interval" => _interfaceTextService.Text("progress.reloadgames.preparing", CurrentLanguage()),
            "post-scrape-quiet-period" => _interfaceTextService.Text("progress.reloadgames.stabilizing", CurrentLanguage()),
            "priority-scrapes" => _interfaceTextService.Text("progress.reloadgames.waiting_media", CurrentLanguage()),
            _ => _interfaceTextService.Text("progress.reloadgames.pending", CurrentLanguage())
        };
        _taskProgressService.Report(
            ReloadProgressTaskId,
            ReloadProgressTitle(),
            0,
            1,
            detail);
    }

    private string ReloadProgressTitle()
    {
        return _interfaceTextService.Text("progress.reloadgames.title", CurrentLanguage());
    }

    private string CurrentLanguage()
    {
        return _settingsService.GetScrapingSettings().Language;
    }

    private bool ShouldSuppressStartupReloadProgress()
    {
        return _startupOverlayService.IsStartupActiveOrRecentlyCompleted(StartupProgressSuppressionWindow);
    }

    private static bool IsFrontendUnavailable(HttpRequestException ex)
    {
        return ex.InnerException is SocketException socketEx
            && socketEx.SocketErrorCode == SocketError.ConnectionRefused;
    }
}
