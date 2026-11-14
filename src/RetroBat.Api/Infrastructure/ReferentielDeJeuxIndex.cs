using System.Buffers;
using System.Text;
using System.Text.Json;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// (brief du 13/11, PERF-API-07) L'INDEX D'UN REFERENTIEL DE JEUX (<c>resources/gamelist/systems/&lt;systeme&gt;_lt.json</c>).
///
/// <see cref="GamelistIdentity"/> relisait le fichier entier a chaque cle inconnue : mesure du 13/11 sur la borne, 149
/// balayages en 3 h, <c>fbneo_lt.json</c> (24 Mo) lu 44 fois, plus de 1,6 Go relus au total. L'index est construit une
/// fois par fichier (une lecture complete, sans arbre JSON) et garde, pour chaque ligne, sa position dans le fichier sous
/// trois cles : <c>set</c>, <c>id</c> et le <c>grp</c> slugifie. Une recherche ne lit plus que les quelques lignes
/// candidates, puis leur applique EXACTEMENT le test ligne a ligne du balayage (filtre sur le texte, puis set, id ou
/// groupe) : le resultat est celui du balayage, ligne gagnante comprise.
///
/// Les cles sont des empreintes de 64 bits (8 octets par entree au lieu d'une chaine) : une collision n'ajoute qu'une
/// ligne candidate, que le test ligne a ligne ecarte. Un fichier qui contient une ligne illisible, ou un set, un id ou un
/// groupe qui n'est pas une chaine, n'est pas indexe : le balayage, qui s'y comporte a sa facon, reste la reference.
/// L'index est refait si la date ou la taille du fichier change.
/// </summary>
internal sealed class ReferentielDeJeuxIndex
{
    /// <summary>Au-dela, pas d'index : la memoire de la borne compte plus que la vitesse d'une recherche rare.</summary>
    internal const long TailleMax = 64L * 1024 * 1024;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReferentielDeJeuxIndex?> Index =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Verrous =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DateTime _ecritureUtc;
    private readonly long _taille;
    private readonly Positions _parSet = new();
    private readonly Positions _parGroupe = new();

    public int Lignes { get; private set; }

    private ReferentielDeJeuxIndex(DateTime ecritureUtc, long taille)
    {
        _ecritureUtc = ecritureUtc;
        _taille = taille;
    }

    /// <summary>
    /// L'index du fichier, construit au premier appel. Null si le fichier manque, est trop gros ou ne s'indexe pas
    /// (l'appelant balaie alors comme avant).
    /// </summary>
    public static ReferentielDeJeuxIndex? Pour(string chemin, ILogger? journal = null)
    {
        var info = new FileInfo(chemin);
        if (!info.Exists || info.Length > TailleMax) return null;
        if (Index.TryGetValue(chemin, out var connu) && Frais(connu, info)) return connu!.Utilisable ? connu : null;

        lock (Verrous.GetOrAdd(chemin, _ => new object()))
        {
            if (Index.TryGetValue(chemin, out connu) && Frais(connu, info)) return connu!.Utilisable ? connu : null;
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            ReferentielDeJeuxIndex? index;
            try
            {
                index = Construire(chemin, info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;   // pas garde : on reessaiera
            }

            // Un fichier non indexable est garde comme tel (null) jusqu'a ce qu'il change : on ne le relit pas a chaque cle.
            Index[chemin] = index ?? new ReferentielDeJeuxIndex(info.LastWriteTimeUtc, info.Length) { _inutilisable = true };
            journal?.LogInformation("Referentiel indexe : {Fichier}, {Lignes} lignes en {Ms} ms{Note}.",
                info.Name, index?.Lignes ?? 0, chrono.ElapsedMilliseconds, index is null ? " (non indexable, balayage garde)" : "");
            return index;
        }
    }

    private bool _inutilisable;

    private static bool Frais(ReferentielDeJeuxIndex? index, FileInfo info)
        => index is not null && index._ecritureUtc == info.LastWriteTimeUtc && index._taille == info.Length;

    /// <summary>Vrai si l'index est utilisable (faux : le fichier ne s'indexe pas, balayer).</summary>
    public bool Utilisable => !_inutilisable;

    /// <summary>Les positions des lignes qui peuvent correspondre, dans l'ordre du fichier.</summary>
    public List<long> Candidates(string slug, string set)
    {
        var positions = new List<long>();
        if (set.Length > 0) _parSet.Ajouter(Empreinte(set), positions);
        if (slug.Length > 0) _parGroupe.Ajouter(Empreinte(slug), positions);
        positions.Sort();
        for (var i = positions.Count - 1; i > 0; i--)
        {
            if (positions[i] == positions[i - 1]) positions.RemoveAt(i);
        }

        return positions;
    }

    /// <summary>Lit les lignes aux positions donnees, dans l'ordre (texte decode comme <see cref="File.ReadLines(string)"/>).</summary>
    public static IEnumerable<string> LireLesLignes(string chemin, IReadOnlyList<long> positions)
    {
        if (positions.Count == 0) yield break;
        using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        var tampon = new List<byte>(1024);
        var octet = new byte[4096];
        foreach (var position in positions)
        {
            flux.Seek(position, SeekOrigin.Begin);
            tampon.Clear();
            var fin = false;
            while (!fin)
            {
                var lus = flux.Read(octet, 0, octet.Length);
                if (lus <= 0) break;
                var nl = Array.IndexOf(octet, (byte)'\n', 0, lus);
                var jusque = nl >= 0 ? nl : lus;
                for (var i = 0; i < jusque; i++) tampon.Add(octet[i]);
                fin = nl >= 0;
            }

            var n = tampon.Count;
            if (n > 0 && tampon[n - 1] == (byte)'\r') n--;
            yield return Encoding.UTF8.GetString(tampon.GetRange(0, n).ToArray());
        }
    }

    private static ReferentielDeJeuxIndex? Construire(string chemin, FileInfo info)
    {
        var index = new ReferentielDeJeuxIndex(info.LastWriteTimeUtc, info.Length);
        using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
            FileOptions.SequentialScan);
        var morceau = new byte[1 << 16];
        var ligne = new byte[4096];
        var longueur = 0;
        long position = 0, debut = 0;
        var premier = true;
        int lus;
        while ((lus = flux.Read(morceau, 0, morceau.Length)) > 0)
        {
            var depart = 0;
            if (premier)
            {
                premier = false;
                if (lus >= 3 && morceau[0] == 0xEF && morceau[1] == 0xBB && morceau[2] == 0xBF)
                {
                    depart = 3;
                    debut = 3;
                }
            }

            var i = depart;
            while (i < lus)
            {
                var nl = Array.IndexOf(morceau, (byte)'\n', i, lus - i);
                var jusque = nl >= 0 ? nl : lus;
                var n = jusque - i;
                if (longueur + n > ligne.Length) Array.Resize(ref ligne, Math.Max(ligne.Length * 2, longueur + n));
                Buffer.BlockCopy(morceau, i, ligne, longueur, n);
                longueur += n;
                if (nl < 0) break;

                if (!index.IndexerLaLigne(ligne, longueur, debut)) return null;
                longueur = 0;
                i = nl + 1;
                debut = position + i;
            }

            position += lus;
        }

        if (longueur > 0 && !index.IndexerLaLigne(ligne, longueur, debut)) return null;
        return index;
    }

    /// <summary>Faux si la ligne rend le fichier non indexable.</summary>
    private bool IndexerLaLigne(byte[] octets, int longueur, long debut)
    {
        if (longueur > 0 && octets[longueur - 1] == (byte)'\r') longueur--;
        if (longueur == 0) return true;   // ligne vide : le balayage l'ignore aussi
        Lignes++;

        string? set = null, id = null, grp = null;
        try
        {
            var lecteur = new Utf8JsonReader(new ReadOnlySpan<byte>(octets, 0, longueur));
            if (!lecteur.Read() || lecteur.TokenType != JsonTokenType.StartObject) return false;
            while (lecteur.Read())
            {
                if (lecteur.TokenType == JsonTokenType.EndObject) break;
                if (lecteur.TokenType != JsonTokenType.PropertyName) return false;
                var nom = lecteur.ValueTextEquals("set"u8) ? 1 : lecteur.ValueTextEquals("id"u8) ? 2 : lecteur.ValueTextEquals("grp"u8) ? 3 : 0;
                if (!lecteur.Read()) return false;
                if (nom == 0)
                {
                    lecteur.Skip();
                    continue;
                }

                if (lecteur.TokenType == JsonTokenType.Null) continue;
                if (lecteur.TokenType != JsonTokenType.String) return false;   // le balayage leverait : on le laisse faire
                var valeur = lecteur.GetString();
                // Une propriete en double : on ne sait pas laquelle le balayage lirait, on le laisse faire.
                if (nom == 1) { if (set is not null) return false; set = valeur; }
                else if (nom == 2) { if (id is not null) return false; id = valeur; }
                else { if (grp is not null) return false; grp = valeur; }
            }

            // Le balayage lit la ligne entiere (JsonDocument) : une fin illisible doit aussi rendre le fichier non indexable.
            while (lecteur.Read()) { }
        }
        catch (JsonException)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(set)) _parSet.Noter(Empreinte(set), debut);
        if (!string.IsNullOrEmpty(id) && !string.Equals(id, set, StringComparison.OrdinalIgnoreCase)) _parSet.Noter(Empreinte(id), debut);
        var groupe = GamelistIdentity.Slugifier(grp);
        if (groupe.Length > 0) _parGroupe.Noter(Empreinte(groupe), debut);
        return true;
    }

    /// <summary>FNV-1a 64 bits sur les caracteres mis en majuscules invariantes : deux valeurs egales sans tenir compte
    /// de la casse (OrdinalIgnoreCase) ont la meme empreinte.</summary>
    internal static ulong Empreinte(string valeur)
    {
        var h = 14695981039346656037UL;
        foreach (var c in valeur)
        {
            var u = char.ToUpperInvariant(c);
            h ^= (byte)u;
            h *= 1099511628211UL;
            h ^= (byte)(u >> 8);
            h *= 1099511628211UL;
        }

        return h;
    }

    /// <summary>Empreinte vers positions : la premiere en direct, les suivantes a part (la plupart des cles n'en ont qu'une).</summary>
    private sealed class Positions
    {
        private readonly Dictionary<ulong, long> _premiere = new();
        private readonly Dictionary<ulong, List<long>> _suivantes = new();

        public void Noter(ulong cle, long position)
        {
            if (_premiere.TryAdd(cle, position)) return;
            if (_premiere[cle] == position) return;
            if (!_suivantes.TryGetValue(cle, out var liste)) _suivantes[cle] = liste = new List<long>(2);
            if (liste.Count == 0 || liste[^1] != position) liste.Add(position);
        }

        public void Ajouter(ulong cle, List<long> vers)
        {
            if (!_premiere.TryGetValue(cle, out var p)) return;
            vers.Add(p);
            if (_suivantes.TryGetValue(cle, out var liste)) vers.AddRange(liste);
        }
    }
}
