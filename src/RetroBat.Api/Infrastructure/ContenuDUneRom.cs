using System.IO.Compression;
using SharpCompress.Archives.SevenZip;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LE CONTENU D'UNE ROM, TEL QUE LE COEUR LE RECOIT : le fichier lui-meme, ou le fichier UNIQUE d'une archive .zip ou .7z
/// (RetroArch extrait l'archive et donne au coeur ce qu'elle contient ; le wrapper mesure ce tampon). Une archive a plusieurs
/// fichiers n'a pas de contenu unique : null, comme un fichier au-dela du plafond ou illisible.
/// <para>
/// LE .7Z (08/11) : lu par SharpCompress (deja la bibliotheque de l'updater), dans le processus, sans 7za.exe ni fichier
/// temporaire. Le plafond porte sur la taille declaree de l'entree, et la lecture s'arrete a cette taille : une archive qui
/// ment sur sa taille ne donne rien (voir <see cref="EmpreinteSansEnTete.Empreintes"/>).
/// </para>
/// </summary>
public sealed class ContenuDUneRom : IDisposable
{
    private readonly IDisposable? _archive;

    private ContenuDUneRom(Stream flux, long taille, uint? crc32, IDisposable? archive)
    {
        Flux = flux;
        Taille = taille;
        Crc32 = crc32;
        _archive = archive;
    }

    /// <summary>Le contenu, lu depuis son debut.</summary>
    public Stream Flux { get; }

    /// <summary>Sa taille en octets (declaree par l'archive pour une entree).</summary>
    public long Taille { get; }

    /// <summary>Le crc32 que l'archive declare pour son entree ; null pour un fichier nu.</summary>
    public uint? Crc32 { get; }

    /// <summary>Le contenu de ce fichier, ou null (absent, archive a plusieurs fichiers, au-dela du plafond, illisible).</summary>
    public static ContenuDUneRom? Ouvrir(string chemin, long plafond)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(chemin);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.Equals(info.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            var zip = ZipFile.OpenRead(info.FullName);
            try
            {
                var fichiers = zip.Entries.Where(entree => entree.Length > 0).ToList();
                if (fichiers.Count != 1 || fichiers[0].Length > plafond)
                {
                    zip.Dispose();
                    return null;
                }
                return new ContenuDUneRom(fichiers[0].Open(), fichiers[0].Length, fichiers[0].Crc32, zip);
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        if (string.Equals(info.Extension, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            var archive = SevenZipArchive.OpenArchive(info.FullName);
            try
            {
                var fichiers = archive.Entries.Where(entree => !entree.IsDirectory && entree.Size > 0).ToList();
                if (fichiers.Count != 1 || fichiers[0].Size > plafond)
                {
                    archive.Dispose();
                    return null;
                }
                var crc = fichiers[0].Crc is long c and >= 0 and <= uint.MaxValue ? (uint?)c : null;
                return new ContenuDUneRom(fichiers[0].OpenEntryStream(), fichiers[0].Size, crc == 0 ? null : crc, archive);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }

        if (info.Length > plafond) return null;
        return new ContenuDUneRom(File.OpenRead(info.FullName), info.Length, null, null);
    }

    /// <summary>Les erreurs d'une archive abimee ou d'un fichier verrouille : le contenu est alors inconnu, jamais une panne.</summary>
    public static bool EstUneLectureRatee(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException
            || ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true;

    public void Dispose()
    {
        Flux.Dispose();
        _archive?.Dispose();
    }
}
