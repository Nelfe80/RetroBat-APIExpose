using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RetroBat.Api.Adventure;

/// <summary>
/// L'ETAT LIE DE mGBA SPLITSCREEN, lu et retouche sans RetroArch (format mesure au banc le 26/11 par le lot 0) : un fichier RASTATE (RetroArch),
/// eventuellement compresse en RZIP (#RZIPv : blocs zlib), dont le bloc <c>MEM </c> est un <c>SPST</c> : version, nombre de consoles, la taille
/// de chacune, puis chaque console = un etat mGBA complet (0x61000 octets) suivi de sa table d'extdata (tag u32, taille i32, decalage u64,
/// 16 octets par entree, fin a tag 0). L'extdata de tag 2 est le SAVEDATA, la sauvegarde du jeu de cette console.
///
/// Pourquoi : charger un etat RESTAURE le SAVEDATA qu'il porte, donc ecrase la sauvegarde de cartouche que l'API vient de poser dans le dossier
/// de l'aventure. Pour repartir d'un depart pret avec la sauvegarde de l'aventure, on la place DANS l'etat, avant de le charger.
/// </summary>
public static class AdventureEtat
{
    private const int TailleDEtatMgba = 0x61000;
    private const uint TagSauvegarde = 2;

    /// <summary>Les octets bruts d'un etat : decompresses s'il est en RZIP, tels quels sinon.</summary>
    public static byte[]? Decompresser(byte[] fichier)
    {
        if (fichier.Length < 20 || !Encoding.ASCII.GetString(fichier, 0, 6).Equals("#RZIPv", StringComparison.Ordinal))
        {
            return fichier;
        }

        try
        {
            var total = BinaryPrimitives.ReadUInt64LittleEndian(fichier.AsSpan(12, 8));
            if (total > 64UL * 1024 * 1024)
            {
                return null;
            }

            using var sortie = new MemoryStream((int)total);
            var p = 20;
            while (p + 4 <= fichier.Length)
            {
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(fichier.AsSpan(p, 4));
                p += 4;
                if (n < 0 || p + n > fichier.Length)
                {
                    return null;
                }

                using var zlib = new ZLibStream(new MemoryStream(fichier, p, n), CompressionMode.Decompress);
                zlib.CopyTo(sortie);
                p += n;
            }

            return sortie.Length == (long)total ? sortie.ToArray() : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>Ou se trouve, dans l'etat BRUT, le SAVEDATA de la console <paramref name="console"/> (1 = la premiere) : decalage et taille, ou null.</summary>
    public static (int Decalage, int Taille)? TrouverLaSauvegarde(byte[] brut, int console = 1)
    {
        if (brut.Length < 16 || !Encoding.ASCII.GetString(brut, 0, 7).Equals("RASTATE", StringComparison.Ordinal))
        {
            return null;
        }

        // Les blocs de RetroArch : etiquette (4), taille (4), donnees arrondies a 8.
        var i = 8;
        while (i + 8 <= brut.Length)
        {
            var taille = (int)BinaryPrimitives.ReadUInt32LittleEndian(brut.AsSpan(i + 4, 4));
            if (taille < 0 || i + 8 + taille > brut.Length)
            {
                return null;
            }

            if (Encoding.ASCII.GetString(brut, i, 4) == "MEM ")
            {
                return DansLeBlocMemoire(brut, i + 8, taille, console);
            }

            i += 8 + (taille + 7) / 8 * 8;
        }

        return null;
    }

    private static (int, int)? DansLeBlocMemoire(byte[] brut, int debut, int taille, int console)
    {
        if (taille < 12 || Encoding.ASCII.GetString(brut, debut, 4) != "SPST")
        {
            return null;
        }

        var joueurs = (int)BinaryPrimitives.ReadUInt32LittleEndian(brut.AsSpan(debut + 8, 4));
        if (joueurs < 1 || joueurs > 4 || console < 1 || console > joueurs || 12 + 4 * joueurs > taille)
        {
            return null;
        }

        var p = debut + 12 + 4 * joueurs;
        for (var c = 1; c <= joueurs; c++)
        {
            var tailleDeLaConsole = (int)BinaryPrimitives.ReadUInt32LittleEndian(brut.AsSpan(debut + 12 + 4 * (c - 1), 4));
            if (c == console)
            {
                var k = p + TailleDEtatMgba;
                while (k + 16 <= p + tailleDeLaConsole)
                {
                    var tag = BinaryPrimitives.ReadUInt32LittleEndian(brut.AsSpan(k, 4));
                    var octets = BinaryPrimitives.ReadInt32LittleEndian(brut.AsSpan(k + 4, 4));
                    var decalage = BinaryPrimitives.ReadUInt64LittleEndian(brut.AsSpan(k + 8, 8));
                    k += 16;
                    if (tag == 0)
                    {
                        break;
                    }

                    if (tag == TagSauvegarde && octets > 0 && decalage + (ulong)octets <= (ulong)tailleDeLaConsole)
                    {
                        return (p + (int)decalage, octets);
                    }
                }

                return null;
            }

            p += tailleDeLaConsole;
        }

        return null;
    }

    /// <summary>Le SAVEDATA de la console 1 d'un etat (compresse ou non), ou null.</summary>
    public static byte[]? LireLaSauvegarde(byte[] fichier, int console = 1)
    {
        var brut = Decompresser(fichier);
        return brut is not null && TrouverLaSauvegarde(brut, console) is var (decalage, taille) ? brut.AsSpan(decalage, taille).ToArray() : null;
    }

    /// <summary>
    /// L'etat avec <paramref name="sauvegarde"/> comme SAVEDATA de la console <paramref name="console"/>, en etat BRUT (RetroArch charge aussi un
    /// etat non compresse). Meme taille que l'emplacement exigee : une sauvegarde plus petite est completee de 0xFF (une EEPROM ou une SRAM
    /// vierge), une plus grande refusee (null). Aucun autre octet de l'etat ne change.
    /// </summary>
    public static byte[]? PoserLaSauvegarde(byte[] fichier, byte[] sauvegarde, int console = 1, bool tronquer = false)
    {
        var brut = Decompresser(fichier);
        if (brut is null || TrouverLaSauvegarde(brut, console) is not var (decalage, taille))
        {
            return null;
        }

        // Le .srm de mGBA Splitscreen a un emplacement par console : celui de la console 1 en tete, les premiers octets.
        if (sauvegarde.Length > taille)
        {
            if (!tronquer)
            {
                return null;
            }

            sauvegarde = sauvegarde.AsSpan(0, taille).ToArray();
        }

        var retouche = (byte[])brut.Clone();
        Array.Fill(retouche, (byte)0xFF, decalage, taille);
        Array.Copy(sauvegarde, 0, retouche, decalage, sauvegarde.Length);
        return retouche;
    }
}
