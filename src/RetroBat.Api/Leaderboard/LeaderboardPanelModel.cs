namespace RetroBat.Api.Leaderboard;

/// <summary>
/// La navigation du panneau de classement : deux volets, un seul foyer, et la BORDURE QUI EST
/// UNE PORTE.
///
/// Le menu d'EmulationStation reste ouvert a droite pendant que notre panneau vit a gauche, et
/// un seul des deux recoit la manette. Gauche et droite ne peuvent pas a la fois changer de vue
/// et changer de volet : la sortie est donc donnee par la POSITION. Les vues sont des onglets
/// ranges du plus proche au plus lointain, on entre sur celui de droite (contre la porte), et
///
///   droite sur l'onglet le plus a droite  -> on sort, ES reprend la main
///   gauche sur l'onglet le plus a gauche  -> mur, rien ne bouge (ES ne reboucle pas non plus)
///   haut / bas                            -> se deplacer dans les scores
///   annuler                               -> on sort aussi, « BACK » veut dire la meme chose partout
///
/// Les onglets sont DYNAMIQUES : une vue qui ne peut rien montrer n'existe pas. « Ma salle » ne
/// parait que si la borne est reliee a un hub, « Ma ville » et « Mon pays » que si on les
/// connait. Rien de pire qu'un onglet vide qu'on traverse a chaque fois.
///
/// Pur : pas d'ecran, pas de reseau, pas d'horloge. C'est ce qui se teste.
/// </summary>
public sealed class LeaderboardPanelModel
{
    /// <summary>Les vues possibles, de la plus proche du joueur a la plus lointaine.</summary>
    public enum Vue
    {
        /// <summary>Directs et contests en cours sur ce jeu : n'existe que s'il y en a.</summary>
        LiveEtContest,
        MesRecords,
        CetteBorne,
        MaSalle,
        MaVille,
        MonPays,
        Monde,
    }

    /// <summary>Qui tient la manette. Le panneau ne s'affiche pas quand il est ferme.</summary>
    public enum Foyer
    {
        Ferme,
        MenuEs,      // affiche a cote du menu, mais c'est ES qui navigue
        Panneau,     // nous avons le focus, ES est fige
    }

    /// <summary>Ce que le modele demande a l'exterieur de faire. Il ne le fait jamais lui-meme.</summary>
    public enum Effet
    {
        Rien,
        PrendreLeFocus,
        RendreLeFocus,
        Fermer,
        ChargerLaVue,
        AgirSurLaLigne,
        /// <summary>Lancer le jeu pour battre le classement.</summary>
        Defier,
        /// <summary>Basculer le suivi du joueur de la ligne choisie.</summary>
        BasculerLeSuivi,
    }

    private readonly List<Vue> _onglets = new();
    private int _onglet;
    private int _ligne;
    private int _lignes;

    public Foyer Etat { get; private set; } = Foyer.Ferme;
    public IReadOnlyList<Vue> Onglets => _onglets;
    public Vue VueCourante => _onglets.Count == 0 ? Vue.Monde : _onglets[_onglet];
    public int IndexOnglet => _onglet;
    public int Ligne => _ligne;

    /// <summary>Vrai quand un appui a droite sortirait : c'est ce que la fleche doit annoncer.</summary>
    public bool SurLaPorte => _onglets.Count == 0 || _onglet == _onglets.Count - 1;

    /// <summary>
    /// Ouvre le panneau a cote du menu d'ES. Les vues disponibles se decident ICI, une fois, sur
    /// ce que la borne sait d'elle-meme.
    /// </summary>
    /// <summary>
    /// L'onglet LIVE & CONTEST existe-t-il ? Il se range APRES « Monde », contre la porte du menu
    /// d'ES, et ses changements ne deplacent JAMAIS le curseur : un onglet qui surgit sous le
    /// pouce du joueur lui volerait sa lecture. Rend vrai si la rangee d'onglets a change.
    /// </summary>
    public bool PoserLesEvenements(bool presents)
    {
        var avant = VueCourante;
        var deja = _onglets.Contains(Vue.LiveEtContest);
        if (presents == deja) return false;
        if (presents)
        {
            _onglets.Add(Vue.LiveEtContest);
        }
        else
        {
            _onglets.Remove(Vue.LiveEtContest);
            if (avant == Vue.LiveEtContest) { avant = Vue.Monde; _ligne = 0; }
        }
        var index = _onglets.IndexOf(avant);
        _onglet = index >= 0 ? index : Math.Clamp(_onglet, 0, Math.Max(0, _onglets.Count - 1));
        return true;
    }

    public void Ouvrir(bool salleConnue, bool villeConnue, bool paysConnu, bool aDesRecords)
    {
        _onglets.Clear();
        if (aDesRecords) _onglets.Add(Vue.MesRecords);
        _onglets.Add(Vue.CetteBorne);
        if (salleConnue) _onglets.Add(Vue.MaSalle);
        if (villeConnue) _onglets.Add(Vue.MaVille);
        if (paysConnu) _onglets.Add(Vue.MonPays);
        _onglets.Add(Vue.Monde);

        // On entre sur le dernier onglet : celui qui touche la porte. Chaque pas vers la gauche
        // resserre ensuite vers le joueur.
        _onglet = _onglets.Count - 1;
        _ligne = 0;
        _lignes = 0;
        Etat = Foyer.MenuEs;
    }

    /// <summary>La main revient a ES, le panneau reste ouvert (chien de garde).</summary>
    public void RendreLaMain()
    {
        if (Etat == Foyer.Panneau) Etat = Foyer.MenuEs;
    }

    public void Fermer()
    {
        Etat = Foyer.Ferme;
        _onglets.Clear();
        _onglet = 0;
        _ligne = 0;
        _lignes = 0;
    }

    /// <summary>Combien de lignes la vue courante porte. Borne la selection.</summary>
    public void PoserLesLignes(int combien)
    {
        _lignes = Math.Max(0, combien);
        if (_ligne >= _lignes) _ligne = Math.Max(0, _lignes - 1);
    }

    /// <summary>
    /// Une direction ou un bouton. Rend ce que l'appelant doit faire ; le modele ne touche ni au
    /// focus ni a l'ecran.
    /// </summary>
    public Effet Entree(EntreePanneau entree)
    {
        switch (Etat)
        {
            case Foyer.Ferme:
                return Effet.Rien;

            // Le panneau est visible mais ES navigue : la SEULE chose qui nous concerne est
            // « gauche », qui nous donne la main. Le reste appartient a son menu.
            case Foyer.MenuEs:
                if (entree == EntreePanneau.Gauche)
                {
                    Etat = Foyer.Panneau;
                    return Effet.PrendreLeFocus;
                }
                // Annuler pendant que ES a la main : c'est SON menu qui se referme, donc nous aussi.
                if (entree == EntreePanneau.Annuler) return Effet.Fermer;
                return Effet.Rien;

            case Foyer.Panneau:
                switch (entree)
                {
                    case EntreePanneau.Droite:
                        if (SurLaPorte)
                        {
                            Etat = Foyer.MenuEs;
                            return Effet.RendreLeFocus;
                        }
                        _onglet++;
                        _ligne = 0;
                        return Effet.ChargerLaVue;

                    case EntreePanneau.Gauche:
                        // Mur a l'extremite gauche : on ne reboucle pas, ES non plus.
                        if (_onglet == 0) return Effet.Rien;
                        _onglet--;
                        _ligne = 0;
                        return Effet.ChargerLaVue;

                    case EntreePanneau.Haut:
                        if (_lignes == 0 || _ligne == 0) return Effet.Rien;
                        _ligne--;
                        return Effet.Rien;

                    case EntreePanneau.Bas:
                        if (_lignes == 0 || _ligne >= _lignes - 1) return Effet.Rien;
                        _ligne++;
                        return Effet.Rien;

                    case EntreePanneau.PageHaut:
                        if (_lignes == 0) return Effet.Rien;
                        _ligne = Math.Max(0, _ligne - PageDeLignes);
                        return Effet.Rien;

                    case EntreePanneau.PageBas:
                        if (_lignes == 0) return Effet.Rien;
                        _ligne = Math.Min(_lignes - 1, _ligne + PageDeLignes);
                        return Effet.Rien;

                    case EntreePanneau.Agir:
                        return _lignes == 0 ? Effet.Rien : Effet.AgirSurLaLigne;

                    // Defier ne depend d'AUCUNE ligne : c'est le jeu qu'on defie, et l'action
                    // reste offerte meme quand le classement est vide - c'est meme la qu'elle
                    // a le plus de sens.
                    case EntreePanneau.Defier:
                        return Effet.Defier;

                    case EntreePanneau.Suivre:
                        return _lignes == 0 ? Effet.Rien : Effet.BasculerLeSuivi;

                    case EntreePanneau.Annuler:
                        // On rend la main ET on se ferme : « BACK » sort de notre panneau, il ne
                        // renvoie pas dans un menu ou le joueur ne pensait pas retourner.
                        Etat = Foyer.Ferme;
                        return Effet.Fermer;

                    default:
                        return Effet.Rien;
                }

            default:
                return Effet.Rien;
        }
    }

    /// <summary>Ce qu'une gachette fait sauter : une page de classement, comme dans ES.</summary>
    public const int PageDeLignes = 5;
}

/// <summary>Ce que le panneau comprend. Traduit des slots de la borne par l'appelant.</summary>
/// <summary>Ce que le joueur peut demander depuis le panneau, au-dela de la navigation.</summary>
public enum EntreePanneau
{
    Gauche,
    Droite,
    Haut,
    Bas,
    PageHaut,
    PageBas,
    Agir,
    Annuler,
    /// <summary>Defier le jeu : une action GLOBALE, pas liee a une ligne.</summary>
    Defier,
    /// <summary>Suivre ou ne plus suivre le joueur de la ligne choisie.</summary>
    Suivre,
}
