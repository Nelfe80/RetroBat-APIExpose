namespace RetroBat.Api.Infrastructure;

public sealed class StartupReadinessState
{
    private readonly object _gate = new();
    private bool _ready;
    private DateTimeOffset? _readyAtUtc;

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    public DateTimeOffset? ReadyAtUtc
    {
        get
        {
            lock (_gate)
            {
                return _readyAtUtc;
            }
        }
    }

    public void MarkReady()
    {
        lock (_gate)
        {
            _ready = true;
            _readyAtUtc ??= DateTimeOffset.UtcNow;
        }
    }
}
