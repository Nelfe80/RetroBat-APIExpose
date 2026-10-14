using System.Buffers.Binary;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// REDRESSER UN FILM VERTICAL SANS LE REENCODER (essais du 14 octobre 2026). RetroArch 1.22 filme l'image du coeur
/// avant de la tourner : un jeu vertical sort couche, avec en plus le format d'affichage du jeu redresse (3:4)
/// pose sur l'image couchee. YouTube suit la rotation inscrite dans le conteneur : un MKV dont la piste video
/// porte <c>Projection/ProjectionPoseRoll</c> s'affiche droit (verifie : « rotation clockwise » chez YouTube,
/// miniature droite), alors que l'orientation inscrite dans le flux H.264 est ignoree.
/// <para>On corrige donc l'en-tete sur place : format d'affichage remis dans le sens de l'image, rotation
/// ajoutee. Les octets en plus sont pris sur l'espace de reserve (Void) que RetroArch laisse avant les pistes ;
/// rien d'autre ne bouge, ni les images ni l'index. Les positions de l'en-tete de recherche (SeekHead) et les
/// sommes de controle (CRC-32) des blocs modifies sont recalculees. Si le fichier n'a pas cette forme, ou si une
/// somme d'origine ne se verifie pas, rien n'est touche.</para>
/// </summary>
public static class MatroskaRotation
{
    private const long Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekPosition = 0x53AC;
    private const long Vide = 0xEC, Pistes = 0x1654AE6B, Piste = 0xAE, TypeDePiste = 0x83, Video = 0xE0;
    private const long Grappe = 0x1F43B675, Crc = 0xBF;
    private const long LargeurAff = 0x54B0, HauteurAff = 0x54BA, Projection = 0x7670, Roulis = 0x7675;

    /// <summary>
    /// Le roulis a inscrire pour la rotation demandee par le coeur (RETRO_ENVIRONMENT_SET_ROTATION : quarts de tour
    /// dans le sens antihoraire). Ms. Pac-Man annonce 3 (270 degres) : roulis -90, un quart de tour horaire.
    /// </summary>
    public static double RoulisPour(int rotationDuCoeur) => (((rotationDuCoeur % 4) + 4) % 4) switch
    {
        1 => 90,
        2 => 180,
        3 => -90,
        _ => 0,
    };

    /// <summary>Redresse le film sur place. Rend vrai si l'en-tete a ete corrige.</summary>
    public static bool Redresser(string chemin, int rotationDuCoeur)
    {
        try
        {
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var tete = new byte[(int)Math.Min(flux.Length, 64 * 1024)];
            flux.ReadExactly(tete);
            var corrige = Corriger(tete, rotationDuCoeur);
            if (corrige is null) return false;
            var (debut, octets) = corrige.Value;
            flux.Seek(debut, SeekOrigin.Begin);
            flux.Write(octets);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// La correction du debut du fichier <paramref name="tete"/> : la position et les octets a recrire, de meme
    /// longueur que ceux qu'ils remplacent ; null si le fichier n'a pas la forme attendue, porte deja une rotation
    /// ou si une somme de controle d'origine ne se verifie pas. Pur, pour etre teste.
    /// </summary>
    internal static (int Debut, byte[] Octets)? Corriger(byte[] tete, int rotationDuCoeur)
    {
        var roulis = RoulisPour(rotationDuCoeur);
        if (roulis == 0) return null;

        // Le segment, puis ses enfants jusqu'a la premiere grappe d'images.
        var segment = -1;
        for (var i = 0; i < tete.Length && Element(tete, i, out var id, out _, out var f, out _, out _); i = f)
        {
            if (id == Segment) { segment = i; break; }
        }
        if (segment < 0) return null;
        var origine = Donnees(tete, segment);

        int vide = -1, seekHead = -1, pistes = -1;
        for (var i = origine; i < tete.Length && Element(tete, i, out var id, out _, out var f, out _, out var inconnue); i = f)
        {
            if (id == Grappe || inconnue) break;
            if (id == SeekHead && seekHead < 0) seekHead = i;
            else if (id == Vide && vide < 0 && pistes < 0) vide = i;
            else if (id == Pistes) { pistes = i; break; }
        }
        if (vide < 0 || pistes < 0 || vide > pistes) return null;
        var video = PisteVideo(tete, pistes);
        if (video < 0 || Enfant(tete, video, Projection) >= 0) return null;
        if (!CrcIntact(tete, pistes) || (seekHead >= 0 && !CrcIntact(tete, seekHead))) return null;

        // La nouvelle piste video : format d'affichage remis dans le sens de l'image (quart de tour : largeur et
        // hauteur echangees), rotation ajoutee a la fin.
        var quartDeTour = Math.Abs(roulis) == 90;
        var largeurAff = Enfant(tete, video, LargeurAff);
        var hauteurAff = Enfant(tete, video, HauteurAff);
        var enfants = new List<byte>();
        for (var j = Donnees(tete, video); j < FinDe(tete, video) && Element(tete, j, out var id, out _, out var f, out _, out _); j = f)
        {
            if (quartDeTour && id == LargeurAff && hauteurAff >= 0) enfants.AddRange(Encoder(LargeurAff, Valeur(tete, hauteurAff)));
            else if (quartDeTour && id == HauteurAff && largeurAff >= 0) enfants.AddRange(Encoder(HauteurAff, Valeur(tete, largeurAff)));
            else enfants.AddRange(tete[j..f]);
        }
        var flottant = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(flottant, roulis);
        enfants.AddRange(Encoder(Projection, Encoder(Roulis, flottant)));
        var nouvelleVideo = EnTete(Video, enfants.Count, LongueurEnTete(tete, video)).Concat(enfants).ToArray();
        var finVideo = FinDe(tete, video);
        var ecart = nouvelleVideo.Length - (finVideo - video);
        var resteDuVide = FinDe(tete, vide) - Donnees(tete, vide) - ecart;
        if (ecart <= 0 || resteDuVide < 0) return null;

        // Le nouveau debut de fichier : la reserve raccourcie de l'ecart, les elements suivants avances d'autant,
        // les pistes et la piste qui contiennent la video agrandies, puis la nouvelle piste video ; la suite ne
        // bouge pas.
        var neuf = (byte[])tete.Clone();
        var region = new List<byte>();
        region.AddRange(EnTete(Vide, resteDuVide, LongueurEnTete(tete, vide)));
        region.AddRange(new byte[resteDuVide]);
        if (!Recopier(tete, FinDe(tete, vide), video, ecart, region)) return null;
        region.AddRange(nouvelleVideo);
        if (region.Count != finVideo - vide) return null;
        region.CopyTo(neuf, vide);

        // L'en-tete de recherche, avant la reserve : les elements entre la reserve et la fin de la video ont recule.
        if (seekHead >= 0 && seekHead < vide && !DecalerLesPositions(neuf, seekHead, origine, FinDe(tete, vide), finVideo, ecart))
            return null;

        // Les sommes de controle des blocs modifies.
        var nouvellesPistes = pistes - ecart;
        RecalculerCrc(neuf, nouvellesPistes);
        if (seekHead >= 0) RecalculerCrc(neuf, seekHead);

        var debut = seekHead >= 0 && seekHead < vide ? seekHead : vide;
        var fin = Math.Max(finVideo, FinDe(neuf, nouvellesPistes));
        return (debut, neuf[debut..fin]);
    }

    /// <summary>L'element Video de la piste video (type 1) parmi les pistes.</summary>
    private static int PisteVideo(byte[] b, int pistes)
    {
        for (var i = Donnees(b, pistes); i < FinDe(b, pistes) && Element(b, i, out var id, out _, out var f, out _, out _); i = f)
        {
            if (id != Piste) continue;
            var type = Enfant(b, i, TypeDePiste);
            var video = Enfant(b, i, Video);
            if (type >= 0 && video >= 0 && Entier(Valeur(b, type)) == 1) return video;
        }
        return -1;
    }

    /// <summary>
    /// Recopie les octets [<paramref name="debut"/>, <paramref name="fin"/>) en agrandissant de
    /// <paramref name="ecart"/> la taille des elements qui contiennent <paramref name="fin"/>, sans changer la
    /// longueur de leurs en-tetes.
    /// </summary>
    private static bool Recopier(byte[] b, int debut, int fin, int ecart, List<byte> sortie)
    {
        var i = debut;
        while (i < fin)
        {
            if (!Element(b, i, out var id, out var d, out var f, out var longueur, out _)) return false;
            if (f <= fin)
            {
                sortie.AddRange(b[i..f]);
                i = f;
                continue;
            }
            var enTete = EnTete(id, f - d + ecart, longueur);
            if (enTete.Length != d - i) return false;
            sortie.AddRange(enTete);
            i = d;
        }
        return i == fin;
    }

    /// <summary>Recule de l'ecart, en place et sur la meme longueur, les positions des elements decales.</summary>
    private static bool DecalerLesPositions(byte[] b, int seekHead, int origine, int depuis, int jusqua, int ecart)
    {
        for (var i = Donnees(b, seekHead); i < FinDe(b, seekHead) && Element(b, i, out var id, out _, out var f, out _, out _); i = f)
        {
            if (id != Seek) continue;
            var position = Enfant(b, i, SeekPosition);
            if (position < 0) continue;
            var d = Donnees(b, position);
            var fp = FinDe(b, position);
            var valeur = Entier(b[d..fp]);
            if (origine + valeur < depuis || origine + valeur >= jusqua) continue;
            var nouvelle = valeur - ecart;
            for (var k = fp - 1; k >= d; k--)
            {
                b[k] = (byte)(nouvelle & 0xFF);
                nouvelle >>= 8;
            }
            if (nouvelle != 0) return false;
        }
        return true;
    }

    /// <summary>
    /// La somme de controle d'un bloc : premier enfant CRC-32 (EBML), calculee sur la suite du bloc, rangee en
    /// petit-boutiste. Un bloc sans somme est intact.
    /// </summary>
    private static bool CrcIntact(byte[] b, int bloc)
    {
        var crc = PremierEnfantCrc(b, bloc);
        return crc < 0 || BinaryPrimitives.ReadUInt32LittleEndian(Valeur(b, crc)) == Crc32(b, FinDe(b, crc), FinDe(b, bloc));
    }

    private static void RecalculerCrc(byte[] b, int bloc)
    {
        var crc = PremierEnfantCrc(b, bloc);
        if (crc < 0) return;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Donnees(b, crc), 4), Crc32(b, FinDe(b, crc), FinDe(b, bloc)));
    }

    private static int PremierEnfantCrc(byte[] b, int bloc)
    {
        var d = Donnees(b, bloc);
        return Element(b, d, out var id, out var dc, out var fc, out _, out _) && id == Crc && fc - dc == 4 ? d : -1;
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 IEEE (celui de zlib) sur [<paramref name="debut"/>, <paramref name="fin"/>).</summary>
    internal static uint Crc32(byte[] b, int debut, int fin)
    {
        var c = 0xFFFFFFFFu;
        for (var i = debut; i < fin; i++) c = Table[(c ^ b[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static int Enfant(byte[] b, int element, long voulu)
    {
        for (var i = Donnees(b, element); i < FinDe(b, element) && Element(b, i, out var id, out _, out var f, out _, out _); i = f)
        {
            if (id == voulu) return i;
        }
        return -1;
    }

    private static int Donnees(byte[] b, int element) => Element(b, element, out _, out var d, out _, out _, out _) ? d : b.Length;
    private static int FinDe(byte[] b, int element) => Element(b, element, out _, out _, out var f, out _, out _) ? f : b.Length;
    private static int LongueurEnTete(byte[] b, int element) => Element(b, element, out _, out _, out _, out var l, out _) ? l : 0;
    private static byte[] Valeur(byte[] b, int element) => b[Donnees(b, element)..FinDe(b, element)];

    private static long Entier(byte[] octets)
    {
        long v = 0;
        foreach (var o in octets.Take(8)) v = (v << 8) | o;
        return v;
    }

    private static byte[] Encoder(long id, byte[] donnees) => EnTete(id, donnees.Length, 0).Concat(donnees).ToArray();

    /// <summary>
    /// L'identifiant puis la taille, sur <paramref name="longueurVoulue"/> octets en tout si on la donne (pour
    /// recrire un en-tete sans le deplacer), sinon sur la longueur la plus courte.
    /// </summary>
    private static byte[] EnTete(long id, long taille, int longueurVoulue)
    {
        var ident = new List<byte>();
        for (var v = id; v > 0; v >>= 8) ident.Insert(0, (byte)(v & 0xFF));
        var n = longueurVoulue > 0 ? longueurVoulue - ident.Count : 1;
        while (longueurVoulue <= 0 && taille >= (1L << (7 * n)) - 1) n++;
        var t = new byte[n];
        var valeur = taille;
        for (var k = n - 1; k >= 0; k--)
        {
            t[k] = (byte)(valeur & 0xFF);
            valeur >>= 8;
        }
        t[0] |= (byte)(0x80 >> (n - 1));
        return [.. ident, .. t];
    }

    /// <summary>Un element EBML : identifiant, debut et fin de ses donnees, longueur de son en-tete.</summary>
    private static bool Element(byte[] b, int i, out long id, out int donnees, out int fin, out int longueurEnTete, out bool inconnue)
    {
        donnees = fin = longueurEnTete = 0;
        inconnue = false;
        if (!Nombre(b, i, true, out id, out var n1, out _) || n1 > 4) return false;
        if (!Nombre(b, i + n1, false, out var taille, out var n2, out inconnue)) return false;
        donnees = i + n1 + n2;
        longueurEnTete = n1 + n2;
        fin = inconnue || taille > b.Length - donnees ? b.Length : donnees + (int)taille;
        return true;
    }

    private static bool Nombre(byte[] b, int i, bool garderLaMarque, out long valeur, out int longueur, out bool inconnue)
    {
        valeur = 0;
        longueur = 0;
        inconnue = false;
        if (i < 0 || i >= b.Length) return false;
        var premier = b[i];
        var masque = 0x80;
        var n = 1;
        while (n <= 8 && (premier & masque) == 0)
        {
            masque >>= 1;
            n++;
        }
        if (n > 8 || i + n > b.Length) return false;
        long v = garderLaMarque ? premier : premier & (masque - 1);
        for (var k = 1; k < n; k++) v = (v << 8) | b[i + k];
        valeur = v;
        longueur = n;
        inconnue = !garderLaMarque && v == (1L << (7 * n)) - 1;
        return true;
    }
}
