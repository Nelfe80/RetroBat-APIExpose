using System.Text;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// LES VERROUS DU DEFI QUI SE LANCE (D18, wrapper 0.343). Le wrapper les lit dans certified.txt au chargement du jeu, a cote
/// des reglages certifies, sous la forme <c>lock.&lt;n&gt;=&lt;ligne&gt;</c>. Le lancement d'un defi les pose ici et les ecrit
/// aussitot ; le service des reglages certifies, qui reecrit ce fichier a la selection et au demarrage du jeu, les y remet a
/// chaque fois (sinon il les effacerait avant que le wrapper ne les lise). Ils valent pour une ROM et un temps : un autre
/// jeu, ou un lancement rate, ne se voit rien tenir.
/// </summary>
public static class VerrousDuLancement
{
    /// <summary>Le temps laisse au lanceur et au chargement du jeu avant que les verrous poses ne vaillent plus.</summary>
    public static readonly TimeSpan Validite = TimeSpan.FromMinutes(3);

    private static readonly object Verrou = new();
    private static (string Rom, IReadOnlyList<string> Lignes, DateTime Jusqua)? _poses;

    /// <summary>Pose les verrous du defi pour cette ROM (nom de base, sans extension) ; une liste vide retire tout.</summary>
    public static void Poser(string romBasename, IReadOnlyList<string> lignes, DateTime? maintenant = null)
    {
        lock (Verrou)
            _poses = lignes.Count == 0 ? null : (romBasename, lignes, (maintenant ?? DateTime.UtcNow) + Validite);
    }

    public static void Retirer()
    {
        lock (Verrou) _poses = null;
    }

    /// <summary>Les lignes a tenir pour cette ROM, vide si rien n'est pose pour elle ou si le delai est passe.</summary>
    public static IReadOnlyList<string> Pour(string romBasename, DateTime? maintenant = null)
    {
        lock (Verrou)
        {
            if (_poses is not { } p) return [];
            if ((maintenant ?? DateTime.UtcNow) > p.Jusqua) return [];
            return string.Equals(p.Rom, romBasename, StringComparison.OrdinalIgnoreCase) ? p.Lignes : [];
        }
    }

    /// <summary>Les lignes <c>lock.&lt;n&gt;=</c> a ajouter a certified.txt. Le n sur deux chiffres garde l'ordre du reglement.</summary>
    public static void Ecrire(StringBuilder texte, IReadOnlyList<string> lignes)
    {
        // Une consigne (mem_layer=) s'ecrit telle quelle ; les verrous gardent leur numero dans l'ordre du reglement.
        var n = 0;
        foreach (var ligne in lignes)
        {
            if (ligne.StartsWith("mem_layer=", StringComparison.Ordinal)) { texte.Append(ligne).Append('\n'); continue; }
            texte.Append("lock.").Append((n++).ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append('=').Append(ligne).Append('\n');
        }
    }

    /// <summary>La consigne qui fait charger au wrapper le .MEM de la couche des challenges (ram\.defis, wrapper 0.344).</summary>
    public const string CoucheDesChallenges = "mem_layer=defis";
}
