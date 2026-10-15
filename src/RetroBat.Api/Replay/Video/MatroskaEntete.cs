namespace RetroBat.Api.Replay.Video;

/// <summary>
/// La piste video d'un film : la taille de l'image et celle de l'affichage (en pixels, ou en rapport d'aspect), et le
/// profil H.264 annonce dans sa configuration (0 s'il n'y en a pas).
/// </summary>
public sealed record EnteteVideo(long Largeur, long Hauteur, long LargeurAffichee, long HauteurAffichee, int Profil = 0)
{
    /// <summary>
    /// Le film a la taille d'origine que la borne fait ecrire a RetroArch (decision du 15 octobre 2026) : H.264 en
    /// 4:4:4 (profil High 4:4:4), que ni Windows ni YouTube ne prennent tel quel. Sa version YouTube se fabrique avec
    /// NelfePlay.LiveStreaming.exe. Les profils 4:2:2 et 10 bits en sont aussi.
    /// </summary>
    public bool Origine => Profil is 244 or 122 or 110 or 44;

    /// <summary>
    /// L'image est en paysage et l'affichage en portrait, ou l'inverse : le film est couche. C'est le cas d'un
    /// jeu vertical filme par RetroArch.
    /// </summary>
    public bool Couchee => Largeur != Hauteur && LargeurAffichee != HauteurAffichee
        && (Largeur > Hauteur) != (LargeurAffichee > HauteurAffichee);
}

/// <summary>
/// L'EN-TETE D'UN FILM DE RETROARCH (Matroska). RetroArch filme l'image du coeur avant que l'ecran ne la tourne :
/// un jeu vertical sort couche, mais l'en-tete garde le format d'affichage du jeu. Mesure du 14 octobre 2026 :
/// Ms. Pac-Man donne une image de 1152x896 affichee en 3:4, Double Dragon 1024x960 affichee en 4:3. La borne
/// reconnait ainsi un film couche sans autre outil. Seul le debut du fichier est lu : RetroArch ecrit les pistes
/// avant les images.
/// </summary>
public static class MatroskaEntete
{
    private const long Segment = 0x18538067;
    private const long Pistes = 0x1654AE6B;
    private const long Piste = 0xAE;
    private const long Grappe = 0x1F43B675;
    private const long TypeDePiste = 0x83;
    private const long Video = 0xE0;
    private const long LargeurPx = 0xB0;
    private const long HauteurPx = 0xBA;
    private const long LargeurAff = 0x54B0;
    private const long HauteurAff = 0x54BA;
    private const long ConfigurationDuCodec = 0x63A2;

    /// <summary>L'en-tete du film, ou null s'il est illisible (fichier absent, tronque, autre format).</summary>
    public static EnteteVideo? Lire(string chemin)
    {
        try
        {
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var octets = new byte[(int)Math.Min(flux.Length, 1 << 20)];
            var lus = 0;
            while (lus < octets.Length)
            {
                var n = flux.Read(octets, lus, octets.Length - lus);
                if (n <= 0) break;
                lus += n;
            }
            return Lire(octets, lus);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>L'en-tete lu dans les <paramref name="longueur"/> premiers octets. Pur, pour etre teste.</summary>
    public static EnteteVideo? Lire(byte[] octets, int longueur)
    {
        EnteteVideo? trouve = null;
        Parcourir(octets, 0, Math.Min(longueur, octets.Length), ref trouve);
        return trouve;
    }

    /// <summary>Descend dans le segment et les pistes ; rend faux quand il faut s'arreter (images, fichier tronque).</summary>
    private static bool Parcourir(byte[] b, int debut, int fin, ref EnteteVideo? trouve)
    {
        var i = debut;
        while (i < fin && trouve is null)
        {
            if (!Element(b, i, fin, out var id, out var donnees, out var finElement)) return false;
            switch (id)
            {
                case Grappe:
                    return false;
                case Segment:
                case Pistes:
                    if (!Parcourir(b, donnees, finElement, ref trouve)) return false;
                    break;
                case Piste:
                    trouve = LirePiste(b, donnees, finElement);
                    break;
            }
            i = finElement;
        }
        return true;
    }

    /// <summary>Une piste : son en-tete video si c'en est une (type 1), sinon null.</summary>
    private static EnteteVideo? LirePiste(byte[] b, int debut, int fin)
    {
        long type = 0, largeur = 0, hauteur = 0, largeurAff = 0, hauteurAff = 0;
        var profil = 0;
        var i = debut;
        while (i < fin && Element(b, i, fin, out var id, out var donnees, out var finElement))
        {
            if (id == TypeDePiste) type = Entier(b, donnees, finElement);
            // avcC : version 1, puis le profil.
            else if (id == ConfigurationDuCodec && finElement - donnees >= 2 && b[donnees] == 1) profil = b[donnees + 1];
            else if (id == Video)
            {
                var j = donnees;
                while (j < finElement && Element(b, j, finElement, out var idVideo, out var d, out var f))
                {
                    if (idVideo == LargeurPx) largeur = Entier(b, d, f);
                    else if (idVideo == HauteurPx) hauteur = Entier(b, d, f);
                    else if (idVideo == LargeurAff) largeurAff = Entier(b, d, f);
                    else if (idVideo == HauteurAff) hauteurAff = Entier(b, d, f);
                    j = f;
                }
            }
            i = finElement;
        }
        if (type != 1 || largeur <= 0 || hauteur <= 0) return null;
        // Sans taille d'affichage, l'image s'affiche telle quelle.
        return largeurAff > 0 && hauteurAff > 0
            ? new EnteteVideo(largeur, hauteur, largeurAff, hauteurAff, profil)
            : new EnteteVideo(largeur, hauteur, largeur, hauteur, profil);
    }

    /// <summary>
    /// Un element EBML a la position <paramref name="i"/> : son identifiant (marque comprise), le debut de ses
    /// donnees et leur fin, bornee au parent (une taille inconnue va jusqu'a la fin du parent).
    /// </summary>
    private static bool Element(byte[] b, int i, int fin, out long id, out int donnees, out int finElement)
    {
        donnees = finElement = 0;
        if (!Nombre(b, i, fin, garderLaMarque: true, out id, out var n1, out _) || n1 > 4) return false;
        if (!Nombre(b, i + n1, fin, garderLaMarque: false, out var taille, out var n2, out var inconnue)) return false;
        donnees = i + n1 + n2;
        finElement = inconnue || taille > fin - donnees ? fin : donnees + (int)taille;
        return true;
    }

    /// <summary>Un entier EBML de longueur variable (1 a 8 octets).</summary>
    private static bool Nombre(byte[] b, int i, int fin, bool garderLaMarque, out long valeur, out int longueur, out bool inconnue)
    {
        valeur = 0;
        longueur = 0;
        inconnue = false;
        if (i >= fin) return false;
        var premier = b[i];
        var masque = 0x80;
        var n = 1;
        while (n <= 8 && (premier & masque) == 0)
        {
            masque >>= 1;
            n++;
        }
        if (n > 8 || i + n > fin) return false;
        long v = garderLaMarque ? premier : premier & (masque - 1);
        for (var k = 1; k < n; k++) v = (v << 8) | b[i + k];
        valeur = v;
        longueur = n;
        inconnue = !garderLaMarque && v == (1L << (7 * n)) - 1;
        return true;
    }

    private static long Entier(byte[] b, int debut, int fin)
    {
        long v = 0;
        for (var k = debut; k < fin && k < debut + 8; k++) v = (v << 8) | b[k];
        return v;
    }
}
