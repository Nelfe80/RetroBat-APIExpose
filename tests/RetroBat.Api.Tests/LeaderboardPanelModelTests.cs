using RetroBat.Api.Leaderboard;
using Xunit;
using static RetroBat.Api.Leaderboard.LeaderboardPanelModel;

namespace RetroBat.Api.Tests;

/// <summary>
/// La navigation du panneau de classement. Ce qui est en jeu n'est pas du confort : le menu
/// d'EmulationStation reste ouvert a cote, un seul des deux volets recoit la manette, et c'est
/// la POSITION dans les onglets qui dit ou l'on sort. Une erreur ici et le joueur se retrouve
/// enferme dans un panneau, sur une borne sans clavier.
/// </summary>
public class LeaderboardPanelModelTests
{
    private static LeaderboardPanelModel Ouvert(bool salle = true, bool ville = true, bool pays = true, bool records = true, int lignes = 10)
    {
        var m = new LeaderboardPanelModel();
        m.Ouvrir(salle, ville, pays, records);
        m.PoserLesLignes(lignes);
        return m;
    }

    [Fact]
    public void Ferme_par_defaut_et_sourd_a_tout()
    {
        var m = new LeaderboardPanelModel();
        Assert.Equal(Foyer.Ferme, m.Etat);
        foreach (var e in Enum.GetValues<EntreePanneau>())
        {
            Assert.Equal(Effet.Rien, m.Entree(e));
        }
    }

    [Fact]
    public void On_entre_sur_l_onglet_contre_la_porte()
    {
        var m = Ouvert();
        Assert.Equal(Foyer.MenuEs, m.Etat);
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.True(m.SurLaPorte);
    }

    [Fact]
    public void Les_onglets_vont_du_plus_proche_au_plus_lointain()
    {
        var m = Ouvert();
        Assert.Equal(
            new[] { Vue.MesRecords, Vue.CetteBorne, Vue.MaSalle, Vue.MaVille, Vue.MonPays, Vue.Monde },
            m.Onglets.ToArray());
    }

    [Fact]
    public void Une_vue_qui_ne_peut_rien_montrer_n_existe_pas()
    {
        // Sans hub, « Ma salle » ne doit pas exister : un onglet vide se traverse a chaque fois.
        var m = Ouvert(salle: false, ville: false, pays: false, records: false);
        Assert.Equal(new[] { Vue.CetteBorne, Vue.Monde }, m.Onglets.ToArray());
        Assert.Equal(Vue.Monde, m.VueCourante);
    }

    [Fact]
    public void Gauche_donne_la_main_au_panneau_puis_navigue()
    {
        var m = Ouvert();
        Assert.Equal(Effet.PrendreLeFocus, m.Entree(EntreePanneau.Gauche));
        Assert.Equal(Foyer.Panneau, m.Etat);
        Assert.Equal(Vue.Monde, m.VueCourante);         // prendre la main ne change pas de vue

        Assert.Equal(Effet.ChargerLaVue, m.Entree(EntreePanneau.Gauche));
        Assert.Equal(Vue.MonPays, m.VueCourante);
        Assert.False(m.SurLaPorte);
    }

    [Fact]
    public void Droite_sur_la_porte_rend_la_main_a_ES()
    {
        var m = Ouvert();
        m.Entree(EntreePanneau.Gauche);                  // on prend la main, sur « Monde »
        Assert.True(m.SurLaPorte);
        Assert.Equal(Effet.RendreLeFocus, m.Entree(EntreePanneau.Droite));
        Assert.Equal(Foyer.MenuEs, m.Etat);
        // Le panneau reste AFFICHE : c'est le menu d'ES qui reprend la navigation, pas nous qui
        // disparaissons.
        Assert.Equal(Vue.Monde, m.VueCourante);
    }

    [Fact]
    public void Gauche_a_l_extremite_est_un_mur()
    {
        var m = Ouvert();
        m.Entree(EntreePanneau.Gauche);                  // prise de main
        for (var i = 0; i < 5; i++) m.Entree(EntreePanneau.Gauche);
        Assert.Equal(Vue.MesRecords, m.VueCourante);
        // Encore a gauche : rien. On ne reboucle pas sur « Monde », ES ne reboucle pas non plus.
        Assert.Equal(Effet.Rien, m.Entree(EntreePanneau.Gauche));
        Assert.Equal(Vue.MesRecords, m.VueCourante);
        Assert.Equal(Foyer.Panneau, m.Etat);
    }

    [Fact]
    public void Revenir_a_droite_traverse_les_onglets_avant_de_sortir()
    {
        var m = Ouvert();
        m.Entree(EntreePanneau.Gauche);
        m.Entree(EntreePanneau.Gauche);                  // MonPays
        m.Entree(EntreePanneau.Gauche);                  // MaVille
        Assert.Equal(Vue.MaVille, m.VueCourante);
        Assert.Equal(Effet.ChargerLaVue, m.Entree(EntreePanneau.Droite));
        Assert.Equal(Vue.MonPays, m.VueCourante);
        Assert.Equal(Effet.ChargerLaVue, m.Entree(EntreePanneau.Droite));
        Assert.Equal(Vue.Monde, m.VueCourante);
        Assert.Equal(Effet.RendreLeFocus, m.Entree(EntreePanneau.Droite));
    }

    [Fact]
    public void Haut_et_bas_parcourent_les_scores_sans_deborder()
    {
        var m = Ouvert(lignes: 3);
        m.Entree(EntreePanneau.Gauche);
        Assert.Equal(0, m.Ligne);
        Assert.Equal(Effet.Rien, m.Entree(EntreePanneau.Haut));    // deja en haut
        Assert.Equal(0, m.Ligne);
        m.Entree(EntreePanneau.Bas);
        m.Entree(EntreePanneau.Bas);
        Assert.Equal(2, m.Ligne);
        m.Entree(EntreePanneau.Bas);                                // deja en bas
        Assert.Equal(2, m.Ligne);
    }

    [Fact]
    public void Changer_de_vue_repart_du_haut()
    {
        var m = Ouvert(lignes: 10);
        m.Entree(EntreePanneau.Gauche);
        m.Entree(EntreePanneau.Bas);
        m.Entree(EntreePanneau.Bas);
        Assert.Equal(2, m.Ligne);
        m.Entree(EntreePanneau.Gauche);
        Assert.Equal(0, m.Ligne);
    }

    [Fact]
    public void Les_gachettes_sautent_une_page_et_s_arretent_aux_bords()
    {
        var m = Ouvert(lignes: 12);
        m.Entree(EntreePanneau.Gauche);
        m.Entree(EntreePanneau.PageBas);
        Assert.Equal(PageDeLignes, m.Ligne);
        m.Entree(EntreePanneau.PageBas);
        m.Entree(EntreePanneau.PageBas);
        Assert.Equal(11, m.Ligne);                                  // borne a la derniere ligne
        m.Entree(EntreePanneau.PageHaut);
        m.Entree(EntreePanneau.PageHaut);
        m.Entree(EntreePanneau.PageHaut);
        Assert.Equal(0, m.Ligne);
    }

    [Fact]
    public void Une_vue_vide_n_agit_pas_et_ne_bouge_pas()
    {
        var m = Ouvert(lignes: 0);
        m.Entree(EntreePanneau.Gauche);
        Assert.Equal(Effet.Rien, m.Entree(EntreePanneau.Agir));
        Assert.Equal(Effet.Rien, m.Entree(EntreePanneau.Bas));
        Assert.Equal(0, m.Ligne);
    }

    [Fact]
    public void Agir_ne_vaut_que_quand_nous_avons_la_main()
    {
        var m = Ouvert();
        Assert.Equal(Effet.Rien, m.Entree(EntreePanneau.Agir));     // ES navigue : ce bouton est a lui
        m.Entree(EntreePanneau.Gauche);
        Assert.Equal(Effet.AgirSurLaLigne, m.Entree(EntreePanneau.Agir));
    }

    [Fact]
    public void Annuler_ferme_tout_depuis_le_panneau()
    {
        var m = Ouvert();
        m.Entree(EntreePanneau.Gauche);
        Assert.Equal(Effet.Fermer, m.Entree(EntreePanneau.Annuler));
        Assert.Equal(Foyer.Ferme, m.Etat);
    }

    [Fact]
    public void Annuler_pendant_qu_ES_navigue_ferme_aussi_le_panneau()
    {
        // C'est SON menu qui se referme sous ce bouton : rester affiche a cote de rien n'aurait
        // pas de sens.
        var m = Ouvert();
        Assert.Equal(Effet.Fermer, m.Entree(EntreePanneau.Annuler));
    }

    [Fact]
    public void Moins_de_lignes_qu_avant_ne_laisse_pas_la_selection_dans_le_vide()
    {
        var m = Ouvert(lignes: 10);
        m.Entree(EntreePanneau.Gauche);
        m.Entree(EntreePanneau.PageBas);
        Assert.Equal(5, m.Ligne);
        m.PoserLesLignes(2);                                        // la vue suivante est plus courte
        Assert.Equal(1, m.Ligne);
    }
}

/// <summary>
/// L'onglet LIVE & CONTEST arrive APRES l'ouverture (les evenements se chargent en fond) : il ne
/// doit jamais deplacer le curseur du joueur, et sa disparition ne doit pas le laisser sur un
/// onglet qui n'existe plus.
/// </summary>
public class LeaderboardLiveTabTests
{
    [Fact]
    public void L_onglet_live_apparait_sans_deplacer_le_curseur()
    {
        var m = new RetroBat.Api.Leaderboard.LeaderboardPanelModel();
        m.Ouvrir(salleConnue: false, villeConnue: false, paysConnu: false, aDesRecords: true);
        Assert.Equal(RetroBat.Api.Leaderboard.LeaderboardPanelModel.Vue.Monde, m.VueCourante);

        Assert.True(m.PoserLesEvenements(true));
        Assert.Equal(RetroBat.Api.Leaderboard.LeaderboardPanelModel.Vue.Monde, m.VueCourante);   // le curseur reste
        Assert.Equal(RetroBat.Api.Leaderboard.LeaderboardPanelModel.Vue.LiveEtContest, m.Onglets[^1]);
        Assert.False(m.SurLaPorte);                                                              // la porte a recule d'un cran
        Assert.False(m.PoserLesEvenements(true));                                                // idempotent
    }

    [Fact]
    public void Si_l_onglet_live_disparait_sous_le_curseur_on_revient_sur_monde()
    {
        var m = new RetroBat.Api.Leaderboard.LeaderboardPanelModel();
        m.Ouvrir(false, false, false, true);
        m.PoserLesEvenements(true);
        m.Entree(RetroBat.Api.Leaderboard.EntreePanneau.Gauche);                                 // on entre (prendre le focus)
        m.Entree(RetroBat.Api.Leaderboard.EntreePanneau.Droite);                                 // vers LIVE
        Assert.Equal(RetroBat.Api.Leaderboard.LeaderboardPanelModel.Vue.LiveEtContest, m.VueCourante);

        Assert.True(m.PoserLesEvenements(false));
        Assert.Equal(RetroBat.Api.Leaderboard.LeaderboardPanelModel.Vue.Monde, m.VueCourante);
        Assert.DoesNotContain(RetroBat.Api.Leaderboard.LeaderboardPanelModel.Vue.LiveEtContest, m.Onglets);
    }
}

/// <summary>
/// Les cibles du cartouche de defi. Logique pure, donc verifiee ici plutot qu'a l'ecran : la
/// premiere cible est le joueur juste au-dessus de notre meilleur score (sinon la derniere place
/// du top), nos propres lignes ne sont jamais une cible, et plusieurs joueurs peuvent tomber
/// d'un seul coup de score.
/// </summary>
public class ChallengeTargetTests
{
    private static RetroBat.Api.Leaderboard.LeaderboardClient.Ligne L(int rang, string joueur, long valeur, bool moi = false)
        => new(rang, joueur, valeur, "", "", "", true, null, "", moi);

    [Fact]
    public void La_cible_est_le_joueur_juste_au_dessus_de_mon_meilleur_score()
    {
        var classement = new[] { L(1, "ACE", 90000), L(2, "BOB", 50000), L(3, "MOI", 30000, true), L(4, "ZED", 10000) };
        var cible = RetroBat.Api.Leaderboard.ChallengeHudService.CibleInitiale(classement);
        Assert.Equal("BOB", cible!.Joueur);
    }

    [Fact]
    public void Sans_score_a_moi_la_cible_est_la_derniere_place_du_top()
    {
        var classement = Enumerable.Range(1, 12).Select(r => L(r, "J" + r, 100000 - r * 1000)).ToArray();
        var cible = RetroBat.Api.Leaderboard.ChallengeHudService.CibleInitiale(classement);
        Assert.Equal(10, cible!.Rang);   // la 10e place : celle qu'il faut prendre pour entrer
    }

    [Fact]
    public void En_tete_il_n_y_a_plus_de_cible()
    {
        var classement = new[] { L(1, "MOI", 90000, true), L(2, "BOB", 50000) };
        Assert.Null(RetroBat.Api.Leaderboard.ChallengeHudService.CibleInitiale(classement));
    }

    [Fact]
    public void Un_score_qui_depasse_deux_joueurs_avance_de_deux_rangs_en_sautant_mes_lignes()
    {
        var classement = new[] { L(1, "ACE", 90000), L(2, "MOI", 60000, true), L(3, "BOB", 50000), L(4, "CAT", 40000), L(5, "MOI", 30000, true) };
        var cible = (RetroBat.Api.Leaderboard.LeaderboardClient.Ligne?) classement[3];   // CAT
        var gagne = RetroBat.Api.Leaderboard.ChallengeHudService.Avancer(classement, 55000, ref cible);
        Assert.True(gagne);
        Assert.Equal("ACE", cible!.Joueur);   // CAT puis BOB depasses ; MOI (#2) n'est pas une cible
    }

    [Fact]
    public void Un_score_insuffisant_ne_change_rien()
    {
        var classement = new[] { L(1, "ACE", 90000), L(2, "BOB", 50000) };
        var cible = (RetroBat.Api.Leaderboard.LeaderboardClient.Ligne?) classement[1];
        Assert.False(RetroBat.Api.Leaderboard.ChallengeHudService.Avancer(classement, 50000, ref cible));   // egaler ne suffit pas
        Assert.Equal("BOB", cible!.Joueur);
    }
}
