using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Media;

public sealed class PendingExtendedGamelistHostedService : IHostedService
{
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly ILogger<PendingExtendedGamelistHostedService>? _logger;

    public PendingExtendedGamelistHostedService(
        GamelistUpdateService gamelistUpdateService,
        ILogger<PendingExtendedGamelistHostedService>? logger = null)
    {
        _gamelistUpdateService = gamelistUpdateService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var systems = await _gamelistUpdateService.ApplyPendingExtendedGamelistsAsync(
                "api-startup",
                cancellationToken);
            await StartupGamelistPreparationLog.AppendAsync(
                "pending-extended-gamelist",
                "completed",
                new
                {
                    processedSystems = systems,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                },
                cancellationToken);
            if (systems > 0)
            {
                _logger?.LogInformation(
                    "Pending extended gamelists applied at startup for {SystemCount} systems; no APIExpose reloadgames requested because ES performs its startup reload.",
                    systems);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Pending extended gamelist startup synchronization failed.");
            await StartupGamelistPreparationLog.AppendAsync(
                "pending-extended-gamelist",
                "failed",
                new
                {
                    exceptionType = ex.GetType().FullName,
                    ex.Message,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                },
                CancellationToken.None);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
