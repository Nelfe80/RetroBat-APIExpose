namespace RetroBat.Api.Multiplayer;

/// <summary>
/// La navigation du panneau MULTIPLAYER (CDC MULTIPLAYER v2.5, V24-04), comme celle du classement :
/// deux volets, un seul foyer, et la bordure qui est une porte.
///
/// Les onglets se rangent de droite a gauche pour suivre le geste : on entre depuis le menu d'ES par
/// ◄ sur WAITING FOR PLAYERS…, puis ◄ mene a WAITING FOR FRIENDS, puis a INVITE FRIENDS (le mur).
/// ► sur l'onglet de droite rend la main a ES. B rend aussi la main ; B MAINTENU quitte la gameroom
/// (V24-07) ; ▲▼ choisit une ligne ; A agit sur la ligne (inviter un ami).
///
/// Pur : pas d'ecran, pas de reseau, pas d'horloge. C'est ce qui se teste.
/// </summary>
public sealed class MultiplayerPanelModel
{
    /// <summary>Les onglets, de gauche a droite a l'ecran. Toujours en anglais (V24-22).</summary>
    public enum Onglet
    {
        InviteFriends,
        WaitingForFriends,
        WaitingForPlayers,
    }

    public static readonly IReadOnlyList<Onglet> Onglets = [Onglet.InviteFriends, Onglet.WaitingForFriends, Onglet.WaitingForPlayers];

    /// <summary>
    /// Les titres, courts : les trois tiennent dans le panneau (retour du user du 22/10, « INVITE FRIENDS »
    /// sortait a gauche, verifie a l'apercu). Les deux onglets d'attente se lisent d'un trait :
    /// WAITING FOR FRIENDS | FOR PLAYERS….
    /// </summary>
    public static string Titre(Onglet onglet) => onglet switch
    {
        Onglet.InviteFriends => "INVITE",
        Onglet.WaitingForFriends => "WAITING FOR FRIENDS",
        _ => "FOR PLAYERS…",
    };

    public enum Foyer
    {
        Ferme,
        MenuEs,
        Panneau,
    }

    public enum Effet
    {
        Rien,
        PrendreLeFocus,
        RendreLeFocus,
        Fermer,
        ChargerLOnglet,
        AgirSurLaLigne,
        QuitterLaGameroom,
    }

    private int _onglet = Onglets.Count - 1;

    // -1 : aucune ligne choisie, seul l'onglet est actif (retour du user du 22/10 : entrer dans le panneau surlignait le
    // premier joueur, qui n'etait pas toujours soi). Seul INVITE FRIENDS a des lignes a choisir, ▼ y prend la premiere.
    private int _ligne = -1;
    private int _lignes;

    public Foyer Etat { get; private set; } = Foyer.Ferme;

    public Onglet OngletCourant => Onglets[_onglet];

    public int IndexOnglet => _onglet;

    public int Ligne => _ligne;

    /// <summary>S'ouvre a cote du menu d'ES, sur WAITING FOR PLAYERS… : l'appui long vient de placer le joueur.</summary>
    public void Ouvrir()
    {
        Etat = Foyer.MenuEs;
        _onglet = Onglets.Count - 1;
        _ligne = -1;
    }

    public void Fermer()
    {
        Etat = Foyer.Ferme;
        _ligne = -1;
    }

    private bool LignesAChoisir => Etat == Foyer.Panneau && OngletCourant == Onglet.InviteFriends;

    /// <summary>Le nombre de lignes de l'onglet courant, pour borner ▲▼.</summary>
    public void PoserLesLignes(int nombre)
    {
        _lignes = Math.Max(0, nombre);
        _ligne = OngletCourant != Onglet.InviteFriends || _lignes == 0 ? -1 : Math.Clamp(_ligne, -1, _lignes - 1);
    }

    public Effet Gauche()
    {
        switch (Etat)
        {
            case Foyer.MenuEs:
                // Depuis le menu d'ES : on entre chez nous, contre la porte.
                Etat = Foyer.Panneau;
                _onglet = Onglets.Count - 1;
                _ligne = -1;
                return Effet.PrendreLeFocus;
            case Foyer.Panneau when _onglet > 0:
                _onglet--;
                _ligne = -1;
                return Effet.ChargerLOnglet;
            default:
                return Effet.Rien;   // le mur : ES ne reboucle pas non plus
        }
    }

    public Effet Droite()
    {
        if (Etat != Foyer.Panneau)
        {
            return Effet.Rien;
        }

        if (_onglet == Onglets.Count - 1)
        {
            Etat = Foyer.MenuEs;
            return Effet.RendreLeFocus;
        }

        _onglet++;
        _ligne = -1;
        return Effet.ChargerLOnglet;
    }

    /// <summary>▲ : remonte d'une ligne, jusqu'a l'onglet seul (aucune ligne).</summary>
    public Effet Haut()
    {
        if (!LignesAChoisir || _ligne < 0)
        {
            return Effet.Rien;
        }

        _ligne--;
        return Effet.ChargerLOnglet;
    }

    public Effet Bas()
    {
        if (!LignesAChoisir || _ligne >= _lignes - 1)
        {
            return Effet.Rien;
        }

        _ligne++;
        return Effet.ChargerLOnglet;
    }

    /// <summary>A : agir sur la ligne. Seul INVITE FRIENDS a une action ; ailleurs, rien.</summary>
    public Effet Valider()
        => LignesAChoisir && _lignes > 0 && _ligne >= 0 ? Effet.AgirSurLaLigne : Effet.Rien;

    /// <summary>B relache : « BACK » veut dire la meme chose partout. Depuis le menu d'ES, ferme le panneau.</summary>
    public Effet Annuler()
    {
        switch (Etat)
        {
            case Foyer.Panneau:
                Etat = Foyer.MenuEs;
                return Effet.RendreLeFocus;
            case Foyer.MenuEs:
                // Fermer le panneau veut dire attendre depuis « Votre selection » (V24-09).
                Fermer();
                return Effet.Fermer;
            default:
                return Effet.Rien;
        }
    }

    /// <summary>
    /// Une autre fenetre est passee devant, ou plus rien n'arrive : le panneau RESTE a l'ecran (on y attend un
    /// joueur) et rend seulement la main a ES. Rend vrai si le foyer a change.
    /// </summary>
    public bool RendreLaMain()
    {
        if (Etat != Foyer.Panneau)
        {
            return false;
        }

        Etat = Foyer.MenuEs;
        return true;
    }

    /// <summary>B maintenu : le joueur quitte la gameroom, et le panneau se ferme.</summary>
    public Effet AnnulerMaintenu()
    {
        if (Etat == Foyer.Ferme)
        {
            return Effet.Rien;
        }

        Fermer();
        return Effet.QuitterLaGameroom;
    }
}
