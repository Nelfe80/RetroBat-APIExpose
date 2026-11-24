using System.Text.RegularExpressions;
using System.Xml.Linq;
using RetroBat.Api.Media;

namespace RetroBat.Api.Netplay;

/// <summary>
/// LA FICHE <c>-gameinfo</c> D'UN LANCEMENT PAR L'API. Quand ES lance un jeu, il donne a emulatorLauncher une fiche
/// <c>game.xml</c> du seul jeu (<c>-gameinfo</c>). Sans elle, emulatorLauncher parcourt la gamelist du systeme
/// (<c>GameList.FormatPath</c> -> <c>Path.Combine</c>) et PLANTE des qu'une entree porte un caractere interdit dans un chemin
/// (21/11, DEFIER chez un joueur : « Caracteres non conformes dans le chemin d'acces », 9 lancements perdus).
/// La fiche reprend l'entree de la gamelist de CE jeu (ses reglages propres restent), sinon une fiche minimale
/// (chemin et nom). Pur, sauf <see cref="Ecrire"/>.
/// </summary>
public static class FicheDeLancement
{
    private static readonly Regex Rom = new(@"(?:^|\s)-rom\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool ADejaUneFiche(string arguments)
        => Regex.IsMatch(arguments, @"(?:^|\s)-gameinfo(\s|$)", RegexOptions.IgnoreCase);

    private static readonly Regex Systeme = new(@"(?:^|\s)-system\s+""?([^""\s]+)""?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Le systeme (<c>-system</c>) des arguments du lanceur, ou null. Pur.</summary>
    public static string? SystemeDesArguments(string arguments)
    {
        var m = Systeme.Match(arguments ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? RomDesArguments(string arguments)
    {
        var m = Rom.Match(arguments);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>La fiche du jeu <paramref name="rom"/> ; <paramref name="gamelist"/> : la gamelist de son dossier, si on l'a lue.</summary>
    public static XDocument Construire(string rom, XDocument? gamelist)
    {
        var dossier = Path.GetDirectoryName(rom) ?? string.Empty;
        var complet = Normaliser(rom);
        XElement? entree = null;
        foreach (var game in gamelist?.Root?.Elements("game") ?? [])
        {
            var chemin = game.Element("path")?.Value?.Trim();
            if (!GamelistEntreesInvalides.CheminDeJeuValide(chemin) || GamelistEntreesInvalides.NomDePseudoJeu(game.Element("name")?.Value))
            {
                continue;
            }

            var absolu = chemin!.StartsWith("./", StringComparison.Ordinal) || chemin.StartsWith(".\\", StringComparison.Ordinal)
                ? Path.Combine(dossier, chemin[2..])
                : chemin;
            if (string.Equals(Normaliser(absolu), complet, StringComparison.OrdinalIgnoreCase))
            {
                entree = new XElement(game);
                break;
            }
        }

        entree ??= new XElement("game", new XElement("name", Path.GetFileNameWithoutExtension(rom)));
        entree.Element("path")?.Remove();
        CheminsEnAbsolu(entree, dossier);
        entree.AddFirst(new XElement("path", rom.Replace(Path.DirectorySeparatorChar, '/')));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("gameList", entree));
    }

    /// <summary>
    /// Les medias relatifs de l'entree (« ./images/x.png », « ./../../plugins/... ») passent en absolu, comme dans le
    /// game.xml d'ES : la fiche vit dans un dossier temporaire, et emulatorLauncher lit le bezel, l'image, la carte ou le
    /// manuel du jeu courant (BezelFiles : <c>File.Exists(CurrentGame.Bezel)</c>) sans les resoudre par rapport a la
    /// gamelist. Relecture de la 1.9.210.
    /// </summary>
    private static readonly HashSet<string> Medias = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "thumbnail", "marquee", "video", "fanart", "titleshot", "map", "cartridge", "boxart", "boxback",
        "manual", "magazine", "wheel", "mix", "bezel", "label", "extra1",
    };

    private static void CheminsEnAbsolu(XElement entree, string dossier)
    {
        if (string.IsNullOrEmpty(dossier))
        {
            return;
        }

        foreach (var element in entree.Elements().Where(e => !e.HasElements && Medias.Contains(e.Name.LocalName)))
        {
            var valeur = element.Value.Trim();
            if (!valeur.StartsWith("./", StringComparison.Ordinal) && !valeur.StartsWith(".\\", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                element.Value = Path.GetFullPath(Path.Combine(dossier, valeur[2..])).Replace(Path.DirectorySeparatorChar, '/');
            }
            catch (Exception)
            {
                // chemin illisible : laisse tel quel
            }
        }
    }

    private static string Normaliser(string chemin)
    {
        try { return Path.GetFullPath(chemin); }
        catch (Exception) { return chemin; }
    }

    /// <summary>Les arguments avec <c>-gameinfo "fiche"</c> en tete.</summary>
    public static string AjouterLaFiche(string arguments, string fiche) => "-gameinfo \"" + fiche + "\" " + arguments;

    /// <summary>
    /// Ecrit la fiche du jeu de ces arguments et rend son chemin ; null (le lancement part comme avant) si les arguments ont deja
    /// une fiche, n'ont pas de ROM, ou si l'ecriture echoue.
    /// </summary>
    public static string? Ecrire(string arguments, ILogger logger)
    {
        try
        {
            if (ADejaUneFiche(arguments) || RomDesArguments(arguments) is not { Length: > 0 } rom)
            {
                return null;
            }

            XDocument? gamelist = null;
            var fichierGamelist = Path.Combine(Path.GetDirectoryName(rom) ?? string.Empty, "gamelist.xml");
            if (File.Exists(fichierGamelist))
            {
                try
                {
                    using var flux = new FileStream(fichierGamelist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    gamelist = XDocument.Load(flux);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Fiche de lancement : gamelist illisible, fiche minimale ({Chemin}).", fichierGamelist);
                }
            }

            var dossierDesFiches = Path.Combine(Path.GetTempPath(), "nelfeplay-gameinfo");
            Directory.CreateDirectory(dossierDesFiches);
            var fiche = Path.Combine(dossierDesFiches, Guid.NewGuid().ToString("N") + ".xml");
            Construire(rom, gamelist).Save(fiche);
            return fiche;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fiche de lancement non ecrite : le lanceur lira la gamelist du systeme.");
            return null;
        }
    }

    /// <summary>Efface une fiche (lanceur refuse) ; sans erreur.</summary>
    public static void Effacer(string fiche)
    {
        try { File.Delete(fiche); } catch (Exception) { /* nettoyee au prochain lancement */ }
    }

    /// <summary>Efface les fiches de plus d'un jour (un lancement n'en garde aucune apres sa sortie, mais un plantage en laisse).</summary>
    public static void NettoyerLesVieillesFiches()
    {
        try
        {
            var dossier = Path.Combine(Path.GetTempPath(), "nelfeplay-gameinfo");
            if (!Directory.Exists(dossier)) return;
            foreach (var f in Directory.GetFiles(dossier, "*.xml").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)))
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
        catch (Exception) { /* sans importance */ }
    }
}
