using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Media;

public sealed class LocalizedGamelistCachePrebuildHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);
    private readonly LocalizedGamelistCacheService _cacheService;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<LocalizedGamelistCachePrebuildHostedService>? _logger;

    public LocalizedGamelistCachePrebuildHostedService(
        LocalizedGamelistCacheService cacheService,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<LocalizedGamelistCachePrebuildHostedService>? logger = null)
    {
        _cacheService = cacheService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            if (!_options.CurrentValue.LocalizedGamelistCache.Enabled ||
                !_options.CurrentValue.LocalizedGamelistCache.PrebuildOnStartup)
            {
                _logger?.LogInformation("Localized gamelist cache prebuild disabled.");
                return;
            }

            var result = await _cacheService.PrebuildActiveLanguagesAsync(stoppingToken);
            _logger?.LogInformation(
                "Localized gamelist cache prebuild completed: languages={Languages}, generated={Generated}, failed={Failed}, reason={Reason}.",
                string.Join(",", result.Languages),
                result.SystemsGenerated,
                result.SystemsFailed,
                result.Reason);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Localized gamelist cache prebuild failed.");
        }
    }
}
