using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroBat.Api.Speedrun;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// Une epreuve certifiee que porte un replay (le site la joint a la cible d'un film, <c>checks</c>, 11/11) : son defi, la
/// version de son reglement, son temps officiel en images (penalites comprises, metric_value) et, s'il est connu, la fin de
/// son film dans le replay (arrivee + 3 s, frames du replay).
/// </summary>
public sealed record EpreuveCertifiee(string ChallengeId, int Version, long Frames, long? FinFrame = null);

/// <summary>
/// LE CONTROLE DE FIDELITE DU FILM D'UNE EPREUVE (11/11, demande du user). Un speedrun ou un challenge n'a pas de score a
/// relire : on verifie que l'ARRIVEE tombe a la bonne image. Pendant le rendu (sans recalage), toutes les adresses du .MEM
/// sont lues par READ_CORE_RAM dans le meme datagramme que la frame du replay (RetroArch traite un datagramme entier entre
/// deux images : la frame et les valeurs vont ensemble). Ces lectures passent par le wrapper simule et l'evaluateur du
/// reglement (<see cref="BancDesReplays.Mesurer"/>, le banc des speedruns, prouve sur les traces image par image) : le temps
/// officiel rejoue doit etre le temps certifie.
///
/// La tolerance est exacte, pas une marge : entre deux lectures, l'evenement a pu arriver a n'importe quelle image. Si la
/// lecture d'avant le depart vu est a gD images et celle d'avant l'arrivee vue a gA images, le vrai temps est entre
/// (mesure - gA + 1) et (mesure + gD - 1). Lu a chaque image (essais du 11/11 : ecart 1 sous Genesis Plus GX en avance
/// rapide), c'est l'egalite.
/// </summary>
public sealed class ControleDEpreuve
{
    /// <summary>Un bloc lu d'un coup : adresse de depart et nombre d'octets.</summary>
    public sealed record Bloc(long Adresse, int Octets);

    /// <summary>RetroArch lit un datagramme de commandes de 2 047 octets au plus (command_network_poll).</summary>
    internal const int DatagrammeMax = 1900;

    private readonly Reglement _reglement;
    private readonly LignesDuMem _lignes;
    private readonly IReadOnlyList<(long Adresse, string Type, int Octets, long[] Index)> _lectures;
    private readonly Dictionary<(long, string), long> _dernieres = new();
    private readonly StringBuilder _trace = new();
    private readonly List<long> _releves = new();

    public IReadOnlyList<Bloc> Blocs { get; }

    /// <summary>Le datagramme d'un releve : la frame du replay puis les blocs.</summary>
    public string Datagramme { get; }

    public Reglement Reglement => _reglement;

    private ControleDEpreuve(Reglement reglement, LignesDuMem lignes, string? coeur)
    {
        _reglement = reglement;
        _lignes = lignes;
        // (11/11) chaque adresse la ou le coeur la range (adresses MAME de l'arcade sous FBNeo) ; une adresse qu'on ne sait pas
        // placer n'est pas lue, et l'evaluateur la tient pour inconnue.
        var banques = AdressesDuCoeur.FamilleFbneo(coeur) ? AdressesDuCoeur.BanquesDe(reglement.RomGroup) : [];
        _lectures = lignes.Lignes.Select(l => (l.Adresse, l.Type, LecteurDuScore.Octets(l.Type))).Distinct()
            .Select(l => (l.Adresse, l.Type, l.Item3, Index: AdressesDuCoeur.Places(coeur, l.Adresse, l.Item3, banques)))
            .Where(l => l.Index is not null)
            .Select(l => (l.Adresse, l.Type, l.Item3, l.Index!))
            .ToList();
        Blocs = Regrouper(_lectures.SelectMany(l => l.Index).Distinct().Select(i => (i, 1)));
        Datagramme = DatagrammeDe(Blocs);
    }

    /// <summary>
    /// Le controle d'une epreuve, ou la raison de ne pas pouvoir la controler (« non verifie ») : reglement absent ou d'une
    /// autre version, .MEM introuvable ou different de celui que le reglement epingle, datagramme trop gros.
    /// </summary>
    public static (ControleDEpreuve? Controle, string? Raison) Pour(EpreuveCertifiee epreuve, CatalogueDesDefis? catalogue,
        Func<Reglement, byte[]?>? lireLeMem = null, string? coeur = null)
    {
        var reglement = catalogue?.Trouver(epreuve.ChallengeId);
        if (reglement is null) return (null, "reglement " + epreuve.ChallengeId + " absent de cette borne");
        if (reglement.Version != epreuve.Version)
            return (null, "reglement " + epreuve.ChallengeId + " en version " + reglement.Version + ", epreuve jouee en " + epreuve.Version);
        byte[]? mem;
        try { mem = (lireLeMem ?? LireLeMem)(reglement); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { mem = null; }
        if (mem is not { Length: > 0 }) return (null, ".MEM du defi introuvable");
        // L'empreinte que le reglement epingle, comme a son lancement (Reglement.VerifierContre).
        if (reglement.MemSha256 is { Length: > 0 } epingle
            && !string.Equals(Convert.ToHexString(SHA256.HashData(mem)), epingle, StringComparison.OrdinalIgnoreCase))
            return (null, ".MEM de la borne different de celui du reglement");
        return Depuis(reglement, Encoding.UTF8.GetString(mem), coeur);
    }

    internal static (ControleDEpreuve? Controle, string? Raison) Depuis(Reglement reglement, string mem, string? coeur = null)
    {
        var lignes = LignesDuMem.Lire(mem);
        if (lignes.Lignes.Count == 0) return (null, ".MEM sans ligne");
        var c = new ControleDEpreuve(reglement, lignes, coeur);
        if (c._lectures.Count == 0) return (null, "aucune adresse du .MEM lisible sous ce coeur");
        return Encoding.ASCII.GetByteCount(c.Datagramme) > DatagrammeMax ? (null, "trop d'adresses a lire en une image") : (c, null);
    }

    private static byte[]? LireLeMem(Reglement r)
    {
        var fichier = r.DefinitionAuLancement();
        return !string.IsNullOrEmpty(fichier) && File.Exists(fichier) ? File.ReadAllBytes(fichier) : null;
    }

    /// <summary>
    /// Les adresses regroupees en blocs : deux lectures a moins de <paramref name="ecart"/> octets se lisent ensemble ; l'ecart
    /// grandit jusqu'a ce que le datagramme tienne. Pur.
    /// </summary>
    internal static IReadOnlyList<Bloc> Regrouper(IEnumerable<(long Adresse, int Octets)> lectures)
    {
        var triees = lectures.OrderBy(l => l.Adresse).ToList();
        foreach (var ecart in new[] { 8, 32, 128, 512 })
        {
            var blocs = new List<Bloc>();
            foreach (var (a, n) in triees)
            {
                if (blocs.Count > 0 && a <= blocs[^1].Adresse + blocs[^1].Octets + ecart)
                {
                    var b = blocs[^1];
                    blocs[^1] = b with { Octets = (int)Math.Max(b.Octets, a + n - b.Adresse) };
                }
                else blocs.Add(new Bloc(a, n));
            }
            if (Encoding.ASCII.GetByteCount(DatagrammeDe(blocs)) <= DatagrammeMax) return blocs;
        }
        return triees.Select(l => new Bloc(l.Adresse, l.Octets)).ToList();
    }

    internal static string DatagrammeDe(IReadOnlyList<Bloc> blocs)
        => "GET_CONFIG_PARAM active_replay\n" + string.Concat(blocs.Select(b =>
            "READ_CORE_RAM " + b.Adresse.ToString("x", CultureInfo.InvariantCulture) + " " + b.Octets.ToString(CultureInfo.InvariantCulture) + "\n"));

    /// <summary>
    /// Un releve : la frame du replay et les octets de chaque bloc (par adresse de depart ; un bloc illisible manque). Les
    /// valeurs qui ont change s'ecrivent dans la trace, au format de la sonde libretro (<see cref="TraceDeReplay"/>).
    /// </summary>
    public void Noter(long frame, IReadOnlyDictionary<long, IReadOnlyList<int>> octetsParBloc)
    {
        if (_releves.Count > 0 && frame <= _releves[^1]) return;
        _releves.Add(frame);
        foreach (var (adresse, type, n, index) in _lectures)
        {
            // Les octets de l'adresse, chacun la ou il est (mots inverses de FBNeo compris), dans l'ordre de l'adresse.
            var dans = new List<int>(n);
            foreach (var i in index)
            {
                var bloc = Blocs.LastOrDefault(b => b.Adresse <= i && i < b.Adresse + b.Octets);
                if (bloc is null || !octetsParBloc.TryGetValue(bloc.Adresse, out var octets) || i - bloc.Adresse >= octets.Count) break;
                dans.Add(octets[(int)(i - bloc.Adresse)]);
            }
            if (dans.Count != n) continue;
            var valeur = Valeur(type, dans, 0, n);
            if (_dernieres.TryGetValue((adresse, type), out var avant) && avant == valeur) continue;
            _dernieres[(adresse, type)] = valeur;
            _trace.Append(frame.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Hex.Ecrire(adresse)).Append(' ')
                .Append(type).Append(' ').Append(valeur.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    /// <summary>La valeur d'une lecture, dans l'ordre d'octets de son type (le wrapper lit le bloc du coeur tel quel). Pur.</summary>
    internal static long Valeur(string type, IReadOnlyList<int> octets, int debut, int n)
    {
        long v = 0;
        var grand = type.EndsWith("be", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            var o = grand ? octets[debut + i] : octets[debut + n - 1 - i];
            v = (v << 8) | (uint)(o & 0xFF);
        }
        return v;
    }

    /// <summary>Le nombre de releves pris.</summary>
    public int Releves => _releves.Count;

    /// <summary>La trace au format de la sonde (pour le journal et les essais).</summary>
    public string Trace => "# images " + (_releves.Count > 0 ? _releves[^1] : 0).ToString(CultureInfo.InvariantCulture) + "\n" + _trace;

    /// <summary>Juge l'epreuve sur les releves pris. <paramref name="debut"/> et <paramref name="fin"/> : le film.</summary>
    public ControleDeFidelite.Verdict Juger(EpreuveCertifiee epreuve, long debut, long fin)
    {
        var passages = BancDesReplays.Mesurer(TraceDeReplay.Lire(Trace.Split('\n')), _lignes,
            new[] { (_reglement.ChallengeId, _reglement) });
        return JugerLesPassages(epreuve, passages, _releves, debut, fin);
    }

    /// <summary>
    /// Le verdict sur les passages mesures. Le passage retenu : celui dont le depart tombe dans le film, le plus pres de
    /// 3 s apres son debut (les bornes d'une epreuve : 3 s avant le depart). Pur.
    /// </summary>
    internal static ControleDeFidelite.Verdict JugerLesPassages(EpreuveCertifiee epreuve, IReadOnlyList<PassageMesure> passages,
        IReadOnlyList<long> releves, long debut, long fin)
    {
        if (releves.Count == 0) return new(ControleDeFidelite.NonVerifie, "memoire illisible pendant le rendu");
        var dense = EcartMax(releves, debut, fin) <= 2;
        var visee = debut + RetroBat.Api.Scoring.BornesVideo.MargeDeDebut;
        var p = passages.Where(x => x.Depart >= debut && x.Depart <= fin).OrderBy(x => Math.Abs(x.Depart - visee)).FirstOrDefault();
        if (p is null)
            return dense
                ? new(ControleDeFidelite.Desync, "aucun depart de " + epreuve.ChallengeId + " entre les frames " + debut + " et " + fin)
                : new(ControleDeFidelite.NonVerifie, "aucun depart vu (lectures espacees de " + EcartMax(releves, debut, fin) + " images)");
        var r = p.Resultat;
        switch (r.Etat)
        {
            case EtatTentative.Finie when r.FramesOfficielles is long mesure:
            {
                var officiel = r.ChronoOfficiel == Reglement.ChronoIndependant ? r.Independant : r.Cale;
                var gD = officiel?.Depart is long d ? EcartAvant(releves, d) : 1;
                var gA = officiel?.Arrivee is long a ? EcartAvant(releves, a) : 1;
                var bas = mesure - (gA - 1);
                var haut = mesure + (gD - 1);
                var ou = " (depart a la frame " + officiel?.Depart + ", arrivee a la frame " + officiel?.Arrivee + ")";
                if (epreuve.Frames >= bas && epreuve.Frames <= haut)
                    return new(ControleDeFidelite.Fidele, "arrivee a la bonne image : " + mesure + " images rejouees pour "
                        + epreuve.Frames + " certifiees" + (bas == haut ? "" : ", a " + (haut - bas) + " image(s) pres") + ou);
                return new(ControleDeFidelite.Desync, mesure + " images rejouees pour " + epreuve.Frames + " certifiees" + ou);
            }
            case EtatTentative.Ratee:
                return new(ControleDeFidelite.Desync, "echec rejoue (" + r.Cause + ") a la frame " + r.FrameDeFin);
            case EtatTentative.Annulee when r.Cause is not ("sensor_muted" or "pause"):
                return new(ControleDeFidelite.Desync, "tentative annulee rejouee (" + r.Cause + ") a la frame " + r.FrameDeFin);
            case EtatTentative.Annulee:
                return new(ControleDeFidelite.NonVerifie, "mesure coupee (" + r.Cause + ")");
            default:
                return dense && releves[^1] >= fin
                    ? new(ControleDeFidelite.Desync, "pas d'arrivee rejouee avant la frame " + releves[^1])
                    : new(ControleDeFidelite.NonVerifie, "arrivee non vue (dernier releve a la frame " + releves[^1] + ")");
        }
    }

    /// <summary>L'ecart entre la lecture de la frame <paramref name="f"/> (ou la premiere apres) et celle d'avant. Pur.</summary>
    internal static long EcartAvant(IReadOnlyList<long> releves, long f)
    {
        for (var i = 1; i < releves.Count; i++)
            if (releves[i] >= f) return Math.Max(1, releves[i] - releves[i - 1]);
        return 1;
    }

    /// <summary>Le plus grand ecart entre deux releves dans [debut, fin]. Pur.</summary>
    internal static long EcartMax(IReadOnlyList<long> releves, long debut, long fin)
    {
        long max = 0;
        for (var i = 1; i < releves.Count; i++)
            if (releves[i] >= debut && releves[i - 1] <= fin) max = Math.Max(max, releves[i] - releves[i - 1]);
        return max;
    }
}
