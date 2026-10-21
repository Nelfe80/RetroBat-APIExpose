using System.Drawing;
using System.Runtime.Versioning;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Les drapeaux sous le nom d'un joueur (CDC MULTIPLAYER, V24-18 : le pays, jamais la ville), dessines a la main
/// en quelques aplats : bandes, cercle, croix. Un pays absent de la table n'a pas de drapeau : la ligne garde
/// alors son code a deux lettres. Simplifies a dessein (ni blason ni etoiles) : a cette taille, seules les
/// couleurs se lisent.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Drapeaux
{
    private enum Forme
    {
        BandesVerticales,
        BandesHorizontales,
        Cercle,
        Croix,
        CroixNordique,
        Etoiles,
    }

    private sealed record Drapeau(Forme Forme, string[] Couleurs, float[]? Poids = null);

    private static readonly Dictionary<string, Drapeau> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FR"] = new(Forme.BandesVerticales, ["0055A4", "FFFFFF", "EF4135"]),
        ["BE"] = new(Forme.BandesVerticales, ["1A1A1A", "FDDA24", "EF3340"]),
        ["IT"] = new(Forme.BandesVerticales, ["009246", "FFFFFF", "CE2B37"]),
        ["IE"] = new(Forme.BandesVerticales, ["169B62", "FFFFFF", "FF883E"]),
        ["RO"] = new(Forme.BandesVerticales, ["002B7F", "FCD116", "CE1126"]),
        ["MX"] = new(Forme.BandesVerticales, ["006847", "FFFFFF", "CE1126"]),
        ["CA"] = new(Forme.BandesVerticales, ["D52B1E", "FFFFFF", "D52B1E"], [1, 2, 1]),
        ["PT"] = new(Forme.BandesVerticales, ["006600", "FF0000"], [2, 3]),
        ["DE"] = new(Forme.BandesHorizontales, ["1A1A1A", "DD0000", "FFCE00"]),
        ["NL"] = new(Forme.BandesHorizontales, ["AE1C28", "FFFFFF", "21468B"]),
        ["LU"] = new(Forme.BandesHorizontales, ["ED2939", "FFFFFF", "00A1DE"]),
        ["RU"] = new(Forme.BandesHorizontales, ["FFFFFF", "0039A6", "D52B1E"]),
        ["AT"] = new(Forme.BandesHorizontales, ["ED2939", "FFFFFF", "ED2939"]),
        ["HU"] = new(Forme.BandesHorizontales, ["CD2A3E", "FFFFFF", "436F4D"]),
        ["BG"] = new(Forme.BandesHorizontales, ["FFFFFF", "00966E", "D62612"]),
        ["ES"] = new(Forme.BandesHorizontales, ["AA151B", "F1BF00", "AA151B"], [1, 2, 1]),
        ["PL"] = new(Forme.BandesHorizontales, ["FFFFFF", "DC143C"]),
        ["UA"] = new(Forme.BandesHorizontales, ["0057B7", "FFD700"]),
        ["AR"] = new(Forme.BandesHorizontales, ["74ACDF", "FFFFFF", "74ACDF"]),
        ["JP"] = new(Forme.Cercle, ["FFFFFF", "BC002D"]),
        ["BR"] = new(Forme.Cercle, ["009C3B", "FFDF00"]),
        ["CH"] = new(Forme.Croix, ["DA291C", "FFFFFF"]),
        ["GB"] = new(Forme.Croix, ["012169", "C8102E"]),
        ["SE"] = new(Forme.CroixNordique, ["006AA7", "FECC00"]),
        ["FI"] = new(Forme.CroixNordique, ["FFFFFF", "002F6C"]),
        ["DK"] = new(Forme.CroixNordique, ["C8102E", "FFFFFF"]),
        ["NO"] = new(Forme.CroixNordique, ["BA0C2F", "FFFFFF"]),
        ["US"] = new(Forme.Etoiles, ["B22234", "FFFFFF", "3C3B6E"]),
    };

    /// <summary>Dessine le drapeau de ce pays dans la zone ; faux s'il n'est pas dans la table.</summary>
    public static bool Dessiner(Graphics g, string pays, RectangleF zone, int alpha = 255)
    {
        if (!Table.TryGetValue(pays.Trim(), out var drapeau))
        {
            return false;
        }

        Color C(int i) => Couleur(drapeau.Couleurs[i], alpha);
        var etat = g.Save();
        g.SetClip(zone);
        switch (drapeau.Forme)
        {
            case Forme.BandesVerticales:
            case Forme.BandesHorizontales:
            {
                var poids = drapeau.Poids ?? Enumerable.Repeat(1f, drapeau.Couleurs.Length).ToArray();
                var total = poids.Sum();
                var debut = 0f;
                for (var i = 0; i < drapeau.Couleurs.Length; i++)
                {
                    var part = poids[i] / total;
                    var bande = drapeau.Forme == Forme.BandesVerticales
                        ? new RectangleF(zone.X + zone.Width * debut, zone.Y, zone.Width * part + 0.5f, zone.Height)
                        : new RectangleF(zone.X, zone.Y + zone.Height * debut, zone.Width, zone.Height * part + 0.5f);
                    using var pinceau = new SolidBrush(C(i));
                    g.FillRectangle(pinceau, bande);
                    debut += part;
                }

                break;
            }

            case Forme.Cercle:
            {
                using var fond = new SolidBrush(C(0));
                using var disque = new SolidBrush(C(1));
                g.FillRectangle(fond, zone);
                var d = zone.Height * 0.6f;
                g.FillEllipse(disque, zone.X + (zone.Width - d) / 2f, zone.Y + (zone.Height - d) / 2f, d, d);
                break;
            }

            case Forme.Croix:
            case Forme.CroixNordique:
            {
                using var fond = new SolidBrush(C(0));
                using var croix = new SolidBrush(C(1));
                g.FillRectangle(fond, zone);
                var epaisseur = zone.Height * 0.22f;
                var x = drapeau.Forme == Forme.CroixNordique ? zone.X + zone.Width * 0.36f : zone.X + (zone.Width - epaisseur) / 2f;
                g.FillRectangle(croix, x, zone.Y, epaisseur, zone.Height);
                g.FillRectangle(croix, zone.X, zone.Y + (zone.Height - epaisseur) / 2f, zone.Width, epaisseur);
                break;
            }

            case Forme.Etoiles:
            {
                var hauteurBande = zone.Height / 7f;
                for (var i = 0; i < 7; i++)
                {
                    using var bande = new SolidBrush(C(i % 2));
                    g.FillRectangle(bande, zone.X, zone.Y + i * hauteurBande, zone.Width, hauteurBande + 0.5f);
                }

                using var canton = new SolidBrush(C(2));
                g.FillRectangle(canton, zone.X, zone.Y, zone.Width * 0.42f, hauteurBande * 4f);
                break;
            }
        }

        g.Restore(etat);
        using var bord = new Pen(Color.FromArgb(Math.Min(alpha, 70), 255, 255, 255), 1f);
        g.DrawRectangle(bord, zone.X, zone.Y, zone.Width, zone.Height);
        return true;
    }

    private static Color Couleur(string hex, int alpha)
    {
        var (r, v, b, _) = EsMenuStyle.Couleur(hex);
        return Color.FromArgb(alpha, r, v, b);
    }
}
