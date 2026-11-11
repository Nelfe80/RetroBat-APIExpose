using System.Text.Json;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Deck;

/// <summary>
/// L'EPREUVE EN COURS POUR LE PLAYERDECK (CDC PlayerDeck H-D23) : le message <c>deck.trial</c>, tire du reglement (le plan,
/// fixe au lancement) et de ce que la session publie deja (<c>speedrun.session</c>, <c>speedrun.attempt.ended</c>), plus la
/// cible et le record du rang en direct (<c>deck.rank</c>, classement lu au lancement). Aucun appel reseau pendant la course.
///
/// Le chrono ne part pas image par image : le message donne l'heure du depart (horloge de la borne) et la penalite cumulee,
/// le Deck fait avancer l'affichage. Un message part a chaque changement : etat, coup recu, compte d'efficacite, point de
/// passage, cible ou record.
///
/// LES COMPTES D'EFFICACITE (« display.tallies » du reglement) sont comptes ici, dans les signaux du .MEM, pendant la
/// tentative seulement : jamais par l'evaluateur, jamais dans le resultat ni le passeport (affichage, non certifie).
///
/// LA CERTIFICATION NE CHANGE PAS : ce service lit des evenements ; il n'ecrit rien dans la partie, ni dans le passeport, ni
/// dans le prevol, ni dans les verrous.
/// </summary>
public sealed class DeckTrialService : IHostedService, IDisposable
{
    private static readonly TimeSpan DureeDeLArmement = TimeSpan.FromMinutes(2);

    private readonly IEventBus _bus;
    private readonly DeckStateService? _etat;
    private readonly ILogger<DeckTrialService>? _journal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private Arme? _arme;
    private DeckTrial? _publie;
    private long _revision;

    public DeckTrialService(IEventBus bus, DeckStateService? etat = null, ILogger<DeckTrialService>? journal = null)
    {
        _bus = bus;
        _etat = etat;
        _journal = journal;
        if (_etat is not null) _etat.Epreuve = () => Courant;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(Recevoir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    /// <summary>La derniere epreuve publiee (reprise dans deck.snapshot) ; null hors d'une epreuve lancee avec Deck.</summary>
    public DeckTrial? Courant
    {
        get { lock (_verrou) return _arme is null ? null : _publie; }
    }

    /// <summary>Le plan de l'epreuve armee (essais).</summary>
    internal DeckTrialPlan? Plan
    {
        get { lock (_verrou) return _arme?.Plan; }
    }

    /// <summary>
    /// Un lancement d'epreuve avec Deck se prepare : le plan est retenu, un premier message part (etat IDLE, chrono a zero,
    /// compteurs a zero). Sans Deck pour ce jeu, ou hors epreuve, l'epreuve precedente s'eteint.
    /// </summary>
    public void Armer(DeckLancement lancement, DeckTrialPlan? plan)
    {
        lock (_verrou)
        {
            _arme = plan is null || lancement.Choices.Count == 0 ? null : new Arme(plan, DateTime.UtcNow);
            if (_arme is not null) _arme.SplitsDuRecord = DeckPbSplits.Lire(plan!.ChallengeId);
            _publie = null;
        }

        if (plan is not null && lancement.Choices.Count > 0) Calculer();
    }

    // ─────────────────────────────── evenements ───────────────────────────────

    private void Recevoir(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "speedrun.session":
                    Session(e.Payload);
                    break;
                case "speedrun.attempt.ended":
                    Fin(e.Payload);
                    break;
                case "speedrun.session.closed":
                case "ui.game.ended":
                    Fermer();
                    break;
                case "deck.rank":
                    Rang(e.Payload);
                    break;
                case "deck.splits.pb.saved":
                    SplitsGardes(e.Payload);
                    break;
                case "retroarch.memory.changed":
                    Signal(e.Payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "PlayerDeck : evenement {Type} ignore pour l'epreuve.", e.Type);
        }
    }

    private static JsonElement Element(object? payload) => payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);

    private void Session(object? payload)
    {
        if (DeckTrialSession.Lire(Element(payload)) is not { } s) return;
        lock (_verrou)
        {
            if (_arme is not { } a) return;
            if (DateTime.UtcNow - a.Prepare > DureeDeLArmement && a.Session.SessionId is null) { _arme = null; _publie = null; return; }

            // Une nouvelle tentative : ses comptes repartent de zero, la precedente entre dans l'historique si elle est partie
            if (s.AttemptId != a.Session.AttemptId)
            {
                Clore(a);
                a.Comptes.Clear();
                a.Etapes = 0;
                Array.Clear(a.Progression);
                a.Temps.Clear();
                a.Partie = false;
            }

            // Le temps fige d'une tentative ratee vient de speedrun.attempt.ended : il reste tant que la tentative est la meme
            var fin = s.AttemptId == a.Session.AttemptId ? a.Session.EndFrame : null;
            a.Session = s with { EndFrame = fin };
            if (!a.Partie && s.State == SessionSpeedrun.Running)
            {
                // la tentative part sur la premiere valeur de chaque suite de points de passage (le depart du reglement)
                a.Partie = true;
                Array.Fill(a.Progression, 1);
            }

            if (s.State is SessionSpeedrun.Finished or SessionSpeedrun.Invalid or SessionSpeedrun.Aborted) Clore(a);
        }

        Calculer();
    }

    private void Fin(object? payload)
    {
        var p = Element(payload);
        lock (_verrou)
        {
            if (_arme is not { } a || DeckTrialSession.Str(p, "attemptId") != a.Session.AttemptId) return;
            a.Session = a.Session with { EndFrame = DeckTrialSession.Long(p, "frame") };
        }

        Calculer();
    }

    private void Fermer()
    {
        lock (_verrou)
        {
            if (_arme is not { } a || a.Session.SessionId is null) return;
            Clore(a);
            _arme = null;
            _publie = null;
        }
    }

    private void Rang(object? payload)
    {
        var r = payload as DeckRank ?? Element(payload).Deserialize<DeckRank>();
        if (r is null) return;
        lock (_verrou)
        {
            if (_arme is not { } a) return;
            a.Rang = r;
            if (r.Target is { } t && a.Plan.RankingMetric == "frames")
                a.Cible = new DeckTrialMark(t.Value, t.Text ?? Chronometrage.Afficher(t.Value, a.Plan.FrameRate), t.Rank, t.Player, "board");
        }

        Calculer();
    }

    /// <summary>Un signal du .MEM : les comptes d'affichage et les points de passage, pendant la course seulement.</summary>
    /// <summary>Le Deck vient de faire garder les splits d'un nouveau record perso : ils deviennent la reference de la suite.</summary>
    private void SplitsGardes(object? payload)
    {
        var t = payload as DeckPbSplits.Trace ?? Element(payload).Deserialize<DeckPbSplits.Trace>();
        if (t is null) return;
        lock (_verrou)
        {
            if (_arme is not { } a || !string.Equals(a.Plan.ChallengeId, t.ChallengeId, StringComparison.OrdinalIgnoreCase)) return;
            a.SplitsDuRecord = t;
        }

        Calculer();
    }

    private void Signal(object? payload)
    {
        Arme? a;
        lock (_verrou) a = _arme;
        if (a is null || (a.Plan.Tallies.Count == 0 && a.Plan.Steps.Count == 0)) return;
        var racine = Element(payload);
        if (!racine.TryGetProperty("signal", out var s) || s.ValueKind != JsonValueKind.Object) return;
        var nom = s.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        long? adresse = s.TryGetProperty("Address", out var ad) && ad.ValueKind == JsonValueKind.String && ad.GetString() is { Length: > 0 } t ? Hex.Lire(t) : null;
        if (!s.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Number) return;
        var valeur = v.GetInt32();
        var change = false;
        lock (_verrou)
        {
            if (!ReferenceEquals(_arme, a) || a.Session.State != SessionSpeedrun.Running) return;
            foreach (var c in a.Plan.Tallies)
            {
                if (c.Action is { } act && !string.Equals(act, nom, StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Address is { } adr && adr != adresse) continue;
                if (c.Value is { } val && val != valeur) continue;
                a.Comptes[c.Id] = (a.Comptes.TryGetValue(c.Id, out var k) ? k : 0) + 1;
                change = true;
            }

            for (var i = 0; i < a.Plan.Steps.Count; i++)
            {
                var e = a.Plan.Steps[i];
                if (adresse != e.Adresse || a.Progression[i] >= e.Suite.Count || valeur != e.Suite[a.Progression[i]]) continue;
                a.Progression[i]++;
                a.Etapes++;
                if (a.Session.StartClockMs is { } dep) a.Temps.Add(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - dep));
                change = true;
            }
        }

        if (change) Calculer();
    }

    /// <summary>Une tentative partie se clot : son efficacite entre dans l'historique, ses temps de passage gardes s'ils sont les meilleurs.</summary>
    private static void Clore(Arme a)
    {
        if (!a.Partie || a.Close == a.Session.AttemptId) return;
        a.Close = a.Session.AttemptId;
        foreach (var e in a.Plan.Efficiency)
        {
            int Compte(string id) => a.Session.Counters.TryGetValue(id, out var n) ? n : a.Comptes.TryGetValue(id, out var t) ? t : 0;
            var actions = Compte(e.Actions);
            if (actions <= 0) continue;
            var h = a.Historiques.TryGetValue(e.Id, out var l) ? l : a.Historiques[e.Id] = new List<int>();
            h.Insert(0, (int)Math.Round(Compte(e.Results) * 100.0 / actions, MidpointRounding.AwayFromZero));
            if (h.Count > DeckTrialCalcul.Historique) h.RemoveAt(h.Count - 1);
        }

        if (a.Session.State == SessionSpeedrun.Finished && a.Temps.Count > 0
            && (a.MeilleursTemps.Count == 0 || a.Temps[^1] < a.MeilleursTemps[^1]))
        {
            a.MeilleursTemps.Clear();
            a.MeilleursTemps.AddRange(a.Temps);
        }
    }

    // ─────────────────────────────── calcul et publication ───────────────────────────────

    private void Calculer()
    {
        DeckTrial message;
        lock (_verrou)
        {
            if (_arme is not { } a) return;
            var historiques = a.Historiques.ToDictionary(k => k.Key, k => (IReadOnlyList<int>)k.Value.ToList());
            message = DeckTrialCalcul.Composer(a.Plan, _etat?.LaunchIdCourant, a.Session, a.Rang, a.Cible,
                new Dictionary<string, int>(a.Comptes), historiques, a.Etapes, a.Temps.ToList(), a.MeilleursTemps.ToList(), a.SplitsDuRecord);
            if (_publie is not null && Pareil(_publie, message)) return;
            message = message with { Revision = ++_revision };
            _publie = message;
        }

        _ = _bus.PublishAsync(new EventEnvelope { Type = "deck.trial", Payload = message });
    }

    /// <summary>Deux messages egaux hors revision (les listes se comparent par leur JSON).</summary>
    private static bool Pareil(DeckTrial a, DeckTrial b)
        => JsonSerializer.Serialize(a with { Revision = 0 }) == JsonSerializer.Serialize(b with { Revision = 0 });

    private sealed class Arme(DeckTrialPlan plan, DateTime prepare)
    {
        public DeckTrialPlan Plan { get; } = plan;
        public DateTime Prepare { get; } = prepare;
        public DeckTrialSession Session = DeckTrialSession.Avant;
        public DeckRank? Rang;
        public DeckTrialMark? Cible;
        public DeckPbSplits.Trace? SplitsDuRecord;
        public readonly Dictionary<string, int> Comptes = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<int>> Historiques = new(StringComparer.Ordinal);
        public readonly int[] Progression = new int[plan.Steps.Count];
        public int Etapes;
        public readonly List<long> Temps = new();
        public readonly List<long> MeilleursTemps = new();
        public bool Partie;
        public string? Close;
    }
}
