using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public class EsControllerService
{
    private const int MaxPageNavigationFineCorrectionInputs = 60;
    private const int MaxUnknownViewRecoveryAttempts = 3;

    private readonly ApiContext _context;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly EsControllerInputBackendProvider _backendProvider;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly ILogger<EsControllerService>? _logger;
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://127.0.0.1:1234") };
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private readonly object _selectionLock = new();
    private CancellationTokenSource? _navigationCts;
    private EsSelectionSnapshot? _lastSelection;

    public EsControllerService(
        ApiContext context,
        IOptionsMonitor<ApiExposeOptions> options,
        EsControllerInputBackendProvider backendProvider,
        GamelistUpdateService gamelistUpdateService,
        ILogger<EsControllerService>? logger = null)
    {
        _context = context;
        _options = options;
        _backendProvider = backendProvider;
        _gamelistUpdateService = gamelistUpdateService;
        _logger = logger;
    }

    public EsControllerStatus GetStatus()
    {
        var options = _options.CurrentValue.EsController;
        var current = GetCurrentSelection();
        var backendStatus = GetBackendStatus(options);
        return new EsControllerStatus
        {
            Enabled = options.Enabled,
            Ready = options.Enabled && backendStatus.Ready,
            Backend = options.Backend,
            DryRun = backendStatus.DryRun,
            EsRunning = IsEmulationStationRunning(),
            CurrentView = GetCurrentView(),
            CurrentSystem = current.SystemId,
            CurrentGame = current.GamePath,
            NavigationInProgress = _navigationLock.CurrentCount == 0,
            BackendStatus = backendStatus,
            LastSelection = GetLastSelection(),
            SupportedInputs = EsControllerInputs.Allowed,
            Message = BuildStatusMessage(options, backendStatus)
        };
    }

    public bool ShouldRestoreSelectionAfterReloadGames()
    {
        var options = _options.CurrentValue.EsController;
        return options.Enabled && options.RestoreSelectionAfterReloadGames;
    }

    public int GetRestoreSelectionDelayMs()
    {
        return Math.Clamp(_options.CurrentValue.EsController.RestoreSelectionDelayMs, 0, 10000);
    }

    public EsSelectionSnapshot CaptureSelection(string source)
    {
        var current = GetCurrentSelection();
        if (!string.IsNullOrWhiteSpace(current.SystemId) ||
            !string.IsNullOrWhiteSpace(current.GamePath) ||
            !string.IsNullOrWhiteSpace(current.GameName))
        {
            var snapshot = new EsSelectionSnapshot
            {
                SystemId = current.SystemId,
                GamePath = current.GamePath,
                GameName = current.GameName,
                GameId = current.GameId,
                Source = source,
                CapturedAtUtc = DateTime.UtcNow
            };
            SetLastSelection(snapshot);
            return snapshot;
        }

        var empty = new EsSelectionSnapshot
        {
            Source = source,
            CapturedAtUtc = DateTime.UtcNow
        };
        SetLastSelection(empty);
        return empty;
    }

    public EsSelectionSnapshot? GetLastSelection()
    {
        lock (_selectionLock)
        {
            return _lastSelection == null ? null : Clone(_lastSelection);
        }
    }

    public async Task<EsControllerActionResult> TapAsync(EsControllerTapRequest request, CancellationToken cancellationToken)
    {
        var input = EsControllerInputs.Normalize(request.Input);
        if (!EsControllerInputs.IsAllowed(input))
        {
            return Failure("tap", "invalid_input", $"Unsupported input '{request.Input}'.");
        }

        var count = Math.Clamp(request.Count <= 0 ? 1 : request.Count, 1, 200);
        var inputs = Enumerable.Repeat(input, count).ToList();
        return await SendSequenceAsync("tap", inputs, request.HoldMs, request.GapMs, cancellationToken);
    }

    public async Task<EsControllerActionResult> ComboAsync(EsControllerComboRequest request, CancellationToken cancellationToken)
    {
        var inputs = request.Inputs
            .Select(EsControllerInputs.Normalize)
            .Where(input => !string.IsNullOrWhiteSpace(input))
            .ToList();

        var invalid = inputs.FirstOrDefault(input => !EsControllerInputs.IsAllowed(input));
        if (!string.IsNullOrWhiteSpace(invalid))
        {
            return Failure("combo", "invalid_input", $"Unsupported input '{invalid}'.");
        }

        if (inputs.Count == 0)
        {
            return Failure("combo", "empty_sequence", "No input was provided.");
        }

        return await SendSequenceAsync("combo", inputs, request.HoldMs, request.GapMs, cancellationToken);
    }

    public async Task<EsControllerActionResult> StopAsync()
    {
        _navigationCts?.Cancel();
        await ReleaseAllAsync();
        return new EsControllerActionResult
        {
            Success = true,
            Action = "stop",
            Status = "stopped"
        };
    }

    public async Task<EsControllerProbeViewResult> ProbeViewAsync(EsControllerProbeViewRequest request, CancellationToken cancellationToken)
    {
        await _navigationLock.WaitAsync(cancellationToken);
        using var linkedCts = BeginNavigation(cancellationToken);
        try
        {
            return await ProbeViewCoreAsync(request, linkedCts.Token);
        }
        finally
        {
            EndNavigation(linkedCts);
            _navigationLock.Release();
        }
    }

    public async Task<EsControllerActionResult> RightClickAsync(EsControllerRightClickRequest request, CancellationToken cancellationToken)
    {
        await _navigationLock.WaitAsync(cancellationToken);
        using var linkedCts = BeginNavigation(cancellationToken);
        try
        {
            return await RightClickCoreAsync(request, linkedCts.Token);
        }
        finally
        {
            EndNavigation(linkedCts);
            _navigationLock.Release();
        }
    }

    public async Task<EsControllerActionResult> GotoSystemAsync(EsControllerGotoSystemRequest request, CancellationToken cancellationToken)
    {
        var targetSystem = NormalizeId(request.System);
        if (string.IsNullOrWhiteSpace(targetSystem))
        {
            return Failure("goto-system", "missing_system", "System is required.");
        }

        await _navigationLock.WaitAsync(cancellationToken);
        using var linkedCts = BeginNavigation(cancellationToken);
        try
        {
            var previous = GetCurrentSelection();
            var currentView = GetCurrentView();
            if (currentView.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(previous.GamePath))
            {
                var viewProbe = await EnsureKnownViewAsync("goto-system", linkedCts.Token);
                if (!viewProbe.Success)
                {
                    var failure = Failure(
                        "goto-system",
                        viewProbe.Reason,
                        viewProbe.Message,
                        targetSystem: targetSystem);
                    failure.PreviousSystem = previous.SystemId;
                    failure.PreviousGame = previous.GamePath;
                    return failure;
                }

                currentView = viewProbe.View;
            }

            var systems = await LoadSystemsAsync(linkedCts.Token);
            var path = BuildSystemPath(systems, previous.SystemId, targetSystem, request.Enter, currentView);
            if (!path.Success)
            {
                path.Result.PreviousSystem = previous.SystemId;
                path.Result.PreviousGame = previous.GamePath;
                return path.Result;
            }

            var result = await SendSequenceCoreAsync("goto-system", path.Inputs, request.HoldMs, request.GapMs, linkedCts.Token);
            result.TargetSystem = targetSystem;
            result.PreviousSystem = previous.SystemId;
            result.PreviousGame = previous.GamePath;

            if (!result.DryRun)
            {
                result.Verified = await WaitForSystemAsync(targetSystem, request.VerifyTimeoutMs, linkedCts.Token);
                result.Success = result.Verified;
                result.Status = result.Verified ? "completed" : "verification_timeout";
                result.Reason = result.Verified ? string.Empty : "verification_timeout";
            }

            return result;
        }
        finally
        {
            EndNavigation(linkedCts);
            _navigationLock.Release();
        }
    }

    public async Task<EsControllerActionResult> GotoGameAsync(EsControllerGotoGameRequest request, CancellationToken cancellationToken)
    {
        var targetSystem = NormalizeId(request.System);
        if (string.IsNullOrWhiteSpace(targetSystem))
        {
            return Failure("goto-game", "missing_system", "System is required.");
        }

        await _navigationLock.WaitAsync(cancellationToken);
        using var linkedCts = BeginNavigation(cancellationToken);
        try
        {
            var previous = GetCurrentSelection();
            var currentView = GetCurrentView();
            if (currentView.Equals("playing", StringComparison.OrdinalIgnoreCase))
            {
                var failure = Failure("goto-game", "game_running", "A game is currently running; navigation is paused to avoid sending frontend inputs to the wrong state.", targetSystem: targetSystem);
                failure.PreviousSystem = previous.SystemId;
                failure.PreviousGame = previous.GamePath;
                return failure;
            }

            var systems = await LoadSystemsAsync(linkedCts.Token);
            var games = await LoadGamesAsync(targetSystem, linkedCts.Token);
            var targetGame = FindTargetGame(games, request.GamePath, request.GameName);
            if (targetGame == null)
            {
                return Failure("goto-game", "game_not_found", "Target game was not found in gamelist.xml.", targetSystem: targetSystem);
            }

            var viewProbe = await EnsureKnownViewAsync("goto-game", linkedCts.Token);
            if (!viewProbe.Success)
            {
                var failure = Failure("goto-game", viewProbe.Reason, viewProbe.Message, targetSystem: targetSystem, targetGame: targetGame.Path);
                failure.PreviousSystem = previous.SystemId;
                failure.PreviousGame = previous.GamePath;
                return failure;
            }

            currentView = viewProbe.View;
            previous = viewProbe.Selection ?? GetCurrentSelection();

            var inputsSentBeforeGamePath = new List<string>();
            var alreadyInTargetGamelist =
                string.Equals(previous.SystemId, targetSystem, StringComparison.OrdinalIgnoreCase) &&
                (currentView.Equals("gamelist", StringComparison.OrdinalIgnoreCase) ||
                 !string.IsNullOrWhiteSpace(previous.GamePath));
            var previousGameKnown = !string.IsNullOrWhiteSpace(previous.GamePath);
            var targetIsPreviousGame = previousGameKnown && PathsMatch(previous.GamePath, targetGame.Path);
            if (currentView.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                previousGameKnown &&
                !targetIsPreviousGame)
            {
                var failure = Failure(
                    "goto-game",
                    "ambiguous_view",
                    "Current view is reported as system but a previous game is still selected; refusing to navigate to another game to avoid moving the system carousel.",
                    targetSystem: targetSystem,
                    targetGame: targetGame.Path);
                failure.PreviousSystem = previous.SystemId;
                failure.PreviousGame = previous.GamePath;
                return failure;
            }

            if (!alreadyInTargetGamelist)
            {
                EsControllerActionResult systemResult;
                if (currentView.Equals("gamelist", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(viewProbe.SystemNavigationAxis))
                {
                    systemResult = await NavigateToSystemInsideGamelistProgressivelyAsync(
                        systems,
                        previous,
                        targetSystem,
                        viewProbe.SystemNavigationAxis,
                        viewProbe.SystemForwardInput,
                        viewProbe.SystemBackwardInput,
                        request.HoldMs,
                        request.GapMs,
                        linkedCts.Token);
                    if (!systemResult.Success &&
                        string.Equals(systemResult.Reason, "system_navigation_stalled", StringComparison.OrdinalIgnoreCase))
                    {
                        inputsSentBeforeGamePath.AddRange(systemResult.InputsSent);
                        systemResult = await NavigateToSystemProgressivelyAsync(
                            systems,
                            GetCurrentSelection(),
                            targetSystem,
                            enter: true,
                            currentView: "gamelist",
                            request.HoldMs,
                            request.GapMs,
                            linkedCts.Token);
                    }
                }
                else
                {
                    systemResult = await NavigateToSystemProgressivelyAsync(
                        systems,
                        previous,
                        targetSystem,
                        enter: true,
                        currentView,
                        request.HoldMs,
                        request.GapMs,
                        linkedCts.Token);
                }

                systemResult.TargetSystem = targetSystem;
                systemResult.TargetGame = targetGame.Path;
                systemResult.PreviousSystem = previous.SystemId;
                systemResult.PreviousGame = previous.GamePath;
                inputsSentBeforeGamePath.AddRange(systemResult.InputsSent);
                if (!systemResult.Success)
                {
                    return systemResult;
                }

                await Task.Delay(ClampProbeDelay(800), linkedCts.Token);
                viewProbe = await EnsureKnownViewAsync("goto-game", linkedCts.Token);
                if (!viewProbe.Success)
                {
                    var failure = Failure("goto-game", viewProbe.Reason, viewProbe.Message, targetSystem: targetSystem, targetGame: targetGame.Path);
                    failure.PreviousSystem = previous.SystemId;
                    failure.PreviousGame = previous.GamePath;
                    failure.InputsSent.AddRange(inputsSentBeforeGamePath);
                    return failure;
                }

                currentView = viewProbe.View;
                previous = viewProbe.Selection ?? GetCurrentSelection();
            }

            var allowPageInputs = currentView.Equals("gamelist", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.SystemId, targetSystem, StringComparison.OrdinalIgnoreCase);
            return await NavigateToGameProgressivelyAsync(
                games,
                previous,
                targetSystem,
                targetGame.Path,
                viewProbe.GameNavigationAxis,
                allowPageInputs,
                inputsSentBeforeGamePath,
                request.HoldMs,
                request.GapMs,
                request.VerifyTimeoutMs,
                linkedCts.Token);
        }
        finally
        {
            EndNavigation(linkedCts);
            _navigationLock.Release();
        }
    }

    public Task<EsControllerActionResult> RestoreSelectionAsync(EsControllerRestoreSelectionRequest request, CancellationToken cancellationToken)
    {
        var snapshot = GetRestoreTarget(request);
        if (snapshot == null)
        {
            return Task.FromResult(Failure("restore-selection", "no_selection", "No selection is available to restore."));
        }

        return GotoGameAsync(new EsControllerGotoGameRequest
        {
            System = snapshot.SystemId,
            GamePath = snapshot.GamePath,
            GameName = snapshot.GameName,
            HoldMs = request.HoldMs,
            GapMs = request.GapMs,
            VerifyTimeoutMs = request.VerifyTimeoutMs
        }, cancellationToken);
    }

    public async Task<EsControllerReloadGamesResult> ReloadGamesAsync(
        EsControllerReloadGamesRequest request,
        CancellationToken cancellationToken)
    {
        var debounceMs = Math.Clamp(request.DebounceMs, 0, 30000);
        var result = new EsControllerReloadGamesResult
        {
            Requested = !request.DryRun,
            DryRun = request.DryRun,
            DebounceMs = debounceMs,
            BypassLastGameSelectedGuard = request.BypassLastGameSelectedGuard,
            RestoreSelectionAfterReloadGames = ShouldRestoreSelectionAfterReloadGames()
        };

        if (request.DryRun)
        {
            result.Message = "reloadgames direct request was planned only.";
            await RefreshTrackingLog.AppendAsync(
                "reloadgames",
                "dry-run",
                new { source = "es-controller", debounceMs, request.BypassLastGameSelectedGuard },
                cancellationToken);
            return result;
        }

        EsSelectionSnapshot? selectionBeforeReload = null;
        if (result.RestoreSelectionAfterReloadGames)
        {
            selectionBeforeReload = CaptureSelection("before-reloadgames");
        }

        if (debounceMs > 0)
        {
            await Task.Delay(debounceMs, cancellationToken);
        }

        var extendedSystems = await _gamelistUpdateService.ApplyPendingExtendedGamelistsAsync(
            "manual-reloadgames",
            cancellationToken);
        if (extendedSystems > 0)
        {
            _logger?.LogInformation(
                "Pending extended gamelists applied for {SystemCount} systems before manual reloadgames.",
                extendedSystems);
        }

        result.FrontendReloadRequested = await TryRequestFrontendReloadGamesAsync(cancellationToken);
        if (!result.FrontendReloadRequested)
        {
            result.Message = "reloadgames was requested by Swagger but EmulationStation did not accept /reloadgames.";
            await RefreshTrackingLog.AppendAsync(
                "reloadgames",
                "direct-http-failed",
                new { source = "es-controller", debounceMs, result.RestoreSelectionAfterReloadGames },
                cancellationToken);
            return result;
        }

        if (result.RestoreSelectionAfterReloadGames &&
            selectionBeforeReload != null &&
            !string.IsNullOrWhiteSpace(selectionBeforeReload.SystemId) &&
            !string.IsNullOrWhiteSpace(selectionBeforeReload.GamePath))
        {
            result.RestoreAttempted = true;
            var delayMs = GetRestoreSelectionDelayMs();
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            var restoreRequest = new EsControllerRestoreSelectionRequest
            {
                System = selectionBeforeReload.SystemId,
                GamePath = selectionBeforeReload.GamePath,
                GameName = selectionBeforeReload.GameName,
                VerifyTimeoutMs = 8000
            };
            EsControllerActionResult restoreResult;
            for (var restoreAttempt = 0; ; restoreAttempt++)
            {
                restoreResult = await RestoreSelectionAsync(restoreRequest, cancellationToken);
                if (restoreResult.Success ||
                    !IsRetriableReloadRestoreFailure(restoreResult.Reason) ||
                    restoreAttempt >= 2)
                {
                    break;
                }

                await Task.Delay(500, cancellationToken);
            }

            result.Restored = restoreResult.Success;
            result.RestoreReason = restoreResult.Reason;
        }

        result.Message = result.RestoreAttempted
            ? result.Restored
                ? "reloadgames was sent directly to EmulationStation and the selection was restored."
                : "reloadgames was sent directly to EmulationStation, but selection restore did not complete."
            : "reloadgames was sent directly to EmulationStation.";
        await RefreshTrackingLog.AppendAsync(
            "reloadgames",
            "direct-success",
            new
            {
                source = "es-controller",
                debounceMs,
                result.RestoreSelectionAfterReloadGames,
                result.RestoreAttempted,
                result.Restored,
                result.RestoreReason
            },
            cancellationToken);
        return result;
    }

    private static bool IsRetriableReloadRestoreFailure(string reason)
    {
        return string.Equals(reason, "ambiguous_view", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "verification_timeout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "page_navigation_stalled", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EsControllerActionResult> NavigateToGameProgressivelyAsync(
        IReadOnlyList<EsControllerGameEntry> games,
        EsSelectionSnapshot previous,
        string targetSystem,
        string targetGamePath,
        string gameNavigationAxis,
        bool allowPageInputs,
        IReadOnlyList<string> inputsAlreadySent,
        int holdMs,
        int gapMs,
        int verifyTimeoutMs,
        CancellationToken cancellationToken)
    {
        var targetIndex = IndexOfGame(games, targetGamePath);
        if (targetIndex < 0)
        {
            var failure = Failure("goto-game", "game_not_found", "Target game was not found in gamelist.xml.", targetSystem: targetSystem, targetGame: targetGamePath);
            failure.PreviousSystem = previous.SystemId;
            failure.PreviousGame = previous.GamePath;
            failure.InputsSent.AddRange(inputsAlreadySent);
            return failure;
        }

        var result = new EsControllerActionResult
        {
            Success = true,
            Action = "goto-game",
            Status = "sent",
            TargetSystem = targetSystem,
            TargetGame = targetGamePath,
            PreviousSystem = previous.SystemId,
            PreviousGame = previous.GamePath,
            InputsSent = inputsAlreadySent.ToList()
        };

        var backendStatus = GetBackendStatus(_options.CurrentValue.EsController);
        if (backendStatus.DryRun)
        {
            var currentGamePath = string.Equals(previous.SystemId, targetSystem, StringComparison.OrdinalIgnoreCase)
                ? previous.GamePath
                : string.Empty;
            var plan = BuildGamePath(games, currentGamePath, targetGamePath, gameNavigationAxis, allowPageInputs);
            if (!plan.Success)
            {
                plan.Result.InputsSent.AddRange(inputsAlreadySent);
                return plan.Result;
            }

            result.DryRun = true;
            result.Status = "planned";
            result.InputsSent.AddRange(plan.Inputs);
            return result;
        }

        var options = _options.CurrentValue.EsController;
        var pageSize = Math.Clamp(options.GameNavigationPageSize, 2, 200);
        var canUsePageInputs = allowPageInputs &&
            string.Equals(gameNavigationAxis, "vertical", StringComparison.OrdinalIgnoreCase) &&
            options.GameNavigationPageInputsEnabled;
        var pageBackwardInput = ResolveNavigationInput(options.GameNavigationPageBackwardInput, "pageup");
        var pageForwardInput = ResolveNavigationInput(options.GameNavigationPageForwardInput, "pagedown");
        var seenPagePositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var maxPageSteps = Math.Max(1, games.Count / pageSize + 4);

        for (var pageStep = 0; pageStep <= maxPageSteps; pageStep++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = GetCurrentSelection();
            if (!string.Equals(current.SystemId, targetSystem, StringComparison.OrdinalIgnoreCase))
            {
                result.Success = false;
                result.Status = "failed";
                result.Reason = "page_navigation_left_system";
                result.Message = "Navigation changed the active system; refusing correction in the wrong gamelist.";
                return result;
            }

            if (PathsMatch(current.GamePath, targetGamePath))
            {
                result.Verified = true;
                result.Success = true;
                result.Status = "completed";
                result.Reason = string.Empty;
                return result;
            }

            var finePlan = BuildGamePath(games, current.GamePath, targetGamePath, gameNavigationAxis, allowPageInputs: false);
            if (!finePlan.Success)
            {
                finePlan.Result.TargetSystem = targetSystem;
                finePlan.Result.TargetGame = targetGamePath;
                finePlan.Result.PreviousSystem = previous.SystemId;
                finePlan.Result.PreviousGame = previous.GamePath;
                finePlan.Result.InputsSent.AddRange(result.InputsSent);
                return finePlan.Result;
            }

            if (!canUsePageInputs || finePlan.Inputs.Count <= MaxPageNavigationFineCorrectionInputs)
            {
                return await SendFineGameCorrectionAsync(
                    result,
                    games,
                    finePlan.Inputs,
                    targetSystem,
                    targetGamePath,
                    gameNavigationAxis,
                    holdMs,
                    gapMs,
                    verifyTimeoutMs,
                    cancellationToken);
            }

            var currentIndex = IndexOfGame(games, current.GamePath);
            if (currentIndex < 0)
            {
                result.Success = false;
                result.Status = "failed";
                result.Reason = "current_game_unknown";
                result.Message = $"Current game '{current.GamePath}' was not found in the visible games list; cannot compute page direction.";
                return result;
            }

            var pageInput = targetIndex >= currentIndex ? pageForwardInput : pageBackwardInput;
            var positionKey = $"{current.SystemId}|{NormalizePath(current.GamePath)}|{pageInput}";
            seenPagePositions.TryGetValue(positionKey, out var repeatedPositionCount);
            seenPagePositions[positionKey] = repeatedPositionCount + 1;
            if (repeatedPositionCount >= 1)
            {
                result.Success = false;
                result.Status = "failed";
                result.Reason = "page_navigation_stalled";
                result.Message = "Page navigation repeated the same position and direction twice; refusing to loop.";
                return result;
            }

            var pageResult = await SendSequenceAndObserveAsync("goto-game-page-step", [pageInput], holdMs, gapMs, 1200, cancellationToken);
            result.InputsSent.AddRange(pageResult.InputsSent);
            if (!pageResult.Success)
            {
                result.Success = false;
                result.Status = pageResult.Status;
                result.Reason = pageResult.Reason;
                result.Message = pageResult.Message;
                return result;
            }

        }

        result.Success = false;
        result.Status = "failed";
        result.Reason = "page_navigation_correction_too_large";
        result.Message = "Page navigation did not get close enough to the target within the safety limit.";
        return result;
    }

    private async Task<EsControllerActionResult> NavigateToSystemProgressivelyAsync(
        IReadOnlyList<string> systems,
        EsSelectionSnapshot previous,
        string targetSystem,
        bool enter,
        string currentView,
        int holdMs,
        int gapMs,
        CancellationToken cancellationToken)
    {
        if (systems.Count == 0)
        {
            return Failure("goto-system", "systems_unavailable", "No systems were found in es_systems.cfg.", targetSystem: targetSystem);
        }

        if (IndexOf(systems, targetSystem) < 0)
        {
            return Failure("goto-system", "system_not_found", "Target system was not found in es_systems.cfg.", targetSystem: targetSystem);
        }

        var result = new EsControllerActionResult
        {
            Success = true,
            Action = "goto-system",
            Status = "sent",
            TargetSystem = targetSystem,
            PreviousSystem = previous.SystemId,
            PreviousGame = previous.GamePath
        };

        if (currentView.Equals("gamelist", StringComparison.OrdinalIgnoreCase))
        {
            var backResult = await SendSequenceAndObserveAsync("goto-system-back", ["back"], holdMs, gapMs, 1200, cancellationToken);
            result.InputsSent.AddRange(backResult.InputsSent);
            if (!backResult.Success)
            {
                result.Success = false;
                result.Status = backResult.Status;
                result.Reason = backResult.Reason;
                result.Message = backResult.Message;
                return result;
            }

        }

        foreach (var input in BuildSystemSearchInputs(systems, previous.SystemId, targetSystem, "right", "left"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = GetCurrentSelection();
            var currentSystem = string.IsNullOrWhiteSpace(current.SystemId)
                ? previous.SystemId
                : current.SystemId;
            if (string.Equals(currentSystem, targetSystem, StringComparison.OrdinalIgnoreCase))
            {
                if (enter)
                {
                    if (!await WaitForStableSystemAsync(targetSystem, cancellationToken))
                    {
                        await Task.Delay(ClampProbeDelay(800), cancellationToken);
                        continue;
                    }

                    var enterResult = await SendSequenceAndObserveAsync("goto-system-enter", ["confirm"], holdMs, gapMs, 1800, cancellationToken);
                    result.InputsSent.AddRange(enterResult.InputsSent);
                    if (!enterResult.Success)
                    {
                        result.Success = false;
                        result.Status = enterResult.Status;
                        result.Reason = enterResult.Reason;
                        result.Message = enterResult.Message;
                        return result;
                    }
                    var afterEnter = GetCurrentSelection();
                    if (!string.Equals(afterEnter.SystemId, targetSystem, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Success = false;
                        result.Status = "failed";
                        result.Reason = "system_navigation_enter_mismatch";
                        result.Message = $"Confirm was sent on '{targetSystem}', but EmulationStation reported '{afterEnter.SystemId}' after entering the gamelist.";
                        return result;
                    }
                }

                result.Verified = true;
                result.Success = true;
                result.Status = "completed";
                result.Reason = string.Empty;
                return result;
            }

            var stepResult = await SendSequenceAndObserveAsync("goto-system-step", [input], holdMs, gapMs, 1800, cancellationToken);
            result.InputsSent.AddRange(stepResult.InputsSent);
            if (!stepResult.Success)
            {
                result.Success = false;
                result.Status = stepResult.Status;
                result.Reason = stepResult.Reason;
                result.Message = stepResult.Message;
                return result;
            }
        }

        result.Success = false;
        result.Status = "failed";
        result.Reason = "system_navigation_stalled";
        result.Message = "System navigation did not reach the target within the safety limit.";
        return result;
    }

    private async Task<EsControllerActionResult> NavigateToSystemInsideGamelistProgressivelyAsync(
        IReadOnlyList<string> systems,
        EsSelectionSnapshot previous,
        string targetSystem,
        string systemNavigationAxis,
        string forwardInput,
        string backwardInput,
        int holdMs,
        int gapMs,
        CancellationToken cancellationToken)
    {
        if (systems.Count == 0)
        {
            return Failure("goto-system", "systems_unavailable", "No systems were found in es_systems.cfg.", targetSystem: targetSystem);
        }

        if (IndexOf(systems, targetSystem) < 0)
        {
            return Failure("goto-system", "system_not_found", "Target system was not found in es_systems.cfg.", targetSystem: targetSystem);
        }

        var backward = ResolveNavigationInput(backwardInput, systemNavigationAxis.Equals("vertical", StringComparison.OrdinalIgnoreCase) ? "up" : "left");
        var forward = ResolveNavigationInput(forwardInput, systemNavigationAxis.Equals("vertical", StringComparison.OrdinalIgnoreCase) ? "down" : "right");
        var result = new EsControllerActionResult
        {
            Success = true,
            Action = "goto-system",
            Status = "sent",
            TargetSystem = targetSystem,
            PreviousSystem = previous.SystemId,
            PreviousGame = previous.GamePath
        };

        foreach (var input in BuildSystemSearchInputs(systems, previous.SystemId, targetSystem, forward, backward))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = GetCurrentSelection();
            if (string.Equals(current.SystemId, targetSystem, StringComparison.OrdinalIgnoreCase))
            {
                result.Verified = true;
                result.Success = true;
                result.Status = "completed";
                result.Reason = string.Empty;
                return result;
            }

            var stepResult = await SendSequenceAndObserveAsync("goto-system-gamelist-step", [input], holdMs, gapMs, 1800, cancellationToken);
            result.InputsSent.AddRange(stepResult.InputsSent);
            if (!stepResult.Success)
            {
                result.Success = false;
                result.Status = stepResult.Status;
                result.Reason = stepResult.Reason;
                result.Message = stepResult.Message;
                return result;
            }
        }

        result.Success = false;
        result.Status = "failed";
        result.Reason = "system_navigation_stalled";
        result.Message = "Gamelist system navigation did not reach the target within the safety limit.";
        return result;
    }

    private async Task<bool> WaitForStableSystemAsync(string systemId, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(GetCurrentSelection().SystemId, systemId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await Task.Delay(300, cancellationToken);
        }

        return true;
    }

    private static List<string> BuildSystemSearchInputs(
        IReadOnlyList<string> systems,
        string currentSystem,
        string targetSystem,
        string forwardInput,
        string backwardInput)
    {
        var preferred = GetPreferredSystemDirection(systems, currentSystem, targetSystem, forwardInput, backwardInput);
        var alternate = string.Equals(preferred, forwardInput, StringComparison.OrdinalIgnoreCase)
            ? backwardInput
            : forwardInput;
        var maxSteps = Math.Max(1, systems.Count + 2);
        var inputs = Enumerable.Repeat(preferred, maxSteps).ToList();
        inputs.AddRange(Enumerable.Repeat(alternate, maxSteps));
        return inputs;
    }

    private static string GetPreferredSystemDirection(
        IReadOnlyList<string> systems,
        string currentSystem,
        string targetSystem,
        string forwardInput,
        string backwardInput)
    {
        var currentIndex = IndexOf(systems, currentSystem);
        var targetIndex = IndexOf(systems, targetSystem);
        if (currentIndex < 0 || targetIndex < 0 || systems.Count == 0)
        {
            return forwardInput;
        }

        var forwardDistance = (targetIndex - currentIndex + systems.Count) % systems.Count;
        var backwardDistance = (currentIndex - targetIndex + systems.Count) % systems.Count;
        return forwardDistance <= backwardDistance ? forwardInput : backwardInput;
    }

    private async Task<EsControllerActionResult> SendFineGameCorrectionAsync(
        EsControllerActionResult result,
        IReadOnlyList<EsControllerGameEntry> games,
        IReadOnlyList<string> inputs,
        string targetSystem,
        string targetGamePath,
        string gameNavigationAxis,
        int holdMs,
        int gapMs,
        int verifyTimeoutMs,
        CancellationToken cancellationToken)
    {
        if (inputs.Count > 0)
        {
            var correctionResult = await SendSequenceCoreAsync("goto-game-correction", inputs, holdMs, gapMs, cancellationToken);
            result.InputsSent.AddRange(correctionResult.InputsSent);
            if (!correctionResult.Success)
            {
                result.Success = false;
                result.Status = correctionResult.Status;
                result.Reason = correctionResult.Reason;
                result.Message = correctionResult.Message;
                return result;
            }
        }

        result.Verified = await WaitForGameAsync(targetSystem, targetGamePath, verifyTimeoutMs, cancellationToken);
        if (!result.Verified)
        {
            await TryFinalGameCorrectionAsync(result, games, targetSystem, targetGamePath, gameNavigationAxis, holdMs, gapMs, verifyTimeoutMs, cancellationToken);
        }

        result.Success = result.Verified;
        result.Status = result.Verified ? "completed" : "verification_timeout";
        result.Reason = result.Verified ? string.Empty : "verification_timeout";
        return result;
    }

    private async Task TryFinalGameCorrectionAsync(
        EsControllerActionResult result,
        IReadOnlyList<EsControllerGameEntry> games,
        string targetSystem,
        string targetGamePath,
        string gameNavigationAxis,
        int holdMs,
        int gapMs,
        int verifyTimeoutMs,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = GetCurrentSelection();
            if (!string.Equals(current.SystemId, targetSystem, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (PathsMatch(current.GamePath, targetGamePath))
            {
                result.Verified = true;
                return;
            }

            var correction = BuildGamePath(games, current.GamePath, targetGamePath, gameNavigationAxis, allowPageInputs: false);
            if (!correction.Success || correction.Inputs.Count == 0 || correction.Inputs.Count > 20)
            {
                return;
            }

            var correctionResult = await SendSequenceCoreAsync("goto-game-final-correction", correction.Inputs, holdMs, gapMs, cancellationToken);
            result.InputsSent.AddRange(correctionResult.InputsSent);
            if (!correctionResult.Success)
            {
                result.Status = correctionResult.Status;
                result.Reason = correctionResult.Reason;
                result.Message = correctionResult.Message;
                return;
            }

            result.Verified = await WaitForGameAsync(targetSystem, targetGamePath, verifyTimeoutMs, cancellationToken);
            if (result.Verified)
            {
                return;
            }
        }
    }

    public EsControllerConfigAuditResult AuditConfig()
    {
        var options = _options.CurrentValue.EsController;
        var esInputPath = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_input.cfg");
        var audit = new EsControllerConfigAuditResult
        {
            Enabled = options.Enabled,
            Backend = options.Backend,
            DryRun = GetBackendStatus(options).DryRun,
            BackendStatus = GetBackendStatus(options),
            EsInputPath = esInputPath,
            EsInputExists = File.Exists(esInputPath)
        };

        if (!audit.BackendStatus.Ready)
        {
            audit.Warnings.Add(audit.BackendStatus.Message);
        }

        if (!audit.EsInputExists)
        {
            audit.Warnings.Add("es_input.cfg was not found.");
        }

        return audit;
    }

    public EsControllerConfigAuditResult RepairConfig(bool dryRun)
    {
        var audit = AuditConfig();
        audit.Warnings.Add("No repair is needed for the keyboard/PostMessage backend; APIExpose reads the user's existing es_input.cfg and does not write controller configuration.");
        return audit;
    }

    private async Task<EsControllerActionResult> SendSequenceAsync(
        string action,
        IReadOnlyList<string> inputs,
        int holdMs,
        int gapMs,
        CancellationToken cancellationToken)
    {
        await _navigationLock.WaitAsync(cancellationToken);
        using var linkedCts = BeginNavigation(cancellationToken);
        try
        {
            return await SendSequenceCoreAsync(action, inputs, holdMs, gapMs, linkedCts.Token);
        }
        finally
        {
            EndNavigation(linkedCts);
            _navigationLock.Release();
        }
    }

    private async Task<EsControllerActionResult> SendSequenceCoreAsync(
        string action,
        IReadOnlyList<string> inputs,
        int holdMs,
        int gapMs,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.EsController;
        if (!options.Enabled)
        {
            return Failure(action, "disabled", "ES controller module is disabled.");
        }

        var backend = _backendProvider.Resolve(options.Backend);
        var backendStatus = backend.GetStatus(options);
        if (!backendStatus.Ready)
        {
            return Failure(action, "backend_not_ready", backendStatus.Message);
        }

        var result = new EsControllerActionResult
        {
            Success = true,
            Action = action,
            Status = backendStatus.DryRun ? "planned" : "sent",
            DryRun = backendStatus.DryRun,
            InputsSent = inputs.ToList()
        };

        if (backendStatus.DryRun)
        {
            _logger?.LogInformation("ES controller dry-run {Action}: {Inputs}", action, string.Join(",", inputs));
            return result;
        }

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await backend.SendInputAsync(input, Math.Clamp(holdMs, 20, 5000), options, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Failure(action, "input_failed", ex.Message);
            }

            if (index < inputs.Count - 1)
            {
                await Task.Delay(Math.Clamp(gapMs, 20, 5000), cancellationToken);
            }
        }

        return result;
    }

    private async Task<EsControllerActionResult> SendSequenceAndObserveAsync(
        string action,
        IReadOnlyList<string> inputs,
        int holdMs,
        int gapMs,
        int observeDelayMs,
        CancellationToken cancellationToken)
    {
        var before = BuildProbeSnapshot();
        var eventsWriteBeforeUtc = TryGetEventsIniLastWriteUtc();
        var result = await SendSequenceCoreAsync(action, inputs, holdMs, gapMs, cancellationToken);
        if (result.Success && !result.DryRun)
        {
            await WaitForControllerObservationAsync(before, eventsWriteBeforeUtc, observeDelayMs, cancellationToken, requireSelectionChange: true);
        }

        return result;
    }

    private async Task<EsControllerProbeViewResult> ProbeViewCoreAsync(EsControllerProbeViewRequest request, CancellationToken cancellationToken)
    {
        var result = new EsControllerProbeViewResult
        {
            Success = true,
            Status = "completed"
        };

        await ProbeAxisAsync(result, "horizontal", "right", "left", request, cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        await ProbeAxisAsync(result, "vertical", "down", "up", request, cancellationToken);

        ClassifyProbe(result);
        return result;
    }

    private async Task<ViewProbeResult> EnsureKnownViewAsync(string action, CancellationToken cancellationToken)
    {
        var probe = await ProbeViewCoreAsync(new EsControllerProbeViewRequest(), cancellationToken);
        if (probe.Success && IsKnownNavigableView(probe.DetectedView))
        {
            return ViewProbeResult.Ok(
                probe.DetectedView,
                probe.GameNavigationAxis,
                probe.GameForwardInput,
                probe.GameBackwardInput,
                probe.SystemNavigationAxis,
                probe.SystemForwardInput,
                probe.SystemBackwardInput,
                BuildProbeSelection(probe));
        }

        var recovery = await RecoverUnknownViewAsync(action, cancellationToken);
        if (recovery.Success)
        {
            return recovery;
        }

        return recovery;
    }

    private static bool IsKnownNavigableView(string view)
    {
        return view.Equals("gamelist", StringComparison.OrdinalIgnoreCase) ||
            view.Equals("system", StringComparison.OrdinalIgnoreCase);
    }

    private static EsSelectionSnapshot? BuildProbeSelection(EsControllerProbeViewResult probe)
    {
        var final = probe.Steps.LastOrDefault()?.After;
        if (final == null ||
            (string.IsNullOrWhiteSpace(final.System) && string.IsNullOrWhiteSpace(final.Game)))
        {
            return null;
        }

        return new EsSelectionSnapshot
        {
            SystemId = final.System,
            GamePath = final.Game,
            Source = "probe",
            CapturedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<ViewProbeResult> RecoverUnknownViewAsync(string action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxUnknownViewRecoveryAttempts; attempt++)
        {
            var rightClick = await RightClickCoreAsync(new EsControllerRightClickRequest { Warn = true, ObserveDelayMs = 800 }, cancellationToken);
            if (!rightClick.Success)
            {
                return ViewProbeResult.Fail("input_failed", rightClick.Message);
            }

            var movementProbe = await ProbeMovementUntilChangedAsync(cancellationToken);
            if (!movementProbe.Success)
            {
                return ViewProbeResult.Fail("input_failed", movementProbe.Message);
            }

            if (!ProbeHasAnySelectionChange(movementProbe))
            {
                continue;
            }

            var classificationProbe = await ProbeViewCoreAsync(new EsControllerProbeViewRequest(), cancellationToken);
            if (classificationProbe.Success && IsKnownNavigableView(classificationProbe.DetectedView))
            {
                return ViewProbeResult.Ok(
                    classificationProbe.DetectedView,
                    classificationProbe.GameNavigationAxis,
                    classificationProbe.GameForwardInput,
                    classificationProbe.GameBackwardInput,
                    classificationProbe.SystemNavigationAxis,
                    classificationProbe.SystemForwardInput,
                    classificationProbe.SystemBackwardInput,
                    BuildProbeSelection(classificationProbe));
            }

        }

        return ViewProbeResult.Fail(
            "unknown_view",
            $"{action} refused navigation because the current EmulationStation view could not be identified after repeated right-click/movement probes.");
    }

    private async Task<EsControllerProbeViewResult> ProbeMovementUntilChangedAsync(CancellationToken cancellationToken)
    {
        var request = new EsControllerProbeViewRequest { HoldMs = 70, GapMs = 120, ObserveDelayMs = 800 };
        var result = new EsControllerProbeViewResult
        {
            Success = true,
            Status = "completed"
        };

        foreach (var input in new[] { "up", "down", "left", "right" })
        {
            var step = await AppendProbeInputAsync(result, $"recovery-{input}", input, request, cancellationToken);
            if (step != null && ProbeStepChanged(step))
            {
                break;
            }
        }

        return result;
    }

    private static bool ProbeHasAnySelectionChange(EsControllerProbeViewResult result)
    {
        return result.Steps.Any(ProbeStepChanged);
    }

    private async Task ProbeAxisAsync(
        EsControllerProbeViewResult result,
        string axis,
        string forwardInput,
        string backwardInput,
        EsControllerProbeViewRequest request,
        CancellationToken cancellationToken)
    {
        var forward = await AppendProbeInputAsync(result, $"probe-{axis}-forward", forwardInput, request, cancellationToken);
        if (forward == null)
        {
            return;
        }

        if (ProbeStepChanged(forward))
        {
            if (ProbeStepChangedSystemFromGamelist(forward))
            {
                await RecoverProbeSystemChangeAsync(result, forward.Before, request, cancellationToken);
                return;
            }

            await AppendProbeInputAsync(result, $"probe-{axis}-backward", backwardInput, request, cancellationToken);
            return;
        }

        var backward = await AppendProbeInputAsync(result, $"probe-{axis}-backward", backwardInput, request, cancellationToken);
        if (backward != null && ProbeStepChanged(backward))
        {
            await AppendProbeInputAsync(result, $"probe-{axis}-forward-return", forwardInput, request, cancellationToken);
        }
    }

    private async Task<EsControllerProbeStep?> AppendProbeInputAsync(
        EsControllerProbeViewResult result,
        string name,
        string input,
        EsControllerProbeViewRequest request,
        CancellationToken cancellationToken)
    {
        var stepResult = await SendProbeInputAsync(name, input, request, cancellationToken);
        result.InputsSent.AddRange(stepResult.InputsSent);
        result.Steps.AddRange(stepResult.Steps);
        if (!stepResult.Success)
        {
            result.Success = false;
            result.Status = stepResult.Status;
        }

        return stepResult.Steps.LastOrDefault();
    }

    private async Task<EsControllerActionResult> RightClickCoreAsync(EsControllerRightClickRequest request, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.EsController;
        if (!options.Enabled)
        {
            return Failure("right-click", "disabled", "ES controller module is disabled.");
        }

        var backend = _backendProvider.Resolve(options.Backend);
        var backendStatus = backend.GetStatus(options);
        if (!backendStatus.Ready)
        {
            return Failure("right-click", "backend_not_ready", backendStatus.Message);
        }

        if (backendStatus.DryRun)
        {
            return new EsControllerActionResult
            {
                Success = true,
                Action = "right-click",
                Status = "planned",
                DryRun = true,
                InputsSent = ["right-click"]
            };
        }

        var before = BuildProbeSnapshot();
        var eventsWriteBeforeUtc = TryGetEventsIniLastWriteUtc();
        try
        {
            await backend.RightClickAsync(options, request.Warn, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Failure("right-click", "input_failed", ex.Message);
        }

        await WaitForControllerObservationAsync(before, eventsWriteBeforeUtc, request.ObserveDelayMs, cancellationToken);
        return new EsControllerActionResult
        {
            Success = true,
            Action = "right-click",
            Status = "sent",
            InputsSent = ["right-click"]
        };
    }

    private async Task<EsControllerProbeViewResult> SendProbeInputAsync(
        string name,
        string input,
        EsControllerProbeViewRequest request,
        CancellationToken cancellationToken)
    {
        var before = BuildProbeSnapshot();
        var eventsWriteBeforeUtc = TryGetEventsIniLastWriteUtc();
        var action = await SendSequenceCoreAsync(name, [input], request.HoldMs, request.GapMs, cancellationToken);
        var observation = EsControllerObservationResult.Skipped;
        if (action.Success && !action.DryRun)
        {
            observation = await WaitForControllerObservationAsync(before, eventsWriteBeforeUtc, request.ObserveDelayMs, cancellationToken);
        }

        var after = BuildProbeSnapshot();
        var result = new EsControllerProbeViewResult
        {
            Success = action.Success,
            Status = action.Status,
            InputsSent = action.InputsSent.ToList()
        };
        result.Steps.Add(new EsControllerProbeStep
        {
            Name = name,
            Input = input,
            ObservationMs = observation.ElapsedMs,
            SelectionChanged = observation.SelectionChanged,
            EventsIniChanged = observation.EventsIniChanged,
            ObservationTimedOut = observation.TimedOut,
            Before = before,
            After = after
        });

        return result;
    }

    private async Task<EsControllerObservationResult> WaitForControllerObservationAsync(
        EsControllerProbeSnapshot before,
        DateTime? eventsWriteBeforeUtc,
        int requestedObserveDelayMs,
        CancellationToken cancellationToken,
        bool requireSelectionChange = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var options = _options.CurrentValue.EsController;
        var minDelayMs = Math.Clamp(options.EventsObservationMinDelayMs, 50, 5000);
        var maxConfiguredDelayMs = Math.Clamp(options.EventsObservationMaxDelayMs, minDelayMs, 10000);
        var maxDelayMs = Math.Max(ClampProbeDelay(requestedObserveDelayMs), maxConfiguredDelayMs);
        var settleDelayMs = Math.Clamp(options.EventsObservationSettleMs, 0, 2000);
        var pollDelayMs = Math.Clamp(options.EventsObservationPollMs, 20, 500);

        await Task.Delay(minDelayMs, cancellationToken);
        var deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, maxDelayMs - minDelayMs));
        while (DateTime.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = BuildProbeSnapshot();
            var selectionChanged = ProbeSnapshotChanged(before, current);
            var eventsIniChanged = EventsIniChanged(eventsWriteBeforeUtc);
            if (selectionChanged || (!requireSelectionChange && eventsIniChanged))
            {
                if (settleDelayMs > 0)
                {
                    await Task.Delay(settleDelayMs, cancellationToken);
                }

                var settled = BuildProbeSnapshot();
                selectionChanged = selectionChanged || ProbeSnapshotChanged(before, settled);
                return new EsControllerObservationResult(
                    (int)stopwatch.ElapsedMilliseconds,
                    selectionChanged,
                    eventsIniChanged,
                    TimedOut: false);
            }

            await Task.Delay(pollDelayMs, cancellationToken);
        }

        return new EsControllerObservationResult(
            (int)stopwatch.ElapsedMilliseconds,
            SelectionChanged: false,
            EventsIniChanged: false,
            TimedOut: true);
    }

    private EsControllerProbeSnapshot BuildProbeSnapshot()
    {
        var selection = GetCurrentSelection();
        return new EsControllerProbeSnapshot
        {
            View = GetCurrentView(),
            System = selection.SystemId,
            Game = selection.GamePath
        };
    }

    private static bool ProbeSnapshotChanged(EsControllerProbeSnapshot before, EsControllerProbeSnapshot after)
    {
        return !string.Equals(before.View, after.View, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.System, after.System, StringComparison.OrdinalIgnoreCase) ||
            !PathsMatch(before.Game, after.Game);
    }

    private static bool EventsIniChanged(DateTime? previousWriteUtc)
    {
        var currentWriteUtc = TryGetEventsIniLastWriteUtc();
        if (!currentWriteUtc.HasValue)
        {
            return false;
        }

        return !previousWriteUtc.HasValue || currentWriteUtc.Value > previousWriteUtc.Value;
    }

    private static DateTime? TryGetEventsIniLastWriteUtc()
    {
        try
        {
            return File.Exists(RetroBatPaths.EventsIniPath)
                ? File.GetLastWriteTimeUtc(RetroBatPaths.EventsIniPath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClassifyProbe(EsControllerProbeViewResult result)
    {
        var firstStep = result.Steps.FirstOrDefault();
        if (firstStep == null)
        {
            result.Success = false;
            result.Status = "failed";
            result.DetectedView = "unknown";
            result.Confidence = "none";
            result.Message = "No probe step was executed.";
            return;
        }

        var horizontal = ObserveProbeAxis(result, "horizontal");
        var vertical = ObserveProbeAxis(result, "vertical");
        var knownViews = result.Steps
            .SelectMany(step => new[] { step.Before.View, step.After.View })
            .Where(view => !string.IsNullOrWhiteSpace(view))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (horizontal.GameChangedWithoutSystem)
        {
            result.DetectedView = "gamelist";
            result.GameNavigationAxis = "horizontal";
            result.GameForwardInput = "right";
            result.GameBackwardInput = "left";
            if (vertical.SystemChanged)
            {
                result.SystemNavigationAxis = "vertical";
                result.SystemForwardInput = "down";
                result.SystemBackwardInput = "up";
            }

            result.Confidence = knownViews.Contains("gamelist", StringComparer.OrdinalIgnoreCase) ? "high" : "medium";
            result.Message = "Right/left changed the selected game without changing system; game navigation axis is horizontal.";
            return;
        }

        if (vertical.GameChangedWithoutSystem)
        {
            result.DetectedView = "gamelist";
            result.GameNavigationAxis = "vertical";
            result.GameForwardInput = "down";
            result.GameBackwardInput = "up";
            if (horizontal.SystemChanged)
            {
                result.SystemNavigationAxis = "horizontal";
                result.SystemForwardInput = "right";
                result.SystemBackwardInput = "left";
            }

            result.Confidence = knownViews.Contains("gamelist", StringComparer.OrdinalIgnoreCase) ? "high" : "medium";
            result.Message = "Up/down changed the selected game while right/left did not provide a game-only move; game navigation axis is vertical.";
            return;
        }

        if (horizontal.SystemChanged || vertical.SystemChanged)
        {
            result.DetectedView = "system";
            if (horizontal.SystemChanged)
            {
                result.SystemNavigationAxis = "horizontal";
                result.SystemForwardInput = "right";
                result.SystemBackwardInput = "left";
            }
            else
            {
                result.SystemNavigationAxis = "vertical";
                result.SystemForwardInput = "down";
                result.SystemBackwardInput = "up";
            }

            result.Confidence = knownViews.Contains("system", StringComparer.OrdinalIgnoreCase) ? "high" : "medium";
            result.Message = "Probe changed the selected system and no game-only navigation axis was detected.";
            return;
        }

        if (knownViews.Contains("playing", StringComparer.OrdinalIgnoreCase))
        {
            result.DetectedView = "playing";
            result.Confidence = "high";
            result.Message = "API context reports a running game.";
            return;
        }

        result.DetectedView = "unknown";
        result.Confidence = "low";
        result.Message = "Probe did not change the known system or game; a menu or non-navigable view may be open.";
    }

    private static ProbeAxisObservation ObserveProbeAxis(EsControllerProbeViewResult result, string axis)
    {
        var prefix = $"probe-{axis}-";
        var steps = result.Steps
            .Where(step => step.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new ProbeAxisObservation(
            steps.Any(step => !string.Equals(step.Before.System, step.After.System, StringComparison.OrdinalIgnoreCase)),
            steps.Any(step => !PathsMatch(step.Before.Game, step.After.Game) &&
                string.Equals(step.Before.System, step.After.System, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ProbeStepChanged(EsControllerProbeStep step)
    {
        return !string.Equals(step.Before.System, step.After.System, StringComparison.OrdinalIgnoreCase) ||
            !PathsMatch(step.Before.Game, step.After.Game);
    }

    private static bool ProbeStepChangedSystemFromGamelist(EsControllerProbeStep step)
    {
        return !string.IsNullOrWhiteSpace(step.Before.Game) &&
            !string.Equals(step.Before.System, step.After.System, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RecoverProbeSystemChangeAsync(
        EsControllerProbeViewResult result,
        EsControllerProbeSnapshot original,
        EsControllerProbeViewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(original.System))
        {
            return;
        }

        var backResult = await SendSequenceAndObserveAsync(
            "probe-recover-system-back",
            ["back"],
            request.HoldMs,
            request.GapMs,
            1800,
            cancellationToken);
        result.InputsSent.AddRange(backResult.InputsSent);
        if (!backResult.Success)
        {
            result.Success = false;
            result.Status = backResult.Status;
            return;
        }

        var systems = await LoadSystemsAsync(cancellationToken);
        var navigationResult = await NavigateToSystemProgressivelyAsync(
            systems,
            new EsSelectionSnapshot { SystemId = original.System, GamePath = original.Game, Source = "probe" },
            original.System,
            enter: true,
            currentView: "system",
            request.HoldMs,
            request.GapMs,
            cancellationToken);
        result.InputsSent.AddRange(navigationResult.InputsSent);
        if (!navigationResult.Success)
        {
            result.Success = false;
            result.Status = navigationResult.Status;
            result.Message = navigationResult.Message;
        }
    }

    private static int ClampProbeDelay(int delayMs)
    {
        return Math.Clamp(delayMs, 100, 3000);
    }

    private Task ReleaseAllAsync()
    {
        var backend = _backendProvider.Resolve(_options.CurrentValue.EsController.Backend);
        return backend.ReleaseAllAsync(_options.CurrentValue.EsController);
    }

    private NavigationPlan BuildSystemPath(IReadOnlyList<string> systems, string currentSystem, string targetSystem, bool enter, string currentView)
    {
        if (systems.Count == 0)
        {
            return NavigationPlan.Fail(Failure("goto-system", "systems_unavailable", "No systems were found in es_systems.cfg.", targetSystem: targetSystem));
        }

        var targetIndex = IndexOf(systems, targetSystem);
        if (targetIndex < 0)
        {
            return NavigationPlan.Fail(Failure("goto-system", "system_not_found", "Target system was not found in es_systems.cfg.", targetSystem: targetSystem));
        }

        var inputs = new List<string>();
        var currentIndex = IndexOf(systems, currentSystem);
        if (currentIndex < 0)
        {
            return NavigationPlan.Fail(Failure(
                "goto-system",
                "current_system_unknown",
                $"Current system '{currentSystem}' was not found in the visible systems list; cannot compute directional path.",
                targetSystem: targetSystem));
        }

        if (currentView.Equals("gamelist", StringComparison.OrdinalIgnoreCase))
        {
            inputs.Add("back");
        }

        inputs.AddRange(BuildCircularMoves(currentIndex, targetIndex, systems.Count, "left", "right"));
        if (enter)
        {
            inputs.Add("confirm");
        }

        return NavigationPlan.Ok(inputs);
    }

    private NavigationPlan BuildGamePath(
        IReadOnlyList<EsControllerGameEntry> games,
        string currentGamePath,
        string targetGamePath,
        string gameNavigationAxis = "horizontal",
        bool allowPageInputs = true)
    {
        if (games.Count == 0)
        {
            return NavigationPlan.Fail(Failure("goto-game", "games_unavailable", "No games were found in gamelist.xml."));
        }

        var targetIndex = IndexOfGame(games, targetGamePath);
        if (targetIndex < 0)
        {
            return NavigationPlan.Fail(Failure("goto-game", "game_not_found", "Target game was not found in gamelist.xml."));
        }

        var currentIndex = IndexOfGame(games, currentGamePath);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var options = _options.CurrentValue.EsController;
        var useVerticalAxis = string.Equals(gameNavigationAxis, "vertical", StringComparison.OrdinalIgnoreCase);
        var backwardInput = useVerticalAxis
            ? "up"
            : ResolveNavigationInput(options.GameNavigationBackwardInput, "left");
        var forwardInput = useVerticalAxis
            ? "down"
            : ResolveNavigationInput(options.GameNavigationForwardInput, "right");
        var pageBackwardInput = ResolveNavigationInput(options.GameNavigationPageBackwardInput, "pageup");
        var pageForwardInput = ResolveNavigationInput(options.GameNavigationPageForwardInput, "pagedown");
        var pageSize = Math.Clamp(options.GameNavigationPageSize, 2, 200);
        var usePageInputs = allowPageInputs &&
            useVerticalAxis &&
            options.GameNavigationPageInputsEnabled &&
            games.Count > pageSize &&
            !string.Equals(backwardInput, pageBackwardInput, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(forwardInput, pageForwardInput, StringComparison.OrdinalIgnoreCase);

        if (useVerticalAxis)
        {
            return NavigationPlan.Ok(BuildLinearMoves(
                currentIndex,
                targetIndex,
                backwardInput,
                forwardInput,
                usePageInputs ? pageBackwardInput : string.Empty,
                usePageInputs ? pageForwardInput : string.Empty,
                pageSize));
        }

        return NavigationPlan.Ok(BuildCircularMoves(
            currentIndex,
            targetIndex,
            games.Count,
            backwardInput,
            forwardInput,
            pageSize: pageSize));
    }

    private static string ResolveNavigationInput(string configuredInput, string fallbackInput)
    {
        var input = EsControllerInputs.Normalize(configuredInput);
        return EsControllerInputs.IsAllowed(input) ? input : fallbackInput;
    }

    private static List<string> BuildCircularMoves(
        int currentIndex,
        int targetIndex,
        int count,
        string backwardInput,
        string forwardInput,
        string pageBackwardInput = "",
        string pageForwardInput = "",
        int pageSize = 10)
    {
        var forward = (targetIndex - currentIndex + count) % count;
        var backward = (currentIndex - targetIndex + count) % count;
        return forward <= backward
            ? BuildDirectionalMoves(forward, forwardInput, pageForwardInput, pageSize)
            : BuildDirectionalMoves(backward, backwardInput, pageBackwardInput, pageSize);
    }

    private static List<string> BuildDirectionalMoves(int distance, string singleInput, string pageInput, int pageSize)
    {
        if (distance <= 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(pageInput) || pageSize <= 1)
        {
            return Enumerable.Repeat(singleInput, distance).ToList();
        }

        var pageCount = distance / pageSize;
        var remainder = distance % pageSize;
        if (pageCount <= 0 || pageCount + remainder >= distance)
        {
            return Enumerable.Repeat(singleInput, distance).ToList();
        }

        return Enumerable.Repeat(pageInput, pageCount)
            .Concat(Enumerable.Repeat(singleInput, remainder))
            .ToList();
    }

    private static List<string> BuildLinearMoves(
        int currentIndex,
        int targetIndex,
        string backwardInput,
        string forwardInput,
        string pageBackwardInput = "",
        string pageForwardInput = "",
        int pageSize = 10)
    {
        return targetIndex >= currentIndex
            ? BuildDirectionalMoves(targetIndex - currentIndex, forwardInput, pageForwardInput, pageSize)
            : BuildDirectionalMoves(currentIndex - targetIndex, backwardInput, pageBackwardInput, pageSize);
    }

    private async Task<IReadOnlyList<string>> LoadSystemsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/systems", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var systems = await ReadSystemsFromApiResponseAsync(response.Content, cancellationToken);
                if (systems.Count > 0)
                {
                    return systems;
                }

                _logger?.LogWarning("ES HTTP API /systems returned no usable visible systems.");
            }
            else
            {
                _logger?.LogWarning("ES HTTP API /systems returned HTTP {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to read ES visible systems from HTTP API; falling back to es_systems.cfg.");
        }

        return LoadSystemsFromConfig();
    }

    private static async Task<IReadOnlyList<string>> ReadSystemsFromApiResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var systemsElement = document.RootElement;
        if (systemsElement.ValueKind != JsonValueKind.Array &&
            (!TryGetJsonProperty(document.RootElement, "value", out systemsElement) ||
             systemsElement.ValueKind != JsonValueKind.Array))
        {
            return [];
        }

        var systems = new List<string>();
        foreach (var systemElement in systemsElement.EnumerateArray())
        {
            if (TryGetJsonProperty(systemElement, "name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                var name = nameElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    systems.Add(name);
                }
            }
        }

        return systems;
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private IReadOnlyList<string> LoadSystemsFromConfig()
    {
        var path = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_systems.cfg");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var document = XDocument.Load(path);
            return document
                .Descendants("system")
                .Select(node => node.Element("name")?.Value?.Trim() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read ES systems from {Path}.", path);
            return [];
        }
    }

    private async Task<IReadOnlyList<EsControllerGameEntry>> LoadGamesAsync(string systemId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"/systems/{Uri.EscapeDataString(systemId)}/games", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var games = await response.Content.ReadFromJsonAsync<List<EsControllerGameEntry>>(JsonOptions, cancellationToken);
                if (games is { Count: > 0 })
                {
                    return games
                        .Where(game => !string.IsNullOrWhiteSpace(game.Path))
                        .Where(IsNavigableGameEntry)
                        .ToList();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Failed to read ES visible games from HTTP API; falling back to gamelist.xml.");
        }

        return LoadGamesFromGamelist(systemId);
    }

    private IReadOnlyList<EsControllerGameEntry> LoadGamesFromGamelist(string systemId)
    {
        var path = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var document = XDocument.Load(path);
            return document
                .Descendants("game")
                .Select(node => new EsControllerGameEntry(
                    node.Element("path")?.Value?.Trim() ?? string.Empty,
                    node.Element("name")?.Value?.Trim() ?? string.Empty))
                .Where(game => !string.IsNullOrWhiteSpace(game.Path))
                .Where(IsNavigableGameEntry)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read gamelist for {SystemId} from {Path}.", systemId, path);
            return [];
        }
    }

    private static EsControllerGameEntry? FindTargetGame(IReadOnlyList<EsControllerGameEntry> games, string gamePath, string gameName)
    {
        var normalizedPath = NormalizePath(gamePath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var pathMatch = games.FirstOrDefault(game => PathsMatch(game.Path, normalizedPath));
            if (pathMatch != null)
            {
                return pathMatch;
            }
        }

        var normalizedName = (gameName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            return games.FirstOrDefault(game => string.Equals(game.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static bool IsNavigableGameEntry(EsControllerGameEntry game)
    {
        return !game.Name.Trim().StartsWith("ZZZ(notgame):", StringComparison.OrdinalIgnoreCase);
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

            _logger?.LogWarning("Manual ES controller reloadgames returned HTTP {StatusCode}.", (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("Manual ES controller reloadgames timed out after send; continuing as accepted.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogInformation(ex, "Manual ES controller reloadgames produced an atypical response; continuing as accepted.");
            return true;
        }
    }

    private EsSelectionSnapshot? GetRestoreTarget(EsControllerRestoreSelectionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.System) &&
            (!string.IsNullOrWhiteSpace(request.GamePath) || !string.IsNullOrWhiteSpace(request.GameName)))
        {
            return new EsSelectionSnapshot
            {
                SystemId = request.System,
                GamePath = request.GamePath,
                GameName = request.GameName,
                Source = "request",
                CapturedAtUtc = DateTime.UtcNow
            };
        }

        return GetLastSelection();
    }

    private async Task<bool> WaitForSystemAsync(string systemId, int timeoutMs, CancellationToken cancellationToken)
    {
        return await WaitUntilAsync(
            () => string.Equals(GetCurrentSelection().SystemId, systemId, StringComparison.OrdinalIgnoreCase),
            timeoutMs,
            cancellationToken);
    }

    private async Task<bool> WaitForGameAsync(string systemId, string gamePath, int timeoutMs, CancellationToken cancellationToken)
    {
        var targetPath = NormalizePath(gamePath);
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 250, 30000));
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCurrentGame(systemId, targetPath) &&
                await IsCurrentGameStableAsync(systemId, targetPath, cancellationToken))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return IsCurrentGame(systemId, targetPath) &&
            await IsCurrentGameStableAsync(systemId, targetPath, cancellationToken);
    }

    private async Task<bool> IsCurrentGameStableAsync(string systemId, string normalizedGamePath, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
            if (!IsCurrentGame(systemId, normalizedGamePath))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsCurrentGame(string systemId, string normalizedGamePath)
    {
        var current = GetCurrentSelection();
        return string.Equals(current.SystemId, systemId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizePath(current.GamePath), normalizedGamePath, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int timeoutMs, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 250, 30000));
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return predicate();
    }

    private EsSelectionSnapshot GetCurrentSelection()
    {
        var running = _context.Ui.Running;
        if (running != null && !string.IsNullOrWhiteSpace(running.SystemId))
        {
            return EsSelectionSnapshot.FromGame(running, "context");
        }

        var eventsSelection = TryReadSelectionFromEventsIni();
        if (eventsSelection != null)
        {
            return eventsSelection;
        }

        var game = _context.Ui.Selected;
        if (game != null && !string.IsNullOrWhiteSpace(game.SystemId))
        {
            return EsSelectionSnapshot.FromGame(game, "context");
        }

        if (game != null &&
            (!string.IsNullOrWhiteSpace(game.GamePath) ||
             !string.IsNullOrWhiteSpace(game.GameName)))
        {
            return EsSelectionSnapshot.FromGame(game, "context");
        }

        return new EsSelectionSnapshot
        {
            SystemId = _context.Ui.SelectedSystem?.Name ?? string.Empty,
            Source = "context",
            CapturedAtUtc = DateTime.UtcNow
        };
    }

    private static EsSelectionSnapshot? TryReadSelectionFromEventsIni()
    {
        if (!File.Exists(RetroBatPaths.EventsIniPath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(RetroBatPaths.EventsIniPath)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            if (lines.Length == 0 || !lines[0].StartsWith("event=", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var eventName = lines[0][6..].Trim();
            if (string.Equals(eventName, "system-selected", StringComparison.OrdinalIgnoreCase) && lines.Length > 1)
            {
                return new EsSelectionSnapshot
                {
                    SystemId = lines[1].Trim(),
                    Source = "events.ini",
                    CapturedAtUtc = DateTime.UtcNow
                };
            }

            if (string.Equals(eventName, "game-selected", StringComparison.OrdinalIgnoreCase) && lines.Length > 1)
            {
                var args = ParseEventArguments(lines[1]);
                return new EsSelectionSnapshot
                {
                    SystemId = args.Count > 0 ? args[0] : string.Empty,
                    GamePath = args.Count > 1 ? args[1] : string.Empty,
                    GameName = args.Count > 2 ? args[2] : string.Empty,
                    Source = "events.ini",
                    CapturedAtUtc = DateTime.UtcNow
                };
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static List<string> ParseEventArguments(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    private string GetCurrentView()
    {
        if (_context.Ui.Running != null || string.Equals(_context.Ui.State, "playing", StringComparison.OrdinalIgnoreCase))
        {
            return "playing";
        }

        var eventsSelection = TryReadSelectionFromEventsIni();
        if (eventsSelection != null)
        {
            return string.IsNullOrWhiteSpace(eventsSelection.GamePath) ? "system" : "gamelist";
        }

        return _context.Ui.Selected != null ? "gamelist" : "system";
    }

    private static bool IsEmulationStationRunning()
    {
        try
        {
            return Process.GetProcessesByName("emulationstation").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfGame(IReadOnlyList<EsControllerGameEntry> values, string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return -1;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (PathsMatch(values[i].Path, normalized))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeId(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizePath(string value)
    {
        return (value ?? string.Empty).Replace('\\', '/').Trim();
    }

    private static bool PathsMatch(string left, string right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(GetPathFileName(normalizedLeft), GetPathFileName(normalizedRight), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPathFileName(string path)
    {
        var normalized = NormalizePath(path);
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private void SetLastSelection(EsSelectionSnapshot snapshot)
    {
        lock (_selectionLock)
        {
            _lastSelection = Clone(snapshot);
        }
    }

    private CancellationTokenSource BeginNavigation(CancellationToken cancellationToken)
    {
        _navigationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return _navigationCts;
    }

    private void EndNavigation(CancellationTokenSource navigationCts)
    {
        if (ReferenceEquals(_navigationCts, navigationCts))
        {
            _navigationCts = null;
        }
    }

    private static EsSelectionSnapshot Clone(EsSelectionSnapshot snapshot)
    {
        return new EsSelectionSnapshot
        {
            SystemId = snapshot.SystemId,
            GamePath = snapshot.GamePath,
            GameName = snapshot.GameName,
            GameId = snapshot.GameId,
            Source = snapshot.Source,
            CapturedAtUtc = snapshot.CapturedAtUtc
        };
    }

    private static EsControllerActionResult Failure(
        string action,
        string reason,
        string message,
        string targetSystem = "",
        string targetGame = "")
    {
        return new EsControllerActionResult
        {
            Success = false,
            Action = action,
            Status = "failed",
            Reason = reason,
            Message = message,
            TargetSystem = targetSystem,
            TargetGame = targetGame,
            InputsSent = [],
            DryRun = false
        };
    }

    private EsControllerBackendStatus GetBackendStatus(ApiExposeOptions.EsControllerOptions options)
    {
        return _backendProvider.Resolve(options.Backend).GetStatus(options);
    }

    private static string BuildStatusMessage(ApiExposeOptions.EsControllerOptions options, EsControllerBackendStatus backendStatus)
    {
        if (!options.Enabled)
        {
            return "ES controller module is disabled.";
        }

        return backendStatus.Message;
    }

    private sealed class EsControllerGameEntry
    {
        public EsControllerGameEntry()
        {
        }

        public EsControllerGameEntry(string path, string name)
        {
            Path = path;
            Name = name;
        }

        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private sealed class NavigationPlan
    {
        public bool Success { get; private init; }
        public List<string> Inputs { get; private init; } = new();
        public EsControllerActionResult Result { get; private init; } = new();
        public bool UsedPageNavigation { get; private init; }

        public static NavigationPlan Ok(List<string> inputs)
        {
            var usedPageNavigation = inputs.Any(input =>
                string.Equals(input, "pageup", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(input, "pagedown", StringComparison.OrdinalIgnoreCase));
            return new NavigationPlan { Success = true, Inputs = inputs, UsedPageNavigation = usedPageNavigation };
        }

        public static NavigationPlan Fail(EsControllerActionResult result)
        {
            return new NavigationPlan { Success = false, Result = result };
        }
    }

    private sealed class ViewProbeResult
    {
        public bool Success { get; private init; }
        public string View { get; private init; } = string.Empty;
        public string GameNavigationAxis { get; private init; } = string.Empty;
        public string GameForwardInput { get; private init; } = string.Empty;
        public string GameBackwardInput { get; private init; } = string.Empty;
        public string SystemNavigationAxis { get; private init; } = string.Empty;
        public string SystemForwardInput { get; private init; } = string.Empty;
        public string SystemBackwardInput { get; private init; } = string.Empty;
        public EsSelectionSnapshot? Selection { get; private init; }
        public string Reason { get; private init; } = string.Empty;
        public string Message { get; private init; } = string.Empty;

        public static ViewProbeResult Ok(
            string view,
            string gameNavigationAxis = "",
            string gameForwardInput = "",
            string gameBackwardInput = "",
            string systemNavigationAxis = "",
            string systemForwardInput = "",
            string systemBackwardInput = "",
            EsSelectionSnapshot? selection = null)
        {
            return new ViewProbeResult
            {
                Success = true,
                View = view,
                GameNavigationAxis = gameNavigationAxis,
                GameForwardInput = gameForwardInput,
                GameBackwardInput = gameBackwardInput,
                SystemNavigationAxis = systemNavigationAxis,
                SystemForwardInput = systemForwardInput,
                SystemBackwardInput = systemBackwardInput,
                Selection = selection
            };
        }

        public static ViewProbeResult Fail(string reason, string message)
        {
            return new ViewProbeResult { Success = false, Reason = reason, Message = message };
        }
    }

    private sealed record EsControllerObservationResult(
        int ElapsedMs,
        bool SelectionChanged,
        bool EventsIniChanged,
        bool TimedOut)
    {
        public static EsControllerObservationResult Skipped { get; } = new(0, false, false, false);
    }

    private sealed record ProbeAxisObservation(bool SystemChanged, bool GameChangedWithoutSystem);
}
