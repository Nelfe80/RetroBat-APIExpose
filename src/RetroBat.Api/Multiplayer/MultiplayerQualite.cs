using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE RELEVE D'UNE PARTIE EN LIGNE (31/10/2026) : le ping vers le relais ne dit pas la latence entre joueurs, et le journal de
/// RetroArch, seul a la connaitre, est ecrase au lancement suivant. A la fin de chaque partie : le vrai ping, les lignes du
/// netplay dans le journal de l'API (donc dans les rapports de support), et une copie du journal de RetroArch.
/// </summary>
internal static partial class MultiplayerQualite
{
    /// <summary>Les copies gardees du journal de RetroArch : de quoi remonter une soiree, pas plus.</summary>
    internal const int CopiesGardees = 6;

    /// <summary>Les lignes du netplay recopiees dans le journal de l'API, les dernieres.</summary>
    internal const int LignesGardees = 20;

    private const int LongueurMax = 160;

    /// <summary>« (ping: N ms) » est ecrit en dur par RetroArch a l'arrivee d'un joueur, dans toutes les langues.</summary>
    [GeneratedRegex(@"\(ping: (\d+) ms\)")]
    private static partial Regex Ping();

    /// <summary>Le pire ping vu dans le journal de la partie, ou null : l'hote en a un par invite.</summary>
    internal static int? PingDe(IEnumerable<string> lignes)
    {
        int? pire = null;
        foreach (var ligne in lignes)
        {
            foreach (Match m in Ping().Matches(ligne))
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && (pire is null || ms > pire))
                {
                    pire = ms;
                }
            }
        }

        return pire;
    }

    /// <summary>Les dernieres lignes du netplay de ce journal, sans les lignes vides ni le bruit de fin de ligne.</summary>
    internal static IReadOnlyList<string> LignesDuNetplay(IEnumerable<string> lignes)
        => lignes.Where(l => l.Contains("[Netplay]", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .Select(l => l.Length <= LongueurMax ? l : l[..LongueurMax])
            .TakeLast(LignesGardees)
            .ToList();

    /// <summary>Le journal en cours d'ecriture, lu sans gener RetroArch ; vide s'il n'existe pas.</summary>
    internal static IReadOnlyList<string> Lire(string journal)
    {
        try
        {
            using var flux = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lecteur = new StreamReader(flux);
            return lecteur.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Copie le journal sous ce nom et ne garde que les <see cref="CopiesGardees"/> plus recentes copies.</summary>
    internal static void Garder(string journal, string dossier, string nom)
    {
        if (!File.Exists(journal))
        {
            return;
        }

        Directory.CreateDirectory(dossier);
        using (var source = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var cible = new FileStream(Path.Combine(dossier, nom), FileMode.Create, FileAccess.Write))
        {
            source.CopyTo(cible);
        }

        foreach (var ancienne in new DirectoryInfo(dossier).GetFiles("*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(CopiesGardees))
        {
            ancienne.Delete();
        }
    }
}
