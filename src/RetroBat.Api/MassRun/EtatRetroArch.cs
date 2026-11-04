using System.IO.Compression;

namespace RetroBat.Api.MassRun;

/// <summary>
/// L'etat brut du coeur dans un etat de RetroArch : rzip (« #RZIPv\x01# », taille de bloc u32, taille totale u64, puis des
/// blocs zlib precedes de leur taille u32), puis conteneur RASTATE (blocs de 4 lettres, taille u32, alignes sur 8) dont le
/// bloc « MEM » porte les octets du coeur. Un etat deja brut est rendu tel quel.
/// </summary>
public static class EtatRetroArch
{
    public static byte[] Brut(byte[] fichier)
    {
        var octets = fichier;
        if (octets.Length >= 20 && octets.AsSpan(0, 8).SequenceEqual("#RZIPv\u0001#"u8))
        {
            using var sortie = new MemoryStream();
            var i = 20;
            while (i + 4 <= octets.Length)
            {
                var n = BitConverter.ToInt32(octets, i);
                if (n <= 0 || i + 4 + n > octets.Length)
                {
                    throw new InvalidDataException("bloc rzip tronque");
                }
                using (var bloc = new ZLibStream(new MemoryStream(octets, i + 4, n), CompressionMode.Decompress))
                {
                    bloc.CopyTo(sortie);
                }
                i += 4 + n;
            }
            octets = sortie.ToArray();
        }
        if (octets.Length >= 8 && octets.AsSpan(0, 8).SequenceEqual("RASTATE\u0001"u8))
        {
            var i = 8;
            while (i + 8 <= octets.Length)
            {
                var n = BitConverter.ToInt32(octets, i + 4);
                if (n < 0 || i + 8 + n > octets.Length)
                {
                    break;
                }
                if (octets.AsSpan(i, 4).SequenceEqual("MEM "u8))
                {
                    return octets.AsSpan(i + 8, n).ToArray();
                }
                i += 8 + ((n + 7) & ~7);
            }
            throw new InvalidDataException("etat RASTATE sans bloc MEM");
        }
        return octets;
    }
}
