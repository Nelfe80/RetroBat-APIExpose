using System.Diagnostics;
using Microsoft.Extensions.Options;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Infrastructure;

public sealed class EmulationStationLifecycleHostedService : BackgroundService
{
    private readonly EsFeaturesMenuDeploymentService _esFeaturesMenuDeploymentService;
    private readonly EsControllerInputBackendProvider _backendProvider;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<EmulationStationLifecycleHostedService> _logger;
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://127.0.0.1:1234"), Timeout = TimeSpan.FromSeconds(2) };
    private bool _hasSeenEmulationStation;
    private DateTimeOffset? _missingSince;
    private bool _shutdownStarted;
    private int? _currentEmulationStationProcessId;
    private int? _startupF5ScheduledForProcessId;
    private int? _startupF5SentForProcessId;

    public EmulationStationLifecycleHostedService(
        EsFeaturesMenuDeploymentService esFeaturesMenuDeploymentService,
        EsControllerInputBackendProvider backendProvider,
        MediaRuntimeState runtimeState,
        IHostApplicationLifetime applicationLifetime,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<EmulationStationLifecycleHostedService> logger)
    {
        _esFeaturesMenuDeploymentService = esFeaturesMenuDeploymentService;
        _backendProvider = backendProvider;
        _runtimeState = runtimeState;
        _applicationLifetime = applicationLifetime;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue.EmulationStationLifecycle;
            if (!options.Enabled || !options.StopApiWhenEmulationStationStops)
            {
                await DelayAsync(options.PollIntervalMilliseconds, stoppingToken);
                continue;
            }

            var isRunning = TryGetEmulationStationProcessId(out var processId);
            if (isRunning)
            {
                if (!_hasSeenEmulationStation)
                {
                    _logger.LogInformation("EmulationStation lifecycle monitor is armed.");
                }

                if (_currentEmulationStationProcessId != processId)
                {
                    _currentEmulationStationProcessId = processId;
                    _startupF5ScheduledForProcessId = null;
                    _startupF5SentForProcessId = null;
                }

                _hasSeenEmulationStation = true;
                _missingSince = null;
                await TryScheduleStartupF5Async(processId, options, stoppingToken);
                await DelayAsync(options.PollIntervalMilliseconds, stoppingToken);
                continue;
            }

            if (!_hasSeenEmulationStation)
            {
                await DelayAsync(options.PollIntervalMilliseconds, stoppingToken);
                continue;
            }

            _missingSince ??= DateTimeOffset.Now;
            _currentEmulationStationProcessId = null;
            _startupF5ScheduledForProcessId = null;
            _startupF5SentForProcessId = null;
            if (DateTimeOffset.Now - _missingSince.Value < TimeSpan.FromMilliseconds(Math.Max(0, options.ShutdownGraceMilliseconds)))
            {
                await DelayAsync(options.PollIntervalMilliseconds, stoppingToken);
                continue;
            }

            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            await ShutdownAfterEmulationStationExitAsync(options, stoppingToken);
            return;
        }
    }

    private async Task ShutdownAfterEmulationStationExitAsync(
        ApiExposeOptions.EmulationStationLifecycleOptions options,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("EmulationStation exited; APIExpose is cleaning ES features and stopping.");
        if (options.RemoveEsFeaturesOnShutdown)
        {
            var result = await _esFeaturesMenuDeploymentService.RemoveAsync(dryRun: false, cancellationToken);
            _logger.LogInformation(
                "ES features cleanup completed before API shutdown. Changed={Changed}, LocaleChanged={LocaleChanged}, RemovedShared={RemovedShared}, RemovedGlobal={RemovedGlobal}, RemovedPanels={RemovedPanels}, RemovedLocales={RemovedLocales}, Warnings={WarningCount}",
                result.Changed,
                result.LocaleChanged,
                result.RemovedSharedFeatureCount,
                result.RemovedGlobalFeatureCount,
                result.RemovedSystemPanelFeatureCount,
                result.RemovedLocaleCount,
                result.Warnings.Count);
        }

        _applicationLifetime.StopApplication();
    }

    private async Task TryScheduleStartupF5Async(
        int processId,
        ApiExposeOptions.EmulationStationLifecycleOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.SendF5AfterEsApiReady ||
            _startupF5SentForProcessId == processId ||
            _startupF5ScheduledForProcessId == processId ||
            !await IsEmulationStationApiReadyAsync(cancellationToken))
        {
            return;
        }

        if (_runtimeState.TryConsumeStartupReloadGamesF5Suppression())
        {
            _startupF5SentForProcessId = processId;
            _logger.LogInformation("Startup F5 skipped because pending extended gamelists already requested a startup reloadgames for process={ProcessId}.", processId);
            await RefreshTrackingLog.AppendAsync(
                "startup-f5",
                "skipped-startup-reloadgames",
                new { processId },
                cancellationToken);
            return;
        }

        _startupF5ScheduledForProcessId = processId;
        _ = Task.Run(
            () => SendStartupF5AfterDelayAsync(processId, options.F5AfterEsApiReadyDelayMilliseconds, options.F5AfterEsApiReadyHoldMilliseconds, cancellationToken),
            CancellationToken.None);
    }

    private async Task SendStartupF5AfterDelayAsync(
        int processId,
        int delayMilliseconds,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delayMilliseconds, 1000, 30000)), cancellationToken);
            if (_currentEmulationStationProcessId != processId ||
                _startupF5SentForProcessId == processId ||
                !await IsEmulationStationApiReadyAsync(cancellationToken))
            {
                return;
            }

            var controllerOptions = BuildSilentStartupF5ControllerOptions(_options.CurrentValue.EsController);
            var backend = _backendProvider.Resolve(controllerOptions.Backend);
            await backend.SendInputAsync("f5", Math.Clamp(holdMilliseconds, 20, 1000), controllerOptions, cancellationToken);
            _startupF5SentForProcessId = processId;
            _logger.LogInformation("Startup F5 sent after EmulationStation API became ready for process={ProcessId}.", processId);
            await RefreshTrackingLog.AppendAsync(
                "startup-f5",
                "success",
                new { processId, delayMilliseconds, holdMilliseconds },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup F5 after EmulationStation API ready failed for process={ProcessId}.", processId);
            await RefreshTrackingLog.AppendAsync(
                "startup-f5",
                "failed",
                new { processId, exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
        }
    }

    private async Task<bool> IsEmulationStationApiReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/systems", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static ApiExposeOptions.EsControllerOptions BuildSilentStartupF5ControllerOptions(ApiExposeOptions.EsControllerOptions source)
    {
        return new ApiExposeOptions.EsControllerOptions
        {
            Enabled = source.Enabled,
            Backend = source.Backend,
            RequireEmulationStationForeground = source.RequireEmulationStationForeground,
            FocusEmulationStationBeforeInput = source.FocusEmulationStationBeforeInput,
            ClickEmulationStationIfFocusFails = source.ClickEmulationStationIfFocusFails,
            RightClickWarningEnabled = false,
            FocusWarningEnabled = false,
            FocusWarningDurationMs = source.FocusWarningDurationMs,
            RestoreSelectionAfterReloadGames = source.RestoreSelectionAfterReloadGames,
            RestoreSelectionDelayMs = source.RestoreSelectionDelayMs,
            GameNavigationForwardInput = source.GameNavigationForwardInput,
            GameNavigationBackwardInput = source.GameNavigationBackwardInput,
            GameNavigationPageInputsEnabled = source.GameNavigationPageInputsEnabled,
            GameNavigationPageForwardInput = source.GameNavigationPageForwardInput,
            GameNavigationPageBackwardInput = source.GameNavigationPageBackwardInput,
            GameNavigationPageSize = source.GameNavigationPageSize,
            EventsObservationMinDelayMs = source.EventsObservationMinDelayMs,
            EventsObservationMaxDelayMs = source.EventsObservationMaxDelayMs,
            EventsObservationSettleMs = source.EventsObservationSettleMs,
            EventsObservationPollMs = source.EventsObservationPollMs
        };
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(250, milliseconds)), cancellationToken);
    }

    private static bool TryGetEmulationStationProcessId(out int processId)
    {
        processId = 0;
        foreach (var process in Process.GetProcessesByName("emulationstation").OrderByDescending(process => process.StartTime))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        processId = process.Id;
                        return true;
                    }
                }
                catch
                {
                    // The process can exit between enumeration and HasExited access.
                }
            }
        }

        return false;
    }
}
