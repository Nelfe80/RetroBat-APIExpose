namespace RetroBat.Api.Extend;

/// <summary>
/// EXTEND MODE (28/11) : l'applicateur de patchs BPS (format « beat » de byuu). Classe PURE : octets de la source et du
/// patch en entree, octets du resultat (ou une erreur explicite) en sortie.
/// <para>
/// Un BPS porte ses propres controles : taille et CRC32 de la source, CRC32 de la cible, CRC32 du patch lui-meme (ses 12
/// derniers octets). Les trois sont verifies, avec la taille annoncee de la source ; un ecart = aucun resultat. Les actions
/// SourceRead, TargetRead, SourceCopy et TargetCopy sont toutes comprises ; le resultat ne depasse jamais <c>plafond</c> octets.
/// </para>
/// </summary>
public static class PatchBps
{
    public static bool Appliquer(ReadOnlySpan<byte> source, ReadOnlySpan<byte> patch, long plafond, out byte[]? resultat, out string? erreur)
    {
        resultat = null;
        if (patch.Length < 4 + 3 + 12 || patch[0] != 'B' || patch[1] != 'P' || patch[2] != 'S' || patch[3] != '1')
        {
            erreur = "BPS : en-tete BPS1 absent";
            return false;
        }

        var fin = patch.Length - 12;
        var crcSource = Lire32(patch, fin);
        var crcCible = Lire32(patch, fin + 4);
        var crcPatch = Lire32(patch, fin + 8);
        if (Crc32(patch[..(patch.Length - 4)]) != crcPatch)
        {
            erreur = "BPS : le CRC32 du patch ne correspond pas (patch corrompu)";
            return false;
        }

        if (Crc32(source) != crcSource)
        {
            erreur = "BPS : le CRC32 de la source ne correspond pas (ce n'est pas la ROM attendue)";
            return false;
        }

        var pos = 4;
        if (!Varint(patch, fin, ref pos, out var tailleSource) || !Varint(patch, fin, ref pos, out var tailleCible)
            || !Varint(patch, fin, ref pos, out var tailleMeta))
        {
            erreur = "BPS : en-tete illisible";
            return false;
        }

        if (tailleSource != (ulong)source.Length)
        {
            erreur = "BPS : taille de source annoncee differente de celle de la ROM";
            return false;
        }

        if (tailleCible > (ulong)plafond)
        {
            erreur = "BPS : le resultat depasserait " + plafond.ToString(System.Globalization.CultureInfo.InvariantCulture) + " octets";
            return false;
        }

        if (tailleMeta > (ulong)(fin - pos))
        {
            erreur = "BPS : metadonnees tronquees";
            return false;
        }

        pos += (int)tailleMeta;
        var cible = new byte[(int)tailleCible];
        var sortie = 0;
        long relSource = 0, relCible = 0;
        while (pos < fin)
        {
            if (!Varint(patch, fin, ref pos, out var donnee))
            {
                erreur = "BPS : action illisible";
                return false;
            }

            var commande = (int)(donnee & 3);
            var longueur = (donnee >> 2) + 1;
            if (longueur > (ulong)(cible.Length - sortie))
            {
                erreur = "BPS : une action depasse la taille de la cible";
                return false;
            }

            var n = (int)longueur;
            switch (commande)
            {
                case 0: // SourceRead : memes octets de la source, au meme decalage
                    if (sortie + n > source.Length)
                    {
                        erreur = "BPS : SourceRead au-dela de la source";
                        return false;
                    }

                    source.Slice(sortie, n).CopyTo(cible.AsSpan(sortie));
                    sortie += n;
                    break;
                case 1: // TargetRead : octets litteraux du patch
                    if (n > fin - pos)
                    {
                        erreur = "BPS : TargetRead au-dela du patch";
                        return false;
                    }

                    patch.Slice(pos, n).CopyTo(cible.AsSpan(sortie));
                    pos += n;
                    sortie += n;
                    break;
                default: // 2 = SourceCopy, 3 = TargetCopy
                    if (!Varint(patch, fin, ref pos, out var deplacement))
                    {
                        erreur = "BPS : decalage de copie illisible";
                        return false;
                    }

                    var signe = (deplacement & 1) != 0 ? -1L : 1L;
                    var delta = signe * (long)(deplacement >> 1);
                    if (commande == 2)
                    {
                        relSource += delta;
                        if (relSource < 0 || relSource + n > source.Length)
                        {
                            erreur = "BPS : SourceCopy hors de la source";
                            return false;
                        }

                        source.Slice((int)relSource, n).CopyTo(cible.AsSpan(sortie));
                        relSource += n;
                        sortie += n;
                    }
                    else
                    {
                        relCible += delta;
                        if (relCible < 0 || relCible >= sortie)
                        {
                            erreur = "BPS : TargetCopy hors de la cible deja ecrite";
                            return false;
                        }

                        // Octet par octet : une copie qui chevauche sa propre sortie repete un motif.
                        for (var i = 0; i < n; i++)
                        {
                            cible[sortie++] = cible[relCible++];
                        }
                    }

                    break;
            }
        }

        if (sortie != cible.Length)
        {
            erreur = "BPS : la cible n'est pas entierement ecrite";
            return false;
        }

        if (Crc32(cible) != crcCible)
        {
            erreur = "BPS : le CRC32 de la cible ne correspond pas";
            return false;
        }

        resultat = cible;
        erreur = null;
        return true;
    }

    private static uint Lire32(ReadOnlySpan<byte> octets, int pos)
        => octets[pos] | ((uint)octets[pos + 1] << 8) | ((uint)octets[pos + 2] << 16) | ((uint)octets[pos + 3] << 24);

    private static bool Varint(ReadOnlySpan<byte> octets, int limite, ref int pos, out ulong valeur)
    {
        valeur = 0;
        ulong decalage = 1;
        while (true)
        {
            if (pos >= limite || decalage > (1UL << 56))
            {
                return false;
            }

            var x = octets[pos++];
            valeur += (x & 0x7Ful) * decalage;
            if ((x & 0x80) != 0)
            {
                return true;
            }

            decalage <<= 7;
            valeur += decalage;
        }
    }

    private static readonly uint[] Table = CreerTable();

    private static uint[] CreerTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>Le CRC32 usuel (polynome 0xEDB88320), celui de zip et de BPS.</summary>
    public static uint Crc32(ReadOnlySpan<byte> octets)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in octets)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}
