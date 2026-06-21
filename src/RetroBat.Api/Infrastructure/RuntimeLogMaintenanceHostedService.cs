using System.Text;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class RuntimeLogMaintenanceHostedService : IHostedService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<RuntimeLogMaintenanceHostedService> _logger;

    public RuntimeLogMaintenanceHostedService(
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<RuntimeLogMaintenanceHostedService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.Logging;
        if (!options.ResetRuntimeLogsOnStartup)
        {
            return Task.CompletedTask;
        }

        foreach (var configuredPath in options.RuntimeLogFilesToReset ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetLogFile(configuredPath);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void ResetLogFile(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return;
        }

        try
        {
            var path = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(RetroBatPaths.PluginRoot, configuredPath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not reset runtime log file {Path}.", configuredPath);
        }
    }
}
