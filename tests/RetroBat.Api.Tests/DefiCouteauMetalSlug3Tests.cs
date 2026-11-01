using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// « MY ### AND MY KNIFE » (Metal Slug 3, decision du user du 01/11) : mission 1 du depart a l'arrivee de Huge Hermit, au
/// couteau, sans mourir. Valeurs mesurees au labo (MAME sans fenetre) et dans deux parties du user sous FBNeo : le joueur
/// parait (vehicule 0x1002C1 de 00 a 01) en mission 0 section 0, la poursuite de Huge Hermit commence en section 4.
/// </summary>
public class DefiCouteauMetalSlug3Tests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static readonly Reglement Couteau = Reglement.Lire(File.ReadAllText(Fichier("metal-slug-3.mission-1-knife.json")));

    private static int _ids;

    private static SessionSpeedrun Nouvelle() => new(Couteau.ChallengeId, Couteau,
        () => "id" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    private static void Image(SessionSpeedrun s, params Signal[] signaux) => s.RecevoirImage(signaux);

    /// <summary>Piece, HOW TO PLAY, SOLDIER SELECT, puis le joueur parait : la course part.</summary>
    private static void Partir(SessionSpeedrun s)
    {
        Image(s, S(800, "GAME_START", "0x10FDB6", 0x01));
        Image(s, S(803, "PROGRESSION_STAGE", "0x10040C", 0x08));
        Image(s, S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00));
        foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0x10FDB6 ? 1 : 0);
        Image(s, S(1700, "MOUNT_STATE", "0x1002C1", 0x01));
    }

    [Fact]
    public void Le_reglement_cite_seulement_des_lignes_du_MEM_de_la_borne()
    {
        Assert.Empty(Couteau.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.MEM"))));
        Assert.Equal("challenge_rule", Couteau.Kind);
        Assert.Equal("My ### and my knife", Couteau.Titre("en"));
        Assert.Equal("Ma ### et mon couteau", Couteau.Titre("fr"));
        Assert.Equal("Mission 1 jusqu'à Huge Hermit, au couteau, sans mourir", Couteau.Resume("fr_FR"));
    }

    [Theory]
    [InlineData(0x1002C1, 0x02C0)]
    [InlineData(0x10040C, 0x040D)]
    [InlineData(0x10FDB6, 0xFDB7)]
    public void Les_releves_lisent_le_bloc_que_FBNeo_expose(long adresse, long offset)
        => Assert.Equal(offset, Couteau.OffsetDansLeCoeur(adresse));

    [Fact]
    public void Sans_bloc_declare_l_adresse_se_lit_telle_quelle()
        => Assert.Equal(0xF601, Reglement.Lire(File.ReadAllText(Fichier("sonic-the-hedgehog.green-hill-1.json"))).OffsetDansLeCoeur(0xF601));

    [Fact]
    public void Apres_une_tentative_ratee_le_joueur_deja_la_n_arme_pas_une_nouvelle_course()
    {
        var s = Nouvelle();
        Partir(s);
        Image(s, S(3000, "LOSE_LIFE", "0x1002BB", 0x01));
        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);

        // Le joueur reapparait sur place, le vehicule reste a 01 ; monter puis descendre d'un Metal Slug ne part pas non plus.
        Image(s, S(3400, "MOUNT_STATE", "0x1002C1", 0x04));
        Image(s, S(3600, "MOUNT_STATE", "0x1002C1", 0x01));
        Assert.NotEqual(SessionSpeedrun.Running, s.Etat);

        Image(s, S(9000, "PROGRESSION_STAGE", "0x10040D", 0x04));
        Assert.NotEqual(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Le_vehicule_d_avant_l_apparition_se_releve()
    {
        var s = Nouvelle();
        Image(s, S(800, "GAME_START", "0x10FDB6", 0x01));
        Image(s, S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00));

        Assert.Contains(0x1002C1L, s.AdressesInconnues);
    }

    [Fact]
    public void Au_couteau_jusqu_a_la_section_4_la_course_finit_au_temps()
    {
        var s = Nouvelle();
        Partir(s);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);

        Image(s, S(5000, "PROGRESSION_STAGE", "0x10040D", 0x03));
        Image(s, S(12000, "PROGRESSION_STAGE", "0x10040D", 0x04));

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Theory]
    [InlineData("AMMO_LOSE", "0x100590", 0x01)]
    [InlineData("BOMB_FIRED", "0x10EAF5", 0x09)]
    [InlineData("LOSE_LIFE", "0x1002BB", 0x01)]
    [InlineData("MOUNT_STATE", "0x1002C1", 0x04)]
    public void Un_tir_une_grenade_une_mort_ou_un_vehicule_font_echouer(string action, string adresse, int valeur)
    {
        var s = Nouvelle();
        Partir(s);

        Image(s, S(3000, action, adresse, valeur));

        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
    }

    [Fact]
    public void Descendre_d_un_Metal_Slug_ne_relance_pas_une_tentative_en_cours_de_route()
    {
        var s = Nouvelle();
        Partir(s);
        Image(s, S(3000, "MOUNT_STATE", "0x1002C1", 0x04));
        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);

        Image(s, S(3600, "MOUNT_STATE", "0x1002C1", 0x01));
        Image(s, S(9000, "PROGRESSION_STAGE", "0x10040D", 0x04));

        Assert.NotEqual(SessionSpeedrun.Running, s.Etat);
        Assert.NotEqual(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Le_couteau_et_les_baisses_du_compteur_de_tirs_ne_comptent_pas()
    {
        var s = Nouvelle();
        Partir(s);
        // Le compteur revient a 0 a la mort ou au changement de section : le .MEM ne signale que les hausses de 1 a 4.
        Image(s, S(4000, "AMMO_STATE", "0x10EAF5", 0x0A));
        Image(s, S(12000, "PROGRESSION_STAGE", "0x10040D", 0x04));

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }
}
