using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le credit fait foi (charte de la partie certifiee, 2026-09-30) : un credit consomme en pleine
/// partie, score garde, est un continue ; les vies ne coupent plus rien.
/// </summary>
public class ContinuesParCreditsTests
{
    private static List<(long, long)> Lectures(params (long frame, long total)[] points) => points.ToList();

    [Fact]
    public void Double_Dragon_une_vie_bonus_ne_coupe_plus_rien()
    {
        // Speedbull, 2026-09-29 : un seul credit (1 -> 0 au depart), 41 520 au premier credit.
        var credits = new List<EvenementDeCredit> { new(1, 10), new(0, 100) };
        var lectures = Lectures((50, 0), (500, 29960), (900, 30000), (1400, 41520));
        var bilan = ContinuesParCredits.Calculer(credits, lectures, []);
        Assert.Empty(bilan.Coupes);
        Assert.False(bilan.PlusieursJoueurs);
    }

    [Fact]
    public void Un_credit_consomme_en_partie_score_garde_est_un_continue()
    {
        // Deux credits remis avant de commencer : autorise. Le second part en pleine partie.
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 1100) };
        var lectures = Lectures((50, 0), (1000, 29960), (1200, 30500), (1600, 41520));
        var bilan = ContinuesParCredits.Calculer(credits, lectures, []);
        Assert.Equal(new long[] { 1100 }, bilan.Coupes);
    }

    [Fact]
    public void Une_nouvelle_partie_remet_le_score_a_zero_et_ne_coupe_pas()
    {
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 2000) };
        var lectures = Lectures((50, 0), (1000, 29960), (2010, 0), (2500, 5000));
        Assert.Empty(ContinuesParCredits.Calculer(credits, lectures, []).Coupes);
    }

    [Fact]
    public void L_arrivee_d_un_joueur_2_sort_la_partie_du_classement_solo()
    {
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 1550) };
        var lectures = Lectures((50, 0), (1000, 12000), (1800, 15000));
        var departs = new List<DepartDeJoueur> { new(1, 90), new(2, 1500) };
        var bilan = ContinuesParCredits.Calculer(credits, lectures, departs);
        Assert.Empty(bilan.Coupes);
        Assert.True(bilan.PlusieursJoueurs);
    }

    [Fact]
    public void Une_piece_remise_en_partie_puis_consommee_est_un_continue()
    {
        var credits = new List<EvenementDeCredit> { new(1, 10), new(0, 100), new(1, 800), new(0, 1100) };
        var lectures = Lectures((50, 0), (1000, 20000), (1300, 26000));
        Assert.Equal(new long[] { 1100 }, ContinuesParCredits.Calculer(credits, lectures, []).Coupes);
    }

    [Fact]
    public void Un_credit_consomme_a_score_nul_ne_coupe_rien()
    {
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 300) };
        var lectures = Lectures((50, 0), (600, 1000));
        Assert.Empty(ContinuesParCredits.Calculer(credits, lectures, []).Coupes);
    }

    [Fact]
    public void Sans_ligne_de_credits_rien_ne_coupe()
    {
        var lectures = Lectures((50, 0), (1000, 29960), (1400, 41520));
        Assert.Empty(ContinuesParCredits.Calculer([], lectures, []).Coupes);
    }
}
