using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le controle d'avant le film (14/11) : la machine et la connexion sur l'instant, rien de durable.</summary>
public sealed class ControleAvantFilmTests
{
    [Fact]
    public void Une_machine_chargee_mais_jouable_filme()
    {
        // La borne du user pendant une partie : processeur a 80-100 %, 450 Mo libres, jeu a sa cadence.
        Assert.Null(ControleAvantFilm.RaisonMachine(92, 450, 59.5, 59.63));
        // Rien de mesure : rien ne retient le film.
        Assert.Null(ControleAvantFilm.RaisonMachine(null, null, null, null));
    }

    [Fact]
    public void Le_processeur_au_plus_haut_au_lancement_ne_retient_jamais_le_film()
    {
        // La borne du user le 14/11 a 09:54 : 99,8 % pendant le lancement de RetroArch et du Deck, jeu a sa cadence.
        Assert.Null(ControleAvantFilm.RaisonMachine(99.8, 900, 59.6, 59.63));
        Assert.Null(ControleAvantFilm.RaisonMachine(100, 900, 60, 60));
        // Cadence pas encore mesuree : le film part (le garde-fou du film jugera la cadence une fois la partie stabilisee).
        Assert.Null(ControleAvantFilm.RaisonMachine(100, 900, null, 59.63));
        Assert.Null(ControleAvantFilm.RaisonMachine(100, 900, 42, null));
    }

    [Fact]
    public void Un_jeu_a_70_pour_cent_de_sa_cadence_ne_filme_pas()
    {
        Assert.StartsWith("jeu ralenti", ControleAvantFilm.RaisonMachine(40, 900, 42, 60));
        Assert.StartsWith("jeu ralenti", ControleAvantFilm.RaisonMachine(null, null, 0.7 * 59.63, 59.63));
        // 92 % de la cadence annoncee : la machine suit, le film part.
        Assert.Null(ControleAvantFilm.RaisonMachine(null, null, 0.92 * 59.63, 59.63));
    }

    [Fact]
    public void Une_machine_saturee_ne_filme_pas_cette_partie_et_filme_la_suivante()
    {
        Assert.StartsWith("memoire presque pleine", ControleAvantFilm.RaisonMachine(50, 120, 60, 60));
        Assert.StartsWith("jeu ralenti", ControleAvantFilm.RaisonMachine(50, 900, 48, 59.63));
        // La partie suivante, la machine revenue : le film repart (aucune trace de la precedente).
        Assert.Null(ControleAvantFilm.RaisonMachine(70, 900, 59.6, 59.63));
    }

    [Theory]
    [InlineData(10)]   // pause ou menu
    [InlineData(120)]  // avance rapide
    public void Une_cadence_hors_jeu_ne_juge_rien(double cadence)
        => Assert.Null(ControleAvantFilm.RaisonMachine(50, 900, cadence, 60));

    [Theory]
    [InlineData(59.63, 59.63)]
    [InlineData(50.0, 50.0)]
    [InlineData(null, null)]
    [InlineData(0.0, null)]     // pas d'av_info
    [InlineData(120.0, null)]   // cadence hors des machines suivies
    public void La_cadence_annoncee_ne_sert_de_reference_qu_entre_40_et_75(double? annoncee, double? attendue)
        => Assert.Equal(attendue, ControleAvantFilm.CadenceDeReference(annoncee));

    [Fact]
    public void Le_garde_fou_laisse_filmer_une_borne_a_100_pour_cent_qui_tient_sa_cadence()
    {
        // Chargement lent (premieres secondes a 30 images/s), puis le jeu a 59,6 pour 59,63 annoncees : film garde.
        var garde = new GardeDeCapture(59.63);
        long frame = 0;
        var t = 0.0;
        Assert.False(garde.Relever(t, frame));
        for (var i = 0; i < 2; i++) { t += 2; frame += 60; Assert.False(garde.Relever(t, frame)); }
        for (var i = 0; i < 30; i++) { t += 2; frame += 119; Assert.False(garde.Relever(t, frame)); }
    }

    [Fact]
    public void Le_garde_fou_arrete_le_film_d_un_jeu_stabilise_sous_90_pour_cent_de_sa_cadence_annoncee()
    {
        // 52 images/s des le depart pour 59,63 annoncees (87 %) : la meilleure cadence vue (52) ne le trahit pas, la
        // cadence annoncee si, une fois la partie stabilisee.
        var sansAnnonce = new GardeDeCapture();
        var garde = new GardeDeCapture(59.63);
        long frame = 0;
        var t = 0.0;
        sansAnnonce.Relever(t, frame);
        garde.Relever(t, frame);
        var arretSansAnnonce = false;
        double? arret = null;
        for (var i = 0; i < 20 && arret is null; i++)
        {
            t += 2; frame += 104;
            arretSansAnnonce |= sansAnnonce.Relever(t, frame);
            if (garde.Relever(t, frame)) arret = t;
        }
        Assert.False(arretSansAnnonce);
        Assert.NotNull(arret);
        // Rien n'est juge avant la stabilisation : trois releves lents apres elle.
        Assert.True(arret >= ControleAvantFilm.StabilisationS + 2 * 2);
        Assert.Equal(52, garde.DerniereCadence!.Value, 1);
    }

    [Fact]
    public void La_cadence_se_lit_entre_deux_releves()
    {
        Assert.Equal(60, ControleAvantFilm.Cadence(1000, 1060, 1.0));
        Assert.Null(ControleAvantFilm.Cadence(null, 1060, 1.0));
        Assert.Null(ControleAvantFilm.Cadence(1060, 1060, 1.0));
        Assert.Null(ControleAvantFilm.Cadence(1000, 1060, 0.1));
    }

    [Fact]
    public void La_charge_du_processeur_se_lit_aux_temps_du_systeme()
    {
        Assert.Equal(75, ControleAvantFilm.Charge(250, 1000));
        Assert.Null(ControleAvantFilm.Charge(10, 0));
        Assert.Null(ControleAvantFilm.Charge(2000, 1000));
    }

    [Fact]
    public void La_connexion_retient_le_direct_sans_reseau_ou_trop_lente()
    {
        Assert.Equal("pas de reseau", ControleAvantFilm.RaisonConnexion(false, null, false));
        Assert.Equal("site injoignable", ControleAvantFilm.RaisonConnexion(true, null, false));
        Assert.StartsWith("site trop lent", ControleAvantFilm.RaisonConnexion(true, 2400, true));
        Assert.Null(ControleAvantFilm.RaisonConnexion(true, 80, true));
        Assert.Null(ControleAvantFilm.RaisonConnexion(true, null, true));
    }
}
