using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// UNE NOUVELLE PARTIE LANCEE APRES UN GAME OVER, sans revenir au menu, est une partie 1CC DISTINCTE (regle du user,
/// rappelee le 24/11 « depuis toujours »). Un credit consomme PENDANT l'ecran de continue reste un continue.
/// Cas d'Ash (Batsugun Special, API 1.9.213, replay rp_06H0XYD5Y90GQM2M3Q44AWVVE4) : game over, nom saisi, demo, puis
/// START et credit : « fin du 1CC solo : 7541 lectures ecartees », la 2e partie (1 389 550) ne comptait nulle part.
/// </summary>
public class NouvellePartieApresGameOverTests
{
    // Les images de la session d'Ash (le film vaut environ -240 images) : credit du depart 279, GAME_OVER 19 671 (au debut
    // de l'ecran « CONTINUE ? 9 »), demo 24 610, piece puis START et credit consomme a 25 156.
    private static readonly List<EvenementDeCredit> CreditsDAsh = [new(1, 250), new(0, 279), new(1, 25_100), new(0, 25_156)];
    private static readonly List<DepartDeJoueur> DepartsDAsh = [new(1, 279), new(1, 25_150)];

    [Fact]
    public void Le_credit_d_Ash_apres_le_game_over_et_la_demo_ouvre_une_nouvelle_partie()
    {
        var fins = new List<FinDePartieVue> { new(19_671, false), new(24_610, true) };
        var bilan = ContinuesParCredits.Calculer(CreditsDAsh, DepartsDAsh, false, fins);

        Assert.Equal(new long[] { 25_156 }, bilan.NouvellesParties);
        Assert.Empty(bilan.Coupes);
        Assert.Empty(bilan.FinsDuSolo);   // la partie 1 n'est plus coupee : son passeport part en entier

        var parties = bilan.Parties;
        Assert.Equal(2, parties.Count);
        Assert.True(parties[0].Contient(19_558));      // son pic (665 540)
        Assert.False(parties[0].Contient(25_156));
        Assert.True(parties[1].Contient(25_156));      // la lecture a la frame du credit est celle de la neuve
        Assert.True(parties[1].Contient(1_000_000));   // jusqu'a 1 389 550
    }

    [Fact]
    public void Le_game_over_seul_suffit_une_fois_l_ecran_de_continue_ecoule()
    {
        // Sans demo vue (le .MEM de Batsugun n'en declare aucune) : 5 485 images (plus de 90 s) apres le GAME_OVER.
        var bilan = ContinuesParCredits.Calculer(CreditsDAsh, DepartsDAsh, false, [new(19_671, false)]);
        Assert.Equal(new long[] { 25_156 }, bilan.NouvellesParties);
    }

    [Fact]
    public void Un_credit_pendant_l_ecran_de_continue_reste_un_continue()
    {
        // GAME_OVER au debut du decompte (Batsugun : « CONTINUE ? 9 »), piece et START 6 s plus tard : un continue.
        var credits = new List<EvenementDeCredit> { new(1, 250), new(0, 279), new(1, 19_900), new(0, 20_031) };
        var bilan = ContinuesParCredits.Calculer(credits, [new(1, 279), new(1, 20_025)], false, [new(19_671, false)]);

        Assert.Equal(new long[] { 20_031 }, bilan.Coupes);
        Assert.Empty(bilan.NouvellesParties);
        Assert.Single(bilan.Parties);
        Assert.True(bilan.Parties[0].EstLaSeule);
    }

    [Fact]
    public void Le_continue_juste_avant_la_fin_du_delai_est_un_continue_et_juste_apres_une_nouvelle_partie()
    {
        long go = 10_000;
        Assert.False(ContinuesParCredits.EstNouvellePartie(go + ContinuesParCredits.DelaiEcranDeContinue - 1, 279, [new(go, false)]));
        Assert.True(ContinuesParCredits.EstNouvellePartie(go + ContinuesParCredits.DelaiEcranDeContinue, 279, [new(go, false)]));
    }

    [Fact]
    public void La_demo_ou_le_titre_vus_suffisent_sans_attendre()
    {
        // Une fin sure (demo, score remis a zero) : le credit qui suit est une nouvelle partie, meme 2 s apres.
        Assert.True(ContinuesParCredits.EstNouvellePartie(10_120, 279, [new(10_000, true)]));
        // Mais jamais une fin vue APRES le credit (Double Dragon remet le score a zero au continue meme).
        Assert.False(ContinuesParCredits.EstNouvellePartie(10_000, 279, [new(10_000, true)]));
        Assert.False(ContinuesParCredits.EstNouvellePartie(10_000, 279, [new(10_005, true)]));
    }

    [Fact]
    public void Sans_aucune_fin_vue_tout_credit_apres_le_depart_reste_un_continue_comme_avant()
    {
        // Metal Slug 3, SMB, DaiOuJou, TGM2 sans signal de fin : rien ne change.
        var credits = new List<EvenementDeCredit> { new(1, 100), new(0, 130), new(1, 9_000), new(0, 9_040), new(1, 90_000), new(0, 90_030) };
        var departs = new List<DepartDeJoueur> { new(1, 130), new(1, 9_035), new(1, 90_025) };
        var avant = ContinuesParCredits.Calculer(credits, departs);
        var apres = ContinuesParCredits.Calculer(credits, departs, false, []);

        Assert.Equal(new long[] { 9_040, 90_030 }, avant.Coupes);
        Assert.Equal(avant.Coupes, apres.Coupes);
        Assert.Empty(apres.NouvellesParties);
    }

    [Fact]
    public void Une_fin_d_avant_le_dernier_credit_ne_compte_plus()
    {
        // GAME_OVER a 5 000, continue a 5 300 (pendant l'ecran), la partie reprend ; un 2e continue a 9 000 ne doit pas
        // se prendre pour une nouvelle partie a cause du GAME_OVER d'avant le premier.
        var credits = new List<EvenementDeCredit> { new(1, 100), new(0, 130), new(1, 5_250), new(0, 5_300), new(1, 8_900), new(0, 9_000) };
        var departs = new List<DepartDeJoueur> { new(1, 130), new(1, 5_295), new(1, 8_995) };
        var bilan = ContinuesParCredits.Calculer(credits, departs, false, [new(5_000, false)]);

        Assert.Equal(new long[] { 5_300, 9_000 }, bilan.Coupes);
        Assert.Empty(bilan.NouvellesParties);
    }

    [Fact]
    public void Un_continue_puis_un_game_over_puis_une_nouvelle_partie_forment_deux_parties()
    {
        var credits = new List<EvenementDeCredit> { new(1, 100), new(0, 130), new(1, 5_250), new(0, 5_300), new(1, 14_000), new(0, 14_050) };
        var departs = new List<DepartDeJoueur> { new(1, 130), new(1, 5_295), new(1, 14_045) };
        var fins = new List<FinDePartieVue> { new(5_000, false), new(9_000, false) };
        var bilan = ContinuesParCredits.Calculer(credits, departs, false, fins);

        Assert.Equal(new long[] { 5_300 }, bilan.Coupes);          // le continue de la partie 1
        Assert.Equal(new long[] { 14_050 }, bilan.NouvellesParties);
        var parties = bilan.Parties;
        Assert.True(parties[0].Contient(5_300) && !parties[1].Contient(5_300));
        Assert.True(parties[1].Contient(14_050));
    }

    [Fact]
    public void L_arrivee_d_un_joueur_2_apres_la_fin_n_est_pas_une_nouvelle_partie_solo()
    {
        var credits = new List<EvenementDeCredit> { new(1, 100), new(0, 130), new(1, 25_100), new(0, 25_156) };
        var departs = new List<DepartDeJoueur> { new(1, 130), new(2, 25_150) };
        var bilan = ContinuesParCredits.Calculer(credits, departs, false, [new(19_671, false)]);

        Assert.Empty(bilan.NouvellesParties);
        Assert.Equal(new long[] { 25_156 }, bilan.Arrivees);
    }

    [Fact]
    public void Le_bandeau_dit_nouvelle_partie_et_le_pic_de_la_partie_d_avant()
    {
        Assert.Equal(NelfePlayScoringReporter.BandeauDeCredit.NouvellePartie,
            NelfePlayScoringReporter.QuelBandeau(depart: false, arrivee: false, premiereFin: false, dejaADeux: false, scoreSolo: true, nouvellePartie: true));
        // Un continue garde son bandeau.
        Assert.Equal(NelfePlayScoringReporter.BandeauDeCredit.ScoreCertifie,
            NelfePlayScoringReporter.QuelBandeau(depart: false, arrivee: false, premiereFin: true, dejaADeux: false, scoreSolo: true));

        // Le pic : la trajectoire d'Ash (665 540 puis la demo, puis 3 040) jusqu'au credit ; la derniere lecture valait 3 040.
        var enJeu = new List<(long frame, long total)> { (300, 0), (5_000, 120_000), (19_558, 665_540) };
        var meilleur = NelfePlayScoringReporter.SelectBestRun(enJeu);
        Assert.Equal(665_540, meilleur[^1].total);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("zh")]
    public void Le_texte_de_la_nouvelle_partie_existe_dans_chaque_langue(string langue)
    {
        var titre = CabinetAnnounceText.Get("scoring_newgame_title", langue);
        Assert.Contains("{0}", titre);
        Assert.NotEqual("scoring_newgame_title", titre);
        Assert.NotEqual("scoring_newgame_sub", CabinetAnnounceText.Get("scoring_newgame_sub", langue));
    }
}
