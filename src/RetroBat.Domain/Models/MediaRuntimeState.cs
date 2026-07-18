using RetroBat.Domain.Paths;

namespace RetroBat.Domain.Models;

public readonly record struct CurrentGameSelectionToken(
    string SelectionKey,
    long Sequence,
    DateTime SelectedAtUtc)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(SelectionKey) && Sequence > 0;
}

public class MediaRuntimeState
{
    private readonly object _lock = new();
    private static readonly TimeSpan PostScrapeQuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostGameEndLiveAddGamesQuietPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReloadGamesLastGameSelectedBypassWindow = TimeSpan.FromSeconds(15);
    private readonly HashSet<string> _gamesChangedSinceLastReload = new(StringComparer.OrdinalIgnoreCase);
    private bool _reloadGamesPending;
    private bool _hasMediaChangesSinceLastReload;
    private DateTime _reloadGamesDueAtUtc = DateTime.MinValue;
    private DateTime _lastReloadGamesAtUtc = DateTime.MinValue;
    private DateTime _lastBlockingScrapeCompletedAtUtc = DateTime.MinValue;
    private int _activeBlockingScrapeCount;
    private int _activeBackgroundScrapeCount;
    private int _activeLivePriorityScrapeCount;
    private bool _startupReloadGamesRequested;
    private string _lastFrontendEvent = string.Empty;
    private DateTime _lastGameSelectedAtUtc = DateTime.MinValue;
    private DateTime _reloadGamesBypassLastGameSelectedUntilUtc = DateTime.MinValue;
    private bool _reloadGamesAllowedDuringActiveScrape;
    private bool _reloadGamesRequestedByScrape;
    private bool _visibleMediaReallocationWorkflowPending;
    private DateTime _visibleMediaReallocationWorkflowDueAtUtc = DateTime.MinValue;
    private VisibleMediaReallocationRequest? _pendingVisibleMediaReallocationRequest;
    private bool _languageGamelistSyncWorkflowPending;
    private DateTime _languageGamelistSyncWorkflowDueAtUtc = DateTime.MinValue;
    private LanguageGamelistSyncWorkflowRequest? _pendingLanguageGamelistSyncWorkflowRequest;
    private bool _romSetManagerWorkflowPending;
    private DateTime _romSetManagerWorkflowDueAtUtc = DateTime.MinValue;
    private RomSetManagerWorkflowRequest? _pendingRomSetManagerWorkflowRequest;
    private readonly Dictionary<string, LocalizedGamelistCacheRefreshEntry> _localizedGamelistCacheRefreshEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScrapeQueueItem> _scrapeQueueItems = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _remoteScrapeInvalidationCts = new();
    private long _remoteScrapeInvalidationVersion;
    private string _remoteScrapeInvalidationReason = string.Empty;
    private DateTime _remoteScrapeInvalidatedAtUtc = DateTime.MinValue;
    private long _scrapeQueueActivityVersion;
    private DateTime _lastScrapeQueueActivityAtUtc = DateTime.MinValue;
    private int _activeMediaReallocationCount;
    private long _mediaReallocationVersion;
    private DateTime _suppressGameSelectedScrapeUntilUtc = DateTime.MinValue;
    private DateTime _suppressAnyGameSelectedScrapeUntilUtc = DateTime.MinValue;
    private string _suppressGameSelectedScrapeReason = string.Empty;
    private string _suppressGameSelectedSystemId = string.Empty;
    private string _suppressGameSelectedGamePath = string.Empty;
    private DateTime _suppressLiveAddGamesUntilUtc = DateTime.MinValue;
    private string _suppressLiveAddGamesReason = string.Empty;
    private string _suppressLiveAddGamesSystemId = string.Empty;
    private string _suppressLiveAddGamesGameSlug = string.Empty;
    private string _currentGameSelectedKey = string.Empty;
    private long _currentGameSelectedSequence;
    private DateTime _currentGameSelectedAtUtc = DateTime.MinValue;
    private string _liveAddGamesPushedSelectionKey = string.Empty;
    private string _liveVideoAddGamesPushedSelectionKey = string.Empty;
    private bool _liveAddGamesSelectionBounceActive;
    private string _postLiveAddGamesExpectedSelectionKey = string.Empty;
    private bool _postLiveAddGamesIgnoreFirstGamelistSelection;
    private bool _gameSessionActive;
    private DateTime _liveAddGamesBlockedUntilUtc = DateTime.MinValue;
    private readonly Dictionary<string, Dictionary<string, string>> _pendingLiveMetadataRestores = new(StringComparer.OrdinalIgnoreCase);

    public void MarkReloadGamesPending(bool requestedByScrape = false)
    {
        RequestReloadGames(requestedByScrape: requestedByScrape, forceMediaDirty: true);
    }

    public void RecordMediaChange(string gameKey, int batchSize)
    {
        lock (_lock)
        {
            _hasMediaChangesSinceLastReload = true;
            _reloadGamesRequestedByScrape = true;
            if (!string.IsNullOrWhiteSpace(gameKey))
            {
                _gamesChangedSinceLastReload.Add(gameKey);
            }

            if (_gamesChangedSinceLastReload.Count >= Math.Max(1, batchSize))
            {
                _reloadGamesPending = true;
                _reloadGamesDueAtUtc = DateTime.UtcNow.Add(TimeSpan.FromSeconds(1));
                _reloadGamesAllowedDuringActiveScrape = true;
                _reloadGamesRequestedByScrape = true;
            }
        }
    }

    public void RequestReloadGames(TimeSpan? debounce = null, bool requestedByScrape = false, bool forceMediaDirty = false)
    {
        var effectiveDebounce = debounce ?? TimeSpan.FromSeconds(2);
        lock (_lock)
        {
            if (forceMediaDirty)
            {
                _hasMediaChangesSinceLastReload = true;
            }

            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = DateTime.UtcNow.Add(effectiveDebounce);
            _reloadGamesAllowedDuringActiveScrape = false;
            _reloadGamesRequestedByScrape = _reloadGamesRequestedByScrape || requestedByScrape;
        }
    }

    public bool TryRequestReloadGames(TimeSpan? debounce = null, TimeSpan? suppressIfReloadedWithin = null)
    {
        var effectiveDebounce = debounce ?? TimeSpan.FromSeconds(2);
        var effectiveSuppressWindow = suppressIfReloadedWithin ?? TimeSpan.FromSeconds(8);
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            if (_lastReloadGamesAtUtc != DateTime.MinValue &&
                nowUtc - _lastReloadGamesAtUtc < effectiveSuppressWindow)
            {
                return false;
            }

            if (_reloadGamesPending)
            {
                _reloadGamesRequestedByScrape = true;
                return false;
            }

            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = nowUtc.Add(effectiveDebounce);
            _reloadGamesAllowedDuringActiveScrape = false;
            _reloadGamesRequestedByScrape = true;
            return true;
        }
    }

    public void RequestReloadGamesBypassingLastGameSelected(TimeSpan? debounce = null)
    {
        var effectiveDebounce = debounce ?? TimeSpan.FromSeconds(2);
        lock (_lock)
        {
            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = DateTime.UtcNow.Add(effectiveDebounce);
            _reloadGamesBypassLastGameSelectedUntilUtc = DateTime.UtcNow.Add(ReloadGamesLastGameSelectedBypassWindow);
            _reloadGamesAllowedDuringActiveScrape = false;
        }
    }

    public void RequestLocalizedGamelistCacheRefreshForGame(MediaProjectionPlan? plan)
    {
        if (plan == null)
        {
            return;
        }

        RequestLocalizedGamelistCacheRefreshForGame(
            plan.FrontendSystemId,
            plan.SystemId,
            plan.GamePath,
            plan.DisplayName,
            plan.GameSlug);
    }

    public void RequestLocalizedGamelistCacheRefreshForGame(LocalizedGamelistCacheRefreshEntry entry)
    {
        RequestLocalizedGamelistCacheRefreshForGame(
            entry.FrontendSystemId,
            entry.SystemId,
            entry.GamePath,
            entry.GameName,
            entry.GameSlug);
    }

    public void RequestLocalizedGamelistCacheRefreshForGame(
        string? frontendSystemId,
        string? systemId,
        string? gamePath,
        string? gameName,
        string? gameSlug)
    {
        if (string.IsNullOrWhiteSpace(frontendSystemId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(gamePath) &&
            string.IsNullOrWhiteSpace(gameName) &&
            string.IsNullOrWhiteSpace(gameSlug))
        {
            return;
        }

        var entry = new LocalizedGamelistCacheRefreshEntry(
            frontendSystemId.Trim(),
            (systemId ?? string.Empty).Trim(),
            (gamePath ?? string.Empty).Trim(),
            (gameName ?? string.Empty).Trim(),
            (gameSlug ?? string.Empty).Trim());
        var key = BuildLocalizedGamelistCacheRefreshKey(entry);
        lock (_lock)
        {
            _localizedGamelistCacheRefreshEntries[key] = entry;
            if (!_reloadGamesPending)
            {
                _reloadGamesPending = true;
                _reloadGamesDueAtUtc = DateTime.UtcNow.Add(PostScrapeQuietPeriod);
                _reloadGamesAllowedDuringActiveScrape = false;
                _reloadGamesRequestedByScrape = true;
            }
        }
    }

    public IReadOnlyList<LocalizedGamelistCacheRefreshEntry> ConsumeLocalizedGamelistCacheRefreshEntries()
    {
        lock (_lock)
        {
            if (_localizedGamelistCacheRefreshEntries.Count == 0)
            {
                return Array.Empty<LocalizedGamelistCacheRefreshEntry>();
            }

            var entries = _localizedGamelistCacheRefreshEntries.Values
                .OrderBy(entry => entry.FrontendSystemId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.GamePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.GameSlug, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _localizedGamelistCacheRefreshEntries.Clear();
            return entries;
        }
    }

    public int DiscardLocalizedGamelistCacheRefreshEntries()
    {
        lock (_lock)
        {
            var count = _localizedGamelistCacheRefreshEntries.Count;
            _localizedGamelistCacheRefreshEntries.Clear();
            return count;
        }
    }

    public void RequestStartupReloadGamesBypassingLastGameSelected(TimeSpan? debounce = null)
    {
        var effectiveDebounce = debounce ?? TimeSpan.FromSeconds(2);
        lock (_lock)
        {
            _startupReloadGamesRequested = true;
            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = DateTime.UtcNow.Add(effectiveDebounce);
            _reloadGamesBypassLastGameSelectedUntilUtc = DateTime.UtcNow.Add(ReloadGamesLastGameSelectedBypassWindow);
            _reloadGamesAllowedDuringActiveScrape = false;
        }
    }

    public bool TryConsumeStartupReloadGamesF5Suppression()
    {
        lock (_lock)
        {
            if (!_startupReloadGamesRequested)
            {
                return false;
            }

            _startupReloadGamesRequested = false;
            return true;
        }
    }

    public bool TryRequestReloadGamesBypassingLastGameSelected(TimeSpan? debounce = null, TimeSpan? suppressIfReloadedWithin = null)
    {
        var effectiveDebounce = debounce ?? TimeSpan.FromSeconds(2);
        var effectiveSuppressWindow = suppressIfReloadedWithin ?? TimeSpan.FromSeconds(8);
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            if (_lastReloadGamesAtUtc != DateTime.MinValue &&
                nowUtc - _lastReloadGamesAtUtc < effectiveSuppressWindow)
            {
                return false;
            }

            if (_reloadGamesPending)
            {
                return false;
            }

            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = nowUtc.Add(effectiveDebounce);
            _reloadGamesBypassLastGameSelectedUntilUtc = nowUtc.Add(ReloadGamesLastGameSelectedBypassWindow);
            _reloadGamesAllowedDuringActiveScrape = false;
            return true;
        }
    }

    public bool ConsumeReloadGamesPending()
    {
        lock (_lock)
        {
            var pending = _reloadGamesPending;
            _reloadGamesPending = false;
            _reloadGamesAllowedDuringActiveScrape = false;
            _reloadGamesRequestedByScrape = false;
            return pending;
        }
    }

    public void RequestVisibleMediaReallocationWorkflow(
        TimeSpan delay,
        VisibleMediaReallocationRequest request)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            var dueAtUtc = nowUtc.Add(delay);
            _visibleMediaReallocationWorkflowPending = true;
            _visibleMediaReallocationWorkflowDueAtUtc = dueAtUtc;
            _pendingVisibleMediaReallocationRequest = CoalesceVisibleMediaReallocationRequest(
                _pendingVisibleMediaReallocationRequest,
                request);
            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = dueAtUtc;
            _reloadGamesBypassLastGameSelectedUntilUtc = nowUtc.Add(ReloadGamesLastGameSelectedBypassWindow);
            _reloadGamesAllowedDuringActiveScrape = false;
            _reloadGamesRequestedByScrape = false;
        }
    }

    public bool TryConsumeVisibleMediaReallocationWorkflow(out VisibleMediaReallocationRequest? request)
    {
        lock (_lock)
        {
            if (!_visibleMediaReallocationWorkflowPending)
            {
                request = null;
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            if (nowUtc < _visibleMediaReallocationWorkflowDueAtUtc)
            {
                request = null;
                return false;
            }

            _visibleMediaReallocationWorkflowPending = false;
            _visibleMediaReallocationWorkflowDueAtUtc = DateTime.MinValue;
            request = _pendingVisibleMediaReallocationRequest;
            _pendingVisibleMediaReallocationRequest = null;
            return request != null;
        }
    }

    private static VisibleMediaReallocationRequest CoalesceVisibleMediaReallocationRequest(
        VisibleMediaReallocationRequest? current,
        VisibleMediaReallocationRequest next)
    {
        if (current == null)
        {
            return next;
        }

        if (string.Equals(current.Scope, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(next.Scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            return new VisibleMediaReallocationRequest("all", string.Empty, next.Reason);
        }

        if (string.Equals(current.SystemId, next.SystemId, StringComparison.OrdinalIgnoreCase))
        {
            return next;
        }

        return new VisibleMediaReallocationRequest("all", string.Empty, next.Reason);
    }

    public void RequestLanguageGamelistSyncWorkflow(
        TimeSpan delay,
        LanguageGamelistSyncWorkflowRequest request)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            var dueAtUtc = nowUtc.Add(delay);
            _languageGamelistSyncWorkflowPending = true;
            _languageGamelistSyncWorkflowDueAtUtc = dueAtUtc;
            _pendingLanguageGamelistSyncWorkflowRequest = CoalesceLanguageGamelistSyncWorkflowRequest(
                _pendingLanguageGamelistSyncWorkflowRequest,
                request);
            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = dueAtUtc;
            _reloadGamesBypassLastGameSelectedUntilUtc = nowUtc.Add(ReloadGamesLastGameSelectedBypassWindow);
            _reloadGamesAllowedDuringActiveScrape = false;
            _reloadGamesRequestedByScrape = false;
            InvalidateRemoteScrapesLocked(nowUtc, "language-gamelist-sync");
        }
    }

    public bool TryConsumeLanguageGamelistSyncWorkflow(out LanguageGamelistSyncWorkflowRequest? request)
    {
        lock (_lock)
        {
            if (!_languageGamelistSyncWorkflowPending)
            {
                request = null;
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            if (nowUtc < _languageGamelistSyncWorkflowDueAtUtc)
            {
                request = null;
                return false;
            }

            _languageGamelistSyncWorkflowPending = false;
            _languageGamelistSyncWorkflowDueAtUtc = DateTime.MinValue;
            request = _pendingLanguageGamelistSyncWorkflowRequest;
            _pendingLanguageGamelistSyncWorkflowRequest = null;
            return request != null;
        }
    }

    public bool HasLanguageGamelistSyncWorkflowPending()
    {
        lock (_lock)
        {
            return _languageGamelistSyncWorkflowPending;
        }
    }

    private static LanguageGamelistSyncWorkflowRequest CoalesceLanguageGamelistSyncWorkflowRequest(
        LanguageGamelistSyncWorkflowRequest? current,
        LanguageGamelistSyncWorkflowRequest next)
    {
        if (current == null)
        {
            return next;
        }

        return next with
        {
            PreviousLanguage = string.IsNullOrWhiteSpace(current.CurrentLanguage)
                ? current.PreviousLanguage
                : current.CurrentLanguage
        };
    }

    public void RequestRomSetManagerWorkflow(
        TimeSpan delay,
        RomSetManagerWorkflowRequest request)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            var dueAtUtc = nowUtc.Add(delay);
            _romSetManagerWorkflowPending = true;
            _romSetManagerWorkflowDueAtUtc = dueAtUtc;
            _pendingRomSetManagerWorkflowRequest = CoalesceRomSetManagerWorkflowRequest(
                _pendingRomSetManagerWorkflowRequest,
                request);
            _reloadGamesPending = true;
            _reloadGamesDueAtUtc = dueAtUtc;
            _reloadGamesBypassLastGameSelectedUntilUtc = nowUtc.Add(ReloadGamesLastGameSelectedBypassWindow);
            _reloadGamesAllowedDuringActiveScrape = false;
            _reloadGamesRequestedByScrape = false;
        }
    }

    public bool TryConsumeRomSetManagerWorkflow(out RomSetManagerWorkflowRequest? request)
    {
        lock (_lock)
        {
            if (!_romSetManagerWorkflowPending)
            {
                request = null;
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            if (nowUtc < _romSetManagerWorkflowDueAtUtc)
            {
                request = null;
                return false;
            }

            _romSetManagerWorkflowPending = false;
            _romSetManagerWorkflowDueAtUtc = DateTime.MinValue;
            request = _pendingRomSetManagerWorkflowRequest;
            _pendingRomSetManagerWorkflowRequest = null;
            return request != null;
        }
    }

    private static RomSetManagerWorkflowRequest CoalesceRomSetManagerWorkflowRequest(
        RomSetManagerWorkflowRequest? current,
        RomSetManagerWorkflowRequest next)
    {
        if (current == null)
        {
            return next;
        }

        if (current.Restore != next.Restore)
        {
            return next;
        }

        if (string.Equals(current.Scope, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(next.Scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            return new RomSetManagerWorkflowRequest(next.Restore, "all", string.Empty, next.Reason);
        }

        if (string.Equals(current.SystemId, next.SystemId, StringComparison.OrdinalIgnoreCase))
        {
            return next;
        }

        return new RomSetManagerWorkflowRequest(next.Restore, "all", string.Empty, next.Reason);
    }

    public bool TryConsumeReloadGamesReady(TimeSpan minimumInterval, out TimeSpan retryAfter)
    {
        return TryConsumeReloadGamesReady(minimumInterval, out retryAfter, out _);
    }

    public bool TryConsumeReloadGamesReady(TimeSpan minimumInterval, out TimeSpan retryAfter, out bool requestedByScrape)
    {
        requestedByScrape = false;
        lock (_lock)
        {
            if (!_reloadGamesPending)
            {
                retryAfter = TimeSpan.Zero;
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            var nextAllowedUtc = _lastReloadGamesAtUtc == DateTime.MinValue
                ? _reloadGamesDueAtUtc
                : Max(_reloadGamesDueAtUtc, _lastReloadGamesAtUtc.Add(minimumInterval));

            if (nowUtc < nextAllowedUtc)
            {
                retryAfter = nextAllowedUtc - nowUtc;
                return false;
            }

            if (_activeBlockingScrapeCount > 0 && !_reloadGamesAllowedDuringActiveScrape)
            {
                retryAfter = TimeSpan.FromMilliseconds(500);
                return false;
            }

            if (!_reloadGamesAllowedDuringActiveScrape && _lastBlockingScrapeCompletedAtUtc != DateTime.MinValue)
            {
                var scrapeQuietUntilUtc = _lastBlockingScrapeCompletedAtUtc.Add(PostScrapeQuietPeriod);
                if (nowUtc < scrapeQuietUntilUtc)
                {
                    retryAfter = scrapeQuietUntilUtc - nowUtc;
                    return false;
                }
            }

            var bypassLastGameSelected = nowUtc <= _reloadGamesBypassLastGameSelectedUntilUtc;
            if (string.Equals(_lastFrontendEvent, "game-selected", StringComparison.OrdinalIgnoreCase) &&
                !bypassLastGameSelected)
            {
                // while the user browses a gamelist this branch repeats forever:
                // a long retry keeps the scheduler quiet instead of waking every
                // 500 ms and flooding the refresh-tracking log
                retryAfter = TimeSpan.FromSeconds(5);
                return false;
            }

            _reloadGamesPending = false;
            _hasMediaChangesSinceLastReload = false;
            _reloadGamesBypassLastGameSelectedUntilUtc = DateTime.MinValue;
            _reloadGamesAllowedDuringActiveScrape = false;
            requestedByScrape = _reloadGamesRequestedByScrape;
            _reloadGamesRequestedByScrape = false;
            _gamesChangedSinceLastReload.Clear();
            _lastReloadGamesAtUtc = nowUtc;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public ReloadGamesStatus GetReloadGamesStatus(TimeSpan minimumInterval)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            var nextAllowedUtc = _lastReloadGamesAtUtc == DateTime.MinValue
                ? _reloadGamesDueAtUtc
                : Max(_reloadGamesDueAtUtc, _lastReloadGamesAtUtc.Add(minimumInterval));

            if (!_reloadGamesPending)
            {
                return new ReloadGamesStatus(false, false, false, _hasMediaChangesSinceLastReload, _activeBlockingScrapeCount, _activeBackgroundScrapeCount, _lastFrontendEvent, TimeSpan.Zero, _reloadGamesAllowedDuringActiveScrape, _reloadGamesRequestedByScrape);
            }

            if (nowUtc < nextAllowedUtc)
            {
                return new ReloadGamesStatus(true, false, false, _hasMediaChangesSinceLastReload, _activeBlockingScrapeCount, _activeBackgroundScrapeCount, _lastFrontendEvent, nextAllowedUtc - nowUtc, _reloadGamesAllowedDuringActiveScrape, _reloadGamesRequestedByScrape);
            }

            if (_activeBlockingScrapeCount > 0 && !_reloadGamesAllowedDuringActiveScrape)
            {
                return new ReloadGamesStatus(true, false, false, _hasMediaChangesSinceLastReload, _activeBlockingScrapeCount, _activeBackgroundScrapeCount, _lastFrontendEvent, TimeSpan.FromMilliseconds(500), _reloadGamesAllowedDuringActiveScrape, _reloadGamesRequestedByScrape);
            }

            if (!_reloadGamesAllowedDuringActiveScrape && _lastBlockingScrapeCompletedAtUtc != DateTime.MinValue)
            {
                var scrapeQuietUntilUtc = _lastBlockingScrapeCompletedAtUtc.Add(PostScrapeQuietPeriod);
                if (nowUtc < scrapeQuietUntilUtc)
                {
                    return new ReloadGamesStatus(true, false, false, _hasMediaChangesSinceLastReload, _activeBlockingScrapeCount, _activeBackgroundScrapeCount, _lastFrontendEvent, scrapeQuietUntilUtc - nowUtc, _reloadGamesAllowedDuringActiveScrape, _reloadGamesRequestedByScrape);
                }
            }

            var bypassLastGameSelected = nowUtc <= _reloadGamesBypassLastGameSelectedUntilUtc;
            if (string.Equals(_lastFrontendEvent, "game-selected", StringComparison.OrdinalIgnoreCase) &&
                !bypassLastGameSelected)
            {
                return new ReloadGamesStatus(true, false, false, _hasMediaChangesSinceLastReload, _activeBlockingScrapeCount, _activeBackgroundScrapeCount, _lastFrontendEvent, TimeSpan.FromMilliseconds(500), _reloadGamesAllowedDuringActiveScrape, _reloadGamesRequestedByScrape);
            }

            return new ReloadGamesStatus(true, true, true, _hasMediaChangesSinceLastReload, _activeBlockingScrapeCount, _activeBackgroundScrapeCount, _lastFrontendEvent, TimeSpan.Zero, _reloadGamesAllowedDuringActiveScrape, _reloadGamesRequestedByScrape);
        }
    }

    public void SetLastFrontendEvent(string eventName)
    {
        lock (_lock)
        {
            _lastFrontendEvent = eventName ?? string.Empty;
            if (string.Equals(_lastFrontendEvent, "game-start", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_lastFrontendEvent, "ui.game.started", StringComparison.OrdinalIgnoreCase))
            {
                _gameSessionActive = true;
                _liveAddGamesBlockedUntilUtc = DateTime.MaxValue;
                ClearCurrentLiveAddGamesSelection();
            }
            else if (string.Equals(_lastFrontendEvent, "game-end", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_lastFrontendEvent, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                _gameSessionActive = false;
                _liveAddGamesBlockedUntilUtc = DateTime.UtcNow.Add(PostGameEndLiveAddGamesQuietPeriod);
                ClearCurrentLiveAddGamesSelection();
            }
            else if (string.Equals(_lastFrontendEvent, "game-selected", StringComparison.OrdinalIgnoreCase))
            {
                _lastGameSelectedAtUtc = DateTime.UtcNow;
                MarkScrapeQueueActivity();
            }
            else
            {
                ClearCurrentLiveAddGamesSelection();
            }
        }
    }

    public bool ShouldBlockLiveAddGames(out string reason, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            if (_gameSessionActive)
            {
                reason = "game-start-active";
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (nowUtc <= _liveAddGamesBlockedUntilUtc)
            {
                reason = "post-game-end-quiet";
                retryAfter = _liveAddGamesBlockedUntilUtc - nowUtc;
                return true;
            }

            _liveAddGamesBlockedUntilUtc = DateTime.MinValue;
            reason = string.Empty;
            retryAfter = TimeSpan.Zero;
            return false;
        }
    }

    public void RecordGameSelectedSelection(string systemId, string gamePath)
    {
        var selectionKey = BuildSelectionKey(systemId, gamePath);
        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            return;
        }

        lock (_lock)
        {
            if (_liveAddGamesSelectionBounceActive &&
                !string.IsNullOrWhiteSpace(_liveAddGamesPushedSelectionKey))
            {
                if (string.Equals(selectionKey, _liveAddGamesPushedSelectionKey, StringComparison.OrdinalIgnoreCase))
                {
                    SetCurrentGameSelectedKey(selectionKey);
                    _liveAddGamesSelectionBounceActive = false;
                    ClearPostLiveAddGamesFirstGamelistGuard();
                    return;
                }

                _liveAddGamesSelectionBounceActive = false;
            }

            SetCurrentGameSelectedKey(selectionKey);
        }
    }

    public bool ShouldIgnorePostLiveAddGamesFirstGamelistSelection(
        string systemId,
        string gamePath,
        bool isFirstGamelistEntry,
        out string reason)
    {
        var selectionKey = BuildSelectionKey(systemId, gamePath);
        lock (_lock)
        {
            if (!_postLiveAddGamesIgnoreFirstGamelistSelection ||
                !isFirstGamelistEntry ||
                string.IsNullOrWhiteSpace(selectionKey) ||
                string.IsNullOrWhiteSpace(_postLiveAddGamesExpectedSelectionKey))
            {
                reason = string.Empty;
                return false;
            }

            if (string.Equals(selectionKey, _postLiveAddGamesExpectedSelectionKey, StringComparison.OrdinalIgnoreCase))
            {
                ClearPostLiveAddGamesFirstGamelistGuard();
                reason = string.Empty;
                return false;
            }

            ClearPostLiveAddGamesFirstGamelistGuard();
            reason = "post-live-addgames-first-gamelist-selection";
            return true;
        }
    }

    public void ClearPostLiveAddGamesFirstGamelistSelectionGuard()
    {
        lock (_lock)
        {
            ClearPostLiveAddGamesFirstGamelistGuard();
        }
    }

    public void ClearPostLiveAddGamesFirstGamelistSelectionGuardForSystemChange(string systemId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(systemId) ||
                string.IsNullOrWhiteSpace(_postLiveAddGamesExpectedSelectionKey) ||
                !SelectionKeyBelongsToSystem(_postLiveAddGamesExpectedSelectionKey, systemId))
            {
                ClearPostLiveAddGamesFirstGamelistGuard();
            }
        }
    }

    public bool ShouldSuppressLiveAddGamesForSelection(
        string systemId,
        string gamePath,
        out string reason,
        bool allowVideoException = false)
    {
        var selectionKey = BuildSelectionKey(systemId, gamePath);
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(selectionKey))
            {
                reason = string.Empty;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_currentGameSelectedKey) &&
                !string.Equals(_currentGameSelectedKey, selectionKey, StringComparison.OrdinalIgnoreCase))
            {
                reason = "not-current-selection";
                return true;
            }

            if (string.Equals(_liveAddGamesPushedSelectionKey, selectionKey, StringComparison.OrdinalIgnoreCase))
            {
                if (allowVideoException &&
                    !string.Equals(_liveVideoAddGamesPushedSelectionKey, selectionKey, StringComparison.OrdinalIgnoreCase))
                {
                    reason = string.Empty;
                    return false;
                }

                // Immutable workflow rule: one game-selected card can receive at most
                // one live /addgames, except the explicitly approved video case:
                // a freshly scraped video for the still-current card may consume one
                // additional /addgames. Time-based spacing is a safety delay only and
                // must never reset this selection gate.
                reason = allowVideoException
                    ? "already-pushed-current-selection-video"
                    : "already-pushed-current-selection";
                return true;
            }

            reason = string.Empty;
            return false;
        }
    }

    private bool? _addGamesSupported;
    private int _consecutiveQualifiedAddGamesNoContent;

    /// <summary>null = unknown, true = ES accepts raw /addgames bodies, false =
    /// this ES build has the upstream file-guard regression (every push → 204).</summary>
    public bool? AddGamesSupported
    {
        get
        {
            lock (_lock)
            {
                return _addGamesSupported;
            }
        }
    }

    public bool IsAddGamesUnsupported => AddGamesSupported == false;

    /// <summary>Records ES's answer to a QUALIFIED live addgames push (a fragment
    /// that passed every APIExpose delta/selection gate, targeting a game ES
    /// itself listed). On a healthy ES such a push cannot answer 204; two
    /// consecutive 204s therefore mean the build rejects raw /addgames bodies.
    /// Returns true only on the call that flips the state to unsupported.</summary>
    public bool RecordQualifiedAddGamesOutcome(bool noContent)
    {
        lock (_lock)
        {
            if (_addGamesSupported == false)
            {
                return false;
            }

            if (!noContent)
            {
                _addGamesSupported = true;
                _consecutiveQualifiedAddGamesNoContent = 0;
                return false;
            }

            _consecutiveQualifiedAddGamesNoContent++;
            if (_consecutiveQualifiedAddGamesNoContent >= 2)
            {
                _addGamesSupported = false;
                return true;
            }

            return false;
        }
    }

    private DateTime _lastEsUiRefreshPushUtc = DateTime.MinValue;

    /// <summary>Records that APIExpose just pushed a UI refresh to EmulationStation
    /// (live /addgames, /reloadgames). ES re-fires game-selected for every view it
    /// refreshes shortly after — including stale cursors from non-visible views —
    /// so selection consumers defer publication until that re-fire burst settles.</summary>
    public void RecordEsUiRefreshPush()
    {
        lock (_lock)
        {
            _lastEsUiRefreshPushUtc = DateTime.UtcNow;
        }
    }

    public bool IsWithinPostEsRefreshWindow(TimeSpan window)
    {
        lock (_lock)
        {
            return DateTime.UtcNow - _lastEsUiRefreshPushUtc <= window;
        }
    }

    public void MarkLiveAddGamesPushedForSelection(string systemId, string gamePath, bool videoException = false)
    {
        var selectionKey = BuildSelectionKey(systemId, gamePath);
        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            return;
        }

        lock (_lock)
        {
            // the ES push happened regardless of the bookkeeping below: always
            // arm the post-refresh window so re-fired selections are deferred
            _lastEsUiRefreshPushUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(_currentGameSelectedKey) &&
                !string.Equals(_currentGameSelectedKey, selectionKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentGameSelectedKey))
            {
                SetCurrentGameSelectedKey(selectionKey);
            }

            _liveAddGamesPushedSelectionKey = selectionKey;
            if (videoException)
            {
                _liveVideoAddGamesPushedSelectionKey = selectionKey;
            }

            _liveAddGamesSelectionBounceActive = true;
            _postLiveAddGamesExpectedSelectionKey = selectionKey;
            _postLiveAddGamesIgnoreFirstGamelistSelection = true;
        }
    }

    public CurrentGameSelectionToken CaptureCurrentGameSelection()
    {
        lock (_lock)
        {
            return new CurrentGameSelectionToken(
                _currentGameSelectedKey,
                _currentGameSelectedSequence,
                _currentGameSelectedAtUtc);
        }
    }

    public bool HasCurrentGameSelection()
    {
        lock (_lock)
        {
            return !string.IsNullOrWhiteSpace(_currentGameSelectedKey);
        }
    }

    public bool IsCurrentGameSelection(string systemId, string gamePath)
    {
        var selectionKey = BuildSelectionKey(systemId, gamePath);
        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            return false;
        }

        lock (_lock)
        {
            return string.Equals(_currentGameSelectedKey, selectionKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsCurrentGameSelectionToken(CurrentGameSelectionToken token, string systemId, string gamePath)
    {
        if (!token.IsValid)
        {
            return false;
        }

        var selectionKey = BuildSelectionKey(systemId, gamePath);
        if (string.IsNullOrWhiteSpace(selectionKey) ||
            !string.Equals(token.SelectionKey, selectionKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_lock)
        {
            return token.Sequence == _currentGameSelectedSequence &&
                string.Equals(_currentGameSelectedKey, selectionKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SuppressGameSelectedScrapeAfterLiveAddGames(TimeSpan duration, string systemId = "", string gamePath = "")
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            _suppressGameSelectedScrapeUntilUtc = nowUtc.Add(duration);
            _suppressAnyGameSelectedScrapeUntilUtc = nowUtc.Add(TimeSpan.FromMilliseconds(1500));
            _suppressGameSelectedScrapeReason = "live-addgames-refresh";
            _suppressGameSelectedSystemId = NormalizeSelectionValue(systemId);
            _suppressGameSelectedGamePath = NormalizeSelectionPath(gamePath, systemId);
        }
    }

    public bool ShouldSuppressGameSelectedScrape(string systemId, string gamePath, out string reason)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            if (nowUtc > _suppressGameSelectedScrapeUntilUtc)
            {
                ClearGameSelectedScrapeSuppression();
                reason = string.Empty;
                return false;
            }

            if (nowUtc <= _suppressAnyGameSelectedScrapeUntilUtc)
            {
                reason = _suppressGameSelectedScrapeReason;
                return true;
            }

            var sameSystem = string.IsNullOrWhiteSpace(_suppressGameSelectedSystemId) ||
                string.Equals(_suppressGameSelectedSystemId, NormalizeSelectionValue(systemId), StringComparison.OrdinalIgnoreCase);
            var samePath = string.IsNullOrWhiteSpace(_suppressGameSelectedGamePath) ||
                string.Equals(_suppressGameSelectedGamePath, NormalizeSelectionPath(gamePath, systemId), StringComparison.OrdinalIgnoreCase);
            if (sameSystem && samePath)
            {
                reason = _suppressGameSelectedScrapeReason;
                return true;
            }

            ClearGameSelectedScrapeSuppression();
            reason = string.Empty;
            return false;
        }
    }

    public bool ShouldSuppressGameSelectedScrape(out string reason)
    {
        return ShouldSuppressGameSelectedScrape(string.Empty, string.Empty, out reason);
    }

    public void SuppressLiveAddGamesAfterThemeRefresh(string systemId, string gameSlug)
    {
        lock (_lock)
        {
            _suppressLiveAddGamesUntilUtc = DateTime.MaxValue;
            _suppressLiveAddGamesReason = "hyperbat-theme-refresh";
            _suppressLiveAddGamesSystemId = NormalizeSelectionValue(systemId);
            _suppressLiveAddGamesGameSlug = NormalizeGameSlugValue(gameSlug);
        }
    }

    public void ClearLiveAddGamesSuppressionOnGameSelected(string systemId, string gamePath, string gameName)
    {
        lock (_lock)
        {
            var sameSystem = string.IsNullOrWhiteSpace(_suppressLiveAddGamesSystemId) ||
                string.Equals(_suppressLiveAddGamesSystemId, NormalizeSelectionValue(systemId), StringComparison.OrdinalIgnoreCase);
            if (!sameSystem)
            {
                ClearLiveAddGamesSuppression();
                return;
            }

            var pathSlug = NormalizeGameSlugValue(Path.GetFileNameWithoutExtension(gamePath));
            var nameSlug = NormalizeGameSlugValue(gameName);
            if (string.Equals(_suppressLiveAddGamesGameSlug, pathSlug, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_suppressLiveAddGamesGameSlug, nameSlug, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ClearLiveAddGamesSuppression();
        }
    }

    public bool ShouldSuppressLiveAddGames(string systemId, string gameSlug, out string reason)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            if (nowUtc > _suppressLiveAddGamesUntilUtc)
            {
                ClearLiveAddGamesSuppression();
                reason = string.Empty;
                return false;
            }

            var sameSystem = string.IsNullOrWhiteSpace(_suppressLiveAddGamesSystemId) ||
                string.Equals(_suppressLiveAddGamesSystemId, NormalizeSelectionValue(systemId), StringComparison.OrdinalIgnoreCase);
            var sameGame = string.IsNullOrWhiteSpace(_suppressLiveAddGamesGameSlug) ||
                string.Equals(_suppressLiveAddGamesGameSlug, NormalizeGameSlugValue(gameSlug), StringComparison.OrdinalIgnoreCase);
            if (sameSystem && sameGame)
            {
                reason = _suppressLiveAddGamesReason;
                return true;
            }

            reason = string.Empty;
            return false;
        }
    }

    public void QueuePendingLiveMetadataRestore(
        string systemId,
        string gameSlug,
        string gamePath,
        string fieldName,
        string value)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        lock (_lock)
        {
            var key = BuildPendingLiveMetadataRestoreKey(systemId, gameSlug, gamePath);
            if (!_pendingLiveMetadataRestores.TryGetValue(key, out var fields))
            {
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _pendingLiveMetadataRestores[key] = fields;
            }

            fields[fieldName.Trim()] = value ?? string.Empty;
        }
    }

    public IReadOnlyDictionary<string, string> GetPendingLiveMetadataRestore(
        string systemId,
        string gameSlug,
        string gamePath)
    {
        lock (_lock)
        {
            var key = BuildPendingLiveMetadataRestoreKey(systemId, gameSlug, gamePath);
            return _pendingLiveMetadataRestores.TryGetValue(key, out var fields)
                ? new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void ClearPendingLiveMetadataRestore(
        string systemId,
        string gameSlug,
        string gamePath)
    {
        lock (_lock)
        {
            _pendingLiveMetadataRestores.Remove(BuildPendingLiveMetadataRestoreKey(systemId, gameSlug, gamePath));
        }
    }

    private void ClearGameSelectedScrapeSuppression()
    {
        _suppressGameSelectedScrapeUntilUtc = DateTime.MinValue;
        _suppressAnyGameSelectedScrapeUntilUtc = DateTime.MinValue;
        _suppressGameSelectedScrapeReason = string.Empty;
        _suppressGameSelectedSystemId = string.Empty;
        _suppressGameSelectedGamePath = string.Empty;
    }

    private void ClearLiveAddGamesSuppression()
    {
        _suppressLiveAddGamesUntilUtc = DateTime.MinValue;
        _suppressLiveAddGamesReason = string.Empty;
        _suppressLiveAddGamesSystemId = string.Empty;
        _suppressLiveAddGamesGameSlug = string.Empty;
    }

    private void SetCurrentGameSelectedKey(string selectionKey)
    {
        if (string.Equals(_currentGameSelectedKey, selectionKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentGameSelectedKey = selectionKey;
        _currentGameSelectedSequence++;
        _currentGameSelectedAtUtc = DateTime.UtcNow;
        _liveAddGamesPushedSelectionKey = string.Empty;
        _liveVideoAddGamesPushedSelectionKey = string.Empty;
        _liveAddGamesSelectionBounceActive = false;
        ClearPostLiveAddGamesFirstGamelistGuard();
    }

    private void ClearCurrentLiveAddGamesSelection()
    {
        if (!string.IsNullOrWhiteSpace(_currentGameSelectedKey))
        {
            _currentGameSelectedSequence++;
        }

        _currentGameSelectedKey = string.Empty;
        _currentGameSelectedAtUtc = DateTime.MinValue;
        _liveAddGamesPushedSelectionKey = string.Empty;
        _liveVideoAddGamesPushedSelectionKey = string.Empty;
        _liveAddGamesSelectionBounceActive = false;
        ClearPostLiveAddGamesFirstGamelistGuard();
    }

    private void ClearPostLiveAddGamesFirstGamelistGuard()
    {
        _postLiveAddGamesExpectedSelectionKey = string.Empty;
        _postLiveAddGamesIgnoreFirstGamelistSelection = false;
    }

    private static string NormalizeSelectionValue(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizeSelectionPath(string? value, string? systemId = null)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        try
        {
            var platformPath = normalized.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(platformPath))
            {
                return Path.GetFullPath(platformPath)
                    .Replace('\\', '/')
                    .Trim()
                    .ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(systemId))
            {
                var relativePath = platformPath;
                if (relativePath.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    relativePath = relativePath[2..];
                }

                return Path.GetFullPath(Path.Combine(RetroBatPaths.RomsRoot, systemId.Trim(), relativePath))
                    .Replace('\\', '/')
                    .Trim()
                    .ToLowerInvariant();
            }
        }
        catch
        {
        }

        return normalized
            .TrimStart('.', '/')
            .ToLowerInvariant();
    }

    private static string NormalizeGameSlugValue(string? value)
    {
        var normalized = NormalizeSelectionValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var chars = normalized
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars)
            .Trim('_')
            .Replace("__", "_", StringComparison.Ordinal);
    }

    private static string BuildPendingLiveMetadataRestoreKey(string systemId, string gameSlug, string gamePath)
    {
        return string.Join(
            "|",
            NormalizeSelectionValue(systemId),
            NormalizeGameSlugValue(gameSlug),
            NormalizeSelectionPath(gamePath));
    }

    private static string BuildLocalizedGamelistCacheRefreshKey(LocalizedGamelistCacheRefreshEntry entry)
    {
        return string.Join(
            "|",
            NormalizeSelectionValue(entry.FrontendSystemId),
            NormalizeSelectionPath(entry.GamePath),
            NormalizeGameSlugValue(entry.GameSlug),
            NormalizeGameSlugValue(entry.GameName));
    }

    private static string BuildSelectionKey(string systemId, string gamePath)
    {
        return string.Join(
            "|",
            NormalizeSelectionValue(systemId),
            NormalizeSelectionPath(gamePath, systemId));
    }

    private static bool SelectionKeyBelongsToSystem(string selectionKey, string systemId)
    {
        var separatorIndex = selectionKey.IndexOf('|');
        if (separatorIndex <= 0)
        {
            return false;
        }

        return string.Equals(
            selectionKey[..separatorIndex],
            NormalizeSelectionValue(systemId),
            StringComparison.OrdinalIgnoreCase);
    }

    public bool HasMediaChangesPending()
    {
        lock (_lock)
        {
            return _hasMediaChangesSinceLastReload;
        }
    }

    public void BeginScrapeActivity(bool blocksReload)
    {
        lock (_lock)
        {
            if (blocksReload)
            {
                _activeBlockingScrapeCount++;
            }
            else
            {
                _activeBackgroundScrapeCount++;
            }
        }
    }

    public IDisposable BeginMediaReallocation(string reason)
    {
        lock (_lock)
        {
            _activeMediaReallocationCount++;
            _mediaReallocationVersion++;
        }

        return new MediaReallocationLease(this);
    }

    public bool IsMediaReallocationActive()
    {
        lock (_lock)
        {
            return _activeMediaReallocationCount > 0;
        }
    }

    public async Task WaitForMediaReallocationIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_activeMediaReallocationCount <= 0)
                {
                    return;
                }
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    public IDisposable BeginLivePriorityScrape()
    {
        lock (_lock)
        {
            _activeLivePriorityScrapeCount++;
            MarkScrapeQueueActivity();
        }

        return new LivePriorityScrapeLease(this);
    }

    public bool IsLivePriorityScrapeActive()
    {
        lock (_lock)
        {
            return _activeLivePriorityScrapeCount > 0;
        }
    }

    public bool IsRemoteScrapeQueueAllowed(TimeSpan gameSelectedQuietPeriod, out string reason)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            if (_activeBlockingScrapeCount > 0 || _activeLivePriorityScrapeCount > 0)
            {
                reason = "live-scrape-active";
                return false;
            }

            if (_languageGamelistSyncWorkflowPending)
            {
                reason = "language-gamelist-sync-pending";
                return false;
            }

            if (_lastGameSelectedAtUtc != DateTime.MinValue &&
                nowUtc - _lastGameSelectedAtUtc < gameSelectedQuietPeriod)
            {
                reason = "recent-game-selected";
                return false;
            }

            if (_activeMediaReallocationCount > 0)
            {
                reason = "media-reallocation-active";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    public async Task WaitForLivePriorityScrapeIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_activeLivePriorityScrapeCount <= 0)
                {
                    return;
                }
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public MediaReallocationStatus GetMediaReallocationStatus()
    {
        lock (_lock)
        {
            return new MediaReallocationStatus(_activeMediaReallocationCount, _mediaReallocationVersion);
        }
    }

    public void TrackScrapeQueued(string jobKey, string systemId, string gameSlug, string displayName, string kind, bool blocksReload)
    {
        if (string.IsNullOrWhiteSpace(jobKey))
        {
            return;
        }

        lock (_lock)
        {
            _scrapeQueueItems[jobKey] = new ScrapeQueueItem(
                jobKey,
                systemId ?? string.Empty,
                gameSlug ?? string.Empty,
                string.IsNullOrWhiteSpace(displayName) ? gameSlug ?? string.Empty : displayName,
                kind ?? string.Empty,
                blocksReload,
                false,
                DateTime.UtcNow,
                null);
            MarkScrapeQueueActivity();
        }
    }

    public RemoteScrapeInvalidationSnapshot GetRemoteScrapeInvalidationSnapshot()
    {
        lock (_lock)
        {
            return new RemoteScrapeInvalidationSnapshot(
                _remoteScrapeInvalidationVersion,
                _remoteScrapeInvalidationReason,
                _remoteScrapeInvalidatedAtUtc,
                _remoteScrapeInvalidationCts.Token);
        }
    }

    public bool IsRemoteScrapeInvalidated(long version)
    {
        lock (_lock)
        {
            return version != _remoteScrapeInvalidationVersion;
        }
    }

    public void TrackScrapeStarted(string jobKey)
    {
        if (string.IsNullOrWhiteSpace(jobKey))
        {
            return;
        }

        lock (_lock)
        {
            if (_scrapeQueueItems.TryGetValue(jobKey, out var item))
            {
                _scrapeQueueItems[jobKey] = item with
                {
                    IsRunning = true,
                    StartedAtUtc = DateTime.UtcNow
                };
                MarkScrapeQueueActivity();
            }
        }
    }

    public void TrackScrapeCompleted(string jobKey)
    {
        if (string.IsNullOrWhiteSpace(jobKey))
        {
            return;
        }

        lock (_lock)
        {
            if (_scrapeQueueItems.Remove(jobKey))
            {
                MarkScrapeQueueActivity();
            }
        }
    }

    public ScrapeQueueSnapshot GetScrapeQueueSnapshot()
    {
        lock (_lock)
        {
            var total = _scrapeQueueItems.Count;
            var running = _scrapeQueueItems.Values.Count(item => item.IsRunning);
            var queued = Math.Max(0, total - running);
            var current = _scrapeQueueItems.Values
                .OrderByDescending(item => item.IsRunning)
                .ThenBy(item => item.StartedAtUtc ?? item.QueuedAtUtc)
                .FirstOrDefault();

            return new ScrapeQueueSnapshot(total, running, queued, current, _scrapeQueueActivityVersion, _lastScrapeQueueActivityAtUtc);
        }
    }

    public void EndScrapeActivity(bool blocksReload)
    {
        lock (_lock)
        {
            if (blocksReload)
            {
                if (_activeBlockingScrapeCount > 0)
                {
                    _activeBlockingScrapeCount--;
                    if (_activeBlockingScrapeCount == 0)
                    {
                        _lastBlockingScrapeCompletedAtUtc = DateTime.UtcNow;
                    }
                }
            }
            else if (_activeBackgroundScrapeCount > 0)
            {
                _activeBackgroundScrapeCount--;
            }
        }
    }

    public int GetActiveScrapeCount()
    {
        lock (_lock)
        {
            return _activeBlockingScrapeCount + _activeBackgroundScrapeCount;
        }
    }

    public int GetActiveBlockingScrapeCount()
    {
        lock (_lock)
        {
            return _activeBlockingScrapeCount;
        }
    }

    private static DateTime Max(DateTime left, DateTime right)
    {
        return left >= right ? left : right;
    }

    private void MarkScrapeQueueActivity()
    {
        _scrapeQueueActivityVersion++;
        _lastScrapeQueueActivityAtUtc = DateTime.UtcNow;
    }

    private void InvalidateRemoteScrapesLocked(DateTime nowUtc, string reason)
    {
        _remoteScrapeInvalidationVersion++;
        _remoteScrapeInvalidationReason = string.IsNullOrWhiteSpace(reason) ? "invalidated" : reason.Trim();
        _remoteScrapeInvalidatedAtUtc = nowUtc;
        try
        {
            _remoteScrapeInvalidationCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _remoteScrapeInvalidationCts = new CancellationTokenSource();
        MarkScrapeQueueActivity();
    }

    private void EndMediaReallocation()
    {
        lock (_lock)
        {
            if (_activeMediaReallocationCount > 0)
            {
                _activeMediaReallocationCount--;
                _mediaReallocationVersion++;
            }
        }
    }

    private void EndLivePriorityScrape()
    {
        lock (_lock)
        {
            if (_activeLivePriorityScrapeCount > 0)
            {
                _activeLivePriorityScrapeCount--;
                MarkScrapeQueueActivity();
            }
        }
    }

    private sealed class MediaReallocationLease : IDisposable
    {
        private readonly MediaRuntimeState _owner;
        private bool _disposed;

        public MediaReallocationLease(MediaRuntimeState owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.EndMediaReallocation();
        }
    }

    private sealed class LivePriorityScrapeLease : IDisposable
    {
        private readonly MediaRuntimeState _owner;
        private bool _disposed;

        public LivePriorityScrapeLease(MediaRuntimeState owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.EndLivePriorityScrape();
        }
    }
}

public sealed record ReloadGamesStatus(
    bool Pending,
    bool Ready,
    bool HasMediaChangesPending,
    bool DirtySinceLastReload,
    int ActiveScrapeCount,
    int BackgroundScrapeCount,
    string LastFrontendEvent,
    TimeSpan RetryAfter,
    bool ReloadAllowedDuringActiveScrape,
    bool RequestedByScrape);

public sealed record ScrapeQueueSnapshot(
    int TotalJobs,
    int RunningJobs,
    int QueuedJobs,
    ScrapeQueueItem? CurrentJob,
    long ActivityVersion,
    DateTime LastActivityAtUtc);

public sealed record RemoteScrapeInvalidationSnapshot(
    long Version,
    string Reason,
    DateTime InvalidatedAtUtc,
    CancellationToken CancellationToken);

public sealed record MediaReallocationStatus(
    int ActiveCount,
    long Version);

public sealed record VisibleMediaReallocationRequest(
    string Scope,
    string SystemId,
    string Reason);

public sealed record VisibleMediaReallocationReloadSummary(
    int SystemsUpdated,
    string Scope,
    string SystemId);

public sealed record LanguageGamelistSyncWorkflowRequest(
    string PreviousLanguage,
    string CurrentLanguage,
    string Reason);

public sealed record LanguageGamelistSyncWorkflowReloadSummary(
    string PreviousLanguage,
    string CurrentLanguage,
    int SystemsUpdated);

public sealed record RomSetManagerWorkflowRequest(
    bool Restore,
    string Scope,
    string SystemId,
    string Reason);

public sealed record RomSetManagerWorkflowReloadSummary(
    bool Restore,
    string Scope,
    string SystemId,
    int SystemsProcessed,
    int GamesScanned,
    int GamesToHide,
    int GamesToRestore,
    int GamesChanged,
    int WarningCount,
    string Message);

public sealed record LocalizedGamelistCacheRefreshEntry(
    string FrontendSystemId,
    string SystemId,
    string GamePath,
    string GameName,
    string GameSlug);

public sealed record ScrapeQueueItem(
    string JobKey,
    string SystemId,
    string GameSlug,
    string DisplayName,
    string Kind,
    bool BlocksReload,
    bool IsRunning,
    DateTime QueuedAtUtc,
    DateTime? StartedAtUtc);
