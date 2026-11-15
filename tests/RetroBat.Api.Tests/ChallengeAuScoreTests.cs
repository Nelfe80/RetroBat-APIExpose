using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES CHALLENGES AU SCORE (decision du user du 15/11, Metal Slug 3 : Rambo et « Les mondes a l'envers ») : le score du joueur 1
/// a l'arrivee classe, le temps departage, un temps maximal borne la traite ; le chrono s'arrete a la mort du boss (son propre
/// evenement), la tentative finit quand la mission quitte 00 (le score garde le bilan). Un challenge au temps ne change pas.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class ChallengeAuScoreTests
{
    /// <summary>L'evenement de la mort du boss dans ces tests (le chrono s'y arrete) ; le Data Pack cite la vraie ligne du .MEM.</summary>
    private const string ActionBoss = "BOSS_STATE";
    private const string AdresseBoss = "0x10FFF0";

    private static string Texte(string fichier) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", fichier));

    /// <summary>Rambo v1 tourne au score : rang score puis temps, 6 min 30 s au plus, chrono arrete a la mort du boss.</summary>
    private static Reglement RamboAuScore(long? limite = 23082)
    {
        var doc = JsonNode.Parse(Texte("metal-slug-3.mission-1-rambo.json"))!.AsObject();
        doc["version"] = 2;
        doc["ruleset"] = "ch-ms3-m1-rambo-v2";
        doc["ranking"] = JsonNode.Parse("""{"metric":"score","direction":"higher_better","then":[{"metric":"frames","direction":"lower_better"}]}""");
        doc["timing"]!["independent"]!["finish"] = new JsonObject
        {
            ["event"] = new JsonObject { ["action"] = ActionBoss, ["address"] = AdresseBoss, ["value"] = "0x01" },
        };
        doc["finish"] = JsonNode.Parse("""[{"event":{"action":"PROGRESSION_STAGE","address":"0x10040C","leaves":"0x00"}}]""");
        if (limite is { } l) doc["time_limit_frames"] = l;
        return Reglement.Lire(doc.ToJsonString());
    }

    private static void Armer(SessionSpeedrun s)
    {
        s.Relever(0x10FDB6, 0x01);
        s.Relever(0x10040C, 0x00);
        s.Relever(0x10040D, 0x00);
        s.Relever(0x1002C1, 0x00);
    }

    private static Signal Sig(long frame, string action, string adresse, int valeur) => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + frame * 17);

    private static void Partir(SessionSpeedrun s) => s.RecevoirImage([Sig(400, "MOUNT_STATE", "0x1002C1", 0x01)]);

    private static void Boss(SessionSpeedrun s, long frame) => s.RecevoirImage([Sig(frame, ActionBoss, AdresseBoss, 0x01)]);

    private static void QuitterLaMission(SessionSpeedrun s, long frame) => s.RecevoirImage([Sig(frame, "PROGRESSION_STAGE", "0x10040C", 0x01)]);

    [Fact]
    public void Le_reglement_lit_le_score_le_departage_et_le_temps_maximal()
    {
        var r = RamboAuScore();
        Assert.True(r.ClasseAuScore);
        Assert.True(r.DemandeLeScore);
        Assert.Equal(23082, r.LimiteDeTemps);
        Assert.Equal([new CritereDeRang("score", false), new CritereDeRang("frames", true)], r.Rang);
        Assert.Equal("628 040", r.AfficherLaValeur(628040));

        var auTemps = Reglement.Lire(Texte("metal-slug-3.mission-1-rambo.json"));
        Assert.False(auTemps.ClasseAuScore);
        Assert.Null(auTemps.LimiteDeTemps);
        Assert.Equal(Chronometrage.Afficher(15524, auTemps.Cadence), auTemps.AfficherLaValeur(15524));
        Assert.Throws<ReglementInvalideException>(() => Reglement.Lire(Texte("metal-slug-3.mission-1-rambo.json").Replace("\"order\"", "\"time_limit_frames\": 0, \"order\"")));
    }

    [Fact]
    public void Le_chrono_s_arrete_au_boss_et_le_score_se_fige_quand_la_mission_quitte_00()
    {
        var r = RamboAuScore();
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        s.NoterLeScore(0);
        Partir(s);
        s.NoterLeScore(86460);
        Boss(s, 12480);
        // Le bilan : prisonniers, puis +300 000 et +100 000 ; le chrono, lui, est deja arrete.
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        s.NoterLeScore(206460);
        s.NoterLeScore(628040);
        QuitterLaMission(s, 14265);

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        var c = s.PrendreLaCloture();
        Assert.NotNull(c);
        Assert.Equal(12480 - 400, c.Frames);
        Assert.Equal(628040, c.Score);
        var m = Assert.Single(s.PrendreLesMessages(), x => x.Cle == "speedrun.msg.challenge_complete_score");
        Assert.Contains(("score", "628 040"), m.Valeurs);

        // Fige : la mission 2 ne change plus rien.
        s.NoterLeScore(700000);
        var t = s.PrendreLaMeilleure()!;
        Assert.Equal(628040, t.Resultat.Score);
        Assert.Equal(12480 - 400, t.Resultat.FramesOfficielles);
        Assert.Equal([(628040L, false), (12080L, true)], t.Resultat.ValeursDeRang(r).Select(v => ((long)v.Valeur!, v.PlusPetitMeilleur)).ToList());
    }

    [Fact]
    public void Au_dela_du_temps_maximal_la_tentative_est_ratee_et_le_joueur_le_lit()
    {
        var r = RamboAuScore(limite: 1000);
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);
        s.NoterLeScore(90000);
        Boss(s, 400 + 1001);
        QuitterLaMission(s, 3000);

        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
        Assert.False(s.Close);
        Assert.Null(s.PrendreLaCloture());
        Assert.Null(s.PrendreLaMeilleure());
        Assert.Contains(s.PrendreLesMessages(), m => m.Cle == "speedrun.msg.failed_time_limit");
    }

    [Fact]
    public void Pile_au_temps_maximal_la_tentative_compte()
    {
        var r = RamboAuScore(limite: 1000);
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);
        s.NoterLeScore(90000);
        Boss(s, 400 + 1000);
        QuitterLaMission(s, 3000);
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Sans_mort_du_boss_vue_le_chrono_n_a_pas_d_arrivee_et_rien_ne_se_classe()
    {
        var r = RamboAuScore();
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);
        s.NoterLeScore(90000);
        QuitterLaMission(s, 14265);
        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
        Assert.Null(s.PrendreLaMeilleure());
        Assert.Null(s.PrendreLaCloture());
    }

    [Fact]
    public void Sans_score_lu_la_tentative_au_score_est_annulee()
    {
        var r = RamboAuScore();
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);
        Boss(s, 12480);
        QuitterLaMission(s, 14265);
        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
        Assert.Null(s.PrendreLaMeilleure());
    }

    [Fact]
    public void La_meilleure_se_juge_au_score_puis_au_temps()
    {
        var r = RamboAuScore();
        ResultatTentative R(long score, long frames) => new(EtatTentative.Finie, null, frames, Reglement.ChronoIndependant, null,
            new MesureChrono(0, frames, frames, 0), frames, null, "non_mesure", new Dictionary<string, int>(), new Dictionary<string, int?>(),
            [], [], r.Cadence, score);
        Assert.True(r.Comparer(R(600000, 20000), R(500000, 10000)) < 0);
        Assert.True(r.Comparer(R(600000, 10000), R(600000, 12000)) < 0);
        Assert.Equal(0, r.Comparer(R(600000, 10000), R(600000, 10000)));
        Assert.True(r.Comparer(R(1, 1) with { Score = null }, R(1, 1)) > 0);
    }

    [Fact]
    public void Le_passeport_au_score_porte_le_score_a_l_arrivee_et_le_verdict_le_dit()
    {
        var r = RamboAuScore();
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);
        s.NoterLeScore(628040);
        Boss(s, 12480);
        QuitterLaMission(s, 14265);
        var t = s.PrendreLaMeilleure()!;

        var defi = PasseportDeDefi.Decrire(r, t);
        Assert.Equal(628040, (long?)defi["challenge"]?["score_at_finish"]);
        var p = new JsonObject
        {
            ["game"] = new JsonObject { ["ruleset"] = "1cc", ["profile_version"] = 3, ["profile_document_sha256"] = new string('b', 64) },
            ["metric"] = new JsonObject { ["type"] = "score" },
        };
        PasseportDeDefi.Appliquer(p, defi, new EntreeDeLIndex("ch-ms3-m1-rambo-v2", new string('a', 64), null));
        Assert.Equal("score", (string?)p["metric"]?["type"]);
        Assert.Equal("628040", (string?)p["metric"]?["value"]);
        Assert.Equal("higher_better", (string?)p["metric"]?["ranking_direction"]);
        Assert.Equal("628040", (string?)p["progression"]?["checkpoints"]?[1]?["metric"]);
        Assert.Equal(12080, (long?)p["challenge"]?["timing"]?["independent"]?["frames"]);
        Assert.Equal(12480 - 400, PasseportDeDefi.TempsDuBloc(p["challenge"] as JsonObject));

        var texte = PasseportDeDefi.TexteDuVerdict(p, "published", 2, "", "en");
        Assert.Equal("Challenge: 628 040 points in " + Chronometrage.Afficher(12080, r.Cadence) + " ranked 2nd", texte);
        Assert.StartsWith("Défi : 628 040 points en ", PasseportDeDefi.TexteDuVerdict(p, "published", 2, "", "fr"));
    }

    [Fact]
    public void Un_challenge_au_temps_garde_exactement_son_passeport()
    {
        var r = Reglement.Lire(Texte("metal-slug-3.mission-1-rambo.json"));
        var s = new SessionSpeedrun(r.ChallengeId, r);
        Armer(s);
        Partir(s);
        s.NoterLeScore(628040);
        QuitterLaMission(s, 9000);
        var t = s.PrendreLaMeilleure()!;
        Assert.Null(t.Resultat.Score);
        var defi = PasseportDeDefi.Decrire(r, t);
        Assert.False((defi["challenge"] as JsonObject)!.ContainsKey("score_at_finish"));
        Assert.False(defi.ContainsKey("metrique"));
        var c = s.PrendreLaCloture();
        Assert.Contains(s.PrendreLesMessages(), m => m.Cle == "speedrun.msg.challenge_complete");
        Assert.Null(c!.Score);
    }

    [Fact]
    public void Le_verdict_d_un_challenge_au_score_va_au_score_du_bilan()
    {
        var r = RamboAuScore();
        var b = new BilanDEpreuve(r, "Metal Slug 3", "ASH", null, IssueDuBilan.EnVerification, 12080, 1, 1, Score: 628040, ALArrivee: true);
        var v = DefisPanelService.AvecLeVerdict(b, IssueDuBilan.Classe, 1, 628040, new ClassementDEpreuve(1, [new TempsClasse(1, "ASH", 628040)]));
        Assert.Equal(12080, v.Temps);
        Assert.Equal(628040, v.Score);
        Assert.Equal(628040, v.Record);

        var auTemps = Reglement.Lire(Texte("metal-slug-3.mission-1-rambo.json"));
        var b2 = new BilanDEpreuve(auTemps, "Metal Slug 3", "ASH", null, IssueDuBilan.EnVerification, 12080, 1, 1, Score: 628040, ALArrivee: true);
        var v2 = DefisPanelService.AvecLeVerdict(b2, IssueDuBilan.Classe, 1, 12000, null);
        Assert.Equal(12000, v2.Temps);
        Assert.Equal(628040, v2.Score);
    }
}
