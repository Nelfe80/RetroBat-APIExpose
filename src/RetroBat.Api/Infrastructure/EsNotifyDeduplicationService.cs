namespace RetroBat.Api.Infrastructure;

public sealed class EsNotifyDeduplicationService
{
    private readonly object _sync = new();
    private string? _lastNotifyMessage;

    public bool TryAccept(string message)
    {
        var normalized = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        lock (_sync)
        {
            if (string.Equals(_lastNotifyMessage, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            _lastNotifyMessage = normalized;
            return true;
        }
    }

    public void ForgetIfCurrent(string message)
    {
        var normalized = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_sync)
        {
            if (string.Equals(_lastNotifyMessage, normalized, StringComparison.Ordinal))
            {
                _lastNotifyMessage = null;
            }
        }
    }
}
