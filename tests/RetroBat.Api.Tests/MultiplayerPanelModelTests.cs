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
        Assert.Equal(["INVITE", "WAITING FOR FRIENDS", "FOR PLAYERS…"], M.Onglets.Select(M.Titre).ToArray());
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
    public void Entrer_dans_le_panneau_n_active_que_l_onglet()
    {
        var m = new M();
        m.Ouvrir();
        m.Gauche();
        m.PoserLesLignes(2);
        Assert.Equal(-1, m.Ligne);                    // WAITING FOR PLAYERS : aucun joueur surligne
        Assert.Equal(M.Effet.Rien, m.Bas());          // rien a choisir dans cet onglet
        Assert.Equal(-1, m.Ligne);
    }

    [Fact]
    public void Haut_et_bas_restent_dans_les_lignes_d_invite_friends()
    {
        var m = new M();
        m.Ouvrir();
        m.Gauche();
        m.Gauche();
        m.Gauche();                                   // INVITE FRIENDS
        m.PoserLesLignes(2);
        Assert.Equal(-1, m.Ligne);
        Assert.Equal(M.Effet.Rien, m.Haut());
        Assert.Equal(M.Effet.ChargerLOnglet, m.Bas());
        Assert.Equal(0, m.Ligne);
        Assert.Equal(M.Effet.ChargerLOnglet, m.Bas());
        Assert.Equal(1, m.Ligne);
        Assert.Equal(M.Effet.Rien, m.Bas());
        m.PoserLesLignes(1);
        Assert.Equal(0, m.Ligne);
        Assert.Equal(M.Effet.ChargerLOnglet, m.Haut());
        Assert.Equal(-1, m.Ligne);                    // retour a l'onglet seul
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
        Assert.Equal(M.Effet.Rien, m.Valider());   // aucun ami choisi
        m.Bas();
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

    /// <summary>
    /// LES GAMEROOMS D'AMIS (28/10) : l'appui long d'un ami le mene dans la gameroom d'amis qui l'attend ; le panneau s'ouvre
    /// alors sur WAITING FOR FRIENDS, ES gardant la main, et ◄ ► continuent depuis cet onglet.
    /// </summary>
    [Fact]
    public void Une_gameroom_d_amis_ouvre_le_panneau_sur_waiting_for_friends()
    {
        var m = new M();
        m.Ouvrir();
        m.PlacerSurLOnglet(M.Onglet.WaitingForFriends);
        Assert.Equal((M.Foyer.MenuEs, M.Onglet.WaitingForFriends, -1), (m.Etat, m.OngletCourant, m.Ligne));
        // L'entree (◄ depuis ES, ou l'entree automatique) garde FOR FRIENDS.
        Assert.Equal(M.Effet.PrendreLeFocus, m.Gauche());
        Assert.Equal(M.Onglet.WaitingForFriends, m.OngletCourant);
        Assert.Equal(M.Effet.ChargerLOnglet, m.Gauche());
        Assert.Equal(M.Onglet.InviteFriends, m.OngletCourant);
        Assert.Equal(M.Effet.ChargerLOnglet, m.Droite());
        Assert.Equal(M.Effet.ChargerLOnglet, m.Droite());
        Assert.Equal(M.Onglet.WaitingForPlayers, m.OngletCourant);

        // Rendu a ES depuis FOR FRIENDS, on y rentre par la porte posee par le service (la gameroom ou l'on attend).
        m.Gauche();
        Assert.Equal(M.Effet.RendreLeFocus, m.Annuler());
        m.PoserLaPorte(M.Onglet.WaitingForFriends);
        m.Gauche();
        Assert.Equal(M.Onglet.WaitingForFriends, m.OngletCourant);
    }

    /// <summary>X chez l'ouvreur : un joueur de plus jusqu'au maximum du jeu, puis retour a ceux qui sont la (deux au moins).</summary>
    [Theory]
    [InlineData(2, 2, 4, 3)]
    [InlineData(3, 2, 4, 4)]
    [InlineData(4, 2, 4, 2)]
    [InlineData(4, 3, 4, 3)]   // trois deja la : jamais moins
    [InlineData(4, 1, 4, 2)]   // jamais une partie seul
    [InlineData(2, 3, 4, 3)]   // un ancien reglage sous les presents remonte a eux
    public void X_fait_tourner_le_nombre_de_joueurs_attendus(int actuelle, int presents, int maxDuJeu, int suivante)
        => Assert.Equal(suivante, M.TailleSuivante(actuelle, presents, maxDuJeu));
}
