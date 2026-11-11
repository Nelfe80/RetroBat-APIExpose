using System.Globalization;
using System.Text;

namespace RetroBat.Api.Speedrun;

/// <summary>Un split : le segment de l'epreuve (« scene:troncon », decide par le coeur hote) et l'image du run ou il est atteint.</summary>
public sealed record SplitDeRun(string Segment, long Image);

/// <summary>Les splits d'un run (« run » : le numero du depart pour le fichier du joueur, 0 pour un fantome).</summary>
public sealed record SplitsDuRun(int Run, int SegmentPx, IReadOnlyList<SplitDeRun> Splits);

/// <summary>
/// LES SPLITS DES FANTOMES (11/11, lot L5 du CDC Player Deck Patterns). Le coeur hote (NelfePlay-Ghosts, branche
/// rendu-splits) decoupe l'epreuve en segments de la meme facon pour le joueur et pour ses fantomes (un troncon de 512 pixels
/// de la position du personnage dans sa scene, chaque scene nouvelle aussi) et ecrit, a cote des fantomes :
///   « &lt;fantome&gt;.splits » a la fin de la course d'un fantome (une fois), que l'API garde avec le .nghost ;
///   « joueur.splits » a chaque segment atteint par le joueur, deuxieme ligne « run &lt;numero du depart&gt; ».
/// Format : « NSPLITS1 &lt;px&gt; », puis « &lt;scene 8 hexa&gt;:&lt;troncon&gt; &lt;image&gt; » par ligne. L'ecart au fantome sur un segment
/// est l'image du joueur moins celle du fantome (negatif : en avance). Pur.
/// </summary>
public static class SplitsDesFantomes
{
    public const string NomDuJoueur = "joueur.splits";

    /// <summary>Le contenu d'un fichier de splits ; null s'il n'en est pas un.</summary>
    public static SplitsDuRun? Lire(string? texte)
    {
        if (string.IsNullOrEmpty(texte)) return null;
        var lignes = texte.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lignes.Length == 0 || !lignes[0].StartsWith("NSPLITS1", StringComparison.Ordinal)) return null;
        var px = lignes[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } entete
            && int.TryParse(entete[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
        var run = 0;
        var splits = new List<SplitDeRun>();
        foreach (var ligne in lignes.Skip(1))
        {
            var morceaux = ligne.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (morceaux.Length != 2) continue;
            if (morceaux[0] == "run")
            {
                _ = int.TryParse(morceaux[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out run);
                continue;
            }
            if (morceaux[0].Contains(':') && long.TryParse(morceaux[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var image))
                splits.Add(new SplitDeRun(morceaux[0], image));
        }
        return new SplitsDuRun(run, px, splits);
    }

    /// <summary>Le fichier d'un run (sans numero : celui d'un fantome).</summary>
    public static string Ecrire(SplitsDuRun splits)
    {
        var b = new StringBuilder();
        b.Append("NSPLITS1 ").Append(splits.SegmentPx.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var s in splits.Splits) b.Append(s.Segment).Append(' ').Append(s.Image.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return b.ToString();
    }

    /// <summary>L'ecart en images au fantome sur ce segment ; null si le fantome n'y est pas passe (autre route).</summary>
    public static long? EcartEnImages(SplitDeRun joueur, SplitsDuRun fantome)
        => fantome.Splits.FirstOrDefault(s => s.Segment == joueur.Segment) is { } f ? joueur.Image - f.Image : null;

    /// <summary>Des images en millisecondes, a la cadence du reglement.</summary>
    public static long EnMillisecondes(long images, double cadence)
        => (long)Math.Round(images * 1000.0 / (cadence > 0 ? cadence : 60.0), MidpointRounding.AwayFromZero);

    /// <summary>L'identifiant du replay dans un nom de fichier de fantome (« 0-moi-rp_X.splits », « 1-rp_X.splits ») ; null sinon.</summary>
    public static string? ReplayDuNom(string nom)
    {
        var i = nom.IndexOf("rp_", StringComparison.Ordinal);
        if (i < 0) return null;
        var replay = Path.GetFileNameWithoutExtension(nom[i..]);
        return replay.Length > 3 && replay.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? replay : null;
    }
}
