using System.Text.Json;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Input;

/// <summary>
/// Moteur d'émission des RÉACTIONS d'audience (R4, étape 2a). Pendant une LECTURE, les 8 boutons
/// de façade (libérés par le transport-directions) déclenchent une réaction :
///   • appui d'UN bouton = sa famille ; le MAINTIEN monte l'intensité (niveau 1→2→3) ;
///   • ≥3 boutons pressés ENSEMBLE = accord « CÉLÉBRATION » (intensité = nb de boutons),
///     qui SUPPRIME les réactions individuelles des boutons concernés et ne compte qu'une fois.
/// Anti-spam par BUDGET : **5 réactions par replay et par viewer** (fixe, décision produit — une
/// poignée de réactions choisies vaut mieux qu'un flux ; le budget repart à neuf à chaque lecture,
/// donc « par viewer » se lit naturellement : une borne = un viewer à la fois).
/// Chaque réaction est horodatée (frame + ms), publiée (event replay.reaction) et STOCKÉE en JSONL
/// pour être rejouée (affichage = étape suivante). Les MOTS/emojis (6 langues) sont une table à part.
///
/// NB swap RetroBat A↔B : bouton A physique = identité "b", B = "a" (X/Y non swappés).
/// </summary>
public sealed class ReplayReactionService : IHostedService
{
    private const int ReactionsPerReplay = 5;   // budget FIXE par replay et par viewer
    private const int ChordThreshold = 3;   // ≥3 boutons simultanés = célébration
    private const int HoldLevel2Ms = 400;
    private const int HoldLevel3Ms = 900;
    private const int CooldownMs = 600;     // délai anti-spam entre deux réactions

    // Identité RetroPad (swap pris en compte) → famille de réaction.
    private static readonly IReadOnlyDictionary<string, string> Family = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["b"] = "hype",     // bouton A physique
        ["a"] = "wow",      // bouton B physique
        ["x"] = "respect",
        ["y"] = "laugh",
        ["l"] = "tension",
        ["r"] = "ouch",
        ["l2"] = "love",
        ["r2"] = "rage",
    };

    private readonly IEventBus _bus;
    private readonly ReplayPlaybackService _playback;
    private readonly ReplayStore _store;
    private readonly RetroBat.Api.Infrastructure.NelfePlayAgentService _agent;   // pseudo appairé = auteur du react
    private readonly ILogger<ReplayReactionService> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _down = new();  // boutons de réaction tenus
    private readonly HashSet<string> _gesture = new();            // boutons consommés par une célébration
    private bool _celebrating;
    private int _celebMaxCount;
    private string? _budgetKey;
    private long _sessionSeq;
    private int _budget;
    private int _budgetMax;
    private DateTime _cooldownUntil;

    private IDisposable? _sub;

    private readonly RetroBat.Api.Replay.Sharing.ReplayViewerSession _viewer;
    private readonly RetroBat.Api.Netplay.LiveSpectateState _direct;
    private readonly RetroBat.Api.Netplay.LiveReactionUploader _directUploader;

    public ReplayReactionService(IEventBus bus, ReplayPlaybackService playback, ReplayStore store,
        RetroBat.Api.Infrastructure.NelfePlayAgentService agent,
        RetroBat.Api.Replay.Sharing.ReplayViewerSession viewer,
        RetroBat.Api.Netplay.LiveSpectateState direct,
        RetroBat.Api.Netplay.LiveReactionUploader directUploader,
        ILogger<ReplayReactionService> logger)
    {
        _bus = bus; _playback = playback; _store = store; _agent = agent; _viewer = viewer; _logger = logger;
        _direct = direct; _directUploader = directUploader;
    }

    public Task StartAsync(CancellationToken ct) { _sub = _bus.Subscribe<EventEnvelope>(OnEvent); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { _sub?.Dispose(); return Task.CompletedTask; }

    /// <summary>Charge EN COURS (pour la jauge de l'overlay) : le bouton le plus longtemps tenu,
    /// ou l'accord célébration. Progress 0-1 sur la durée de maintien.</summary>
    public sealed record ChargeSnapshot(bool Active, string Family, int Level, double Progress, bool Chord, int ChordCount);

    /// <summary>Disponibilité des réactions (pour l'indicateur de la légende) : budget restant + cooldown.</summary>
    public sealed record Availability(int Budget, int BudgetMax, bool CanReact, double CooldownProgress);

    public Availability GetAvailability()
    {
        lock (_gate)
        {
            // Le budget doit être connu AVANT la première pression, sinon le HUD affiche 0 alors
            // que le spectateur en a cinq. Le calcul est mis en cache sur la clé replay+spectateur,
            // donc l'appel répété du HUD ne relit pas le journal à chaque image.
            EnsureBudget(_playback.GetState());

            var cdLeft = (_cooldownUntil - DateTime.UtcNow).TotalMilliseconds;
            var can = _budget > 0 && cdLeft <= 0;
            var prog = cdLeft > 0 ? Math.Clamp(1 - cdLeft / CooldownMs, 0, 1) : 1;
            return new Availability(_budget, _budgetMax, can, prog);
        }
    }

    public ChargeSnapshot GetCharge()
    {
        lock (_gate)
        {
            if (_celebrating)
            {
                var lvl = CelebLevel(_celebMaxCount);
                var prog = Math.Clamp(_celebMaxCount / 8.0, 0.15, 1.0);
                return new ChargeSnapshot(true, "celebrate", lvl, prog, true, _celebMaxCount);
            }
            if (_down.Count == 0) return new ChargeSnapshot(false, "", 0, 0, false, 0);
            var earliest = _down.Values.Min();
            var id = _down.First(kv => kv.Value == earliest).Key;
            var elapsed = (DateTime.UtcNow - earliest).TotalMilliseconds;
            var level = elapsed < HoldLevel2Ms ? 1 : elapsed < HoldLevel3Ms ? 2 : 3;
            var progress = Math.Clamp(elapsed / (double)HoldLevel3Ms, 0, 1);
            return new ChargeSnapshot(true, Family[id], level, progress, false, 0);
        }
    }

    private void OnEvent(EventEnvelope e)
    {
        // Chaque LECTURE repart avec un budget neuf (sinon rejouer le même replay resterait à 0).
        if (string.Equals(e.Type, "replay.started", StringComparison.Ordinal))
        {
            lock (_gate) { _budgetKey = null; _down.Clear(); ResetGesture(); }
            return;
        }
        if (string.Equals(e.Type, "replay.finished", StringComparison.Ordinal))
        {
            lock (_gate) { _down.Clear(); ResetGesture(); }
            return;
        }

        var pressed = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
        var released = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
        if (!pressed && !released) return;

        var (identity, system) = ReadButton(e.Payload);
        // Les entrées « système » (START/SELECT/DPAD/L3/R3) portent un System non nul → pas des réactions.
        if (identity is null || system is not null) return;
        var id = identity.ToLowerInvariant();
        if (!Family.ContainsKey(id)) return; // uniquement les 8 boutons de réaction

        // La facade sert pendant un REPLAY ou pendant un DIRECT. Le geste est le meme, seule
        // la cible change : reserver les reactions au replay laissait la facade muette
        // precisement au moment ou l'on veut s'exprimer, une partie en cours.
        var st = _playback.GetState();
        var enReplay = string.Equals(st.Mode, "replay", StringComparison.Ordinal);
        if (!enReplay && !_direct.Actif)
        {
            lock (_gate) { _down.Clear(); ResetGesture(); }
            return;
        }

        if (pressed) OnPress(id, st); else OnRelease(id, st);
    }

    private void OnPress(string id, ReplayPlaybackService.StateSnapshot st)
    {
        lock (_gate)
        {
            EnsureBudget(st);
            _down[id] = DateTime.UtcNow;

            if (_celebrating)
            {
                _gesture.Add(id);
                _celebMaxCount = Math.Max(_celebMaxCount, _down.Count);
            }
            else if (_down.Count >= ChordThreshold)
            {
                // L'accord se forme : bascule en célébration, les singles en cours sont abandonnés.
                _celebrating = true;
                _celebMaxCount = _down.Count;
                _gesture.Clear();
                foreach (var k in _down.Keys) _gesture.Add(k);
            }
            // sinon : réaction simple en cours, le niveau sera calculé au relâché.
        }
    }

    private void OnRelease(string id, ReplayPlaybackService.StateSnapshot st)
    {
        string? family = null; var level = 0; var chord = false;
        lock (_gate)
        {
            if (!_down.TryGetValue(id, out var pressedAt)) return;
            _down.Remove(id);

            if (_celebrating || _gesture.Contains(id))
            {
                _gesture.Add(id);
                if (_celebrating && _down.Count == 0)
                {
                    family = "celebrate";
                    level = CelebLevel(_celebMaxCount);
                    chord = true;
                    ResetGesture();
                }
                // sinon : d'autres boutons de l'accord sont encore tenus, on attend.
            }
            else
            {
                var heldMs = (DateTime.UtcNow - pressedAt).TotalMilliseconds;
                family = Family[id];
                level = heldMs < HoldLevel2Ms ? 1 : heldMs < HoldLevel3Ms ? 2 : 3;
            }
        }

        if (family is not null) Commit(st, family, level, chord);
    }

    private void Commit(ReplayPlaybackService.StateSnapshot st, string family, int level, bool chord)
    {
        lock (_gate)
        {
            if (_budget <= 0) { _logger.LogDebug("Replay réaction ignorée (budget épuisé) : {F} n{L}", family, level); return; }
            if (DateTime.UtcNow < _cooldownUntil) { _logger.LogDebug("Replay réaction ignorée (cooldown) : {F} n{L}", family, level); return; }
            _budget--;
            _cooldownUntil = DateTime.UtcNow.AddMilliseconds(CooldownMs);
        }

        // UN DIRECT prime sur un replay : si les deux étaient possibles, c'est la partie en
        // cours qu'on regarde, pas un enregistrement. Elle part TOUT DE SUITE et ne passe pas
        // par le journal local : une réaction de direct ne vaut que quelques secondes, et rien
        // n'aurait à être rejoué plus tard.
        if (_direct.Actif && !string.Equals(st.Mode, "replay", StringComparison.Ordinal))
        {
            _ = _directUploader.EnvoyerAsync(family, level);
            _logger.LogInformation(
                "Direct réaction : {F} niveau {L}{Chord} (budget restant {B})",
                family, level, chord ? " [accord]" : "", _budget);
            return;
        }

        // PAS DE SPECTATEUR IDENTIFIÉ, PAS DE RÉACTION. Une réaction anonyme ne serait
        // attribuable à personne, donc ni décomptable d'un budget, ni agrégeable honnêtement.
        // Le jeton vient de la page nelfeplay.com qui a lancé la lecture ; il est opaque.
        var viewer = _viewer.Current;
        if (string.IsNullOrEmpty(viewer))
        {
            _logger.LogDebug("Replay : réaction ignorée, aucun spectateur identifié pour cette séance.");
            return;
        }

        var pseudo = _agent.Status.Pseudo;
        var r = new ReplayReaction(st.ReplayId ?? "", family, level, st.Frame,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Lang(), chord,
            string.IsNullOrWhiteSpace(pseudo) ? null : pseudo, viewer, _sessionSeq);
        try { _store.AppendReaction(r); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : append réaction échoué"); }
        _ = Publish(r);
        _logger.LogInformation("Replay réaction : {F} niveau {L}{Chord} @frame {Fr} (budget restant {B})",
            family, level, chord ? " [accord]" : "", st.Frame, _budget);
    }

    // Budget remis à neuf à chaque changement de replay : 5, quelle que soit la durée.
    /// <summary>
    /// Le budget de CE spectateur sur CE replay.
    ///
    /// Il ne se réinitialise pas à chaque lecture : ce qui a déjà été dépensé est relu du journal
    /// append-only, donc relancer le même replay ne redonne pas un lot de cinq. Sans ça, il
    /// suffisait de relancer pour réagir indéfiniment.
    ///
    /// Le journal est la mémoire LOCALE ; la vérité reste au centre, où la plateforme compte par
    /// COMPTE et non par jeton. Ici on empêche surtout le spectateur de dépenser des réactions qui
    /// seraient de toute façon refusées à la remontée, ce qui lui donnerait l'illusion d'avoir réagi.
    /// </summary>
    private void EnsureBudget(ReplayPlaybackService.StateSnapshot st)
    {
        // Pendant un DIRECT, la cible est la session et le spectateur est celui du jeton recu
        // en rejoignant. Le journal local ne garde rien de ces reactions (elles ne se rejouent
        // pas), donc le budget local est un simple garde-fou d'affichage : la verite est au
        // centre, qui compte par COMPTE. Ici on evite surtout de laisser depenser des
        // reactions qui seraient refusees, ce qui donnerait l'illusion d'avoir parle.
        if (_direct.Actif && !string.Equals(st.Mode, "replay", StringComparison.Ordinal))
        {
            var (session, jeton, _) = _direct.Courant;
            var cleDirect = "live:" + session + "|" + jeton;
            if (string.Equals(_budgetKey, cleDirect, StringComparison.Ordinal))
            {
                return;
            }
            _budgetKey = cleDirect;
            _budgetMax = ReactionsPerReplay;
            _budget = ReactionsPerReplay;
            _cooldownUntil = DateTime.MinValue;
            _sessionSeq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger.LogDebug("Direct réactions : budget {B} pour la session {S}", _budget, session);
            return;
        }

        var viewer = _viewer.Current;
        var key = (st.ReplayId ?? string.Empty) + "|" + (viewer ?? string.Empty);
        if (string.Equals(_budgetKey, key, StringComparison.Ordinal)) return;
        _budgetKey = key;
        _budgetMax = ReactionsPerReplay;

        // Rien à quoi réagir, ou personne d'identifié : aucun budget, et le HUD le montre.
        if (string.IsNullOrEmpty(st.ReplayId) || string.IsNullOrEmpty(viewer))
        {
            _budget = 0;
            return;
        }

        // Nouvelle séance de visionnage : cinq réactions neuves. Elles ne s'AJOUTENT pas aux
        // précédentes, elles les SUPPLANTENT — c'est l'agrégat qui ne retient que la séance la
        // plus récente. Le spectateur qui regarde une seconde fois peut donc placer ses réactions
        // au bon moment, sans que le total compté pour lui depasse jamais cinq.
        _sessionSeq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _budget = ReactionsPerReplay;
        _cooldownUntil = DateTime.MinValue;
        _logger.LogDebug("Replay réactions : budget {B} pour {Id}", _budget, st.ReplayId);
    }

    private static int CelebLevel(int count) => count >= 7 ? 3 : count >= 5 ? 2 : 1;

    private void ResetGesture() { _celebrating = false; _celebMaxCount = 0; _gesture.Clear(); }

    // Langue de l'auteur (affichage). TODO : brancher sur la langue ES/UI ; défaut FR pour l'instant.
    private static string Lang() => "fr";

    private async Task Publish(ReplayReaction r)
    {
        try { await _bus.PublishAsync(new EventEnvelope { Type = "replay.reaction", Payload = r }).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : publication réaction échouée"); }
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
