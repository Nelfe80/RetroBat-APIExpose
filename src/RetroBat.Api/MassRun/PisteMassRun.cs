using System.Text;

namespace RetroBat.Api.MassRun;

/// <summary>
/// Une piste .npiste ecrite par le coeur hote : la position d'un personnage image par image (camera, scene, sprites).
/// Format : "NPISTE1\0", crc32 du contenu (u32), couleur (4), pseudo (16), image de course de la premiere image (i32),
/// nombre d'images (u32), puis les images de 40 octets.
/// </summary>
public sealed class PisteMassRun
{
    public required string Pseudo { get; init; }
    public required int Debut { get; init; }
    public required byte[][] Images { get; init; }

    public static PisteMassRun? Lire(string chemin, uint crc)
    {
        try
        {
            var b = File.ReadAllBytes(chemin);
            if (b.Length < 40 || !b.AsSpan(0, 8).SequenceEqual("NPISTE1\0"u8) || BitConverter.ToUInt32(b, 8) != crc)
            {
                return null;
            }
            var pseudo = Encoding.UTF8.GetString(b, 16, 16).TrimEnd('\0');
            var debut = BitConverter.ToInt32(b, 32);
            var n = BitConverter.ToUInt32(b, 36);
            if (n == 0 || 40 + (long)n * MassRunCanal.TailleImageDePiste > b.Length)
            {
                return null;
            }
            var images = new byte[n][];
            for (var i = 0; i < n; i++)
            {
                images[i] = b.AsSpan(40 + i * MassRunCanal.TailleImageDePiste, MassRunCanal.TailleImageDePiste).ToArray();
            }
            return new PisteMassRun { Pseudo = pseudo, Debut = debut, Images = images };
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>L'image de la piste a une image de course donnee, ou null hors de la piste.</summary>
    public byte[]? A(long imageDeCourse)
    {
        var k = imageDeCourse - Debut;
        return k >= 0 && k < Images.Length ? Images[k] : null;
    }
}
