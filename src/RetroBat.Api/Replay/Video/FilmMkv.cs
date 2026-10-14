using System.Buffers.Binary;
using System.Text;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE FILM QUE RETROARCH ECRIT ENCORE, LU GRAPPE PAR GRAPPE (CDC video V2, §5.8). RetroArch (ffmpeg) ecrit chaque grappe
/// d'un bloc, une image cle en tete, toutes les 2 a 4 secondes (mesure du 14 octobre 2026) : le direct d'un record en
/// est la copie, sans recompression. Seule la fin du fichier se relit a chaque passage ; le fichier reste ouvert en
/// ecriture par RetroArch.
/// </summary>
public sealed class FilmMkv
{
    public const ulong IdCluster = 0x1F43B675;
    private const ulong IdSegment = 0x18538067;

    /// <summary>Une piste : video (1) ou son (2), son codec et ses donnees privees (avcC, AudioSpecificConfig).</summary>
    public sealed record Piste(int Numero, int Type, string Codec, byte[] Prive, int Largeur, int Hauteur, double Frequence);

    /// <summary>Une image ou un bloc de son : sa piste, son instant d'affichage (ms), image cle ou non.</summary>
    public sealed record Bloc(int Piste, double TempsMs, bool Cle, byte[] Donnees);

    private readonly string _chemin;
    private long _position = -1;
    private double _msParUnite = 1.0;

    public FilmMkv(string chemin) => _chemin = chemin;

    public Dictionary<int, Piste> Pistes { get; } = new();
    public Piste? Video => Pistes.Values.FirstOrDefault(p => p.Type == 1);
    public Piste? Son => Pistes.Values.FirstOrDefault(p => p.Type == 2);

    private static FileStream Ouvrir(string chemin)
        => new(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Lit l'en-tete (pistes, echelle du temps) ; false tant que le fichier ou ses pistes ne sont pas ecrits.</summary>
    public bool LireEntete()
    {
        try
        {
            using var f = Ouvrir(_chemin);
            var b = new byte[(int)Math.Min(f.Length, 1 << 20)];
            var lus = LireTout(f, b);
            return LireEntete(b.AsSpan(0, lus));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>L'en-tete dans un tampon : pistes et position de la premiere grappe. Pur sur le tampon.</summary>
    internal bool LireEntete(ReadOnlySpan<byte> b)
    {
        if (!Element(b, 0, out _, out var taille, out var donnees) || taille is null) return false;
        var i = donnees + (int)taille.Value;
        if (!Element(b, i, out var id, out _, out donnees) || id != IdSegment) return false;
        i = donnees;
        while (true)
        {
            var debut = i;
            if (!Element(b, i, out id, out taille, out donnees)) return false;
            if (id == IdCluster)
            {
                _position = debut;
                return Pistes.Count > 0;
            }
            if (taille is null || (ulong)donnees + taille.Value > (ulong)b.Length) return false;
            var corps = b.Slice(donnees, (int)taille.Value);
            if (id == 0x1549A966) LireInfo(corps);
            else if (id == 0x1654AE6B) LirePistes(corps);
            i = donnees + (int)taille.Value;
        }
    }

    /// <summary>Le direct part de maintenant : les grappes deja ecrites ne se renvoient pas.</summary>
    public void AllerALaFin()
    {
        if (_position < 0) return;
        using var f = Ouvrir(_chemin);
        var tete = new byte[12];
        while (EnteteSuivant(f, tete, out var fin, out _, out _)) _position = fin;
    }

    /// <summary>Les grappes completes ecrites depuis la derniere lecture, dans l'ordre.</summary>
    public List<List<Bloc>> NouvellesGrappes()
    {
        var sortie = new List<List<Bloc>>();
        if (_position < 0) return sortie;
        using var f = Ouvrir(_chemin);
        var tete = new byte[12];
        while (EnteteSuivant(f, tete, out var fin, out var id, out var taille))
        {
            if (id == IdCluster)
            {
                var corps = new byte[taille];
                f.Position = fin - taille;
                if (LireTout(f, corps) < corps.Length) break;
                sortie.Add(LireGrappe(corps));
            }
            _position = fin;
        }
        return sortie;
    }

    /// <summary>
    /// L'element qui commence a la position courante, s'il est entierement ecrit : sa fin, son identifiant et la taille
    /// de ses donnees.
    /// </summary>
    private bool EnteteSuivant(FileStream f, byte[] tete, out long fin, out ulong id, out long taille)
    {
        fin = 0;
        id = 0;
        taille = 0;
        var longueur = f.Length;
        if (_position + 2 > longueur) return false;
        f.Position = _position;
        var n = LireTout(f, tete);
        if (!Element(tete.AsSpan(0, n), 0, out id, out var t, out var donnees) || t is null) return false;
        taille = (long)t.Value;
        fin = _position + donnees + taille;
        return fin <= longueur;   // sinon, grappe pas encore complete
    }

    /// <summary>Les blocs d'une grappe, instants en millisecondes. Pur.</summary>
    internal List<Bloc> LireGrappe(ReadOnlySpan<byte> c)
    {
        var blocs = new List<Bloc>();
        ulong temps = 0;
        var i = 0;
        while (i < c.Length && Element(c, i, out var id, out var taille, out var donnees) && taille is not null
               && donnees + (long)taille.Value <= c.Length)
        {
            var corps = c.Slice(donnees, (int)taille.Value);
            if (id == 0xE7) temps = Entier(corps);
            else if (id == 0xA3) Ajouter(blocs, corps, temps, null);
            else if (id == 0xA0)
            {
                ReadOnlySpan<byte> bloc = default;
                var reference = false;
                var j = 0;
                while (j < corps.Length && Element(corps, j, out var id2, out var t2, out var d2) && t2 is not null)
                {
                    if (id2 == 0xA1) bloc = corps.Slice(d2, (int)t2.Value);
                    else if (id2 == 0xFB) reference = true;
                    j = d2 + (int)t2.Value;
                }
                if (!bloc.IsEmpty) Ajouter(blocs, bloc, temps, !reference);
            }
            i = donnees + (int)taille.Value;
        }
        return blocs;
    }

    private void Ajouter(List<Bloc> blocs, ReadOnlySpan<byte> b, ulong temps, bool? cleImposee)
    {
        if (!Vint(b, 0, false, out var piste, out var n, out _) || n + 3 > b.Length) return;
        var relatif = BinaryPrimitives.ReadInt16BigEndian(b.Slice(n, 2));
        var drapeaux = b[n + 2];
        // ffmpeg n'enchaine jamais plusieurs images dans un bloc ; un bloc ainsi fait ne se recopie pas.
        if ((drapeaux & 0x06) != 0) return;
        var cle = cleImposee ?? (drapeaux & 0x80) != 0;
        blocs.Add(new Bloc((int)piste, ((long)temps + relatif) * _msParUnite, cle, b[(n + 3)..].ToArray()));
    }

    private void LireInfo(ReadOnlySpan<byte> b)
    {
        var i = 0;
        while (i < b.Length && Element(b, i, out var id, out var taille, out var donnees) && taille is not null)
        {
            if (id == 0x2AD7B1) _msParUnite = Entier(b.Slice(donnees, (int)taille.Value)) / 1_000_000.0;
            i = donnees + (int)taille.Value;
        }
    }

    private void LirePistes(ReadOnlySpan<byte> b)
    {
        var i = 0;
        while (i < b.Length && Element(b, i, out var id, out var taille, out var donnees) && taille is not null)
        {
            if (id == 0xAE)
            {
                var p = LirePiste(b.Slice(donnees, (int)taille.Value));
                if (p.Numero > 0) Pistes[p.Numero] = p;
            }
            i = donnees + (int)taille.Value;
        }
    }

    private static Piste LirePiste(ReadOnlySpan<byte> b)
    {
        int numero = 0, type = 0, largeur = 0, hauteur = 0;
        var codec = "";
        var prive = Array.Empty<byte>();
        double frequence = 0;
        var i = 0;
        while (i < b.Length && Element(b, i, out var id, out var taille, out var donnees) && taille is not null)
        {
            var v = b.Slice(donnees, (int)taille.Value);
            switch (id)
            {
                case 0xD7: numero = (int)Entier(v); break;
                case 0x83: type = (int)Entier(v); break;
                case 0x86: codec = Encoding.ASCII.GetString(v); break;
                case 0x63A2: prive = v.ToArray(); break;
                case 0xE0:
                    var j = 0;
                    while (j < v.Length && Element(v, j, out var id2, out var t2, out var d2) && t2 is not null)
                    {
                        if (id2 == 0xB0) largeur = (int)Entier(v.Slice(d2, (int)t2.Value));
                        else if (id2 == 0xBA) hauteur = (int)Entier(v.Slice(d2, (int)t2.Value));
                        j = d2 + (int)t2.Value;
                    }
                    break;
                case 0xE1:
                    var k = 0;
                    while (k < v.Length && Element(v, k, out var id3, out var t3, out var d3) && t3 is not null)
                    {
                        if (id3 == 0xB5) frequence = Flottant(v.Slice(d3, (int)t3.Value));
                        k = d3 + (int)t3.Value;
                    }
                    break;
            }
            i = donnees + (int)taille.Value;
        }
        return new Piste(numero, type, codec, prive, largeur, hauteur, frequence);
    }

    /// <summary>Un element EBML : son identifiant, sa taille (null si inconnue) et le debut de ses donnees.</summary>
    internal static bool Element(ReadOnlySpan<byte> b, int i, out ulong id, out ulong? taille, out int donnees)
    {
        id = 0;
        taille = null;
        donnees = 0;
        if (!Vint(b, i, true, out id, out var n, out _)) return false;
        if (!Vint(b, i + n, false, out var t, out var m, out var inconnue)) return false;
        taille = inconnue ? null : t;
        donnees = i + n + m;
        return true;
    }

    internal static bool Vint(ReadOnlySpan<byte> b, int i, bool garder, out ulong v, out int n, out bool inconnue)
    {
        v = 0;
        n = 0;
        inconnue = false;
        if (i < 0 || i >= b.Length) return false;
        var premier = b[i];
        n = 1;
        var masque = 0x80;
        while (n <= 8 && (premier & masque) == 0)
        {
            masque >>= 1;
            n++;
        }
        if (n > 8 || i + n > b.Length) return false;
        v = garder ? premier : (ulong)(premier & (masque - 1));
        for (var k = 1; k < n; k++) v = (v << 8) | b[i + k];
        if (!garder) inconnue = v == (1UL << (7 * n)) - 1;
        return true;
    }

    private static ulong Entier(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        foreach (var x in b) v = (v << 8) | x;
        return v;
    }

    private static double Flottant(ReadOnlySpan<byte> b) => b.Length switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(b),
        8 => BinaryPrimitives.ReadDoubleBigEndian(b),
        _ => 0,
    };

    private static int LireTout(Stream f, byte[] tampon)
    {
        var total = 0;
        while (total < tampon.Length)
        {
            var n = f.Read(tampon, total, tampon.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
