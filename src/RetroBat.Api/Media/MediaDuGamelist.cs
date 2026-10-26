using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Un media d'un jeu lu dans le gamelist de la borne (<c>roms/&lt;systeme&gt;/gamelist.xml</c>), le chemin resolu depuis le
/// dossier du systeme. Le premier present de <c>balises</c> l'emporte (« fanart », puis « image »...). Les medias ne sont pas
/// livres (dossier <c>media/</c>) : chaque borne a les siens, ou rien.
/// </summary>
public static class MediaDuGamelist
{
    public static string? Resoudre(string? cheminDuJeu, params string[] balises)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cheminDuJeu))
            {
                return null;
            }

            var normalise = cheminDuJeu.Replace('\\', '/');
            var fichier = Path.GetFileName(normalise);
            var dossierDuSysteme = DossierDuSysteme(normalise);
            var gamelist = Path.Combine(dossierDuSysteme, "gamelist.xml");
            if (!File.Exists(gamelist))
            {
                return null;
            }

            var jeu = XDocument.Load(gamelist).Root?.Elements("game").FirstOrDefault(element =>
                string.Equals(Path.GetFileName(((string?)element.Element("path") ?? "").Replace('\\', '/')), fichier, StringComparison.OrdinalIgnoreCase));
            if (jeu is null)
            {
                return null;
            }

            foreach (var balise in balises)
            {
                var valeur = ((string?)jeu.Element(balise) ?? "").Trim().Replace('\\', '/');
                if (valeur.Length == 0)
                {
                    continue;
                }

                // Ne retirer QUE le prefixe « ./ » : les chemins de RetroBat remontent en « ./../../plugins/... ».
                if (valeur.StartsWith("./", StringComparison.Ordinal))
                {
                    valeur = valeur[2..];
                }

                var complet = Path.IsPathFullyQualified(valeur) ? valeur : Path.GetFullPath(Path.Combine(dossierDuSysteme, valeur));
                if (File.Exists(complet))
                {
                    return complet;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Le dossier du systeme : <c>roms/&lt;systeme&gt;</c> d'un chemin relatif ; sinon le premier dossier, en remontant depuis
    /// celui du jeu, qui porte un gamelist (un jeu range dans un sous-dossier est decrit par le gamelist du systeme).
    /// </summary>
    private static string DossierDuSysteme(string normalise)
    {
        if (normalise.StartsWith("roms/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(RetroBatPaths.RomsRoot, normalise.Split('/')[1]);
        }

        var depart = Path.GetDirectoryName(normalise.Replace('/', Path.DirectorySeparatorChar));
        for (var dossier = depart; !string.IsNullOrEmpty(dossier); dossier = Path.GetDirectoryName(dossier))
        {
            if (File.Exists(Path.Combine(dossier, "gamelist.xml")))
            {
                return dossier;
            }
        }

        return depart ?? RetroBatPaths.RomsRoot;
    }
}
