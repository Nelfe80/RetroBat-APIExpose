namespace RetroBat.Api.Netplay;

/// <summary>
/// L'identite du CONTENU d'une partie, calculee de la meme facon des deux cotes.
///
/// Pourquoi ce fichier existe : la verification d'avant-partie comparait des valeurs
/// FABRIQUEES DIFFEREMMENT. L'hote rapportait ce que publiait le lobby (le CRC calcule par
/// RetroArch) et le client calculait le sien avec son propre code. Deux definitions du meme
/// mot, donc un refus sur des jeux identiques.
///
/// La regle retenue : notre controle compare des valeurs produites par CE code des deux
/// cotes, et RetroArch reste l'arbitre final. Notre controle n'est la que pour DIRE pourquoi
/// ca ne marchera pas, avant que la connexion tombe sans explication.
///
/// Pour une archive, on ne prend pas « la premiere entree » : un romset d'arcade en contient
/// des dizaines, et la premiere ne dit rien du reste. On resume l'index ENTIER, trie par nom,
/// ce qui donne une identite stable sans rien decompresser.
/// </summary>
internal static class NetplayContent
{
    /// <summary>L'empreinte du contenu, ou une chaine vide si le fichier est illisible.</summary>
    public static string Empreinte(string chemin)
    {
        try
        {
            if (chemin.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(chemin);
                // Trie par nom : l'ordre des entrees dans un zip n'est pas garanti, et une
                // empreinte qui depend de l'ordre d'ecriture n'est pas une empreinte.
                var lignes = archive.Entries
                    .Where(e => e.Length > 0)
                    .Select(e => e.FullName + ':' + e.Crc32.ToString("X8"))
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                if (lignes.Length == 0)
                {
                    return "";
                }
                var octets = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lignes));
                using var memoire = new MemoryStream(octets);
                return Crc32De(memoire).ToString("X8");
            }

            using var flux = File.OpenRead(chemin);
            return Crc32De(flux).ToString("X8");
        }
        catch (Exception)
        {
            // Illisible : on rend une chaine vide, et l'appelant ne compare rien. Refuser une
            // partie parce qu'on n'a pas su lire un fichier serait pire que la laisser tenter.
            return "";
        }
    }

    private static uint Crc32De(Stream flux)
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

        var crc = 0xFFFFFFFFu;
        var tampon = new byte[64 * 1024];
        int lu;
        while ((lu = flux.Read(tampon, 0, tampon.Length)) > 0)
        {
            for (var i = 0; i < lu; i++)
            {
                crc = table[(crc ^ tampon[i]) & 0xFF] ^ (crc >> 8);
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
