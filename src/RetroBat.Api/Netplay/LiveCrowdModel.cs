namespace RetroBat.Api.Netplay;

/// <summary>
/// La FOULE des spectateurs d'un direct : etat et regles, sans aucun dessin.
///
/// Jumeau de `nelfe-crowd.js`. Les deux doivent rendre les MEMES nombres, sinon la meme foule
/// se placerait autrement selon l'ecran, et deux personnes qui commentent la meme partie ne
/// parleraient pas de la meme silhouette. Les regles sont donc recopiees telles quelles, et
/// verifiables sans dessiner.
///
/// TROIS RANGEES DE CENT, et c'est le REMPLISSAGE qui est la metrique : on lit la taille de
/// l'audience sans qu'un chiffre soit ecrit. Peu de monde, une rangee incomplete ; beaucoup,
/// les trois rangees pleines.
///
/// Le placement vient de la liste TRIEE des acteurs, jamais d'un hachage. Avec trois cents
/// places et vingt spectateurs, un hachage fait tomber deux personnes sur la meme place une
/// fois sur deux (paradoxe des anniversaires). En triant, tout le monde attribue les places
/// dans le meme ordre : meme foule partout, zero collision.
///
/// La couleur, elle, vient d'un hachage : une collision n'y est qu'une coincidence sans
/// consequence, deux silhouettes de la meme teinte ne genent personne.
/// </summary>
public sealed class LiveCrowdModel
{
    public const int Rangees = 3;
    public const int ParRangee = 100;
    public const int Capacite = Rangees * ParRangee;

    /// <summary>Le plafond d'etiquettes affichees ensemble, et la duree du cran le plus long.</summary>
    private const int PlafondEtiquettes = 8;

    private const int DureeHaute = 1800;
    private const int DureePlancher = 400;

    /// <summary>Duree de vol d'un emoji, et duree d'un saut.</summary>
    public const int DureeVol = 1400;
    public const int DureeSaut = 420;

    /// <summary>Au-dela, on ne cumule plus : trois cents sprites qui volent, personne ne les lit.</summary>
    private const int PlafondVols = 60;

    private readonly object _gate = new();
    private string[] _acteurs = Array.Empty<string>();
    private Dictionary<string, (int Rangee, int Index)> _places = new(StringComparer.Ordinal);
    private int _total;
    private readonly List<Vol> _vols = new();
    private readonly List<Etiquette> _etiquettes = new();
    private readonly Dictionary<string, Saut> _sauts = new(StringComparer.Ordinal);

    public sealed record Vol(string Famille, int Niveau, int Rangee, int Index, long Depuis);
    public sealed record Etiquette(string Texte, int Rangee, int Index, long Jusqua);
    public sealed record Saut(long Fin, int Hauteur);

    /// <summary>Ce qu'il faut pour dessiner une image, pris sous verrou une seule fois.</summary>
    public sealed record Instantane(
        IReadOnlyList<string> Acteurs,
        IReadOnlyDictionary<string, (int Rangee, int Index)> Places,
        int Total,
        IReadOnlyList<Vol> Vols,
        IReadOnlyList<Etiquette> Etiquettes,
        IReadOnlyDictionary<string, Saut> Sauts);

    // ── Les regles, pures ──────────────────────────────────────────────────

    /// <summary>Les places, attribuees depuis la liste TRIEE. Rangee de DEVANT d'abord.</summary>
    public static Dictionary<string, (int Rangee, int Index)> Places(IEnumerable<string> acteurs)
    {
        var tries = acteurs.Where(a => !string.IsNullOrEmpty(a)).ToArray();
        Array.Sort(tries, StringComparer.Ordinal);
        var sortie = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < tries.Length && i < Capacite; i++)
        {
            // « Trois personnes devant moi » se lit mieux que « trois personnes au fond ».
            sortie[tries[i]] = (i / ParRangee, i % ParRangee);
        }
        return sortie;
    }

    /// <summary>La teinte d'un spectateur, 0 a 359. Meme arithmetique que le jumeau web.</summary>
    public static int Teinte(string acteur)
    {
        var h = 0;
        foreach (var c in acteur ?? "")
        {
            h = (h * 31 + c) % 360;
        }
        return h;
    }

    /// <summary>
    /// La duree d'affichage d'une etiquette, d'apres le nombre deja a l'ecran.
    ///
    /// Elle DECROIT : plus ca reagit, plus les noms s'effacent vite, donc plus de monde est
    /// nomme et personne ne monopolise une place. Sous le plancher on rend zero : l'oeil
    /// n'attrape pas un mot en moins de quatre dixiemes de seconde, et l'afficher quand meme
    /// serait du bruit qui pretend informer.
    /// </summary>
    public static int DureeEtiquette(int dejaAffichees)
    {
        if (dejaAffichees >= PlafondEtiquettes)
        {
            return 0;
        }
        var duree = DureeHaute - (DureeHaute - DureePlancher) * (dejaAffichees / (double) PlafondEtiquettes);
        return duree < DureePlancher ? 0 : (int) Math.Round(duree);
    }

    /// <summary>
    /// La hauteur du saut, d'apres l'INTENSITE du geste (trois crans).
    ///
    /// C'est la jauge de charge qu'on voit se remplir en tenant le bouton qui se retrouve
    /// dans le saut. Pas le budget deja depense : celui qui a le plus parle sauterait le plus
    /// haut, ce qui est l'inverse du sens.
    /// </summary>
    public static int HauteurSaut(int niveau) => 6 + Math.Clamp(niveau, 1, 3) * 4;

    // ── L'etat ─────────────────────────────────────────────────────────────

    /// <summary>Pose la liste des presents. Le placement se recalcule : l'ordre trie fait loi.</summary>
    public void Poser(IReadOnlyList<string> acteurs, int total)
    {
        lock (_gate)
        {
            _acteurs = acteurs.ToArray();
            _places = Places(_acteurs);
            _total = total;
        }
    }

    /// <summary>Une reaction : la silhouette saute, l'emoji s'envole, le nom parait.</summary>
    public void Reagir(string acteur, string famille, int niveau, string nom, long maintenant)
    {
        if (string.IsNullOrEmpty(acteur) || string.IsNullOrEmpty(famille))
        {
            return;
        }
        lock (_gate)
        {
            if (!_places.TryGetValue(acteur, out var place))
            {
                // Pas de place : le spectateur n'etait pas dans le dernier releve de presence,
                // ou la foule sature. On ne dessine rien plutot que de dessiner n'importe ou.
                return;
            }

            _sauts[acteur] = new Saut(maintenant + DureeSaut, HauteurSaut(niveau));

            if (_vols.Count < PlafondVols)
            {
                _vols.Add(new Vol(famille, niveau, place.Rangee, place.Index, maintenant));
            }

            var duree = DureeEtiquette(_etiquettes.Count);
            if (duree > 0 && !string.IsNullOrWhiteSpace(nom))
            {
                // Le filet : si le plafond est atteint malgre tout, on retire la plus ANCIENNE
                // plutot que de refuser la nouvelle. Sinon la regle punirait qui arrive tard.
                if (_etiquettes.Count >= PlafondEtiquettes)
                {
                    _etiquettes.RemoveAt(0);
                }
                var texte = nom.Length > 12 ? nom[..12] : nom;
                _etiquettes.Add(new Etiquette(texte, place.Rangee, place.Index, maintenant + duree));
            }
        }
    }

    /// <summary>Retire ce qui est fini et rend de quoi dessiner une image.</summary>
    public Instantane Relever(long maintenant)
    {
        lock (_gate)
        {
            _vols.RemoveAll(v => maintenant - v.Depuis >= DureeVol);
            _etiquettes.RemoveAll(e => e.Jusqua <= maintenant);
            foreach (var mort in _sauts.Where(s => s.Value.Fin <= maintenant).Select(s => s.Key).ToArray())
            {
                _sauts.Remove(mort);
            }

            return new Instantane(
                _acteurs,
                _places,
                _total,
                _vols.ToArray(),
                _etiquettes.ToArray(),
                new Dictionary<string, Saut>(_sauts, StringComparer.Ordinal));
        }
    }

    /// <summary>Y a-t-il quelque chose a dessiner ? Sert a ne pas repeindre pour rien.</summary>
    public bool Anime
    {
        get
        {
            lock (_gate)
            {
                return _vols.Count > 0 || _etiquettes.Count > 0 || _sauts.Count > 0;
            }
        }
    }

    public void Vider()
    {
        lock (_gate)
        {
            _acteurs = Array.Empty<string>();
            _places = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            _total = 0;
            _vols.Clear();
            _etiquettes.Clear();
            _sauts.Clear();
        }
    }
}
