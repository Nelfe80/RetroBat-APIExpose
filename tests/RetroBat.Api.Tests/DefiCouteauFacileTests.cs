using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// METAL SLUG 3 FACILITE (demande du user du 03/11, v5) : le couteau et les grenades seulement, le joueur invincible (minuteur
/// d'invulnerabilite 0x100581 tenu a 02, triche MAME « P1 Invincibility »), et les coups recus comptes : le drapeau du coup
/// 0x100554 (02 au contact, 06 pour l'autre coup) se pose meme quand le joueur est invulnerable (labo MAME du 03/11). Un coup
/// au plus par seconde, 99 au plus (le 99e fait echouer), une seconde de penalite par coup.
/// </summary>
public class DefiCouteauFacileTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static readonly Reglement Facile = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.v5.json")));

    private static int _ids;

    private static SessionSpeedrun Nouvelle() => new(Facile.ChallengeId, Facile,
        () => "f" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    private static void Image(SessionSpeedrun s, params Signal[] signaux) => s.RecevoirImage(signaux);

    private static void Partir(SessionSpeedrun s)
    {
        Image(s, S(800, "GAME_START", "0x10FDB6", 0x01));
        Image(s, S(803, "PROGRESSION_STAGE", "0x10040C", 0x08));
        Image(s, S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00));
        foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0x10FDB6 ? 1 : 0);
        Image(s, S(1700, "MOUNT_STATE", "0x1002C1", 0x01));
    }

    private static void Coup(SessionSpeedrun s, long frame, int valeur = 0x02)
    {
        Image(s, S(frame, "HIT", "0x100554", valeur));
        Image(s, S(frame + 1, "HIT", "0x100554", 0x00));
    }

    [Fact]
    public void La_v5_tient_couteau_arme_et_invulnerabilite_et_compte_les_coups()
    {
        Assert.Equal("ch-ms3-m1-knife-v5", Facile.Ruleset);
        Assert.Equal(5, Facile.Version);
        Assert.Equal("0x100596,1,set_bits,0x01;0x10EAF3,1,value,0x00;0x100581,1,value,0x02;0x10EAF5,1,value,0x32", Facile.LignesDesVerrous);
        var coups = Assert.Single(Facile.Compteurs);
        Assert.Equal(("hits", 98, 60, 60), (coups.Id, coups.Maximum, coups.GraceFrames, coups.PenaliteFrames));
        Assert.Empty(Facile.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.v5.MEM"))));
    }

    [Fact]
    public void Chaque_coup_compte_ajoute_une_seconde_au_temps()
    {
        var s = Nouvelle();
        Partir(s);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        Coup(s, 2000);
        Coup(s, 2010);               // dans la seconde de grace : ne compte pas
        Coup(s, 2100, 0x06);         // l'autre type de coup compte aussi
        Image(s, S(9000, "PROGRESSION_STAGE", "0x10040D", 0x04));
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        var t = s.PrendreLaMeilleure()!;
        Assert.Equal(2, t.Resultat.Compteurs["hits"]);
        Assert.Equal(9000 - 1700 + 2 * 60, t.Resultat.FramesOfficielles);
    }

    [Fact]
    public void Chaque_coup_se_dit_au_joueur()
    {
        var s = Nouvelle();
        Partir(s);
        s.PrendreLesMessages();
        Coup(s, 2000);
        var m = Assert.Single(s.PrendreLesMessages());
        Assert.Equal("speedrun.msg.hit", m.Cle);
        Assert.Contains(("left", "98"), m.Valeurs);
        Assert.Contains(("penalty", "1"), m.Valeurs);
    }

    [Fact]
    public void Le_99e_coup_fait_echouer()
    {
        var s = Nouvelle();
        Partir(s);
        for (var i = 0; i < 98; i++) Coup(s, 2000 + i * 61);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        Coup(s, 2000 + 98 * 61);
        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
    }
}

/// <summary>PAS DE RALENTI EN SPEEDRUN (03/11) : la vitesse du jeu du depart a l'arrivee, a l'horloge du wrapper.</summary>
public class VitesseDUnSpeedrunTests
{
    [Theory]
    [InlineData(1800, 0, 30_040, 1.0)]       // 1800 images a 59,92 en 30,04 s : pleine vitesse
    [InlineData(1800, 0, 54_600, 0.55)]      // le Green Hill 2 du user pendant un direct : 33 images par seconde
    public void La_vitesse_compare_les_images_a_l_horloge(long frames, long h0, long h1, double attendu)
        => Assert.Equal(attendu, Evaluateur.Vitesse(frames, h0, h1, 59.922743)!.Value, 2);

    [Fact]
    public void Sans_horloge_rien_ne_se_juge()
        => Assert.Null(Evaluateur.Vitesse(1800, null, 30_000, 59.92));

    [Fact]
    public void Le_seuil_ne_couvre_que_le_grain_de_l_horloge()
        => Assert.Equal(0.97, Evaluateur.VitesseMinimaleDUnSpeedrun);
}
