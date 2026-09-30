using System.Text;
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

        // La DERNIERE occurrence gagne : on lit chaque journal depuis la fin, et les journaux du
        // plus recent au plus ancien, donc la premiere ligne qui repond fait foi.
        foreach (var journal in Journaux())
        {
            foreach (var ligne in LancementsDepuisLaFin(journal))
            {
                if (string.Equals(NomDeFichier(Valeur(ligne, "rom")), fichier, StringComparison.OrdinalIgnoreCase))
                {
                    return new Resolution(
                        Valeur(ligne, "system"),
                        Valeur(ligne, "emulator"),
                        Valeur(ligne, "core"),
                        ManettesDe(ligne));
                }
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
            // Le dernier lancement du journal, et lui seul : un journal dont le dernier lancement
            // n'a pas de manette passe la main au suivant, comme avant.
            var dernier = LancementsDepuisLaFin(journal).FirstOrDefault();
            var manettes = dernier is null ? string.Empty : ManettesDe(dernier);
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

    /// <summary>
    /// Les lignes de lancement d'un journal, de la DERNIERE a la premiere, lues par la fin.
    ///
    /// Le journal d'ES grossit sans fin (23 Mo sur une borne le 2026-09-30). Chaque lancement
    /// direct le lisait en entier et le decoupait en lignes pour n'en garder que la derniere :
    /// une demi-seconde et des dizaines de Mo a chaque partie, de plus en plus avec le temps. On
    /// lit par blocs depuis la fin, et l'appelant s'arrete a la premiere ligne qui lui repond.
    /// Un saut de ligne est un octet 0x0A en UTF-8, jamais un morceau d'un autre caractere : on
    /// peut couper le fichier en blocs d'octets sans casser une ligne.
    /// </summary>
    internal static IEnumerable<string> LancementsDepuisLaFin(string chemin, int bloc = 64 * 1024)
    {
        using var flux = Ouvrir(chemin);
        if (flux is null)
        {
            yield break;
        }

        var position = flux.Length;
        var reste = Array.Empty<byte>();   // le debut d'une ligne, coupe par le bloc precedent
        var tampon = new byte[bloc];
        while (position > 0)
        {
            var taille = (int)Math.Min(bloc, position);
            position -= taille;
            if (!LireBloc(flux, position, tampon, taille))
            {
                yield break;   // le journal a tourne pendant la lecture : on s'en tient a ce qu'on a lu
            }

            var morceau = new byte[taille + reste.Length];
            Buffer.BlockCopy(tampon, 0, morceau, 0, taille);
            Buffer.BlockCopy(reste, 0, morceau, taille, reste.Length);
            var fin = morceau.Length;
            for (var i = morceau.Length - 1; i >= 0; i--)
            {
                if (morceau[i] != (byte)'\n')
                {
                    continue;
                }
                var ligne = Encoding.UTF8.GetString(morceau, i + 1, fin - i - 1);
                if (EstUnLancement(ligne))
                {
                    yield return ligne.Trim();
                }
                fin = i;
            }
            reste = morceau[..fin];
        }

        if (reste.Length > 0)
        {
            var premiere = Encoding.UTF8.GetString(reste);
            if (EstUnLancement(premiere))
            {
                yield return premiere.Trim();
            }
        }
    }

    private static bool EstUnLancement(string ligne)
        => ligne.Contains("emulatorLauncher.exe", StringComparison.OrdinalIgnoreCase);

    private static FileStream? Ouvrir(string chemin)
    {
        try
        {
            // ES ecrit dedans en continu : lecture PARTAGEE, sinon on echoue exactement quand la
            // borne est vivante.
            return new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool LireBloc(FileStream flux, long position, byte[] tampon, int taille)
    {
        try
        {
            flux.Position = position;
            flux.ReadExactly(tampon, 0, taille);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Une valeur entre guillemets (elle peut contenir des espaces) ou un mot.</summary>
    private const string Argument = "\\s+(\"[^\"]*\"|[^\\s\"]+)";

    private static readonly Regex Manette = new(
        "(-p\\d+[a-zA-Z]+)" + Argument,
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
