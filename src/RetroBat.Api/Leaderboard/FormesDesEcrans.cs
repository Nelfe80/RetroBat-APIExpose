using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LES FORMES DES ECRANS MULTIPLAYER (CDC Ecrans v1.0) : le portrait d'un personnage choisi prend la forme de son type de
/// jeu ; <c>miroir</c> tourne la forme vers l'adversaire (les places paires sont a droite).
/// </summary>
[SupportedOSPlatform("windows")]
public static class FormesDesEcrans
{
    public static GraphicsPath Portrait(FormeDuPortrait forme, RectangleF r, bool miroir)
    {
        var chemin = new GraphicsPath();
        if (forme == FormeDuPortrait.Bulle)
        {
            var cote = Math.Min(r.Width, r.Height);
            chemin.AddEllipse(r.X + (r.Width - cote) / 2f, r.Y + (r.Height - cote) / 2f, cote, cote);
            return chemin;
        }

        // Les sommets en fractions du rectangle, du coin haut gauche dans le sens des aiguilles d'une montre.
        (float X, float Y)[] sommets = forme switch
        {
            FormeDuPortrait.Eclat => [(0, 0.07f), (1, 0), (1, 0.93f), (0, 1)],
            FormeDuPortrait.Parallelogramme => [(0.18f, 0), (1, 0), (0.82f, 1), (0, 1)],
            FormeDuPortrait.Ecusson => [(0, 0), (1, 0), (1, 0.7f), (0.5f, 1), (0, 0.7f)],
            FormeDuPortrait.Blason => [(0.3f, 0), (0.7f, 0), (1, 0.22f), (1, 0.78f), (0.7f, 1), (0.3f, 1), (0, 0.78f), (0, 0.22f)],
            FormeDuPortrait.Hexagone => [(0.5f, 0), (1, 0.22f), (1, 0.78f), (0.5f, 1), (0, 0.78f), (0, 0.22f)],
            _ => [(0.5f, 0), (1, 0.5f), (0.5f, 1), (0, 0.5f)],
        };
        chemin.AddPolygon(sommets.Select(s => new PointF(r.X + (miroir ? 1 - s.X : s.X) * r.Width, r.Y + s.Y * r.Height)).ToArray());
        return chemin;
    }

    /// <summary>L'hexagone a pointes laterales de la lettre de rang.</summary>
    public static GraphicsPath Hexagone(RectangleF r)
    {
        var chemin = new GraphicsPath();
        chemin.AddPolygon(
        [
            new PointF(r.X + r.Width * 0.25f, r.Y), new PointF(r.X + r.Width * 0.75f, r.Y), new PointF(r.Right, r.Y + r.Height / 2f),
            new PointF(r.X + r.Width * 0.75f, r.Bottom), new PointF(r.X + r.Width * 0.25f, r.Bottom), new PointF(r.X, r.Y + r.Height / 2f),
        ]);
        return chemin;
    }

    public static GraphicsPath Arrondi(RectangleF r, float rayon)
    {
        var chemin = new GraphicsPath();
        var d = Math.Max(0.5f, Math.Min(rayon * 2, Math.Min(r.Width, r.Height)));
        chemin.AddArc(r.X, r.Y, d, d, 180, 90);
        chemin.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        chemin.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        chemin.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        chemin.CloseFigure();
        return chemin;
    }
}

/// <summary>
/// LES ICONES PIXEL des mesures et des recompenses, dessinees comme en jeu : des carres pleins sur une grille de 7 a 9 cases,
/// sans lissage. Chaque icone est ecrite comme sur la maquette (suite de « Mx yhLvHh-Lz »).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class IconesPixel
{
    private static readonly Dictionary<string, (int Largeur, int Hauteur, Rectangle[] Cases)> Icones = new(StringComparer.Ordinal)
    {
        ["gant"] = Lire(9, 9, "M2 0h5v1h-5zM1 1h7v1h-7zM0 2h8v1h-8zM0 3h6v1h-6zM7 3h2v1h-2zM0 4h6v1h-6zM7 4h2v1h-2zM0 5h9v1h-9zM1 6h7v1h-7zM2 8h5v1h-5z"),
        ["eclair"] = Lire(7, 9, "M3 0h4v1h-4zM2 1h4v1h-4zM1 2h4v1h-4zM0 3h7v1h-7zM3 4h3v1h-3zM2 5h3v1h-3zM1 6h3v1h-3zM1 7h2v1h-2zM0 8h2v1h-2z"),
        ["diamant"] = Lire(9, 7, "M2 0h5v1h-5zM1 1h7v1h-7zM0 2h9v1h-9zM1 3h7v1h-7zM2 4h5v1h-5zM3 5h3v1h-3zM4 6h1v1h-1z"),
        ["etoile"] = Lire(9, 9, "M4 0h1v1h-1zM4 1h1v1h-1zM3 2h3v1h-3zM0 3h9v1h-9zM1 4h7v1h-7zM2 5h5v1h-5zM2 6h5v1h-5zM1 7h3v1h-3zM5 7h3v1h-3zM1 8h2v1h-2zM6 8h2v1h-2z"),
        ["couronne"] = Lire(9, 5, "M0 0h1v1h-1zM4 0h1v1h-1zM8 0h1v1h-1zM0 1h2v1h-2zM3 1h3v1h-3zM7 1h2v1h-2zM0 2h9v3h-9z"),
        ["joyaux"] = Lire(9, 5, "M2 3h1v1h-1zM4 3h1v1h-1zM6 3h1v1h-1z"),
        ["crane"] = Lire(8, 8, "M2 0h4v1h-4zM1 1h6v1h-6zM0 2h8v1h-8zM0 3h2v1h-2zM3 3h2v1h-2zM6 3h2v1h-2zM0 4h8v1h-8zM1 5h6v1h-6zM2 6h4v1h-4zM2 7h1v1h-1zM5 7h1v1h-1z"),
        ["chrono"] = Lire(9, 8, "M3 0h3v1h-3zM2 1h5v1h-5zM1 2h1v1h-1zM4 2h1v1h-1zM7 2h1v1h-1zM0 3h1v1h-1zM4 3h1v1h-1zM8 3h1v1h-1zM0 4h1v1h-1zM4 4h3v1h-3zM8 4h1v1h-1zM0 5h1v1h-1zM8 5h1v1h-1zM1 6h1v1h-1zM7 6h1v1h-1zM2 7h5v1h-5z"),
        ["chevrons"] = Lire(9, 7, "M0 0h2v1h-2zM4 0h2v1h-2zM1 1h2v1h-2zM5 1h2v1h-2zM2 2h2v1h-2zM6 2h2v1h-2zM3 3h2v1h-2zM7 3h2v1h-2zM2 4h2v1h-2zM6 4h2v1h-2zM1 5h2v1h-2zM5 5h2v1h-2zM0 6h2v1h-2zM4 6h2v1h-2z"),
        ["bille"] = Lire(7, 7, "M2 0h3v1h-3zM1 1h5v1h-5zM0 2h7v3h-7zM1 5h5v1h-5zM2 6h3v1h-3z"),
        ["coeur"] = Lire(9, 8, "M1 0h2v1h-2zM6 0h2v1h-2zM0 1h4v1h-4zM5 1h4v1h-4zM0 2h9v2h-9zM1 4h7v1h-7zM2 5h5v1h-5zM3 6h3v1h-3zM4 7h1v1h-1z"),
        ["trophee"] = Lire(9, 9, "M2 0h5v1h-5zM0 1h9v1h-9zM0 2h1v1h-1zM2 2h5v1h-5zM8 2h1v1h-1zM1 3h7v1h-7zM3 4h3v1h-3zM4 5h1v2h-1zM2 7h5v2h-5z"),
        ["flamme"] = Lire(7, 9, "M3 0h1v1h-1zM3 1h2v1h-2zM2 2h3v1h-3zM1 3h4v1h-4zM6 3h1v1h-1zM1 4h6v1h-6zM0 5h7v2h-7zM1 7h5v1h-5zM2 8h3v1h-3z"),
    };

    /// <summary>L'icone d'une mesure : la sienne si elle est connue, sinon celle de sa categorie.</summary>
    public static string PourLaMesure(string id, string categorie) => id switch
    {
        "coups" or "combos" => "gant",
        "degats" or "vitesse" => "eclair",
        "perfects" => "diamant",
        "ennemis" or "ko" => "crane",
        "tour" or "meilleur_tour" or "temps" => "chrono",
        "depassements" or "attaques" => "chevrons",
        "billes" or "chaines" => "bille",
        "vies" => "coeur",
        _ => categorie switch { "efficacite" => "eclair", "maitrise" => "diamant", _ => "gant" },
    };

    /// <summary>Le rapport largeur sur hauteur d'une icone (1 si elle est inconnue).</summary>
    public static float Rapport(string nom) => Icones.TryGetValue(nom, out var i) ? i.Largeur / (float)i.Hauteur : 1f;

    /// <summary>L'icone centree dans <paramref name="zone"/>, a la plus grande taille entiere de case qui y tienne.</summary>
    public static void Dessiner(Graphics g, string nom, RectangleF zone, Color couleur)
    {
        if (!Icones.TryGetValue(nom, out var icone))
        {
            return;
        }

        var cote = Math.Max(1f, MathF.Floor(Math.Min(zone.Width / icone.Largeur, zone.Height / icone.Hauteur)));
        var x0 = zone.X + (zone.Width - cote * icone.Largeur) / 2f;
        var y0 = zone.Y + (zone.Height - cote * icone.Hauteur) / 2f;
        var lissage = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using var pinceau = new SolidBrush(couleur);
        foreach (var c in icone.Cases)
        {
            g.FillRectangle(pinceau, x0 + c.X * cote, y0 + c.Y * cote, c.Width * cote, c.Height * cote);
        }

        g.SmoothingMode = lissage;
    }

    private static (int, int, Rectangle[]) Lire(int largeur, int hauteur, string chemin)
        => (largeur, hauteur, Case().Matches(chemin)
            .Select(m => new Rectangle(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)))
            .ToArray());

    [GeneratedRegex(@"M(\d+) (\d+)h(\d+)v(\d+)h-\d+z")]
    private static partial Regex Case();
}
