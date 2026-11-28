namespace RetroBat.Api.Extend;

/// <summary>
/// EXTEND MODE (28/11) : l'applicateur de patchs IPS. Classe PURE : octets de la source et du patch en entree, octets du
/// resultat (ou une erreur explicite) en sortie ; aucun fichier, aucun journal.
/// <para>
/// Format : l'en-tete <c>PATCH</c>, des enregistrements (decalage sur 3 octets, taille sur 2 octets, donnees ; une taille nulle
/// annonce un enregistrement RLE : longueur sur 2 octets puis un octet repete), le marqueur <c>EOF</c>, et, facultativement,
/// la taille finale sur 3 octets (troncature, ou allongement par des zeros). Une ecriture au-dela de la fin de la source
/// agrandit le resultat (le vide est rempli de zeros). Le resultat ne depasse jamais <c>plafond</c> octets.
/// </para>
/// </summary>
public static class PatchIps
{
    private const int FinDuFichier = 0x454F46; // les trois lettres E, O, F

    public static bool Appliquer(ReadOnlySpan<byte> source, ReadOnlySpan<byte> patch, long plafond, out byte[]? resultat, out string? erreur)
    {
        resultat = null;
        if (patch.Length < 8 || patch[0] != 'P' || patch[1] != 'A' || patch[2] != 'T' || patch[3] != 'C' || patch[4] != 'H')
        {
            erreur = "IPS : en-tete PATCH absent";
            return false;
        }

        if (source.Length > plafond)
        {
            erreur = "IPS : source plus grande que le plafond";
            return false;
        }

        var tampon = new byte[Math.Max(source.Length, 1)];
        source.CopyTo(tampon);
        var longueur = source.Length;
        var pos = 5;
        var termine = false;
        while (pos < patch.Length)
        {
            if (pos + 3 > patch.Length)
            {
                erreur = "IPS : enregistrement tronque";
                return false;
            }

            var decalage = (patch[pos] << 16) | (patch[pos + 1] << 8) | patch[pos + 2];
            pos += 3;
            if (decalage == FinDuFichier)
            {
                termine = true;
                break;
            }

            if (pos + 2 > patch.Length)
            {
                erreur = "IPS : taille d'enregistrement tronquee";
                return false;
            }

            var taille = (patch[pos] << 8) | patch[pos + 1];
            pos += 2;
            if (taille == 0)
            {
                if (pos + 3 > patch.Length)
                {
                    erreur = "IPS : enregistrement RLE tronque";
                    return false;
                }

                var repetitions = (patch[pos] << 8) | patch[pos + 1];
                var valeur = patch[pos + 2];
                pos += 3;
                if (repetitions == 0)
                {
                    erreur = "IPS : enregistrement RLE de longueur nulle";
                    return false;
                }

                if (!Assurer(ref tampon, ref longueur, (long)decalage + repetitions, plafond, out erreur))
                {
                    return false;
                }

                Array.Fill(tampon, valeur, decalage, repetitions);
            }
            else
            {
                if (pos + taille > patch.Length)
                {
                    erreur = "IPS : donnees d'enregistrement tronquees";
                    return false;
                }

                if (!Assurer(ref tampon, ref longueur, (long)decalage + taille, plafond, out erreur))
                {
                    return false;
                }

                patch.Slice(pos, taille).CopyTo(tampon.AsSpan(decalage));
                pos += taille;
            }
        }

        if (!termine)
        {
            erreur = "IPS : marqueur EOF absent";
            return false;
        }

        var reste = patch.Length - pos;
        if (reste == 3)
        {
            var finale = (patch[pos] << 16) | (patch[pos + 1] << 8) | patch[pos + 2];
            if (finale < longueur)
            {
                longueur = finale;
            }
            else if (finale > longueur && !Assurer(ref tampon, ref longueur, finale, plafond, out erreur))
            {
                return false;
            }
        }
        else if (reste != 0)
        {
            erreur = "IPS : octets inattendus apres EOF";
            return false;
        }

        resultat = tampon.AsSpan(0, longueur).ToArray();
        erreur = null;
        return true;
    }

    private static bool Assurer(ref byte[] tampon, ref int longueur, long necessaire, long plafond, out string? erreur)
    {
        if (necessaire > plafond)
        {
            erreur = "IPS : le resultat depasserait " + plafond.ToString(System.Globalization.CultureInfo.InvariantCulture) + " octets";
            return false;
        }

        if (necessaire > tampon.Length)
        {
            Array.Resize(ref tampon, (int)Math.Min(plafond, Math.Max(necessaire, (long)tampon.Length * 2)));
        }

        if (necessaire > longueur)
        {
            longueur = (int)necessaire;
        }

        erreur = null;
        return true;
    }
}
