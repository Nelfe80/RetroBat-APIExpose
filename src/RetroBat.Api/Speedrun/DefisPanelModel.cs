namespace RetroBat.Api.Speedrun;

/// <summary>
/// La navigation du panneau des defis (CDC du panneau, §7.2), comme celles du classement et de MULTIPLAYER : deux volets,
/// un seul foyer, et la bordure qui est une porte.
///
/// Les onglets se rangent de gauche a droite : RECORDS, TRAINING, CHALLENGES, SPEEDRUNS. On entre depuis le menu d'ES par ◄
/// sur l'onglet de droite. ► sur cet onglet rend la main a ES, ◄ sur celui de gauche ne fait rien (le mur).
///
/// Partout la grammaire de la barre d'aide d'ES : ◄ ► changent d'onglet, ▲ ▼ de ligne, B revient. CHALLENGES est donc une
/// colonne de bandeaux, pas une grille : ◄ ne changeait plus de sens selon la colonne (decision du user du 31/10). LB et RB
/// sautent d'un groupe a l'autre dans une longue liste. Les onglets sont dynamiques : une vue vide n'existe pas.
///
/// Pur : pas d'ecran, pas de reseau, pas d'horloge. C'est ce qui se teste.
/// </summary>
public sealed class DefisPanelModel
{
    /// <summary>Les onglets, de gauche a droite a l'ecran. Toujours en anglais, comme ceux de MULTIPLAYER.</summary>
    public enum Onglet
    {
        Records,
        Training,
        Challenges,
        Speedruns,
    }

    public enum Foyer
    {
        Ferme,
        MenuEs,
        Panneau,
    }

    public enum Effet
    {
        Rien,
        Entree,
        RendreLaMainAEs,
        OngletChange,
        LigneChangee,
        Valider,
        TypeChange,
    }

    private readonly Dictionary<Onglet, IReadOnlyList<int>> _groupes = new();

    private readonly Dictionary<Onglet, int> _lignes = new();
    private readonly Dictionary<Onglet, int> _curseurs = new();
    private IReadOnlyList<Onglet> _onglets = [Onglet.Speedruns];
    private int _onglet;

    public static string Titre(Onglet onglet) => onglet switch
    {
        Onglet.Records => "RECORDS",
        Onglet.Training => "TRAINING",
        Onglet.Challenges => "CHALLENGES",
        _ => "SPEEDRUNS",
    };

    public Foyer Etat { get; private set; } = Foyer.Ferme;

    public IReadOnlyList<Onglet> Onglets => _onglets;

    public Onglet OngletCourant => _onglets[_onglet];

    /// <summary>Le type choisi dans RECORDS : son index dans la rangee (GLOBAL, ANY%...).</summary>
    public int Type { get; private set; }

    public int Types { get; private set; }

    /// <summary>L'epreuve montree par le type choisi (NIVEAUX, BOSS) : LB et RB la changent.</summary>
    public int Epreuve { get; private set; }

    public int Epreuves { get; private set; }

    public int Ligne => _curseurs.GetValueOrDefault(OngletCourant);

    /// <summary>S'ouvre a cote du menu d'ES, sur l'onglet de droite : la porte.</summary>
    public void Ouvrir(IReadOnlyList<Onglet> onglets)
    {
        _onglets = onglets.Count > 0 ? onglets.OrderBy(o => o).ToList() : [Onglet.Speedruns];
        _onglet = _onglets.Count - 1;
        _curseurs.Clear();
        Type = 0;
        Epreuve = 0;
        Etat = Foyer.MenuEs;
    }

    /// <summary>Les classements arrivent apres l'ouverture : RECORDS peut paraitre sous les pieds du joueur, qui reste ou il est.</summary>
    public void PoserLesOnglets(IReadOnlyList<Onglet> onglets)
    {
        var courant = OngletCourant;
        _onglets = onglets.Count > 0 ? onglets.OrderBy(o => o).ToList() : [Onglet.Speedruns];
        var index = _onglets.ToList().IndexOf(courant);
        _onglet = index >= 0 ? index : _onglets.Count - 1;
    }

    public void Fermer() => Etat = Foyer.Ferme;

    /// <summary>
    /// Le nombre de lignes d'un onglet, et l'index de la premiere ligne de chaque groupe (Niveaux, Boss...) pour LB et RB. Le
    /// curseur reste dedans si la liste raccourcit.
    /// </summary>
    public void PoserLesLignes(Onglet onglet, int nombre, IReadOnlyList<int>? debutsDeGroupe = null)
    {
        _lignes[onglet] = Math.Max(0, nombre);
        _groupes[onglet] = (debutsDeGroupe ?? []).Where(i => i >= 0 && i < nombre).Distinct().OrderBy(i => i).ToList();
        _curseurs[onglet] = Math.Clamp(_curseurs.GetValueOrDefault(onglet), 0, Math.Max(0, nombre - 1));
    }

    public void PoserLesTypes(int nombre)
    {
        Types = Math.Max(0, nombre);
        Type = Math.Clamp(Type, 0, Math.Max(0, Types - 1));
    }

    public void PoserLesEpreuves(int nombre)
    {
        Epreuves = Math.Max(0, nombre);
        Epreuve = Math.Clamp(Epreuve, 0, Math.Max(0, Epreuves - 1));
    }

    private int Nombre => _lignes.GetValueOrDefault(OngletCourant);


    public Effet Gauche()
    {
        if (Etat == Foyer.MenuEs)
        {
            Etat = Foyer.Panneau;
            return Effet.Entree;
        }

        if (Etat != Foyer.Panneau || _onglet == 0) return Effet.Rien;
        _onglet--;
        return Effet.OngletChange;
    }

    public Effet Droite()
    {
        if (Etat != Foyer.Panneau) return Effet.Rien;
        if (_onglet < _onglets.Count - 1)
        {
            _onglet++;
            return Effet.OngletChange;
        }

        Etat = Foyer.MenuEs;
        return Effet.RendreLaMainAEs;
    }

    public Effet Haut() => Etat == Foyer.Panneau && Ligne > 0 ? Deplacer(Ligne - 1) : Effet.Rien;

    public Effet Bas() => Etat == Foyer.Panneau && Ligne + 1 < Nombre ? Deplacer(Ligne + 1) : Effet.Rien;

    /// <summary>RB : le debut du groupe suivant, ou l'epreuve suivante dans RECORDS. Sans rien apres, rien.</summary>
    public Effet GroupeSuivant()
    {
        if (Etat != Foyer.Panneau) return Effet.Rien;
        if (OngletCourant == Onglet.Records) return ChangerDEpreuve(Epreuve + 1);
        var suivant = _groupes.GetValueOrDefault(OngletCourant, []).FirstOrDefault(i => i > Ligne, -1);
        return suivant >= 0 ? Deplacer(suivant) : Effet.Rien;
    }

    /// <summary>LB : le debut du groupe courant, ou du precedent si l'on y est deja ; l'epreuve precedente dans RECORDS.</summary>
    public Effet GroupePrecedent()
    {
        if (Etat != Foyer.Panneau) return Effet.Rien;
        if (OngletCourant == Onglet.Records) return ChangerDEpreuve(Epreuve - 1);
        var precedent = _groupes.GetValueOrDefault(OngletCourant, []).LastOrDefault(i => i < Ligne, -1);
        return precedent >= 0 ? Deplacer(precedent) : Effet.Rien;
    }

    /// <summary>RECORDS se regarde : rien n'y lance une epreuve.</summary>
    public Effet Valider() => Etat == Foyer.Panneau && Nombre > 0 && OngletCourant != Onglet.Records ? Effet.Valider : Effet.Rien;

    public Effet Annuler()
    {
        if (Etat != Foyer.Panneau) return Effet.Rien;
        Etat = Foyer.MenuEs;
        return Effet.RendreLaMainAEs;
    }

    /// <summary>
    /// X : le type de record suivant, dans RECORDS seulement (decision du user du 31/10). Ailleurs, la ligne montre deja le
    /// record et le temps du joueur ; les classements des challenges sont sur le site. Pas de « voir ce run » tant que le
    /// site ne relie pas un replay a un temps de defi.
    /// </summary>
    public Effet X()
    {
        if (Etat != Foyer.Panneau || OngletCourant != Onglet.Records || Types < 2) return Effet.Rien;
        Type = (Type + 1) % Types;
        Epreuve = 0;
        _curseurs[Onglet.Records] = 0;
        return Effet.TypeChange;
    }

    private Effet ChangerDEpreuve(int epreuve)
    {
        if (epreuve < 0 || epreuve >= Epreuves) return Effet.Rien;
        Epreuve = epreuve;
        _curseurs[Onglet.Records] = 0;
        return Effet.TypeChange;
    }

    private Effet Deplacer(int ligne)
    {
        _curseurs[OngletCourant] = ligne;
        return Effet.LigneChangee;
    }
}
