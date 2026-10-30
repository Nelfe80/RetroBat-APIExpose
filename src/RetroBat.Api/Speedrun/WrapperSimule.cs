namespace RetroBat.Api.Speedrun;

/// <summary>
/// CE QUE LE WRAPPER AURAIT EMIS, a partir des valeurs lues image par image (banc des speedruns, lot S2).
///
/// La lecture d'un replay sur la borne passe par le vrai coeur : aucun evenement n'en sort. La sonde libretro
/// rejoue le fichier hors ecran et ecrit, a chaque image, les adresses du .MEM qui ont change. Cette classe en
/// tire les memes evenements que le wrapper, regle pour regle (ProcessWatchValue de wrapper.cpp) :
///   - une lecture par adresse et par type ; les lignes ne sont evaluees que quand la valeur brute change ;
///   - la premiere lecture initialise sans rien emettre (le prechauffage de 120 images du wrapper) ;
///   - eq et bit_true emettent a l'entree dans la valeur, change a tout changement (ou vers sa valeur),
///     increase et decrease au sens du changement ;
///   - l'auto-mute : huit emissions de suite a 20 images ou moins d'ecart coupent la ligne pour la session,
///     sauf une ligne de score ou de CREDITS.
/// Les lignes no_log et no_survey n'emettent jamais : elles sont ignorees.
/// </summary>
public sealed class WrapperSimule
{
    private const int EcartDeBoucle = 20;
    private const int EmissionsAvantCoupure = 8;

    private sealed class EtatDeLigne
    {
        public required LigneDuMem Ligne { get; init; }
        public long Derniere;
        public bool Initialisee;
        public long? DerniereEvaluation;
        public long ImagesDepuisChangement;
        public int EcartsReguliers;
        public bool Coupee;
    }

    private readonly Dictionary<(long Adresse, string Type), List<EtatDeLigne>> _groupes = new();
    private readonly Dictionary<(long Adresse, string Type), long> _brutes = new();

    public WrapperSimule(LignesDuMem lignes)
    {
        foreach (var l in lignes.Lignes.Where(l => !l.Muette))
        {
            var cle = (l.Adresse, l.Type);
            if (!_groupes.TryGetValue(cle, out var groupe)) _groupes[cle] = groupe = new List<EtatDeLigne>();
            groupe.Add(new EtatDeLigne { Ligne = l });
        }
    }

    /// <summary>Les lignes coupees par l'auto-mute, au moment ou elles l'ont ete (wrapper.watch.muted).</summary>
    public List<(long Frame, long Adresse, string Action)> Coupures { get; } = new();

    /// <summary>La derniere valeur brute lue a cette adresse (u8 de preference), ou null si jamais lue.</summary>
    public int? Valeur(long adresse)
    {
        if (_brutes.TryGetValue((adresse, "u8"), out var u8)) return (int)u8;
        foreach (var ((a, _), v) in _brutes) if (a == adresse) return (int)v;
        return null;
    }

    /// <summary>
    /// Une valeur brute lue apres l'image <paramref name="frame"/>. Rend les evenements emis, dans l'ordre des
    /// lignes du .MEM.
    /// </summary>
    public IReadOnlyList<Signal> Lire(long frame, long adresse, string type, long brute)
    {
        var cle = (adresse, type.ToLowerInvariant());
        var premiere = !_brutes.TryGetValue(cle, out var precedente);
        if (!premiere && precedente == brute) return Array.Empty<Signal>();
        _brutes[cle] = brute;
        if (!_groupes.TryGetValue(cle, out var groupe)) return Array.Empty<Signal>();

        var emis = new List<Signal>();
        foreach (var e in groupe)
        {
            if (Evaluer(e, frame, brute, prechauffage: premiere) is { } valeur)
                emis.Add(new Signal(frame, e.Ligne.Action, adresse, (int)valeur));
        }

        return emis;
    }

    private long? Evaluer(EtatDeLigne e, long frame, long brute, bool prechauffage)
    {
        var l = e.Ligne;
        var v = l.Masque is { } m ? brute & m : brute;
        if (prechauffage)
        {
            e.Derniere = v;
            e.Initialisee = true;
            e.DerniereEvaluation = frame;
            return null;
        }

        var ecoulees = e.DerniereEvaluation is { } d ? frame - d : 1;
        e.DerniereEvaluation = frame;
        var attendue = l.Valeur ?? 0;
        var masque = l.Masque ?? 0xFFFFFFFF;
        var declenche = l.Condition switch
        {
            "eq" or "equal" => v == attendue && (v != e.Derniere || !e.Initialisee),
            "neq" or "not_equal" => v != attendue && (v != e.Derniere || !e.Initialisee),
            "change" => e.Initialisee && v != e.Derniere && (attendue == 0 || v == attendue),
            "increase" => e.Initialisee && v > e.Derniere,
            "decrease" => e.Initialisee && v < e.Derniere,
            "bit_true" => (v & masque) == masque && ((e.Derniere & masque) != masque || !e.Initialisee),
            "bit_false" => (v & masque) == 0 && ((e.Derniere & masque) != 0 || !e.Initialisee),
            _ => false,
        };

        e.ImagesDepuisChangement += ecoulees;
        if (e.Coupee)
        {
            declenche = false;
        }
        else if (declenche && !l.Score && !l.Action.Equals("CREDITS", StringComparison.OrdinalIgnoreCase))
        {
            if (e.ImagesDepuisChangement <= EcartDeBoucle)
            {
                if (++e.EcartsReguliers >= EmissionsAvantCoupure)
                {
                    e.Coupee = true;
                    declenche = false;
                    Coupures.Add((frame, l.Adresse, l.Action));
                }
            }
            else
            {
                e.EcartsReguliers = 0;
            }

            e.ImagesDepuisChangement = 0;
        }

        e.Derniere = v;
        e.Initialisee = true;
        return declenche ? v : null;
    }
}
