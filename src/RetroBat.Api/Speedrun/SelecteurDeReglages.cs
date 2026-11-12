namespace RetroBat.Api.Speedrun;

/// <summary>
/// Un reglage du selecteur : son nom, ses valeurs (dans l'ordre de gauche a droite), la valeur courante et sa pose, et s'il
/// joue en ce moment (un reglage inactif reste visible, grise, et le curseur le saute).
/// </summary>
public sealed record ReglageDuSelecteur(string Etiquette, IReadOnlyList<string> Valeurs, Func<int> Lire, Action<int> Ecrire,
    Func<bool>? Actif = null)
{
    public bool EstActif => Actif?.Invoke() ?? true;
}

/// <summary>
/// LE SELECTEUR DE REGLAGES (12/11, decision du user) : une surimpression du panneau, une liste de lignes generiques. Haut et
/// bas choisissent la ligne, gauche et droite changent sa valeur (en boucle). Il ne tient que son etat (ouvert, ligne) : les
/// reglages lui sont donnes a chaque geste, il peut donc en accueillir d'autres plus tard sans changer. Pur, sans disque.
/// </summary>
public sealed class SelecteurDeReglages
{
    public bool Ouvert { get; private set; }

    public int Ligne { get; private set; }

    public void Ouvrir(IReadOnlyList<ReglageDuSelecteur> reglages)
    {
        Ouvert = true;
        Ligne = 0;
        Recaler(reglages);
    }

    public void Fermer()
    {
        Ouvert = false;
        Ligne = 0;
    }

    /// <summary>La ligne active d'au-dessus ; rien en haut de la liste.</summary>
    public bool Haut(IReadOnlyList<ReglageDuSelecteur> reglages) => Aller(reglages, -1);

    /// <summary>La ligne active d'en dessous ; rien en bas de la liste.</summary>
    public bool Bas(IReadOnlyList<ReglageDuSelecteur> reglages) => Aller(reglages, +1);

    /// <summary>La valeur precedente de la ligne choisie, en boucle.</summary>
    public bool Gauche(IReadOnlyList<ReglageDuSelecteur> reglages) => Changer(reglages, -1);

    /// <summary>La valeur suivante de la ligne choisie, en boucle.</summary>
    public bool Droite(IReadOnlyList<ReglageDuSelecteur> reglages) => Changer(reglages, +1);

    /// <summary>Un reglage qui vient de passer inactif ne garde pas le curseur : la premiere ligne active le reprend.</summary>
    public void Recaler(IReadOnlyList<ReglageDuSelecteur> reglages)
    {
        if (reglages.Count == 0) { Ligne = 0; return; }
        Ligne = Math.Clamp(Ligne, 0, reglages.Count - 1);
        if (reglages[Ligne].EstActif) return;
        for (var i = 0; i < reglages.Count; i++)
        {
            if (reglages[i].EstActif) { Ligne = i; return; }
        }
    }

    private bool Aller(IReadOnlyList<ReglageDuSelecteur> reglages, int sens)
    {
        if (!Ouvert) return false;
        for (var i = Ligne + sens; i >= 0 && i < reglages.Count; i += sens)
        {
            if (!reglages[i].EstActif) continue;
            Ligne = i;
            return true;
        }

        return false;
    }

    private bool Changer(IReadOnlyList<ReglageDuSelecteur> reglages, int sens)
    {
        if (!Ouvert || Ligne >= reglages.Count) return false;
        var r = reglages[Ligne];
        if (!r.EstActif || r.Valeurs.Count < 2) return false;
        var n = r.Valeurs.Count;
        r.Ecrire(((Math.Clamp(r.Lire(), 0, n - 1) + sens) % n + n) % n);
        Recaler(reglages);
        return true;
    }
}
