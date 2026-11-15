using System.Runtime.Versioning;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// METAL SLUG 3, LA MORT DU BOSS (decision du user du 15/11) : le chrono de la mission 1 s'arrete quand 0x10ED7C passe a 01 (le jeu
/// declare la mission gagnee : minuteur fige, joueur ejecte du tank), et non plus quand la mission quitte 00 (le bilan des prisonniers
/// se pressait au tir martele, 12 s). Trois parties de la borne (replays rejoues au banc sans fenetre), passees par le wrapper simule
/// et l'evaluateur avec les BROUILLONS des reglements et du .MEM (dossier Ms3MortDuBoss).
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class Ms3MortDuBossTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Ms3MortDuBoss", nom);

    private static byte[] Mem => File.ReadAllBytes(Fichier("metal-slug-3.mort-du-boss.MEM"));

    private static Reglement Brouillon(string nom) => Reglement.Lire(File.ReadAllText(Fichier(nom + ".brouillon.json")));

    public static TheoryData<string> Brouillons => new()
    {
        "metal-slug-3.mission-1-rambo.v2", "metal-slug-3.mondes-a-l-envers.v3", "metal-slug-3.mission-1-pistol.v2",
        "metal-slug-3.mission-1-knife.v8", "metal-slug-3.boss-m1.v2",
    };

    [Theory]
    [MemberData(nameof(Brouillons))]
    public void Chaque_brouillon_ne_cite_que_des_lignes_du_nouveau_mem_et_arrete_le_chrono_au_boss(string nom)
    {
        var r = Brouillon(nom);
        Assert.Empty(r.VerifierContre(Mem));
        var arrivee = Assert.Single(r.Independant!.Arrivee.Evenements);
        Assert.Equal(("BOSS_DEFEATED", 0x10ED7CL, 0x01), (arrivee.Action, arrivee.Adresse!.Value, arrivee.Valeur!.Value));
        // La tentative finit quand la mission quitte 00 : le bilan est fait, le score est stable.
        var fin = Assert.Single(r.Arrivee);
        Assert.Equal(("PROGRESSION_STAGE", 0x10040CL, 0x00), (fin.Action, fin.Adresse!.Value, fin.Quitte!.Value));
        Assert.Equal(nom.Contains("rambo") || nom.Contains("mondes"), r.ClasseAuScore);
        Assert.Equal(r.ClasseAuScore ? 23082 : null, r.LimiteDeTemps);
    }

    /// <summary>Le .MEM d'aujourd'hui (0fb6762a) n'a pas la ligne : les brouillons ne s'y verifient pas.</summary>
    [Fact]
    public void Le_mem_d_aujourd_hui_n_a_pas_la_ligne_de_la_mort_du_boss()
    {
        var ancien = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Reglements", "metal-slug-3.MEM"));
        Assert.Contains(Brouillon("metal-slug-3.mission-1-pistol.v2").VerifierContre(ancien), e => e.Contains("BOSS_DEFEATED", StringComparison.Ordinal));
    }

    /// <summary>Le score du joueur 1 tel que l'agregateur le decode : 0x10ED18, u32 big-endian en BCD.</summary>
    private static long Bcd(long brut)
    {
        long s = 0;
        for (var decalage = 28; decalage >= 0; decalage -= 4) s = s * 10 + ((brut >> decalage) & 0xF);
        return s;
    }

    /// <summary>Rejoue une trace : le wrapper simule emet, l'evaluateur juge, le score suit 0x10ED18. Rend le resultat et les coupures.</summary>
    private static (ResultatTentative Resultat, List<(long Frame, long Adresse, string Action)> Coupures) Rejouer(string trace, Reglement r)
    {
        var t = TraceDeReplay.Lire(File.ReadAllLines(Fichier(trace)));
        var sim = new WrapperSimule(LignesDuMem.Lire(System.Text.Encoding.UTF8.GetString(Mem)));
        var ev = new Evaluateur(r);
        foreach (var image in t.Lectures.GroupBy(l => l.Frame))
        {
            var signaux = image.SelectMany(l => sim.Lire(l.Frame, l.Adresse, l.Type, l.Valeur)).ToList();
            foreach (var a in ev.AdressesInconnues)
                if (sim.Valeur(a) is { } v) ev.Relever(a, v);
            if (signaux.Count > 0) ev.RecevoirImage(signaux);
            foreach (var l in image.Where(l => l.Adresse == 0x10ED18)) ev.NoterLeScore(Bcd(l.Valeur));
            if (ev.EstTerminee) break;
        }

        return (ev.Resultat(), sim.Coupures);
    }

    [Theory]
    // replay, frame de la mort du boss (0x10ED7C 00 -> 01), passage en mission 2, score a ce passage
    [InlineData("ms3-r2-mission-1.trace", 12526, 14265, 626460)]
    [InlineData("ms3-r3-mission-1.trace", 13780, 15093, 628040)]
    [InlineData("ms3-r1-mission-1.trace", 13526, 14496, 396740)]
    public void Sur_trois_parties_de_la_borne_le_chrono_s_arrete_a_la_mort_du_boss_et_le_score_garde_le_bilan(string trace, long boss, long mission2, long score)
    {
        var r = Brouillon("metal-slug-3.mission-1-rambo.v2");
        var (res, coupures) = Rejouer(trace, r);

        Assert.Equal(EtatTentative.Finie, res.Etat);
        Assert.Equal(boss, res.Independant!.Arrivee);
        Assert.Equal(mission2, res.FrameDeFin);
        Assert.Equal(boss - res.Independant.Depart, res.FramesOfficielles);
        Assert.Equal(score, res.Score);
        Assert.True(res.FramesOfficielles <= r.LimiteDeTemps);
        Assert.Contains(res.Journal, c => c.Role == "finish" && c.Adresse == 0x10040C);
        // La ligne de la mort du boss n'est jamais coupee par l'auto-mute.
        Assert.DoesNotContain(coupures, c => c.Adresse == 0x10ED7C);
        Assert.DoesNotContain(coupures, c => r.AdressesCitees().Contains(c.Adresse));

        // Le meme chrono pour un challenge au temps (pistolet pur : ses verrous ne jouent pas dans la trace).
        var auTemps = Rejouer(trace, Brouillon("metal-slug-3.mission-1-pistol.v2")).Resultat;
        Assert.Equal(EtatTentative.Finie, auTemps.Etat);
        Assert.Equal(boss - auTemps.Independant!.Depart, auTemps.FramesOfficielles);
        Assert.Null(auTemps.Score);
    }

    [Fact]
    public void Le_temps_maximal_rate_une_partie_trop_lente()
    {
        // Le chrono de r2, puis une limite d'une image en dessous : ratee ; pile a la limite : finie.
        var texte = File.ReadAllText(Fichier("metal-slug-3.mission-1-rambo.v2.brouillon.json"));
        Assert.Contains("\"time_limit_frames\": 23082", texte);
        var chrono = Rejouer("ms3-r2-mission-1.trace", Reglement.Lire(texte)).Resultat.FramesOfficielles!.Value;
        Reglement Limite(long l) => Reglement.Lire(texte.Replace("\"time_limit_frames\": 23082", "\"time_limit_frames\": " + l));
        var (res, _) = Rejouer("ms3-r2-mission-1.trace", Limite(chrono - 1));
        Assert.Equal(EtatTentative.Ratee, res.Etat);
        Assert.Equal("time_limit", res.Cause);
        Assert.Equal(EtatTentative.Finie, Rejouer("ms3-r2-mission-1.trace", Limite(chrono)).Resultat.Etat);
    }
}
