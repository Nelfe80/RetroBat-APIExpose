using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le credit fait foi (charte de la partie certifiee, 2026-09-30) : le premier credit consomme de
/// la session est le depart, tout credit consomme ensuite est un continue. La session fait foi :
/// quitter le jeu termine la partie. Les vies ne coupent plus rien.
/// </summary>
public class ContinuesParCreditsTests
{
    private static List<(long, long)> Lectures(params (long frame, long total)[] points) => points.ToList();

    [Fact]
    public void Double_Dragon_une_vie_bonus_ne_coupe_plus_rien()
    {
        // Speedbull, 2026-09-29 : un seul credit (1 -> 0 au depart), 41 520 au premier credit.
        var credits = new List<EvenementDeCredit> { new(1, 10), new(0, 100) };
        var bilan = ContinuesParCredits.Calculer(credits, []);
        Assert.Empty(bilan.Coupes);
        Assert.False(bilan.PlusieursJoueurs);
    }

    [Fact]
    public void Un_credit_consomme_apres_le_depart_est_un_continue()
    {
        // Deux credits remis avant de commencer : autorise. Le second part en pleine partie.
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 1100) };
        Assert.Equal(new long[] { 1100 }, ContinuesParCredits.Calculer(credits, []).Coupes);
    }

    [Fact]
    public void Double_Dragon_remet_le_score_a_zero_au_continue_et_ne_concourt_plus()
    {
        // Labo du 2026-09-30 : 1 550, continue (le score repart de 0), 1 500, continue.
        var credits = new List<EvenementDeCredit> { new(3, 10), new(2, 100), new(1, 2000), new(0, 4000) };
        var lectures = Lectures((50, 0), (900, 1550), (2010, 0), (3000, 1500), (4010, 0), (4500, 2600));
        var coupes = ContinuesParCredits.Calculer(credits, []).Coupes;
        Assert.Equal(new long[] { 2000, 4000 }, coupes);

        var gardees = ContinuesParCredits.AvantLePremierContinue(lectures, coupes);
        Assert.Equal(1550, gardees[^1].Item2);
        Assert.Equal(2, gardees.Count);
        Assert.Equal(1550, NelfePlayScoringReporter.SelectBestRun(gardees)[^1].total);
    }

    [Fact]
    public void Une_partie_relancee_sans_quitter_le_jeu_ne_concourt_pas()
    {
        // Decision user du 2026-09-30 : quitter le jeu termine la partie. Game over a 4 000, piece,
        // START, 9 000 : le credit consomme ferme la partie certifiee, on garde 4 000.
        var credits = new List<EvenementDeCredit> { new(1, 10), new(0, 100), new(1, 3000), new(0, 3100) };
        var lectures = Lectures((50, 0), (2000, 4000), (3150, 0), (5000, 9000));
        var coupes = ContinuesParCredits.Calculer(credits, []).Coupes;
        Assert.Equal(new long[] { 3100 }, coupes);
        Assert.Equal(4000, ContinuesParCredits.AvantLePremierContinue(lectures, coupes)[^1].Item2);
    }

    [Fact]
    public void Le_premier_credit_consomme_est_le_depart_meme_apres_une_demo_mal_reconnue()
    {
        var credits = new List<EvenementDeCredit> { new(0, 5), new(1, 500), new(0, 600) };
        Assert.Empty(ContinuesParCredits.Calculer(credits, []).Coupes);
    }

    [Fact]
    public void L_arrivee_d_un_joueur_2_sort_la_partie_du_classement_solo()
    {
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 1550) };
        var departs = new List<DepartDeJoueur> { new(1, 90), new(2, 1500) };
        var bilan = ContinuesParCredits.Calculer(credits, departs);
        Assert.Empty(bilan.Coupes);
        Assert.True(bilan.PlusieursJoueurs);
    }

    [Fact]
    public void Un_depart_a_deux_n_est_pas_un_continue_mais_le_credit_suivant_l_est()
    {
        var credits = new List<EvenementDeCredit> { new(3, 10), new(1, 100), new(0, 2000) };
        var departs = new List<DepartDeJoueur> { new(1, 95), new(2, 98) };
        var bilan = ContinuesParCredits.Calculer(credits, departs);
        Assert.True(bilan.PlusieursJoueurs);
        Assert.Equal(new long[] { 2000 }, bilan.Coupes);
    }

    [Fact]
    public void Une_piece_remise_en_partie_puis_consommee_est_un_continue()
    {
        var credits = new List<EvenementDeCredit> { new(1, 10), new(0, 100), new(1, 800), new(0, 1100) };
        Assert.Equal(new long[] { 1100 }, ContinuesParCredits.Calculer(credits, []).Coupes);
    }

    [Fact]
    public void Sans_ligne_de_credits_rien_ne_coupe()
    {
        Assert.Empty(ContinuesParCredits.Calculer([], []).Coupes);
    }

    [Fact]
    public void Une_lecture_pile_sur_la_frame_du_continue_appartient_au_nouveau_credit()
    {
        // 19xx, 2026-09-25 : 36 500 puis 36 801 a la frame meme du continue.
        var lectures = Lectures((100, 0), (900, 36500), (1000, 36801), (1200, 37000));
        var gardees = ContinuesParCredits.AvantLePremierContinue(lectures, [1000]);
        Assert.Equal(36500, gardees[^1].Item2);
    }

    [Fact]
    public void Des_lectures_sans_frame_ne_coupent_rien()
    {
        // Pont MAME d'avant mame-lua-0.3.2 : tout a la frame 0.
        var lectures = Lectures((0, 0), (0, 1500), (0, 3000));
        Assert.Equal(3, ContinuesParCredits.AvantLePremierContinue(lectures, [0]).Count);
    }

    [Theory]
    [InlineData(true, false, false, false, true, "Aucun")]             // depart solo : la partie compte
    [InlineData(false, false, true, false, true, "ScoreCertifie")]     // premier continue : le score certifie
    [InlineData(false, false, false, false, true, "NonCertifiable")]   // chaque credit suivant, a chaque START
    [InlineData(true, true, true, false, false, "PartieAPlusieurs")]   // depart a deux : rien de fait seul
    [InlineData(false, true, true, false, true, "JoueurRejoint")]      // joueur 2 apres un score seul : il reste un 1CC
    [InlineData(false, true, false, false, true, "PartieAPlusieurs")]  // joueur 2 apres un continue : le solo etait deja clos
    [InlineData(false, true, false, true, true, "Aucun")]              // joueur 3 : le 1CC MULTI le permettra aussi
    [InlineData(false, false, false, true, true, "NonCertifiable")]    // continue d'une partie a plusieurs
    public void Chaque_start_qui_consomme_un_credit_dit_ce_que_vaut_la_partie(
        bool depart, bool arrivee, bool premiereFin, bool dejaADeux, bool scoreSolo, string attendu)
    {
        Assert.Equal(attendu, NelfePlayScoringReporter.QuelBandeau(depart, arrivee, premiereFin, dejaADeux, scoreSolo).ToString());
    }

    [Fact]
    public void C_est_cumulatif_le_score_fait_seul_avant_l_arrivee_reste_un_1CC()
    {
        // Decision user du 2026-09-30 : 30 000 seul, un joueur rejoint, 55 000 a deux.
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 2000) };
        var departs = new List<DepartDeJoueur> { new(1, 95), new(2, 1998) };
        var lectures = Lectures((50, 0), (1500, 30000), (2500, 41000), (4000, 55000));
        var bilan = ContinuesParCredits.Calculer(credits, departs);
        Assert.Empty(bilan.Coupes);
        Assert.Equal(new long[] { 2000 }, bilan.Arrivees);
        Assert.Equal(30000, ContinuesParCredits.AvantLePremierContinue(lectures, bilan.FinsDuSolo)[^1].Item2);
    }

    [Fact]
    public void Le_solo_se_ferme_au_premier_des_deux_continue_ou_arrivee()
    {
        var credits = new List<EvenementDeCredit> { new(3, 10), new(2, 100), new(1, 1500), new(0, 2000) };
        var departs = new List<DepartDeJoueur> { new(1, 95), new(1, 1498), new(2, 1998) };
        var bilan = ContinuesParCredits.Calculer(credits, departs);
        Assert.Equal(new long[] { 1500, 2000 }, bilan.FinsDuSolo);
    }

    [Fact]
    public void Ouverte_aux_joueurs_et_restee_seule_c_est_un_1CC()
    {
        var credits = new List<EvenementDeCredit> { new(1, 10), new(0, 100) };
        var departs = new List<DepartDeJoueur> { new(1, 95) };
        var bilan = ContinuesParCredits.Calculer(credits, departs, ouverteAuxJoueurs: true);
        Assert.Empty(bilan.Coupes);
        Assert.False(bilan.PlusieursJoueurs);
    }

    [Fact]
    public void Un_joueur_distant_qui_rejoint_n_est_pas_un_continue()
    {
        // Netplay : l'hote ne voit que son panel. Le START du joueur 2 est sur l'autre borne.
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 2000) };
        var departs = new List<DepartDeJoueur> { new(1, 95) };
        var bilan = ContinuesParCredits.Calculer(credits, departs, ouverteAuxJoueurs: true);
        Assert.Empty(bilan.Coupes);
        Assert.True(bilan.PlusieursJoueurs);
        Assert.Equal(new long[] { 2000 }, bilan.Arrivees);
    }

    [Fact]
    public void Le_continue_de_l_hote_reste_un_continue_en_partie_ouverte()
    {
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 3000) };
        var departs = new List<DepartDeJoueur> { new(1, 95), new(1, 2998) };
        var bilan = ContinuesParCredits.Calculer(credits, departs, ouverteAuxJoueurs: true);
        Assert.Equal(new long[] { 3000 }, bilan.Coupes);
        Assert.False(bilan.PlusieursJoueurs);
    }

    [Fact]
    public void Le_joueur_distant_parti_avant_l_hote_ne_fait_pas_du_depart_de_l_hote_un_continue()
    {
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 602) };
        var departs = new List<DepartDeJoueur> { new(1, 600) };
        var bilan = ContinuesParCredits.Calculer(credits, departs, ouverteAuxJoueurs: true);
        Assert.Empty(bilan.Coupes);
        Assert.True(bilan.PlusieursJoueurs);
    }

    [Fact]
    public void Hors_netplay_un_credit_sans_start_vu_reste_un_continue()
    {
        var credits = new List<EvenementDeCredit> { new(2, 10), new(1, 100), new(0, 2000) };
        var departs = new List<DepartDeJoueur> { new(1, 95) };
        Assert.Equal(new long[] { 2000 }, ContinuesParCredits.Calculer(credits, departs).Coupes);
    }

    [Theory]
    [InlineData("{\"Score\":1500,\"Player\":1}", 1)]
    [InlineData("{\"Score\":1500,\"Player\":2}", 2)]
    [InlineData("{\"Score\":1500}", 1)]            // lignes sans joueur : le joueur 1
    [InlineData("{\"Score\":1500,\"Player\":9}", 1)]
    public void Le_score_d_un_autre_joueur_se_reconnait(string json, int attendu)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(attendu, NelfePlayScoringReporter.JoueurDuScore(doc.RootElement));
    }
}
