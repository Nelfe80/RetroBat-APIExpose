using System.IO.Compression;

namespace RetroBat.Api.Replay.Storage;

/// <summary>
/// LES REPLAYS VOYAGENT COMPRESSÉS, ET SE STOCKENT BRUTS.
///
/// Mesuré le 25 septembre 2026 sur le magasin de cette borne : un replay de RetroArch 1.22.2 se
/// compresse à 1 ou 2 % de sa taille. RetroArch écrit ses points de contrôle toutes les cinq
/// secondes sans les compresser, et ce qu'il écrit est aux deux tiers des octets nuls. Un Ms.
/// Pac-Man sous MAME pesait 37 Mo pour moins de 300 Ko d'information. (Le transit accepte
/// jusqu'à 64 Mo : `.user.ini` du site, relevé depuis les 2 Mo par défaut de PHP.)
///
/// L'IDENTITÉ NE CHANGE PAS. Le SHA-256 reste celui du fichier BRUT, celui que RetroArch lit ; le
/// magasin local garde le brut. On compresse au départ, on décompresse à l'arrivée, et c'est le
/// contenu décompressé qu'on vérifie. Sur l'amorce, un objet compressé porte le nom
/// <c>&lt;sha&gt;.replay.gz</c> : une borne restée sur une ancienne version ne le trouve pas et voit
/// le replay comme absent, sans erreur, au lieu de rejeter un contenu qu'elle ne saurait pas lire.
/// </summary>
public static class ReplayCompression
{
    /// <summary>Suffixe ajouté au nom de l'objet brut sur l'amorce.</summary>
    public const string Suffixe = ".gz";

    /// <summary>Valeur du champ <c>object_encoding</c> envoyé au transit.</summary>
    public const string Encodage = "gzip";

    /// <summary>`Replay:Share:Compress`, vrai par défaut. Faux rend l'envoi brut, sans republier.</summary>
    public static bool Active(IConfiguration config) => config.GetValue("Replay:Share:Compress", true);

    /// <summary>L'adresse de la forme compressée d'un objet, à partir de celle de sa forme brute.</summary>
    public static string UrlCompressee(string urlBrute) => urlBrute + Suffixe;

    /// <summary>Le fichier commence-t-il par l'en-tête gzip (1F 8B) ?</summary>
    public static bool EstGzip(string chemin)
    {
        try
        {
            using var f = File.OpenRead(chemin);
            Span<byte> tete = stackalloc byte[2];
            return f.Read(tete) == 2 && tete[0] == 0x1F && tete[1] == 0x8B;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Compresse <paramref name="source"/> vers <paramref name="destination"/> ; rend sa taille.</summary>
    public static async Task<long> CompresserAsync(string source, string destination, CancellationToken ct)
    {
        await using (var entree = File.OpenRead(source))
        await using (var sortie = File.Create(destination))
        await using (var gz = new GZipStream(sortie, CompressionLevel.SmallestSize))
        {
            await entree.CopyToAsync(gz, 81920, ct).ConfigureAwait(false);
        }
        return new FileInfo(destination).Length;
    }

    /// <summary>
    /// Décompresse sans jamais écrire plus de <paramref name="plafond"/> octets. null si le contenu
    /// dépasse le plafond : une archive qui gonfle au-delà de la taille annoncée est rejetée avant
    /// d'avoir rempli le disque. Lève sur une archive corrompue.
    /// </summary>
    public static async Task<long?> DecompresserPlafonneAsync(string source, string destination, long plafond, CancellationToken ct)
    {
        var tampon = new byte[81920];
        long ecrits = 0;
        await using var entree = File.OpenRead(source);
        await using var gz = new GZipStream(entree, CompressionMode.Decompress);
        await using var sortie = File.Create(destination);
        int lus;
        while ((lus = await gz.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
        {
            ecrits += lus;
            if (ecrits > plafond) return null;
            await sortie.WriteAsync(tampon.AsMemory(0, lus), ct).ConfigureAwait(false);
        }
        return ecrits;
    }
}
