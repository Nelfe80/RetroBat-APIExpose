using System.Text;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class EmulationStationNotificationService : IEmulationStationNotificationService
{
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly EsNotifyDeduplicationService _notifyDeduplication;
    private readonly ILogger<EmulationStationNotificationService>? _logger;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:1234"),
        Timeout = TimeSpan.FromSeconds(2)
    };

    public EmulationStationNotificationService(
        ApiExposeRuntimeOptionsService runtimeOptions,
        EsNotifyDeduplicationService notifyDeduplication,
        ILogger<EmulationStationNotificationService>? logger = null)
    {
        _runtimeOptions = runtimeOptions;
        _notifyDeduplication = notifyDeduplication;
        _logger = logger;
    }

    public async Task NotifyAsync(string message, CancellationToken cancellationToken = default)
    {
        await PostTextAsync("/notify", message, "ES notify", cancellationToken);
    }

    public async Task MessageBoxAsync(string message, CancellationToken cancellationToken = default)
    {
        await PostTextAsync("/messagebox", message, "ES messagebox", cancellationToken);
    }

    private async Task PostTextAsync(
        string endpoint,
        string message,
        string logContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!_runtimeOptions.AreApiNotificationsEnabled())
        {
            return;
        }

        string? acceptedNotifyMessage = null;
        try
        {
            var safeMessage = string.Equals(endpoint, "/notify", StringComparison.OrdinalIgnoreCase)
                ? EsNotificationText.SanitizeForEsPopup(message)
                : EsNotificationText.SanitizeForEs(message);
            if (string.IsNullOrWhiteSpace(safeMessage))
            {
                return;
            }

            if (string.Equals(endpoint, "/notify", StringComparison.OrdinalIgnoreCase)
                && !_notifyDeduplication.TryAccept(safeMessage))
            {
                _logger?.LogDebug("ES notify duplicate suppressed: {Message}", safeMessage);
                return;
            }

            if (string.Equals(endpoint, "/notify", StringComparison.OrdinalIgnoreCase))
            {
                acceptedNotifyMessage = safeMessage;
            }

            using var content = new StringContent(safeMessage, Encoding.UTF8, "text/plain");
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                if (string.Equals(endpoint, "/notify", StringComparison.OrdinalIgnoreCase))
                {
                    _notifyDeduplication.ForgetIfCurrent(safeMessage);
                }

                _logger?.LogDebug(
                    "{Context} returned HTTP {StatusCode}: {Message}",
                    logContext,
                    (int)response.StatusCode,
                    safeMessage);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (string.Equals(endpoint, "/notify", StringComparison.OrdinalIgnoreCase))
            {
                _notifyDeduplication.ForgetIfCurrent(acceptedNotifyMessage ?? message);
            }

            _logger?.LogDebug(ex, "{Context} skipped: EmulationStation API unavailable.", logContext);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (string.Equals(endpoint, "/notify", StringComparison.OrdinalIgnoreCase))
            {
                _notifyDeduplication.ForgetIfCurrent(acceptedNotifyMessage ?? message);
            }

            _logger?.LogDebug(ex, "{Context} skipped: EmulationStation API timeout.", logContext);
        }
    }
}
