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
    public void Une_machine_saturee_ne_filme_pas_cette_partie_et_filme_la_suivante()
    {
        Assert.StartsWith("processeur sature", ControleAvantFilm.RaisonMachine(99.2, 900, 60, 60));
        Assert.StartsWith("memoire presque pleine", ControleAvantFilm.RaisonMachine(50, 120, 60, 60));
        Assert.StartsWith("jeu deja ralenti", ControleAvantFilm.RaisonMachine(50, 900, 48, 59.63));
        // La partie suivante, la machine revenue : le film repart (aucune trace de la precedente).
        Assert.Null(ControleAvantFilm.RaisonMachine(70, 900, 59.6, 59.63));
    }

    [Theory]
    [InlineData(10)]   // pause ou menu
    [InlineData(120)]  // avance rapide
    public void Une_cadence_hors_jeu_ne_juge_rien(double cadence)
        => Assert.Null(ControleAvantFilm.RaisonMachine(50, 900, cadence, 60));

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
