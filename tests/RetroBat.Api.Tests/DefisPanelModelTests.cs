using RetroBat.Api.Speedrun;
using Xunit;
using static RetroBat.Api.Speedrun.DefisPanelModel;

namespace RetroBat.Api.Tests;

/// <summary>La navigation du panneau des defis : la bordure qui est une porte, et ◄ ► qui ne se disputent pas les cartes et les onglets.</summary>
public class DefisPanelModelTests
{
    private static readonly Onglet[] Tous = [Onglet.Records, Onglet.Training, Onglet.Challenges, Onglet.Speedruns];

    private static DefisPanelModel Ouvert(params Onglet[] onglets)
    {
        var m = new DefisPanelModel();
        m.Ouvrir(onglets.Length > 0 ? onglets : Tous);
        m.PoserLesLignes(Onglet.Speedruns, 12);
        m.PoserLesLignes(Onglet.Challenges, 5);
        m.PoserLesLignes(Onglet.Training, 10);
        m.PoserLesLignes(Onglet.Records, 10);
        return m;
    }

    private static DefisPanelModel Dedans(params Onglet[] onglets)
    {
        var m = Ouvert(onglets);
        Assert.Equal(Effet.Entree, m.Gauche());
        return m;
    }

    [Fact]
    public void On_entre_par_l_onglet_de_droite_contre_le_menu_d_ES()
    {
        var m = Ouvert();
        Assert.Equal(Foyer.MenuEs, m.Etat);
        Assert.Equal(Onglet.Speedruns, m.OngletCourant);
        Assert.Equal(Effet.Rien, m.Bas());
        Assert.Equal(Effet.Entree, m.Gauche());
        Assert.Equal(Foyer.Panneau, m.Etat);
        Assert.Equal(Onglet.Speedruns, m.OngletCourant);
    }

    [Fact]
    public void Droite_sur_l_onglet_de_droite_rend_la_main_a_ES_et_gauche_tout_a_gauche_est_un_mur()
    {
        var m = Dedans();
        Assert.Equal(Effet.RendreLaMainAEs, m.Droite());
        Assert.Equal(Foyer.MenuEs, m.Etat);

        m = Dedans(Onglet.Records, Onglet.Speedruns);
        Assert.Equal(Effet.OngletChange, m.Gauche());
        Assert.Equal(Onglet.Records, m.OngletCourant);
        Assert.Equal(Effet.Rien, m.Gauche());
        Assert.Equal(Onglet.Records, m.OngletCourant);
    }

    [Fact]
    public void Les_onglets_gardent_leur_ordre_et_un_onglet_vide_n_existe_pas()
    {
        var m = Ouvert(Onglet.Speedruns, Onglet.Records);
        Assert.Equal([Onglet.Records, Onglet.Speedruns], m.Onglets);
    }

    [Fact]
    public void Une_liste_defile_sans_sortir_de_ses_bornes_et_chaque_onglet_garde_sa_ligne()
    {
        var m = Dedans();
        Assert.Equal(Effet.Rien, m.Haut());
        for (var i = 0; i < 11; i++) Assert.Equal(Effet.LigneChangee, m.Bas());
        Assert.Equal(11, m.Ligne);
        Assert.Equal(Effet.Rien, m.Bas());

        m.Gauche();
        Assert.Equal(0, m.Ligne);
        m.Droite();
        Assert.Equal(Onglet.Speedruns, m.OngletCourant);
        Assert.Equal(11, m.Ligne);
        Assert.Equal(Effet.RendreLaMainAEs, m.Droite());
        Assert.Equal(Effet.Entree, m.Gauche());
        Assert.Equal(11, m.Ligne);
    }

    [Fact]
    public void Dans_CHALLENGES_gauche_droite_changent_d_onglet_comme_partout()
    {
        var m = Dedans();
        Assert.Equal(Effet.OngletChange, m.Gauche());
        Assert.Equal(Onglet.Challenges, m.OngletCourant);
        Assert.Equal(Effet.LigneChangee, m.Bas());
        Assert.Equal(Effet.OngletChange, m.Droite());
        Assert.Equal(Onglet.Speedruns, m.OngletCourant);
        Assert.Equal(Effet.OngletChange, m.Gauche());
        Assert.Equal(1, m.Ligne);
        Assert.Equal(Effet.OngletChange, m.Gauche());
        Assert.Equal(Onglet.Training, m.OngletCourant);
    }

    [Fact]
    public void LB_et_RB_sautent_d_un_groupe_a_l_autre()
    {
        var m = Dedans();
        m.PoserLesLignes(Onglet.Speedruns, 12, [0, 5, 9]);
        Assert.Equal(Effet.LigneChangee, m.GroupeSuivant());
        Assert.Equal(5, m.Ligne);
        Assert.Equal(Effet.LigneChangee, m.GroupeSuivant());
        Assert.Equal(9, m.Ligne);
        Assert.Equal(Effet.Rien, m.GroupeSuivant());
        m.Bas();
        Assert.Equal(Effet.LigneChangee, m.GroupePrecedent());
        Assert.Equal(9, m.Ligne);
        Assert.Equal(Effet.LigneChangee, m.GroupePrecedent());
        Assert.Equal(5, m.Ligne);
    }

    [Fact]
    public void X_change_de_type_dans_RECORDS_et_rien_ailleurs()
    {
        var m = Dedans();
        Assert.Equal(Effet.Rien, m.X());
        m.Gauche();
        Assert.Equal(Effet.Rien, m.X());
        m.Gauche();
        m.Gauche();
        Assert.Equal(Onglet.Records, m.OngletCourant);
        m.PoserLesTypes(3);
        Assert.Equal(Effet.TypeChange, m.X());
        Assert.Equal(1, m.Type);
        m.X();
        Assert.Equal(Effet.TypeChange, m.X());
        Assert.Equal(0, m.Type);
    }

    private static DefisPanelModel DansRecords()
    {
        var m = Dedans();
        m.Gauche();
        m.Gauche();
        m.Gauche();
        Assert.Equal(Onglet.Records, m.OngletCourant);
        return m;
    }

    [Fact]
    public void Dans_RECORDS_LB_et_RB_changent_d_epreuve_sans_tourner_en_rond()
    {
        var m = DansRecords();
        m.PoserLesEpreuves(3);
        m.Bas();

        Assert.Equal(Effet.TypeChange, m.GroupeSuivant());
        Assert.Equal(1, m.Epreuve);
        Assert.Equal(0, m.Ligne);
        Assert.Equal(Effet.TypeChange, m.GroupeSuivant());
        Assert.Equal(Effet.Rien, m.GroupeSuivant());
        Assert.Equal(2, m.Epreuve);
        Assert.Equal(Effet.TypeChange, m.GroupePrecedent());
        Assert.Equal(Effet.TypeChange, m.GroupePrecedent());
        Assert.Equal(Effet.Rien, m.GroupePrecedent());
    }

    [Fact]
    public void X_repart_de_la_premiere_epreuve_et_du_haut_de_la_liste()
    {
        var m = DansRecords();
        m.PoserLesTypes(3);
        m.PoserLesEpreuves(2);
        m.GroupeSuivant();
        m.Bas();

        Assert.Equal(Effet.TypeChange, m.X());
        Assert.Equal(0, m.Epreuve);
        Assert.Equal(0, m.Ligne);
    }

    [Fact]
    public void Valider_ne_lance_rien_depuis_RECORDS()
        => Assert.Equal(Effet.Rien, DansRecords().Valider());

    [Fact]
    public void Un_onglet_qui_arrive_apres_l_ouverture_laisse_le_joueur_ou_il_est()
    {
        var m = Dedans(Onglet.Speedruns);
        m.Bas();

        m.PoserLesOnglets([Onglet.Records, Onglet.Speedruns]);

        Assert.Equal(Onglet.Speedruns, m.OngletCourant);
        Assert.Equal(1, m.Ligne);
        Assert.Equal(Effet.OngletChange, m.Gauche());
        Assert.Equal(Onglet.Records, m.OngletCourant);
    }

    [Fact]
    public void Un_onglet_qui_disparait_ramene_sur_celui_de_droite()
    {
        var m = Dedans(Onglet.Records, Onglet.Speedruns);
        m.Gauche();

        m.PoserLesOnglets([Onglet.Speedruns]);

        Assert.Equal(Onglet.Speedruns, m.OngletCourant);
    }

    [Fact]
    public void Retour_rend_la_main_a_ES_et_valider_ne_fait_rien_sur_un_onglet_sans_ligne()
    {
        var m = Dedans();
        m.PoserLesLignes(Onglet.Speedruns, 0);
        Assert.Equal(Effet.Rien, m.Valider());
        Assert.Equal(Effet.RendreLaMainAEs, m.Annuler());
        Assert.Equal(Foyer.MenuEs, m.Etat);
    }

    [Fact]
    public void Une_liste_qui_raccourcit_garde_le_curseur_dedans()
    {
        var m = Dedans();
        for (var i = 0; i < 8; i++) m.Bas();
        m.PoserLesLignes(Onglet.Speedruns, 3);
        Assert.Equal(2, m.Ligne);
    }
}
