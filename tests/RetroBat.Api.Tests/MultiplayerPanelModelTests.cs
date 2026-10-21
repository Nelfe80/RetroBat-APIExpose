using RetroBat.Api.Multiplayer;
using Xunit;
using M = RetroBat.Api.Multiplayer.MultiplayerPanelModel;

namespace RetroBat.Api.Tests;

/// <summary>
/// La navigation du panneau MULTIPLAYER (V24-04, V24-07) : on entre par ◄ sur WAITING FOR PLAYERS…,
/// les onglets suivent le geste vers la gauche, ► sur l'onglet de droite rend la main a ES, B rend la
/// main ou ferme, B maintenu quitte la gameroom.
/// </summary>
public class MultiplayerPanelModelTests
{
    [Fact]
    public void Les_onglets_se_rangent_de_droite_a_gauche_et_restent_en_anglais()
    {
        Assert.Equal(["INVITE FRIENDS", "WAITING FOR FRIENDS", "FOR PLAYERS…"], M.Onglets.Select(M.Titre).ToArray());
        var m = new M();
        m.Ouvrir();
        Assert.Equal(M.Foyer.MenuEs, m.Etat);
        Assert.Equal(M.Onglet.WaitingForPlayers, m.OngletCourant);
    }

    [Fact]
    public void Gauche_entre_puis_avance_jusqu_au_mur()
    {
        var m = new M();
        m.Ouvrir();
        Assert.Equal(M.Effet.PrendreLeFocus, m.Gauche());
        Assert.Equal(M.Onglet.WaitingForPlayers, m.OngletCourant);
        Assert.Equal(M.Effet.ChargerLOnglet, m.Gauche());
        Assert.Equal(M.Onglet.WaitingForFriends, m.OngletCourant);
        Assert.Equal(M.Effet.ChargerLOnglet, m.Gauche());
        Assert.Equal(M.Onglet.InviteFriends, m.OngletCourant);
        Assert.Equal(M.Effet.Rien, m.Gauche());   // le mur
        Assert.Equal(M.Onglet.InviteFriends, m.OngletCourant);
    }

    [Fact]
    public void Droite_revient_puis_rend_la_main_a_es_sur_la_porte()
    {
        var m = new M();
        m.Ouvrir();
        m.Gauche();
        m.Gauche();
        Assert.Equal(M.Effet.ChargerLOnglet, m.Droite());
        Assert.Equal(M.Onglet.WaitingForPlayers, m.OngletCourant);
        Assert.Equal(M.Effet.RendreLeFocus, m.Droite());
        Assert.Equal(M.Foyer.MenuEs, m.Etat);
        Assert.Equal(M.Effet.Rien, m.Droite());
    }

    [Fact]
    public void Haut_et_bas_restent_dans_les_lignes()
    {
        var m = new M();
        m.Ouvrir();
        m.Gauche();
        m.PoserLesLignes(2);
        Assert.Equal(M.Effet.Rien, m.Haut());
        Assert.Equal(M.Effet.ChargerLOnglet, m.Bas());
        Assert.Equal(1, m.Ligne);
        Assert.Equal(M.Effet.Rien, m.Bas());
        m.PoserLesLignes(1);
        Assert.Equal(0, m.Ligne);
    }

    [Fact]
    public void Seul_invite_friends_agit_sur_sa_ligne()
    {
        var m = new M();
        m.Ouvrir();
        m.Gauche();
        m.PoserLesLignes(3);
        Assert.Equal(M.Effet.Rien, m.Valider());
        m.Gauche();
        m.Gauche();
        m.PoserLesLignes(0);
        Assert.Equal(M.Effet.Rien, m.Valider());   // personne a inviter
        m.PoserLesLignes(2);
        Assert.Equal(M.Effet.AgirSurLaLigne, m.Valider());
    }

    [Fact]
    public void B_rend_la_main_puis_ferme_et_b_maintenu_quitte()
    {
        var m = new M();
        m.Ouvrir();
        m.Gauche();
        Assert.Equal(M.Effet.RendreLeFocus, m.Annuler());
        Assert.Equal(M.Effet.Fermer, m.Annuler());
        Assert.Equal(M.Foyer.Ferme, m.Etat);

        m.Ouvrir();
        Assert.Equal(M.Effet.QuitterLaGameroom, m.AnnulerMaintenu());
        Assert.Equal(M.Foyer.Ferme, m.Etat);
        Assert.Equal(M.Effet.Rien, m.AnnulerMaintenu());
    }

    [Fact]
    public void Une_fenetre_qui_passe_devant_rend_la_main_sans_fermer()
    {
        var m = new M();
        m.Ouvrir();
        Assert.False(m.RendreLaMain());          // ES navigue deja : rien a rendre
        m.Gauche();
        m.Gauche();
        Assert.True(m.RendreLaMain());
        Assert.Equal(M.Foyer.MenuEs, m.Etat);    // toujours ouvert : on attend un joueur
        Assert.Equal(M.Effet.PrendreLeFocus, m.Gauche());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(25, 4)]
    [InlineData(60, 3)]
    [InlineData(120, 2)]
    [InlineData(300, 1)]
    public void La_latence_se_lit_en_quatre_barres(int? rtt, int? barres)
        => Assert.Equal(barres, MultiplayerPanelService.Barres(rtt));

    [Fact]
    public void La_couleur_d_un_joueur_ne_depend_que_de_sa_poignee()
    {
        Assert.Equal(MultiplayerPanelService.CouleurDe("aaaaaaaaaaaaaaaa"), MultiplayerPanelService.CouleurDe("aaaaaaaaaaaaaaaa"));
        Assert.Matches("^[0-9A-F]{6}$", MultiplayerPanelService.CouleurDe("bbbbbbbbbbbbbbbb"));
    }
}
