using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Recuperer la configuration MANETTE qu'EmulationStation a calculee, telle quelle.
///
/// Pourquoi ne pas la recalculer : `%CONTROLLERSCONFIG%` est produit par ES a partir de sa propre
/// enumeration des manettes, et le refaire de notre cote perdrait le reglage du joueur — c'est
/// exactement ce qu'il ne faut pas faire (decision explicite). Or ES JOURNALISE la commande
/// complete qu'il lance :
///
///     "…\emulatorLauncher.exe" -gameinfo "…\game.xml" -p1index 0 -p1guid 0300…
///       -p1path "USB\VID_0079&amp;PID_0006\…" -p1name "Generic USB Joystick"
///       -p1nbbuttons 12 -p1nbhats 1 -p1nbaxes 5  -system fbneo -emulator libretro
///       -core fbneo -rom "E:\RetroBat\roms\…"
///
/// On y reprend donc les arguments de MANETTE mot pour mot. La configuration n'est pas
/// reconstruite : c'est la meme chaine.
///
/// On ne reprend QUE ceux-la. `-system`, `-emulator`, `-core` et `-rom` decrivent la partie
/// PRECEDENTE et n'ont aucune raison de valoir pour celle qu'on veut lancer. Et `-gameinfo`
/// pointe un fichier temporaire qu'ES reecrit puis supprime : le reutiliser serait pointer un
/// fichier mort.
/// </summary>
public static class EsLaunchArguments
{
    /// <summary>Les journaux d'ES, du plus recent au plus ancien (`es_log.txt`, puis `es_log.0.txt`…).</summary>
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
    /// Les arguments de manette du DERNIER lancement journalise, ou une chaine vide si aucun
    /// lancement n'a encore eu lieu.
    ///
    /// Une chaine vide n'est pas une panne : sur une borne qui vient de demarrer, il n'y a rien
    /// a reprendre, et `emulatorLauncher` retombera sur sa propre enumeration.
    /// </summary>
    public static string ManettesDuDernierLancement()
    {
        foreach (var journal in Journaux())
        {
            string contenu;
            try
            {
                // ES ecrit dedans en continu : on ouvre en lecture partagee, sinon on echoue
                // exactement quand la borne est vivante.
                using var flux = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var lecteur = new StreamReader(flux);
                contenu = lecteur.ReadToEnd();
            }
            catch (Exception)
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
        var ligne = DerniereLigneDeLancement(journal);
        return ligne.Length == 0 ? string.Empty : ManettesDe(ligne);
    }

    private static string DerniereLigneDeLancement(string journal)
    {
        var dernier = string.Empty;
        foreach (var ligne in journal.Split('\n'))
        {
            if (ligne.Contains("emulatorLauncher.exe", StringComparison.OrdinalIgnoreCase))
            {
                dernier = ligne;
            }
        }
        return dernier.Trim();
    }

    /// <summary>
    /// Les arguments `-p&lt;n&gt;…` d'une ligne de commande, dans leur ordre d'origine.
    ///
    /// On les reconnait a leur forme — `-p` suivi d'un chiffre — plutot qu'a une liste de noms :
    /// ES en ajoute au fil des versions (`-p1nbaxes` est apparu apres les autres), et une liste
    /// figee laisserait tomber en silence ce qu'elle ne connait pas.
    /// </summary>
    public static string ManettesDe(string commande)
    {
        // Une valeur peut etre entre guillemets et contenir des espaces : « -p1name "Generic USB
        // Joystick" ». On capture donc soit une valeur entre guillemets, soit un mot.
        var motif = new Regex(
            "(-p\\d+[a-zA-Z]+)\\s+(\"[^\"]*\"|[^\\s\"]+)",
            RegexOptions.CultureInvariant);

        var morceaux = new List<string>();
        foreach (Match m in motif.Matches(commande))
        {
            morceaux.Add(m.Groups[1].Value);
            morceaux.Add(m.Groups[2].Value);
        }
        return string.Join(' ', morceaux);
    }
}
