using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Api.Deck;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA COUCHE EPREUVE DU PLAYERDECK (H-D23) : le plan tire du reglement (compteurs, echecs, verrous, efficacite), le message
/// deck.trial (chrono, decompte, penalites, echecs, comparaisons) et le service, nourri par la vraie session de Metal Slug 3
/// au couteau (v7) : chaque coup recu publie un evenement de la session, donc un instantane, donc un deck.trial.
/// </summary>
public sealed class DeckEpreuveTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static readonly Reglement Couteau = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.v7.json")));

    private static DeckLancement Lancement() => new(DeckModes.Speedrun, "fbneo", "mslug3", "metal-slug-3", "arcade/metal-slug-3", "nelfe",
        @"E:\RetroBat\roms\fbneo\mslug3.zip", "posed", "metal-slug-3-pixel", "Rebel Iron", true, "official-default",
        ["metal-slug-3-pixel", "off"], null, null, DateTime.UtcNow);

    // ─────────────────────────── le plan ───────────────────────────

    [Fact]
    public void Le_plan_du_couteau_dit_99_coups_une_seconde_chacun_et_les_echecs()
    {
        var p = DeckTrialPlan.De(Couteau);
        Assert.Equal(("challenge", "My ### and my knife", "ch-ms3-m1-knife-v7"), (p.Category, p.Title, p.Ruleset));
        var c = Assert.Single(p.Counters);
        Assert.Equal(("hits", "HITS", 98, 99, 60), (c.Id, c.Label, c.Max, c.FailsAt, c.PenaltyFramesEach));
        Assert.Equal(["AMMO_LOSE", "LOSE_LIFE"], p.Fails.Select(f => f.Id));
        Assert.Equal(["NO AMMO SPENT", "NO DEATH"], p.Fails.Select(f => f.Label));
        Assert.Contains("0x10EAF3", p.Locked);   // l'arme tenue : les munitions n'ont pas de sens
        Assert.Contains("0x10EAF5", p.Locked);   // les grenades tenues
        Assert.Empty(p.Yields);
        Assert.Empty(p.Efficiency);
        Assert.True(p.LowerIsBetter);
        Assert.Equal("frames", p.RankingMetric);
        // le JSON du plan (lu par le Deck au rendu du decor) n'emporte ni les evenements ni les etapes
        var json = p.ToJson();
        Assert.DoesNotContain("Tallies", json);
        Assert.DoesNotContain("Steps", json);
        Assert.Contains("\"FailsAt\":99", json);
    }

    [Fact]
    public void Chaque_reglement_des_essais_donne_un_plan_lisible()
    {
        var n = 0;
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Reglements"), "*.json"))
        {
            Reglement r;
            try { r = Reglement.Lire(File.ReadAllText(f)); }
            catch (ReglementInvalideException) { continue; }
            var p = DeckTrialPlan.De(r);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.All(p.Fails, x => Assert.False(string.IsNullOrWhiteSpace(x.Label)));
            Assert.True(p.FrameRate > 0);
            n++;
        }

        Assert.True(n > 100, n.ToString(CultureInfo.InvariantCulture));
        var niveau = DeckTrialPlan.De(Reglement.Lire(File.ReadAllText(Fichier("super-mario-bros.1-1.json"))));
        Assert.Equal(("speedrun", "World 1-1", 0), (niveau.Category, niveau.Title, niveau.Milestones));
        Assert.Equal(["NO DEATH"], niveau.Fails.Select(x => x.Label));
        Assert.Empty(niveau.Counters);
        var warpless = DeckTrialPlan.De(Reglement.Lire(File.ReadAllText(Fichier("super-mario-bros.warpless.json"))));
        Assert.Equal(7, warpless.Milestones);   // mondes 2 a 8
        Assert.Equal(["NO GAME OVER"], warpless.Fails.Select(x => x.Label));
    }

    [Fact]
    public void Le_bloc_display_du_reglement_nomme_cede_et_declare_l_efficacite()
    {
        var p = DeckTrialPlan.De(AvecAffichage());
        Assert.Equal("HITS TAKEN", p.Counters[0].Label);
        Assert.Equal("KNIFE ONLY", p.Fails.Single(f => f.Id == "AMMO_LOSE").Label);
        Assert.Equal(["ammo"], p.Yields);
        var e = Assert.Single(p.Efficiency);
        Assert.Equal(("knife", "KNIFE EFF.", "swings", "kills", "percent"), (e.Id, e.Label, e.Actions, e.Results, e.Format));
        Assert.Equal(2, p.Tallies.Count);
        // une efficacite qui cite un compte inconnu ne s'affiche pas
        Assert.Empty(DeckTrialPlan.De(AvecAffichage(results: "nothing")).Efficiency);
        // un bloc mal forme ne fait rien echouer
        Assert.Empty(DeckTrialPlan.De(AvecAffichage(brut: "{\"tallies\": 3, \"yields\": {}}")).Efficiency);
    }

    // ─────────────────────────── le calcul ───────────────────────────

    private static DeckTrialSession Session(string etat, int coups, long? depart = 1000, long? officiel = null, bool provisoire = false,
        string? cause = null, long? pb = null)
        => new("s1", "a1", 1, etat, cause, depart is null ? null : 1700, depart, officiel, provisoire, null, pb, new Dictionary<string, int> { ["hits"] = coups });

    private static DeckTrial Composer(DeckTrialPlan p, DeckTrialSession s, DeckRank? rang = null)
        => DeckTrialCalcul.Composer(p, "L1", s, rang, null, new Dictionary<string, int>(), new Dictionary<string, IReadOnlyList<int>>(), 0, [], []);

    [Fact]
    public void En_course_le_decompte_et_la_penalite_suivent_les_coups()
    {
        var p = DeckTrialPlan.De(Couteau);
        var t = Composer(p, Session(SessionSpeedrun.Running, 3));
        var c = Assert.Single(t.Counters);
        Assert.Equal((3, 99, 96, false, 180L, "+3 s"), (c.Value, c.FailsAt, c.Left, c.Warn, c.PenaltyFrames, c.PenaltyText));
        Assert.True(t.Clock.Running);
        Assert.Equal(1000, t.Clock.StartClockMs);
        Assert.Null(t.Clock.Frames);
        Assert.Equal(180, t.Clock.PenaltyFrames);
        Assert.All(t.Fails, f => Assert.Equal("ok", f.State));

        // alerte pres du maximum : 9 restants ou moins (un dixieme de 99)
        Assert.False(Composer(p, Session(SessionSpeedrun.Running, 89)).Counters[0].Warn);
        Assert.True(Composer(p, Session(SessionSpeedrun.Running, 90)).Counters[0].Warn);
    }

    [Fact]
    public void Un_echec_dit_sa_cause_et_l_arrivee_fige_le_chrono_penalites_comprises()
    {
        var p = DeckTrialPlan.De(Couteau);
        var mort = Composer(p, Session(SessionSpeedrun.Invalid, 2, cause: "LOSE_LIFE"));
        Assert.Equal("tripped", mort.Fails.Single(f => f.Id == "LOSE_LIFE").State);
        Assert.Equal("ok", mort.Fails.Single(f => f.Id == "AMMO_LOSE").State);
        Assert.False(mort.Counters[0].Tripped);
        var trop = Composer(p, Session(SessionSpeedrun.Invalid, 99, cause: "hits"));
        Assert.True(trop.Counters[0].Tripped);
        Assert.Equal(0, trop.Counters[0].Left);

        // arrivee provisoire : le temps sans les penalites, le Deck recoit le temps penalites comprises
        var prov = Composer(p, Session(SessionSpeedrun.Running, 2, officiel: 7300, provisoire: true));
        Assert.False(prov.Clock.Running);
        Assert.Equal(7300 + 120, prov.Clock.Frames);
        // finie : le temps officiel comprend deja les penalites
        var fini = Composer(p, Session(SessionSpeedrun.Finished, 2, officiel: 7420));
        Assert.Equal(7420, fini.Clock.Frames);
        Assert.Equal(Chronometrage.Afficher(7420, Couteau.Cadence), fini.Clock.Text);
        // avant le depart : 0:00.00, rien d'arme
        var avant = Composer(p, Session(SessionSpeedrun.Armed, 0, depart: null));
        Assert.Equal(0, avant.Clock.Frames);
        Assert.All(avant.Fails, f => Assert.Equal("idle", f.State));
    }

    [Fact]
    public void Le_record_le_meilleur_des_deux_et_la_cible_du_rang_vise()
    {
        var p = DeckTrialPlan.De(Couteau);
        var rang = Rang(target: 21_540, pb: 22_102);
        var t = Composer(p, Session(SessionSpeedrun.Running, 0, pb: 23_000), rang);
        Assert.Equal((22_102L, "board"), (t.Comparisons.PersonalBest!.Frames, t.Comparisons.PersonalBest.Source));
        Assert.Equal((21_540L, 4), (t.Comparisons.Target!.Frames, t.Comparisons.Target.Rank));
        var local = Composer(p, Session(SessionSpeedrun.Running, 0, pb: 20_000), rang);
        Assert.Equal((20_000L, "local"), (local.Comparisons.PersonalBest!.Frames, local.Comparisons.PersonalBest.Source));
    }

    private static DeckRank Rang(long? target, long? pb, bool final = false, int? rank = null)
        => new("L1", DeckModes.Speedrun, "arcade/metal-slug-3", "nelfe", DeckRankBoard.Epreuve, "ch-ms3-m1-knife-v7", "CHALLENGE", "time", "lower", true,
            final ? "ranked" : "racing", "fresh", null, 9, "home", null, null, rank, false, null, false, false, null, null, null,
            target is { } t ? new DeckRankEntry(4, "bob", t, null, false, "6:03.97") : null,
            pb is { } b ? new DeckRankEntry(6, "nelfe", b, null, true, "6:13.47") : null, false, final, 59.185606, 1);

    // ─────────────────────────── la session reelle et le service ───────────────────────────

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    private static void Partir(SessionSpeedrun s)
    {
        s.RecevoirImage([S(800, "GAME_START", "0x10FDB6", 0x01)]);
        s.RecevoirImage([S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00)]);
        foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0x10FDB6 ? 1 : 0);
        s.RecevoirImage([S(1700, "MOUNT_STATE", "0x1002C1", 0x01)]);
    }

    private static void Coup(SessionSpeedrun s, long frame)
    {
        s.RecevoirImage([S(frame, "HIT", "0x100554", 0x02)]);
        s.RecevoirImage([S(frame + 1, "HIT", "0x100554", 0x00)]);
    }

    [Fact]
    public void Un_coup_recu_publie_un_evenement_de_session_donc_un_instantane()
    {
        var s = new SessionSpeedrun(Couteau.ChallengeId, Couteau);
        Partir(s);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        s.PrendreLesEvenements();
        var avant = s.Sequence;
        Coup(s, 2000);
        var e = Assert.Single(s.PrendreLesEvenements());
        Assert.Equal("speedrun.counter", e.Type);
        Assert.Equal(avant + 1, s.Sequence);
        var j = JsonSerializer.SerializeToElement(e.Donnees);
        Assert.Equal(("hits", 1, 98, 60L), (j.GetProperty("id").GetString(), j.GetProperty("value").GetInt32(), j.GetProperty("left").GetInt32(),
            j.GetProperty("penaltyFrames").GetInt64()));
        Assert.Equal(1, s.Instantane().Compteurs!["hits"]);
        // le resultat certifie ne change pas : un coup reste un coup, une seconde
        Coup(s, 2100);
        s.RecevoirImage([S(9000, "PROGRESSION_STAGE", "0x10040C", 0x01)]);
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        Assert.Equal(9000 - 1700 + 2 * 60, s.PrendreLaMeilleure()!.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Le_service_suit_la_session_et_publie_deck_trial_a_chaque_coup()
    {
        var bus = new Bus();
        var service = new DeckTrialService(bus);
        service.StartAsync(default).Wait();
        service.Armer(Lancement(), DeckTrialPlan.De(Couteau));
        var premier = bus.Derniere()!;
        Assert.Equal(("IDLE", 0L), (premier.State, premier.Clock.Frames ?? -1));

        var s = new SessionSpeedrun(Couteau.ChallengeId, Couteau);
        void Publier()
        {
            foreach (var e in s.PrendreLesEvenements()) bus.PublishAsync(new EventEnvelope { Type = e.Type, Payload = JsonSerializer.SerializeToNode(e.Donnees) });
            bus.PublishAsync(new EventEnvelope { Type = "speedrun.session", Payload = JsonSerializer.SerializeToNode(s.Instantane()) });
        }

        Partir(s);
        Publier();
        Assert.True(bus.Derniere()!.Clock.Running);
        var n = bus.Messages.Count;
        Coup(s, 2000);
        Publier();
        var t = bus.Derniere()!;
        Assert.True(bus.Messages.Count > n);
        Assert.Equal((1, 98), (t.Counters[0].Value, t.Counters[0].Left!.Value));
        Assert.Equal(60, t.Clock.PenaltyFrames);

        // un message identique ne repart pas
        n = bus.Messages.Count;
        Publier();
        Assert.Equal(n, bus.Messages.Count);

        // la cible du rang vise arrive par deck.rank
        bus.PublishAsync(new EventEnvelope { Type = "deck.rank", Payload = Rang(21_540, 22_102) });
        Assert.Equal(21_540, bus.Derniere()!.Comparisons.Target!.Frames);
        Assert.Same(bus.Derniere(), service.Courant);

        // une mort : l'echec et sa cause ; le chrono se fige a l'image de la fin
        s.RecevoirImage([S(3000, "LOSE_LIFE", "0x1002BB", 0x01)]);
        Publier();
        var mort = bus.Derniere()!;
        Assert.Equal(SessionSpeedrun.Invalid, mort.State);
        Assert.Equal("tripped", mort.Fails.Single(f => f.Id == "LOSE_LIFE").State);
        Assert.False(mort.Clock.Running);
        Assert.Equal(3000 - 1700 + 60, mort.Clock.Frames);

        // la fin du jeu eteint l'epreuve
        bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
        Assert.Null(service.Courant);
    }

    [Fact]
    public void L_efficacite_compte_ses_evenements_pendant_la_course_et_garde_un_historique()
    {
        var r = AvecAffichage();
        var bus = new Bus();
        var service = new DeckTrialService(bus);
        service.StartAsync(default).Wait();
        service.Armer(Lancement(), DeckTrialPlan.De(r));
        void Session(string attempt, string etat, int numero)
            => bus.PublishAsync(new EventEnvelope
            {
                Type = "speedrun.session",
                Payload = JsonNode.Parse($"{{\"sessionId\":\"s\",\"attemptId\":\"{attempt}\",\"attemptNumber\":{numero},\"state\":\"{etat}\",\"start\":{{\"frame\":1700,\"clockMs\":1000}},\"counters\":{{\"hits\":0}}}}"),
            });
        void Signal(string action, string adresse)
            => bus.PublishAsync(new EventEnvelope
            {
                Type = "retroarch.memory.changed",
                Payload = JsonNode.Parse($"{{\"signal\":{{\"Name\":\"{action}\",\"Address\":\"{adresse}\",\"Value\":1,\"Frame\":2000}}}}"),
            });

        Signal("ATTACK_MELEE", "0x100700");   // avant le depart : ne compte pas
        Session("a1", "RUNNING", 1);
        for (var i = 0; i < 8; i++) Signal("ATTACK_MELEE", "0x100700");
        for (var i = 0; i < 5; i++) Signal("ENEMY_DOWN", "0x100701");
        Signal("ENEMY_DOWN", "0x999999");     // une autre adresse : ne compte pas
        var e = Assert.Single(bus.Derniere()!.Efficiency);
        Assert.Equal((8, 5, 63, "63%", "SWINGS", "KILLS"), (e.Actions, e.Results, e.Percent!.Value, e.Text, e.ActionsLabel, e.ResultsLabel));
        Assert.Empty(e.History);

        Session("a1", "FINISHED", 1);
        Session("a2", "RUNNING", 2);
        e = Assert.Single(bus.Derniere()!.Efficiency);
        Assert.Equal((0, 0, null), (e.Actions, e.Results, e.Percent));
        Assert.Equal([63], e.History);
    }

    private static Reglement AvecAffichage(string results = "kills", string? brut = null)
    {
        var json = JsonNode.Parse(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.v7.json")))!.AsObject();
        json["display"] = JsonNode.Parse(brut ?? $$"""
            {
              "labels": { "hits": "Hits taken", "AMMO_LOSE": "Knife only" },
              "yields": ["ammo"],
              "tallies": [
                { "id": "swings", "label": "Swings", "event": { "action": "ATTACK_MELEE", "address": "0x100700" } },
                { "id": "kills", "label": "Kills", "event": { "action": "ENEMY_DOWN", "address": "0x100701" } }
              ],
              "efficiency": [ { "id": "knife", "label": "Knife eff.", "actions": "swings", "results": "{{results}}", "format": "percent" } ]
            }
            """);
        return Reglement.Lire(json.ToJsonString());
    }

    private sealed class Bus : IEventBus
    {
        private readonly List<Action<EventEnvelope>> _abonnes = [];
        public readonly List<DeckTrial> Messages = [];

        public DeckTrial? Derniere() => Messages.Count == 0 ? null : Messages[^1];

        public Task PublishAsync<TEvent>(TEvent evenement) where TEvent : class
        {
            if (evenement is not EventEnvelope e) return Task.CompletedTask;
            if (e.Type == "deck.trial" && e.Payload is DeckTrial t) Messages.Add(t);
            foreach (var a in _abonnes.ToList()) a(e);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (handler is Action<EventEnvelope> h) _abonnes.Add(h);
            return new Fin(() => { if (handler is Action<EventEnvelope> x) _abonnes.Remove(x); });
        }

        private sealed class Fin(Action a) : IDisposable
        {
            public void Dispose() => a();
        }
    }
}
