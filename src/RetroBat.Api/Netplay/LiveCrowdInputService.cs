using System.Text.Json;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Le spectateur pilote son avatar dans la foule avec les directions du panel.
///
/// Seulement quand sa manette est LIBRE : il regarde un direct en netplay sans droit de jouer,
/// donc le jeu n'ecoute pas ses directions. Un invite qui joue garde sa manette pour la partie,
/// et l'hote n'a jamais la foule devant son ecran de jeu. Pendant une lecture de replay, les
/// directions pilotent la lecture (ReplayInputRouterService) et pas la foule.
///
///   ◀ ▶  tap = un pas ; maintenu = marche continue, un pas tous les <see cref="RepetitionMs"/>
///   ▲ ▼  un rang en arriere / en avant, par appui (pas de repetition : c'est un placement)
///
/// Tout reste LOCAL : la position d'un avatar n'a de sens que sur l'ecran qui le montre, et rien
/// ne part vers la plateforme. Les directions arrivent par le canal additif du watcher
/// (System=DPAD, identites up/down/left/right), le meme que la lecture de replay.
/// </summary>
public sealed class LiveCrowdInputService : IHostedService
{
    /// <summary>Un pas tous les 220 ms en maintien : une marche, pas une glissade.</summary>
    private const int RepetitionMs = 220;

    private readonly IEventBus _bus;
    private readonly LiveSpectateState _seance;
    private readonly LiveCrowdModel _foule;
    private readonly ReplayPlaybackService _playback;
    private readonly ILogger<LiveCrowdInputService> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _maintiens = new(StringComparer.Ordinal);
    private IDisposable? _sub;

    public LiveCrowdInputService(
        IEventBus bus,
        LiveSpectateState seance,
        LiveCrowdModel foule,
        ReplayPlaybackService playback,
        ILogger<LiveCrowdInputService> logger)
    {
        _bus = bus;
        _seance = seance;
        _foule = foule;
        _playback = playback;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sub = _bus.Subscribe<EventEnvelope>(OnEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _sub?.Dispose();
        Relacher();
        return Task.CompletedTask;
    }

    /// <summary>La manette est-elle libre pour la foule ?</summary>
    private bool PeutPiloter()
    {
        var (session, viewer, peutJouer) = _seance.Courant;
        return session.Length > 0 && viewer.Length > 0 && !peutJouer && !_playback.IsBusy;
    }

    private void OnEvent(EventEnvelope e)
    {
        var presse = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
        var relache = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
        if (!presse && !relache)
        {
            return;
        }

        var (identite, systeme) = LireBouton(e.Payload);
        if (!string.Equals(systeme, "DPAD", StringComparison.Ordinal) || string.IsNullOrEmpty(identite))
        {
            return;
        }

        if (!PeutPiloter())
        {
            // La manette est au jeu, ou a la lecture : on coupe un maintien qui trainerait.
            Relacher();
            return;
        }

        var direction = identite.ToLowerInvariant();
        if (presse)
        {
            Appuyer(direction);
        }
        else
        {
            Relacher(direction);
        }
    }

    private void Appuyer(string direction)
    {
        if (!_foule.Piloter(direction))
        {
            return;
        }
        if (direction is not ("left" or "right"))
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_maintiens.ContainsKey(direction))
            {
                return;
            }
            cts = new CancellationTokenSource();
            _maintiens[direction] = cts;
        }

        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(RepetitionMs, ct).ConfigureAwait(false);
                    if (!PeutPiloter() || !_foule.Piloter(direction))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Relache : c'est la fin normale d'un maintien.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Direct : maintien {Direction} interrompu.", direction);
            }
        }, ct);
    }

    private void Relacher(string direction)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_maintiens.Remove(direction, out cts))
            {
                return;
            }
        }
        cts.Cancel();
        cts.Dispose();
    }

    private void Relacher()
    {
        List<CancellationTokenSource> tous;
        lock (_gate)
        {
            if (_maintiens.Count == 0)
            {
                return;
            }
            tous = _maintiens.Values.ToList();
            _maintiens.Clear();
        }
        foreach (var cts in tous)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static (string? identite, string? systeme) LireBouton(object? payload)
    {
        if (payload is null)
        {
            return (null, null);
        }
        try
        {
            var el = JsonSerializer.SerializeToElement(payload);
            var id = el.TryGetProperty("Identity", out var i) ? i.GetString() : null;
            var sys = el.TryGetProperty("System", out var s) ? s.GetString() : null;
            return (id, sys);
        }
        catch
        {
            return (null, null);
        }
    }
}
