using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Relire ce qu'EmulationStation a decide, au lieu de le recalculer.
///
/// ES resout deux choses qu'on ne veut refaire ni l'une ni l'autre :
///
/// La configuration MANETTE. `%CONTROLLERSCONFIG%` est produit a partir de sa propre enumeration,
/// et la rebatir perdrait le reglage du joueur. Or ES JOURNALISE la commande complete :
///
///     "…\emulatorLauncher.exe" -gameinfo "…\game.xml" -p1index 0 -p1guid 0300…
///       -p1path "USB\VID_0079&amp;PID_0006\…" -p1name "Generic USB Joystick"
///       -p1nbbuttons 12 -p1nbhats 1 -p1nbaxes 5  -system fbneo -emulator libretro
///       -core fbneo -rom "E:\RetroBat\roms\…"
///
/// L'EMULATEUR et le COEUR d'un jeu — defaut du systeme, plus les surcharges par jeu. Le refaire
/// dupliquerait la logique d'ES et perdrait ses surcharges. La meme ligne les porte.
///
/// Pour un jeu deja lance sur cette borne, la reponse est donc deja dans le journal : rien a
/// relancer, rien a deviner.
/// </summary>
public static class EsLaunchArguments
{
    /// <summary>Ce qu'il faut pour relancer un jeu comme ES l'aurait fait.</summary>
    public sealed record Resolution(string Systeme, string Emulateur, string Coeur, string Manettes);

    /// <summary>Les journaux d'ES, du plus recent au plus ancien.</summary>
    private static IEnumerable<string> Journaux()
    {
        var dossier = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", ".emulationstation");
        if (!Directory.Exists(dossier))
        {
            yield break;
        }
        var principal = Path.Combine(dossier, "es_log.txt");
        if (File.Exists(principal))
        {
            yield return principal;
        }
        for (var i = 0; i <= 3; i++)
        {
            var rotation = Path.Combine(dossier, $"es_log.{i}.txt");
            if (File.Exists(rotation))
            {
                yield return rotation;
            }
        }
    }

    /// <summary>
    /// Ce qu'ES a resolu pour ce jeu la derniere fois qu'il l'a lance, ou null s'il ne l'a
    /// jamais lance ici.
    ///
    /// Null se DIT a l'utilisateur — « ce jeu n'a jamais ete lance sur cette borne, lance-le une
    /// fois » — plutot que de se deviner.
    /// </summary>
    public static Resolution? PourRom(string cheminRom)
    {
        var fichier = NomDeFichier(cheminRom);
        if (fichier.Length == 0)
        {
            return null;
        }

        foreach (var journal in Journaux())
        {
            var contenu = Lire(journal);
            if (contenu is null)
            {
                continue;
            }

            // Dans un journal donne, la DERNIERE occurrence gagne ; les journaux sont parcourus
            // du plus recent au plus ancien, donc le premier qui repond fait foi.
            Resolution? trouvee = null;
            foreach (var ligne in Lancements(contenu))
            {
                if (string.Equals(NomDeFichier(Valeur(ligne, "rom")), fichier, StringComparison.OrdinalIgnoreCase))
                {
                    trouvee = new Resolution(
                        Valeur(ligne, "system"),
                        Valeur(ligne, "emulator"),
                        Valeur(ligne, "core"),
                        ManettesDe(ligne));
                }
            }
            if (trouvee is not null)
            {
                return trouvee;
            }
        }
        return null;
    }

    /// <summary>
    /// Les arguments de manette du DERNIER lancement journalise, quel que soit le jeu.
    ///
    /// Une chaine vide n'est pas une panne : sur une borne qui vient de demarrer il n'y a rien a
    /// reprendre, et `emulatorLauncher` retombera sur sa propre enumeration.
    /// </summary>
    public static string ManettesDuDernierLancement()
    {
        foreach (var journal in Journaux())
        {
            var contenu = Lire(journal);
            if (contenu is null)
            {
                continue;
            }
            var manettes = DernieresManettes(contenu);
            if (manettes.Length > 0)
            {
                return manettes;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Extrait les arguments de manette de la derniere ligne de lancement d'un journal.
    /// Publique pour etre verifiable sur une capture reelle, sans borne.
    /// </summary>
    public static string DernieresManettes(string journal)
    {
        var dernier = string.Empty;
        foreach (var ligne in Lancements(journal))
        {
            dernier = ligne;
        }
        return dernier.Length == 0 ? string.Empty : ManettesDe(dernier);
    }

    /// <summary>
    /// Les arguments `-p&lt;n&gt;…` d'une ligne de commande, dans leur ordre d'origine.
    ///
    /// Reconnus a leur FORME — `-p` suivi d'un chiffre — plutot qu'a une liste de noms : ES en
    /// ajoute au fil des versions, et une liste figee laisserait tomber en silence ce qu'elle ne
    /// connait pas.
    /// </summary>
    public static string ManettesDe(string commande)
    {
        var morceaux = new List<string>();
        foreach (Match m in Manette.Matches(commande))
        {
            morceaux.Add(m.Groups[1].Value);
            morceaux.Add(m.Groups[2].Value);
        }
        return string.Join(' ', morceaux);
    }

    /// <summary>La valeur d'un argument, guillemets retires.</summary>
    public static string Valeur(string commande, string nom)
    {
        var m = Regex.Match(commande, "-" + Regex.Escape(nom) + Argument, RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value.Trim('"') : string.Empty;
    }

    private static string NomDeFichier(string chemin)
    {
        // Les journaux melangent les deux separateurs (« E:/RetroBat/… » et « …\roms\… ») :
        // on normalise avant de comparer, sinon un chemin sur deux ne matche pas.
        return Path.GetFileName(chemin.Replace('/', '\\').Replace('\\', Path.DirectorySeparatorChar));
    }

    private static IEnumerable<string> Lancements(string journal)
    {
        foreach (var ligne in journal.Split('\n'))
        {
            if (ligne.Contains("emulatorLauncher.exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return ligne.Trim();
            }
        }
    }

    private static string? Lire(string chemin)
    {
        try
        {
            // ES ecrit dedans en continu : lecture PARTAGEE, sinon on echoue exactement quand la
            // borne est vivante.
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var lecteur = new StreamReader(flux);
            return lecteur.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Une valeur entre guillemets (elle peut contenir des espaces) ou un mot.</summary>
    private const string Argument = "\\s+(\"[^\"]*\"|[^\\s\"]+)";

    private static readonly Regex Manette = new(
        "(-p\\d+[a-zA-Z]+)" + Argument,
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
