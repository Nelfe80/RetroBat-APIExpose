using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LA COULEUR DU TITRE TIREE DU FANART (CDC Ecrans v1.0, 1.2 ; decision du user du 26/10).
/// 1. Les couleurs vives de la palette presentes dans le fanart : au moins 5 % des pixels tres satures (S >= 0,65,
///    V >= 0,5), chacun votant pour la teinte de la palette la plus proche ; la peau (orange pale) ne vote pas.
/// 2. Sans les couleurs des places en jeu : un titre a la couleur d'un joueur se confondrait avec lui.
/// 3. Celle qui s'ecarte le plus de la teinte moyenne de la zone du titre, d'au moins 45 degres.
/// 4. Sinon la couleur du type ; si c'est celle d'une place en jeu (le vert du combat contre P4), la suivante.
/// Le relief du titre prend la couleur suivante la plus presente.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CouleurDuTitre
{
    /// <summary>La palette et la teinte (en degres) de chaque couleur.</summary>
    internal static readonly IReadOnlyList<(string Hex, double Teinte)> Palette =
    [
        ("d4ff3a", 75), ("ffd23c", 45), ("ff8a3d", 25), ("ff3b4f", 355), ("ff4fa3", 330),
        ("a259ff", 270), ("3d7bff", 222), ("2fd5ff", 193), ("39e58c", 150),
    ];

    /// <summary>La couleur de la palette que chaque place rend interdite (P4, vert, interdit le vert acide).</summary>
    private static readonly IReadOnlyDictionary<int, string> InterditeParLaPlace = new Dictionary<int, string>
    {
        [1] = "a259ff", [2] = "ff8a3d", [3] = "2fd5ff", [4] = "d4ff3a",
    };

    /// <summary>La zone du titre, en pixels d'un ecran 1920 x 1080 (le haut gauche de l'ecran de fin).</summary>
    private static readonly RectangleF ZoneDuTitre = new(0, 60, 1000, 280);

    public static (string Titre, string Relief) Choisir(Bitmap? fanart, IEnumerable<int> placesEnJeu, string couleurDuType)
    {
        var interdites = placesEnJeu.Where(InterditeParLaPlace.ContainsKey).Select(p => InterditeParLaPlace[p]).ToHashSet(StringComparer.Ordinal);
        var scores = fanart is null ? Palette.ToDictionary(p => p.Hex, _ => 0d) : Votes(fanart);
        var total = scores.Values.Sum();
        var candidates = total <= 0 ? [] : Palette.Select(p => p.Hex)
            .Where(c => scores[c] / total >= 0.05 && !interdites.Contains(c))
            .ToList();

        string? titre = null;
        if (candidates.Count > 0)
        {
            var fond = fanart is null ? null : TeinteDeLaZone(fanart);
            if (fond is null)
            {
                titre = candidates.MaxBy(c => scores[c]);
            }
            else
            {
                var meilleure = candidates.MaxBy(c => Ecart(TeinteDe(c), fond.Value))!;
                if (Ecart(TeinteDe(meilleure), fond.Value) >= 45)
                {
                    titre = meilleure;
                }
            }
        }

        titre ??= interdites.Contains(couleurDuType)
            ? Palette.Select(p => p.Hex).First(c => !interdites.Contains(c) && c != couleurDuType)
            : couleurDuType;
        var relief = Palette.Select(p => p.Hex)
            .Where(c => c != titre && !interdites.Contains(c) && total > 0 && scores[c] / total >= 0.05)
            .OrderByDescending(c => scores[c])
            .FirstOrDefault() ?? (titre == "ffd23c" ? "ffffff" : "ffd23c");
        return (titre, relief);
    }

    private static double TeinteDe(string hex) => Palette.First(p => p.Hex == hex).Teinte;

    internal static double Ecart(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return Math.Min(d, 360 - d);
    }

    /// <summary>Les votes des pixels tres satures, ponderes par S² x V, sur une image reduite a 96 x 54.</summary>
    internal static Dictionary<string, double> Votes(Bitmap fanart)
    {
        var scores = Palette.ToDictionary(p => p.Hex, _ => 0d);
        foreach (var (h, s, v) in Pixels(fanart, new RectangleF(0, 0, fanart.Width, fanart.Height), 96, 54))
        {
            if (s < 0.65 || v < 0.5 || (h is >= 18 and <= 40 && s < 0.8))
            {
                continue;
            }

            var proche = Palette.MinBy(p => Ecart(p.Teinte, h)).Hex;
            scores[proche] += s * s * v;
        }

        return scores;
    }

    /// <summary>La teinte moyenne (ponderee par S x V) de la zone du titre, ou null si elle est neutre.</summary>
    internal static double? TeinteDeLaZone(Bitmap fanart)
    {
        var echelleX = fanart.Width / 1920f;
        var echelleY = fanart.Height / 1080f;
        var zone = new RectangleF(ZoneDuTitre.X * echelleX, ZoneDuTitre.Y * echelleY, ZoneDuTitre.Width * echelleX, ZoneDuTitre.Height * echelleY);
        double x = 0, y = 0, poids = 0;
        foreach (var (h, s, v) in Pixels(fanart, zone, 60, 17))
        {
            var w = s * v;
            x += w * Math.Cos(h * Math.PI / 180);
            y += w * Math.Sin(h * Math.PI / 180);
            poids += w;
        }

        if (poids <= 0 || Math.Sqrt(x * x + y * y) / poids < 0.15)
        {
            return null;
        }

        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static IEnumerable<(double H, double S, double V)> Pixels(Bitmap source, RectangleF zone, int largeur, int hauteur)
    {
        using var petite = new Bitmap(largeur, hauteur, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(petite))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, new Rectangle(0, 0, largeur, hauteur), zone, GraphicsUnit.Pixel);
        }

        var donnees = petite.LockBits(new Rectangle(0, 0, largeur, hauteur), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var octets = new byte[donnees.Stride * hauteur];
        System.Runtime.InteropServices.Marshal.Copy(donnees.Scan0, octets, 0, octets.Length);
        petite.UnlockBits(donnees);
        var pixels = new List<(double, double, double)>(largeur * hauteur);
        for (var ligne = 0; ligne < hauteur; ligne++)
        {
            for (var colonne = 0; colonne < largeur; colonne++)
            {
                var i = ligne * donnees.Stride + colonne * 4;
                pixels.Add(Hsv(octets[i + 2] / 255d, octets[i + 1] / 255d, octets[i] / 255d));
            }
        }

        return pixels;
    }

    internal static (double H, double S, double V) Hsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var s = max <= 0 ? 0 : delta / max;
        double h;
        if (delta <= 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * (((b - r) / delta) + 2);
        else h = 60 * (((r - g) / delta) + 4);
        return ((h + 360) % 360, s, max);
    }
}
