using System.Text.Json;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Input;

/// <summary>
/// Contrôles physiques Replay (R3). S'abonne à panel.input.pressed/released et, pendant une
/// LECTURE (playback.IsBusy), traduit les DIRECTIONS du panel en commandes de lecture.
/// N'agit jamais hors lecture (les entrées vont au jeu/ES normalement).
///
/// Mapping (R3.5, sans SELECT — SELECT est l'input_enable_hotkey de RetroArch, on ne s'en sert
/// PAS pour éviter tout télescopage avec ses hotkeys) :
///   ▲ haut          = lecture / pause
///   ▼ bas           = retour au DÉBUT du run
///   ◀ gauche  tap   = checkpoint PRÉCÉDENT      | tenu = recul rapide  (seek -5 s répété)
///   ▶ droite  tap   = checkpoint SUIVANT        | tenu = avance rapide (seek +5 s répété)
///   START (tenu)    = quitter la lecture
///   START (deux appuis rapides) = barre pleine / barre reduite a un filet
/// Les 8 boutons de façade restent LIBRES pour les réactions.
///
/// Les directions arrivent via le canal additif du watcher (System=DPAD, identités
/// up/down/left/right) — cf. CabinetInputReader.SnapshotDirections.
/// </summary>
public sealed class ReplayInputRouterService : IHostedService
{
    private const int QuitHoldMs = 700;
    private const int DoubleTapMs = 450;         // deux appuis brefs sur START a moins de ca = bascule de la barre
    private const int TapMaxMs = 300;            // ≤ 300 ms = TAP (checkpoint) ; au-delà = MAINTIEN (seek)
    private const double FastSeekStepSeconds = 5;
    private const int FastSeekRepeatMs = 350;

    private readonly IEventBus _bus;
    private readonly ReplayPlaybackService _playback;
    private readonly ILogger<ReplayInputRouterService> _logger;

    /// <summary>Une direction ◀/▶ en cours d'appui : on ne sait qu'au RELÂCHÉ si c'était un tap
    /// (→ checkpoint) ou un maintien (→ le seek répété a déjà tourné).</summary>
    private sealed class DirectionHold
    {
        public readonly CancellationTokenSource Cts = new();
        public bool Repeating;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, DirectionHold> _held = new(StringComparer.Ordinal);
    private IDisposable? _sub;
    private DateTime? _startDownAt;
    private DateTime _dernierTapStart = DateTime.MinValue;

    public ReplayInputRouterService(IEventBus bus, ReplayPlaybackService playback,
        ILogger<ReplayInputRouterService> logger)
    {
        _bus = bus; _playback = playback; _logger = logger;
    }

    public Task StartAsync(CancellationToken ct) { _sub = _bus.Subscribe<EventEnvelope>(OnEvent); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); StopAllHolds(); return Task.CompletedTask; }

    private void OnEvent(EventEnvelope e)
    {
        var pressed = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
        var released = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
        if (!pressed && !released) return;

        var (identity, system) = ReadButton(e.Payload);

        // Hors lecture : on ne pilote rien (et on coupe d'éventuels maintiens résiduels).
        if (!_playback.IsBusy) { _startDownAt = null; StopAllHolds(); return; }

        // START tenu = quitter.
        if (string.Equals(system, "START", StringComparison.Ordinal))
        {
            if (pressed) _startDownAt = DateTime.UtcNow;
            else if (_startDownAt is DateTime t)
            {
                _startDownAt = null;
                var tenu = (DateTime.UtcNow - t).TotalMilliseconds;
                if (tenu >= QuitHoldMs) { Fire("quit", _playback.StopAsync); return; }
                // Un appui bref : le second, s'il suit de pres, bascule la barre. Le choix du
                // spectateur, pas un minuteur.
                var maintenant = DateTime.UtcNow;
                if ((maintenant - _dernierTapStart).TotalMilliseconds <= DoubleTapMs)
                {
                    _dernierTapStart = DateTime.MinValue;
                    RetroBat.Api.Replay.Overlay.ReplayOverlayService.BasculerReduite();
                    _logger.LogDebug("Replay contrôle panel : barre {Mode}", RetroBat.Api.Replay.Overlay.ReplayOverlayService.ReduiteVoulue ? "réduite" : "pleine");
                }
                else _dernierTapStart = maintenant;
            }
            return;
        }

        // Directions (canal DPAD).
        if (string.Equals(system, "DPAD", StringComparison.Ordinal) && !string.IsNullOrEmpty(identity))
        {
            var dir = identity.ToLowerInvariant();
            if (pressed) OnDirectionDown(dir); else OnDirectionUp(dir);
        }
    }

    private void OnDirectionDown(string dir)
    {
        switch (dir)
        {
            case "up": Fire("pause", _playback.PauseToggleAsync); break;
            case "down": Fire("restart-run", _playback.RestartRunAsync); break;
            case "left": BeginDirection("left", -FastSeekStepSeconds); break;
            case "right": BeginDirection("right", +FastSeekStepSeconds); break;
        }
    }

    private void OnDirectionUp(string dir)
    {
        if (dir is "left" or "right") EndDirection(dir);
    }

    // ◀ / ▶ : on ARME au down. Si la direction est encore tenue après TapMaxMs, c'est un maintien
    // → seek répété. Sinon le relâché tombe avant, et EndDirection en fait un checkpoint.
    private void BeginDirection(string dir, double stepSeconds)
    {
        DirectionHold hold;
        lock (_gate)
        {
            if (_held.ContainsKey(dir)) return; // déjà armé
            hold = new DirectionHold();
            _held[dir] = hold;
        }

        var ct = hold.Cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TapMaxMs, ct).ConfigureAwait(false); // toujours tenu → maintien
                lock (_gate) hold.Repeating = true;
                _logger.LogDebug("Replay : {Dir} maintenu → seek répété {Step}s", dir, stepSeconds);
                while (!ct.IsCancellationRequested && _playback.IsBusy)
                {
                    await _playback.SeekRelativeAsync(stepSeconds, ct).ConfigureAwait(false);
                    await Task.Delay(FastSeekRepeatMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : maintien {Dir} interrompu", dir); }
        }, ct);
    }

    private void EndDirection(string dir)
    {
        DirectionHold? hold; bool repeating;
        lock (_gate)
        {
            if (!_held.Remove(dir, out hold)) return;
            repeating = hold.Repeating;
        }
        hold.Cts.Cancel();
        hold.Cts.Dispose();

        if (repeating) return; // c'était un maintien : le seek a déjà fait le travail
        if (string.Equals(dir, "left", StringComparison.Ordinal))
            Fire("prev-checkpoint", _playback.PreviousCheckpointAsync);
        else
            Fire("next-checkpoint", _playback.NextCheckpointAsync);
    }

    private void StopAllHolds()
    {
        List<DirectionHold> all;
        lock (_gate)
        {
            if (_held.Count == 0) return;
            all = _held.Values.ToList();
            _held.Clear();
        }
        foreach (var h in all) { h.Cts.Cancel(); h.Cts.Dispose(); }
    }

    private void Fire(string label, Func<CancellationToken, Task> action)
    {
        _logger.LogDebug("Replay contrôle panel : {Label}", label);
        _ = Task.Run(async () =>
        {
            try { await action(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay : commande panel {Label} échouée", label); }
        });
    }

    private static (string? identity, string? system) ReadButton(object? payload)
    {
        if (payload is null) return (null, null);
        try
        {
            var el = JsonSerializer.SerializeToElement(payload);
            var id = el.TryGetProperty("Identity", out var i) ? i.GetString() : null;
            var sys = el.TryGetProperty("System", out var s) ? s.GetString() : null;
            return (id, sys);
        }
        catch { return (null, null); }
    }
}
