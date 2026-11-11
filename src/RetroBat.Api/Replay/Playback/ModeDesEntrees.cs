using System.Buffers.Binary;
using System.IO.Compression;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// COMMENT LE COEUR LISAIT LA MANETTE PENDANT L'ENREGISTREMENT D'UN REPLAY (11/11). Un replay BSV garde les reponses aux
/// lectures de manette du coeur. Un coeur qui croit le frontal capable de masque lit tout d'un coup (id 256, « masque ») ;
/// sinon, touche par touche (ids 0 a 15, « touches »). RetroArch offre toujours le masque a la relecture : un replay
/// enregistre touche par touche y perd ses entrees (Streets of Rage 2 du 09/11 : de 300 a 46 000 octets d'ecart a chaque
/// point de controle ; masque refuse, 0 ecart). Ce mode se lit dans les premieres images du fichier.
/// </summary>
public static class ModeDesEntrees
{
    public enum Mode { Inconnu, Masque, Touches }

    private const uint Signature = 0x42535632;
    private const int ImagesLues = 600;
    private const ushort IdDuMasque = 256;
    private const byte Manette = 1;

    /// <summary>Le mode d'un objet du magasin (gzip ou brut) ; Inconnu s'il ne se lit pas.</summary>
    public static Mode Lire(string? chemin)
    {
        if (string.IsNullOrEmpty(chemin)) return Mode.Inconnu;
        try
        {
            using var fichier = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var b0 = fichier.ReadByte();
            var b1 = fichier.ReadByte();
            fichier.Position = 0;
            using Stream flux = b0 == 0x1F && b1 == 0x8B ? new GZipStream(fichier, CompressionMode.Decompress) : fichier;
            return Lire(flux);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Mode.Inconnu;
        }
    }

    /// <summary>Le mode d'un flux BSV (lecture en avant seulement). Pur sur son flux.</summary>
    public static Mode Lire(Stream flux)
    {
        var tete = new byte[40];
        if (!Exact(flux, tete.AsSpan(0, 8)) || BinaryPrimitives.ReadUInt32LittleEndian(tete) != Signature) return Mode.Inconnu;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(tete.AsSpan(4));
        switch (version)
        {
            case 1:
                if (!Exact(flux, tete.AsSpan(8, 16))) return Mode.Inconnu;
                if (!Passer(flux, BinaryPrimitives.ReadUInt32LittleEndian(tete.AsSpan(12)))) return Mode.Inconnu;
                break;
            case 2:
                if (!Exact(flux, tete.AsSpan(8, 32)) || !Exact(flux, tete.AsSpan(0, 14))) return Mode.Inconnu;
                if (!Passer(flux, BinaryPrimitives.ReadUInt32LittleEndian(tete.AsSpan(10)))) return Mode.Inconnu;
                break;
            default:
                return Mode.Inconnu;
        }

        var touches = false;
        var evenement = new byte[8];
        for (var image = 0; image < ImagesLues; image++)
        {
            if (version > 1 && !Passer(flux, 4)) break;
            var nTouches = flux.ReadByte();
            if (nTouches < 0 || !Passer(flux, nTouches * 12L) || !Exact(flux, tete.AsSpan(0, 2))) break;
            var n = BinaryPrimitives.ReadUInt16LittleEndian(tete);
            for (var i = 0; i < n; i++)
            {
                if (!Exact(flux, evenement)) return touches ? Mode.Touches : Mode.Inconnu;
                // port u8, appareil u8, index u8, bourrage u8, id u16, valeur i16
                if (evenement[1] != Manette) continue;
                var id = BinaryPrimitives.ReadUInt16LittleEndian(evenement.AsSpan(4));
                if (id == IdDuMasque) return Mode.Masque;
                if (id < 16) touches = true;
            }
            var jeton = flux.ReadByte();
            if (jeton == 'C')
            {
                if (!Exact(flux, tete.AsSpan(0, 14)) || !Passer(flux, BinaryPrimitives.ReadUInt32LittleEndian(tete.AsSpan(10)))) break;
            }
            else if (jeton == 'c')
            {
                if (!Exact(flux, tete.AsSpan(0, 8))) break;
                var taille = BinaryPrimitives.ReadUInt64LittleEndian(tete);
                if (taille > int.MaxValue || !Passer(flux, (long)taille)) break;
            }
            else if (jeton != 'f') break;
        }
        return touches ? Mode.Touches : Mode.Inconnu;
    }

    private static bool Exact(Stream flux, Span<byte> destination)
    {
        try { flux.ReadExactly(destination); return true; }
        catch (EndOfStreamException) { return false; }
    }

    /// <summary>Passe <paramref name="octets"/> octets d'un flux qui ne se deplace peut-etre pas (gzip).</summary>
    private static bool Passer(Stream flux, long octets)
    {
        if (octets < 0) return false;
        if (flux.CanSeek)
        {
            if (flux.Position + octets > flux.Length) return false;
            flux.Seek(octets, SeekOrigin.Current);
            return true;
        }
        var tampon = new byte[Math.Min(octets, 1 << 16)];
        while (octets > 0)
        {
            var lus = flux.Read(tampon, 0, (int)Math.Min(tampon.Length, octets));
            if (lus <= 0) return false;
            octets -= lus;
        }
        return true;
    }
}
