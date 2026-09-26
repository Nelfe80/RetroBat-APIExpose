namespace RetroBat.Api.Infrastructure;

public sealed class StartupReadinessState
{
    private readonly object _gate = new();
    private bool _ready;
    private bool _arretEnCours;
    private DateTimeOffset? _readyAtUtc;

    /// <summary>
    /// L'API a commence a s'arreter parce qu'EmulationStation est parti. Elle ne se dit plus
    /// prete : un ES qui redemarre pendant ce temps la remplace par une neuve au lieu de la croire
    /// en service (vu le 2026-09-26 : ES relance 3 s apres, l'API s'eteignait une minute plus tard
    /// et la borne restait sans API, donc sans scoring ni collection).
    /// </summary>
    public bool ArretEnCours
    {
        get
        {
            lock (_gate)
            {
                return _arretEnCours;
            }
        }
    }

    public void MarquerArret()
    {
        lock (_gate)
        {
            _arretEnCours = true;
        }
    }

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
