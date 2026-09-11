using System.Text;
using System.Text.Json;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// L'identité DÉCLARÉE d'un jeu, lue dans la gamelist du référentiel.
///
/// Pourquoi elle existe. En arcade, aucune empreinte MESURÉE ne peut identifier un
/// jeu : le référentiel décrit un set via les DAT, pas via le fichier. Mesuré sur
/// 19xx — le zip donne sha1=24754f53…, le référentiel dit 813f465f…, et la gamelist
/// arcade ne porte aucun md5 (0 % de couverture, contre 100 % en megadrive où les
/// DAT No-Intro empreintent bien le fichier).
///
/// Le pont MAME contourne cela depuis toujours en LISANT le sha1 déclaré. L'intégrité
/// réelle du romset est garantie par l'émulateur, qui le vérifie contre son DAT au
/// chargement — ce que le vérifieur de scoring assume explicitement.
///
/// Deux usages, deux exigences différentes : le scoring y gagne une identité que le
/// serveur sait reconnaître, la télémétrie y gagne de résoudre vers un groupe
/// canonique au lieu de retomber sur une clé de repli « système:~nom ».
/// </summary>
public static class GamelistIdentity
{
    /// <summary>Familles de repli : le même set arcade est décrit sous ces trois noms,
    /// avec le MÊME sha1, mais la couverture diffère d'un fichier à l'autre.</summary>
    private static readonly string[] ReplisArcade = { "arcade", "mame", "fbneo" };

    /// <summary>Une gamelist pèse plusieurs dizaines de milliers de lignes : on ne la
    /// relit pas à chaque partie. La clé est (système, jeu).</summary>
    /// <summary>Meme mecanique que Cache, pour l'orientation : le referentiel arcade fait
    /// 55 000 lignes, on ne le relit pas a chaque capture.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> CacheOrientation = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Le sha1 déclaré, ou null. <paramref name="nomOuGroupe"/> est le nom affiché ou le
    /// rom_group (les deux se slugifient pareil) ; <paramref name="setOuFichier"/> est le
    /// nom du set (« 19xx »), qui est la clé la plus sûre quand on l'a.
    /// </summary>
    public static string? DeclaredSha1(string? systemId, string? nomOuGroupe, string? setOuFichier = null)
    {
        var slug = Slugifier(nomOuGroupe);
        var set = (setOuFichier ?? string.Empty).Trim();
        if (slug.Length == 0 && set.Length == 0) return null;

        var cle = (systemId ?? "") + "|" + slug + "|" + set;
        if (Cache.TryGetValue(cle, out var connu)) return connu;

        var racine = Path.Combine(AppContext.BaseDirectory, "resources", "gamelist", "systems");
        var systemes = new List<string>();
        if (!string.IsNullOrWhiteSpace(systemId)) systemes.Add(systemId.Trim());
        foreach (var repli in ReplisArcade)
        {
            if (!systemes.Contains(repli, StringComparer.OrdinalIgnoreCase)) systemes.Add(repli);
        }

        string? trouve = null;
        foreach (var systeme in systemes)
        {
            trouve = Chercher(Path.Combine(racine, systeme + "_lt.json"), slug, set, PremierSha1);
            if (trouve is not null) break;
        }

        Cache[cle] = trouve;
        return trouve;
    }

    /// <summary>
    /// L'ORIENTATION declaree du jeu (« vertical » / « horizontal »), ou null si le referentiel
    /// ne la porte pas.
    ///
    /// Elle sert a redresser une capture d'ecran. Le tampon d'un coeur est stocke NON TOURNE :
    /// un shoot vertical comme 19XX y tient en 384x224 couche sur le flanc, et c'est ce que
    /// photographie une capture « pixel perfect ». Sans cette information, l'image du record
    /// sortirait a 90 degres, score compris.
    /// </summary>
    public static string? DeclaredOrientation(string? systemId, string? nomOuGroupe, string? setOuFichier = null)
    {
        var slug = Slugifier(nomOuGroupe);
        var set = (setOuFichier ?? string.Empty).Trim();
        if (slug.Length == 0 && set.Length == 0) return null;

        var cle = (systemId ?? "") + "|" + slug + "|" + set;
        if (CacheOrientation.TryGetValue(cle, out var connu)) return connu;

        var racine = Path.Combine(AppContext.BaseDirectory, "resources", "gamelist", "systems");
        var systemes = new List<string>();
        if (!string.IsNullOrWhiteSpace(systemId)) systemes.Add(systemId.Trim());
        foreach (var repli in ReplisArcade)
        {
            if (!systemes.Contains(repli, StringComparer.OrdinalIgnoreCase)) systemes.Add(repli);
        }

        string? trouve = null;
        foreach (var systeme in systemes)
        {
            trouve = Chercher(Path.Combine(racine, systeme + "_lt.json"), slug, set, Orientation);
            if (trouve is not null) break;
        }

        CacheOrientation[cle] = trouve;
        return trouve;
    }

    /// <summary>Vrai seulement si le referentiel l'affirme. Un jeu inconnu est laisse tel quel :
    /// tourner une image au hasard serait pire que ne rien faire.</summary>
    public static bool EstVertical(string? systemId, string? nomOuGroupe, string? setOuFichier = null)
        => string.Equals(DeclaredOrientation(systemId, nomOuGroupe, setOuFichier), "vertical",
            StringComparison.OrdinalIgnoreCase);

    private static string? PremierSha1(JsonElement root)
    {
        if (root.TryGetProperty("hsh", out var hsh) && hsh.ValueKind == JsonValueKind.Array)
        {
            foreach (var entree in hsh.EnumerateArray())
            {
                if (entree.TryGetProperty("sha1", out var sha1) && sha1.GetString() is { Length: > 0 } valeur)
                    return valeur.ToLowerInvariant();
            }
        }
        return null;
    }

    private static string? Orientation(JsonElement root)
        => root.TryGetProperty("ori", out var v) && v.GetString() is { Length: > 0 } o ? o : null;

    /// <summary>
    /// Parcourt une gamelist JSONL. On accepte deux correspondances : le « grp » une fois
    /// slugifié, ou le set/id exact — ce dernier est plus sûr, mais tous les appelants ne
    /// l'ont pas sous la main. Un filtre bon marché sur le premier segment évite de parser
    /// 55 000 lignes de JSON pour rien.
    /// </summary>
    private static string? Chercher(string chemin, string slug, string set,
        Func<JsonElement, string?> extraire)
    {
        if (!File.Exists(chemin)) return null;

        var tete = set.Length > 0 ? set : slug.Split('-')[0];
        if (tete.Length == 0) return null;

        // Un balayage se DIT : ces bases font plusieurs mega-octets, et les lire pendant une partie
        // se sent. Si ca revient souvent, c'est un cache qui manque, et la trace le montrera.
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var lues = 0L;
        try
        {
            foreach (var ligne in File.ReadLines(chemin))
            {
                lues += ligne.Length + 1;
                if (ligne.Length == 0 || ligne.IndexOf(tete, StringComparison.OrdinalIgnoreCase) < 0) continue;

                using var doc = JsonDocument.Parse(ligne);
                var root = doc.RootElement;

                var correspond = false;
                if (set.Length > 0)
                {
                    correspond = Egal(root, "set", set) || Egal(root, "id", set);
                }
                if (!correspond && slug.Length > 0 && root.TryGetProperty("grp", out var grp))
                {
                    correspond = string.Equals(Slugifier(grp.GetString()), slug, StringComparison.OrdinalIgnoreCase);
                }
                if (!correspond) continue;

                IoTrace.Balayage(Journal, chemin, lues, chrono.ElapsedMilliseconds);
                return extraire(root); // le jeu est là : inutile de continuer ce fichier
            }
        }
        catch
        {
            // Une gamelist illisible ne doit jamais faire échouer une partie.
        }
        IoTrace.Balayage(Journal, chemin, lues, chrono.ElapsedMilliseconds);
        return null;
    }

    /// <summary>
    /// Le journal de cette classe statique, pose au demarrage. Sans lui, un balayage de plusieurs
    /// mega-octets resterait invisible : les compteurs Windows disent le processus, jamais le
    /// fichier ni l'appelant.
    /// </summary>
    public static ILogger? Journal { get; set; }

    private static bool Egal(JsonElement root, string propriete, string attendu)
        => root.TryGetProperty(propriete, out var v)
           && string.Equals(v.GetString(), attendu, StringComparison.OrdinalIgnoreCase);

    /// <summary>« 19XX: The War Against Destiny » et « 19xx:_the_war_against_destiny »
    /// donnent tous deux « 19xx-the-war-against-destiny ».</summary>
    public static string Slugifier(string? valeur)
    {
        if (string.IsNullOrWhiteSpace(valeur)) return "";
        var sortie = new StringBuilder(valeur.Length);
        var tiret = false;
        foreach (var c in valeur.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sortie.Append(c);
                tiret = false;
            }
            else if (!tiret && sortie.Length > 0)
            {
                sortie.Append('-');
                tiret = true;
            }
        }
        return sortie.ToString().Trim('-');
    }
}
