using System.Globalization;
using System.IO.Compression;
using RetroBat.Api.Replay.Models;

namespace RetroBat.Api.Replay.Playback;

/// <summary>Ce que deux archives ont en commun : le meme jeu, ou combien de fichiers de l'enregistrement manquent ici.</summary>
/// <param name="Meme">Les fichiers de l'une sont tous dans l'autre, avec le meme CRC.</param>
/// <param name="Differents">Les fichiers de l'enregistrement absents ici, ou presents avec un autre CRC.</param>
/// <param name="Total">Les fichiers de l'enregistrement.</param>
public sealed record ComparaisonDArchives(bool Meme, int Differents, int Total);

/// <summary>
/// LE CONTENU D'UNE ARCHIVE DE ROM (2026-10-11) : ses fichiers et leurs CRC, lus dans l'en-tete du zip, sans rien
/// decompresser. RetroArch annonce pour l'arcade l'empreinte du zip ENTIER, qui change avec l'emballage (autre pack,
/// autre compression, set fusionne avec ses clones) : le meme jeu passait pour un autre fichier, et un replay venu
/// d'une autre borne s'annoncait « pas le fichier exact ». Deux archives qui portent les memes fichiers sont le meme jeu.
/// </summary>
public static class ContenuDArchive
{
    /// <summary>Au-dela, l'archive n'est pas decrite : un manifeste de replay n'a pas a porter un pack entier.</summary>
    public const int FichiersAuPlus = 1024;

    /// <summary>
    /// Les fichiers d'une archive zip et leurs CRC, tries par nom ; null si ce n'est pas un zip lisible, s'il est
    /// vide ou s'il depasse FichiersAuPlus. Les dossiers et les fichiers vides ne comptent pas.
    /// </summary>
    public static IReadOnlyList<ReplayRomFile>? Lire(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin) || !chemin.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(chemin))
        {
            return null;
        }
        try
        {
            using var archive = ZipFile.OpenRead(chemin);
            var fichiers = archive.Entries
                .Where(e => e.Length > 0 && !e.FullName.EndsWith('/'))
                .Select(e => new ReplayRomFile(e.FullName, e.Crc32.ToString("x8", CultureInfo.InvariantCulture)))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return fichiers.Count is > 0 and <= FichiersAuPlus ? fichiers : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Le meme jeu ? Oui quand les fichiers de l'une des deux archives sont tous dans l'autre avec le meme CRC : un set
    /// fusionne porte en plus les fichiers de ses clones, un set separe ne les porte pas, et le jeu est le meme. Les CRC
    /// comptent, pas les noms : MAME renomme parfois un fichier d'une version a l'autre sans en changer le contenu.
    /// Null si l'une des deux est inconnue.
    /// </summary>
    public static ComparaisonDArchives? Comparer(IReadOnlyList<ReplayRomFile>? enregistrement, IReadOnlyList<ReplayRomFile>? ici)
    {
        if (enregistrement is not { Count: > 0 } || ici is not { Count: > 0 })
        {
            return null;
        }
        var attendus = enregistrement.Select(f => Normaliser(f.Crc32)).ToHashSet(StringComparer.Ordinal);
        var presents = ici.Select(f => Normaliser(f.Crc32)).ToHashSet(StringComparer.Ordinal);
        var communs = attendus.Count(presents.Contains);
        var meme = communs > 0 && (communs == attendus.Count || communs == presents.Count);
        return new ComparaisonDArchives(meme, attendus.Count - communs, attendus.Count);
    }

    /// <summary>Le zip de ce chemin porte-t-il le meme jeu que l'enregistrement ? Faux si l'un des deux est inconnu.</summary>
    public static bool MemeJeu(string chemin, IReadOnlyList<ReplayRomFile>? enregistrement)
        => enregistrement is { Count: > 0 } && Comparer(enregistrement, Lire(chemin)) is { Meme: true };

    private static string Normaliser(string crc) => (crc ?? string.Empty).Trim().ToLowerInvariant().PadLeft(8, '0');
}
