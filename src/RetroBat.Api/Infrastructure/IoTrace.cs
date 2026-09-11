using System.Diagnostics;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Le PROCES-VERBAL des gros balayages de fichiers.
///
/// Pourquoi : pendant une partie, mesure a la borne, l'API lisait 1,4 Mo par seconde en moyenne avec
/// des pointes a 5,5 Mo par seconde, par blocs de 4 Ko, toutes les dix secondes environ. Un disque
/// occupe ainsi pendant qu'un jeu tourne se sent, et rien ne le journalisait : les compteurs Windows
/// disent le processus, jamais le fichier ni l'appelant.
///
/// Ici on ne journalise que ce qui COUTE (un seuil en octets), avec le fichier, la duree et la pile
/// d'appel utile. Tout est a `Information` mais rare par construction : un balayage d'une base
/// consolidee ne doit pas arriver plusieurs fois par minute, et si ca arrive, c'est precisement ce
/// qu'on cherche a voir.
/// </summary>
public static class IoTrace
{
    /// <summary>En dessous, ce n'est pas un balayage : on ne dit rien.</summary>
    private const long SeuilOctets = 512 * 1024;

    /// <summary>Les cadres a ignorer dans la pile : ils ne designent pas l'appelant utile.</summary>
    private static readonly string[] Bruit =
    {
        "IoTrace", "System.", "Microsoft.", "at Microsoft", "MoveNext", "ExecutionContext", "ThreadPool",
        "TaskAwaiter", "AsyncMethodBuilder",
    };

    public static bool Actif { get; set; } = true;

    /// <summary>
    /// Dit qu'un fichier vient d'etre parcouru. `octets` est la taille lue (ou celle du fichier),
    /// `ms` la duree. L'appelant utile est retrouve dans la pile, ce qui evite de passer un libelle
    /// a chaque point d'appel.
    /// </summary>
    public static void Balayage(ILogger? logger, string chemin, long octets, long ms)
    {
        if (!Actif || logger is null || octets < SeuilOctets)
        {
            return;
        }

        logger.LogInformation(
            "IO BALAYAGE {Fichier} : {Mo:N1} Mo en {Ms} ms, appele par {Appelant}",
            Path.GetFileName(chemin), octets / 1048576.0, ms, Appelant());
    }

    /// <summary>Les trois premiers cadres qui ne sont ni le framework ni ce fichier.</summary>
    private static string Appelant()
    {
        try
        {
            var cadres = new StackTrace(skipFrames: 2, fNeedFileInfo: false).GetFrames();
            var utiles = cadres
                .Select(c => c.GetMethod())
                .Where(m => m is not null)
                .Select(m => (m!.DeclaringType?.Name ?? "?") + "." + m.Name)
                .Where(nom => !Bruit.Any(b => nom.Contains(b, StringComparison.Ordinal)))
                .Take(3)
                .ToArray();
            return utiles.Length == 0 ? "inconnu" : string.Join(" <- ", utiles);
        }
        catch
        {
            return "inconnu";
        }
    }
}
