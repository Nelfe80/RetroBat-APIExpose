using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA CLOTURE D'UN CHALLENGE A L'ARRIVEE (decision du user du 14/11, apres Ash : « quand tu termines le niveau, tu passes au niveau
/// suivant sans que le challenge s'arrete ») : la tentative est close, RetroArch se met en pause, le jeu se ferme (le temps part
/// avec le proces-verbal du wrapper), et le bilan s'ouvre avec le temps, le score et les compteurs. L'echec, l'annulation,
/// l'abandon, l'entrainement et les speedruns ne changent pas.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class ChallengeClotureTests
{
    private static string Texte(string fichier) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", fichier));

    private static Reglement Lire(string fichier) => Reglement.Lire(Texte(fichier));

    /// <summary>Le soldat en l'air sur l'etat de depart (mission 1, aucune mission finie) : le depart s'arme.</summary>
    private static void Armer(SessionSpeedrun s)
    {
        s.Relever(0x10FDB6, 0x01);
        s.Relever(0x10040C, 0x00);
        s.Relever(0x10040D, 0x00);
        s.Relever(0x1002C1, 0x00);
    }

    private static Signal Sig(long frame, string action, string adresse, int valeur) => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + frame * 17);

    private static void Partir(SessionSpeedrun s) => s.RecevoirImage([Sig(400, "MOUNT_STATE", "0x1002C1", 0x01)]);

    private static void Arriver(SessionSpeedrun s, long frame = 9000) => s.RecevoirImage([Sig(frame, "PROGRESSION_STAGE", "0x10040C", 0x01)]);

    [Theory]
    [InlineData("metal-slug-3.mission-1-rambo.json", true)]
    [InlineData("metal-slug-3.mission-1-pistol.json", true)]
    [InlineData("metal-slug-3.mission-1-knife.v7.json", true)]
    [InlineData("metal-slug-3.boss-m1.json", true)]
    [InlineData("metal-slug-3.mondes-a-l-envers.v2.json", true)]
    [InlineData("metal-slug-3.training-m2-01.json", false)]
    [InlineData("sonic-the-hedgehog.green-hill-1.json", false)]
    public void Seuls_les_challenges_se_closent_a_l_arrivee(string fichier, bool clos)
        => Assert.Equal(clos, SessionSpeedrun.SeClotALArrivee(Lire(fichier)));

    [Fact]
    public void Rambo_arrive_la_tentative_est_close_et_plus_rien_ne_compte()
    {
        var s = new SessionSpeedrun("metal-slug-3.mission-1-rambo", Lire("metal-slug-3.mission-1-rambo.json"));
        Armer(s);
        Assert.Contains(s.PrendreLesMessages(), m => m.Cle == "speedrun.msg.challenge_ready");
        Partir(s);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        Assert.Null(s.PrendreLaCloture());

        Arriver(s);

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        Assert.True(s.Close);
        var c = s.PrendreLaCloture();
        Assert.NotNull(c);
        Assert.Equal(9000 - 400, c.Frames);
        Assert.True(c.Classable);
        Assert.Equal(s.AttemptId, c.AttemptId);
        Assert.Null(s.PrendreLaCloture());
        var messages = s.PrendreLesMessages();
        Assert.Contains(messages, m => m.Cle == "speedrun.msg.challenge_complete");
        Assert.DoesNotContain(messages, m => m.Cle is "speedrun.msg.finish_best" or "speedrun.msg.finish");
        // Le temps reste retenu pour le site : il part a la fermeture du jeu, que le service commande.
        Assert.Equal(9000 - 400, s.PrendreLaMeilleure()?.Resultat.FramesOfficielles);

        // La mission 2 commence derriere (le flux du wrapper a du retard) : aucune tentative ne s'ouvre, aucun releve n'est demande.
        Armer(s);
        s.RecevoirImage([Sig(9300, "PROGRESSION_STAGE", "0x10040C", 0x00)]);
        s.RecevoirImage([Sig(9500, "MOUNT_STATE", "0x1002C1", 0x01)]);
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        Assert.Empty(s.AdressesInconnues);
        Assert.Equal(1, s.Numero);

        // Hotkey + gauche (ou la pause tenue) ne relance plus rien.
        var reponse = s.Commander(new CommandeSpeedrun("r1", s.SessionId, SessionSpeedrun.ActionRelancer, Confirme: true), out var effet);
        Assert.Equal("rejected", reponse.Statut);
        Assert.Equal("challenge_over", reponse.Raison);
        Assert.Equal(EffetDemande.Aucun, effet);
        Assert.Null(s.PrendreLaRelanceAutomatique());
    }

    [Fact]
    public void Le_couteau_rend_ses_coups_et_leur_penalite_a_la_cloture()
    {
        var r = Lire("metal-slug-3.mission-1-knife.v7.json");
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);
        // Trois coups recus, espaces de plus que la grace (60 images).
        s.RecevoirImage([Sig(1000, "HIT", "0x100554", 0x01)]);
        s.RecevoirImage([Sig(1200, "HIT", "0x100554", 0x02)]);
        s.RecevoirImage([Sig(1400, "HIT", "0x100554", 0x03)]);
        var coups = s.Instantane().Compteurs!["hits"];
        Assert.True(coups > 0);

        Arriver(s);

        var c = s.PrendreLaCloture();
        Assert.NotNull(c);
        Assert.Equal(coups, c.Compteurs["hits"]);
        Assert.Equal(60L * coups, c.PenaliteFrames);
        Assert.Equal(9000 - 400 + 60L * coups, c.Frames);
    }

    [Fact]
    public void Un_echec_ne_clot_rien_et_la_relance_reste_possible()
    {
        var r = Lire("metal-slug-3.mission-1-knife.v7.json");
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);

        s.RecevoirImage([Sig(2000, "LOSE_LIFE", "0x1002BB", 0x01)]);

        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
        Assert.False(s.Close);
        Assert.Null(s.PrendreLaCloture());
        Assert.Contains(s.ActionsPermises(), a => a.Action == SessionSpeedrun.ActionRelancer && a.Permise);
    }

    [Fact]
    public void Un_abandon_ne_clot_rien()
    {
        var s = new SessionSpeedrun("metal-slug-3.mission-1-rambo", Lire("metal-slug-3.mission-1-rambo.json"));
        Armer(s);
        Partir(s);

        s.JeuFerme();

        Assert.Equal(SessionSpeedrun.Aborted, s.Etat);
        Assert.False(s.Close);
        Assert.Null(s.PrendreLaCloture());
    }

    [Fact]
    public void Un_speedrun_sur_le_meme_parcours_ne_se_clot_pas()
    {
        var r = Reglement.Lire(Texte("metal-slug-3.mission-1-rambo.json").Replace("\"challenge_rule\"", "\"speedrun_level\""));
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);

        Arriver(s);

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        Assert.False(s.Close);
        Assert.Null(s.PrendreLaCloture());
        // Il se relance seul, comme avant.
        Assert.Equal(TimeSpan.FromSeconds(3), s.PrendreLaRelanceAutomatique());
    }

    // ── Le service : pause, evenement, fermeture du jeu ─────────────────────────

    /// <summary>RetroArch d'essai : il joue, PAUSE_TOGGLE le met en pause et l'en sort, les commandes sont notees.</summary>
    private sealed class RetroArchDEssai : ICommandesRetroArch
    {
        private readonly Reglement _r;
        private readonly List<string> _envoyees = [];
        public volatile bool EnPause;

        public RetroArchDEssai(Reglement r) => _r = r;

        public int Combien(string commande)
        {
            lock (_envoyees) return _envoyees.Count(c => c == commande);
        }

        public List<string> Envoyees()
        {
            lock (_envoyees) return _envoyees.ToList();
        }

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            if (commande == "GET_STATUS") return Task.FromResult<string?>(EnPause ? "GET_STATUS PAUSED fbneo,mslug3,crc32=0" : "GET_STATUS PLAYING fbneo,mslug3,crc32=0");
            if (!commande.StartsWith("READ_CORE_RAM ", StringComparison.Ordinal)) return Task.FromResult<string?>(null);
            var offset = Convert.ToInt64(commande.Split(' ')[1], 16);
            var adresse = new[] { 0x10FDB6L, 0x10040CL, 0x10040DL, 0x1002C1L }.FirstOrDefault(a => _r.OffsetDansLeCoeur(a) == offset);
            var valeur = adresse == 0x10FDB6 ? 1 : 0;
            return Task.FromResult<string?>($"READ_CORE_RAM {commande.Split(' ')[1]} {valeur:X2}");
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            lock (_envoyees) _envoyees.Add(commande);
            if (commande == "PAUSE_TOGGLE") EnPause = !EnPause;
            return Task.CompletedTask;
        }
    }

    private static async Task Attendre(Func<bool> condition, int ms = 5000)
    {
        for (var i = 0; i < ms / 25 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static Task Publier(SimpleEventBus bus, string json)
        => bus.PublishAsync(new EventEnvelope { Type = "retroarch.memory.changed", Payload = JsonNode.Parse(json) });

    [Fact]
    public async Task A_l_arrivee_le_service_met_en_pause_annonce_le_bilan_puis_ferme_le_jeu()
    {
        var reglement = Lire("metal-slug-3.mission-1-rambo.json");
        var bus = new SimpleEventBus();
        var contexte = new ApiContext();
        var rom = Path.Combine(Path.GetTempPath(), "mslug3.zip");
        contexte.Ui.Running = new GameReference { SystemId = "arcade", GamePath = rom };
        var retroArch = new RetroArchDEssai(reglement);
        var journal = Path.Combine(Path.GetTempPath(), "speedrun-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var service = new SpeedrunSessionService(bus, contexte, NullLogger<SpeedrunSessionService>.Instance, null, retroArch, journal)
        {
            AttenteAvantLaFermetureSansPage = TimeSpan.FromMilliseconds(600),
            IntervalleDuQuit = TimeSpan.FromMilliseconds(300),
        };
        var annonces = new List<JsonElement>();
        using var abonnement = bus.Subscribe<EventEnvelope>(e =>
        {
            if (e.Type == "speedrun.challenge.finished") lock (annonces) annonces.Add(JsonSerializer.SerializeToElement(e.Payload));
        });
        try
        {
            await service.StartAsync(default);
            service.AttendreLeLancement(reglement, rom);
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { GamePath = rom } });
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Armed);

            await Publier(bus, """{"signal":{"Name":"MOUNT_STATE","Address":"0x1002C1","Value":1,"Frame":400}}""");
            await Publier(bus, """{"signal":{"Name":"AMMO_STATE","Address":"0x10EAFA","Value":999,"Frame":401}}""");
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Running);
            await bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new ScoreLiveEvent { Player = 1, Score = 123_450 } });
            await bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new ScoreLiveEvent { Player = 2, Score = 9 } });
            Assert.Equal(0, retroArch.Combien("PAUSE_TOGGLE"));

            await Publier(bus, """{"signal":{"Name":"PROGRESSION_STAGE","Address":"0x10040C","Value":1,"Frame":9000}}""");
            await Publier(bus, """{"signal":{"Name":"AMMO_STATE","Address":"0x10EAFA","Value":999,"Frame":9001}}""");

            // 1. Le bilan est annonce : temps, score du joueur 1, classable.
            await Attendre(() => { lock (annonces) return annonces.Count == 1; });
            var a = annonces[0];
            Assert.Equal("metal-slug-3.mission-1-rambo", a.GetProperty("challengeId").GetString());
            Assert.Equal(8600, a.GetProperty("frames").GetInt64());
            Assert.Equal(123_450, a.GetProperty("score").GetInt64());
            Assert.True(a.GetProperty("ranked").GetBoolean());

            // 2. RetroArch en pause, une seule fois : la garde anti-pause des defis ne la leve pas.
            await Attendre(() => retroArch.EnPause);
            await Task.Delay(400);
            Assert.True(retroArch.EnPause);
            Assert.Equal(1, retroArch.Combien("PAUSE_TOGGLE"));
            Assert.Equal(0, retroArch.Combien("QUIT"));

            // 3. Le jeu se ferme ; le score ne bouge plus pendant la pause.
            await bus.PublishAsync(new EventEnvelope { Type = "score.live.changed", Payload = new ScoreLiveEvent { Player = 1, Score = 999_999 } });
            await Attendre(() => retroArch.Combien("QUIT") >= 1);
            Assert.True(retroArch.Envoyees().IndexOf("PAUSE_TOGGLE") < retroArch.Envoyees().IndexOf("QUIT"));
            // RetroArch tarde : QUIT se redit tant que la session est ouverte.
            await Attendre(() => retroArch.Combien("QUIT") >= 2);

            // Le jeu se ferme : plus de QUIT, et le temps retenu part au site (le rapporteur le prend au proces-verbal du wrapper).
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            await Task.Delay(700);
            var quits = retroArch.Combien("QUIT");
            await Task.Delay(700);
            Assert.Equal(quits, retroArch.Combien("QUIT"));
            Assert.Equal(8600, service.PrendreLeMeilleurTemps()?.Tentative.Resultat.FramesOfficielles);
            lock (annonces) Assert.Single(annonces);
        }
        finally
        {
            await service.StopAsync(default);
            service.Dispose();
            if (File.Exists(journal)) File.Delete(journal);
        }
    }

    [Fact]
    public async Task Un_entrainement_qui_arrive_ne_ferme_rien()
    {
        var reglement = Reglement.Lire(Texte("metal-slug-3.mission-1-rambo.json").Replace("\"challenge_rule\"", "\"training\""));
        var bus = new SimpleEventBus();
        var contexte = new ApiContext();
        var rom = Path.Combine(Path.GetTempPath(), "mslug3.zip");
        contexte.Ui.Running = new GameReference { SystemId = "arcade", GamePath = rom };
        var retroArch = new RetroArchDEssai(reglement);
        var journal = Path.Combine(Path.GetTempPath(), "speedrun-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var service = new SpeedrunSessionService(bus, contexte, NullLogger<SpeedrunSessionService>.Instance, null, retroArch, journal)
        {
            AttenteAvantLaFermetureSansPage = TimeSpan.FromMilliseconds(100),
        };
        try
        {
            await service.StartAsync(default);
            service.AttendreLeLancement(reglement, rom);
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { GamePath = rom } });
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Armed);
            await Publier(bus, """{"signal":{"Name":"MOUNT_STATE","Address":"0x1002C1","Value":1,"Frame":400}}""");
            await Publier(bus, """{"signal":{"Name":"PROGRESSION_STAGE","Address":"0x10040C","Value":1,"Frame":9000}}""");
            await Publier(bus, """{"signal":{"Name":"AMMO_STATE","Address":"0x10EAFA","Value":999,"Frame":9001}}""");
            await Attendre(() => service.Instantane()?.Etat == SessionSpeedrun.Finished);

            await Task.Delay(800);
            Assert.Equal(0, retroArch.Combien("PAUSE_TOGGLE"));
            Assert.Equal(0, retroArch.Combien("QUIT"));
        }
        finally
        {
            await service.StopAsync(default);
            service.Dispose();
            if (File.Exists(journal)) File.Delete(journal);
        }
    }

    // ── Le bilan de l'arrivee ───────────────────────────────────────────────────

    private static DefisPanelService Panneau() => new(null!, null!, null!, null!, null!);

    [Fact]
    public void Le_bilan_de_l_arrivee_montre_le_temps_le_score_les_coups_et_la_penalite()
    {
        var r = Lire("metal-slug-3.mission-1-knife.v7.json");
        var panneau = Panneau();
        var b = panneau.BilanALArrivee(r, "Metal Slug 3", null, 8780, true, 123_450, new Dictionary<string, int> { ["hits"] = 3 }, 180, 2, false)
            with { Ouverture = 638_000_000_000_000_000 };

        var fin = panneau.ComposerLeBilan(b, "fr");

        Assert.Equal("CHALLENGE COMPLETE", fin.Titre);
        Assert.Equal(Chronometrage.Afficher(8780, r.Cadence), fin.MonScore!.Valeur);
        var mesures = fin.Joueurs[0].Mesures;
        // Trois colonnes : les tentatives cedent leur place (celle des joueurs classes vient avec le verdict).
        Assert.Equal(["SCORE", "HITS", "PENALTY"], mesures.Select(m => m.Libelle).ToArray());
        Assert.Equal(123_450, mesures[0].Valeur);
        Assert.True(mesures[0].Milliers);
        Assert.Equal(3, mesures[1].Valeur);
        Assert.Equal("+" + Chronometrage.Afficher(180, r.Cadence), mesures[2].Texte);

        // Le verdict garde la cle (l'animation ne repart pas), change le titre et ajoute les joueurs classes.
        var classe = DefisPanelService.AvecLeVerdict(b, IssueDuBilan.Classe, 4, 8780, null) with { Classes = 20 };
        var apres = panneau.ComposerLeBilan(classe, "fr");
        Assert.Equal(fin.Cle, apres.Cle);
        Assert.Equal("TIME RANKED", apres.Titre);
        Assert.Contains(apres.Joueurs[0].Mesures, m => m.Libelle == "SCORE" && m.Valeur == 123_450);
        Assert.Contains(apres.Joueurs[0].Mesures, m => m.Libelle == "PLAYERS" && m.Valeur == 20);
    }

    [Fact]
    public void Sans_compteur_le_bilan_de_l_arrivee_garde_les_tentatives_et_le_score()
    {
        var r = Lire("metal-slug-3.mission-1-rambo.json");
        var fin = Panneau().ComposerLeBilan(Panneau().BilanALArrivee(r, "Metal Slug 3", null, 10_703, true, 52_300, new Dictionary<string, int>(), 0, 1, false), "fr");

        Assert.Equal(["ATTEMPTS", "SCORE"], fin.Joueurs[0].Mesures.Select(m => m.Libelle).ToArray());
        Assert.Equal("CHALLENGE COMPLETE", fin.Titre);
    }

    [Theory]
    [InlineData(false, false, IssueDuBilan.NonEnvoye, "NOT SENT")]
    [InlineData(true, true, IssueDuBilan.NonEnvoye, "NOT SENT")]
    [InlineData(true, false, IssueDuBilan.EnVerification, "CHALLENGE COMPLETE")]
    public void Un_temps_non_classable_ou_un_lancement_sale_ne_part_pas(bool classable, bool sale, IssueDuBilan issue, string titre)
    {
        var r = Lire("metal-slug-3.mission-1-rambo.json");
        var b = Panneau().BilanALArrivee(r, "Metal Slug 3", null, 10_703, classable, null, new Dictionary<string, int>(), 0, 1, sale);

        Assert.Equal(issue, b.Issue);
        Assert.Equal(titre, Panneau().ComposerLeBilan(b, "fr").Titre);
        Assert.True(b.ALArrivee);
    }
}
