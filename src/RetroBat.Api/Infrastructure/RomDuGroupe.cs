using System.Xml.Linq;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>La ROM installee dont l'identite de SCORING est un rom_group : lancement depuis le site, defi de speedrun.</summary>
public static class RomDuGroupe
{
    private static readonly string[] DossiersArcade = ["fbneo", "mame", "mame64", "fba", "arcade"];

    /// <summary>
    /// La ROM installée dont l'identité de SCORING est ce rom_group, ou null.
    ///
    /// On compare des identités de scoring et non des noms : c'est la même résolution qui
    /// décide, à la fin d'une partie, sous quel groupe le score est publié. Comparer des
    /// libellés ferait diverger le jeu lancé et le classement affiché.
    /// </summary>
    public static string? Trouver(IGamelistStore gamelists, RomCanonicalResolver canonical, string system, string romGroup)
    {
        var cible = romGroup.Trim().ToLowerInvariant();
        var systeme = system.Trim().ToLowerInvariant();
        var dossiers = systeme is "arcade" ? DossiersArcade : [systeme];

        foreach (var dossier in dossiers)
        {
            // Un identifiant de système ne doit jamais devenir un chemin : seul un nom de
            // dossier simple est accepte.
            if (dossier.Length == 0 || !string.Equals(Path.GetFileName(dossier), dossier, StringComparison.Ordinal))
            {
                continue;
            }

            var racine = Path.Combine(RetroBatPaths.RomsRoot, dossier);
            var chemin = Path.Combine(racine, "gamelist.xml");
            if (!System.IO.File.Exists(chemin))
            {
                continue;
            }

            XDocument? doc;
            lock (gamelists.GetLock(chemin))
            {
                doc = gamelists.Load(chemin, LoadOptions.None);
            }
            if (doc?.Root is null)
            {
                continue;
            }

            foreach (var jeu in doc.Root.Elements("game"))
            {
                var brut = ((string?)jeu.Element("path") ?? "").Trim().Replace('\\', '/');
                var fichier = Path.GetFileName(brut);
                if (fichier.Length == 0)
                {
                    continue;
                }

                var md5 = ((string?)jeu.Element("md5") ?? "").Trim().ToLowerInvariant();
                var cheevos = ((string?)jeu.Element("cheevosHash") ?? "").Trim();
                var slug = canonical.ResolveScoreSlug(dossier, fichier, md5, cheevos);
                if (slug is null || !string.Equals(slug, cible, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var complet = Path.GetFullPath(Path.Combine(racine, fichier));
                if (System.IO.File.Exists(complet) || Directory.Exists(complet))
                {
                    return complet;
                }
            }
        }

        return null;
    }
}
