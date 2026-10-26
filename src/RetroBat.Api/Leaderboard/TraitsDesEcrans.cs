using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Les outils de dessin communs aux ecrans MULTIPLAYER v2 (ecran de fin, panneaux en jeu) : polices du Data Pack avec repli
/// sur celle du theme, texte detoure a reliefs, texte espace, couleurs.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TraitsDesEcrans
{
    /// <summary>Le format typographique, qui mesure aussi les espaces de fin.</summary>
    public static readonly StringFormat Typo = CreerTypo();

    public static readonly Color Nuit = Color.FromArgb(11, 8, 32);

    private static StringFormat CreerTypo()
    {
        var format = new StringFormat(StringFormat.GenericTypographic);
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return format;
    }

    public static FontFamily Famille(string nom, Font secours) => PolicesDesEcrans.Famille(nom) ?? secours.FontFamily;

    public static Font Police(string famille, float taille, Font secours, FontStyle style = FontStyle.Regular)
        => PolicesDesEcrans.Police(famille, taille, style) ?? new Font(secours.FontFamily, Math.Max(1f, taille), style, GraphicsUnit.Pixel);

    public static Font Chiffres(float taille, Font secours, bool italique = false)
        => PolicesDesEcrans.Police(StylesDesEcrans.PoliceDesChiffres, taille, italique ? FontStyle.Italic : FontStyle.Regular)
           ?? Police(StylesDesEcrans.PoliceDesLibelles, taille, secours, FontStyle.Bold);

    public static Font Libelles(float taille, Font secours)
        => PolicesDesEcrans.Police(StylesDesEcrans.PoliceDesLibelles, taille, FontStyle.Bold) ?? Police("", taille, secours, FontStyle.Bold);

    public static Font LibellesFins(float taille, Font secours)
        => PolicesDesEcrans.Police("Barlow Condensed SemiBold", taille) ?? Libelles(taille, secours);

    public static Color Hex(string hex, int alpha = 255)
    {
        var (r, v, b, _) = EsMenuStyle.Couleur(hex, alpha);
        return Color.FromArgb(alpha, r, v, b);
    }

    public static Color Alpha(Color c, double opacite) => Color.FromArgb((int)Math.Round(Math.Clamp(opacite, 0, 1) * c.A), c.R, c.G, c.B);

    public static SizeF Mesure(Graphics g, string texte, Font police) => g.MeasureString(texte, police, PointF.Empty, Typo);

    /// <summary>
    /// Un texte detoure, avec ses reliefs : chaque relief est une copie decalee et pleine (la plus lointaine d'abord), puis
    /// le contour, puis le texte. Rend la boite du texte.
    /// </summary>
    public static RectangleF TexteEnRelief(Graphics g, string texte, FontFamily famille, float taille, PointF origine, Color remplissage,
        Color contour, float epaisseur, IReadOnlyList<(float Dx, float Dy, Color Couleur)> reliefs, double opacite = 1, FontStyle style = FontStyle.Regular)
    {
        if (texte.Length == 0 || opacite <= 0.01)
        {
            return RectangleF.Empty;
        }

        using var chemin = new GraphicsPath();
        chemin.AddString(texte, famille, (int)(famille.IsStyleAvailable(style) ? style : FontStyle.Regular), taille, origine, Typo);
        for (var i = reliefs.Count - 1; i >= 0; i--)
        {
            var (dx, dy, couleur) = reliefs[i];
            using var copie = (GraphicsPath)chemin.Clone();
            using var decalage = new Matrix();
            decalage.Translate(dx, dy);
            copie.Transform(decalage);
            using var plein = new SolidBrush(Alpha(couleur, opacite));
            g.FillPath(plein, copie);
            if (epaisseur > 0)
            {
                using var bord = new Pen(Alpha(couleur, opacite), epaisseur) { LineJoin = LineJoin.Round };
                g.DrawPath(bord, copie);
            }
        }

        if (epaisseur > 0)
        {
            using var trait = new Pen(Alpha(contour, opacite), epaisseur) { LineJoin = LineJoin.Round };
            g.DrawPath(trait, chemin);
        }

        using var pinceau = new SolidBrush(Alpha(remplissage, opacite));
        g.FillPath(pinceau, chemin);
        return chemin.GetBounds();
    }

    public static RectangleF BoiteDuTexte(string texte, FontFamily famille, float taille)
    {
        using var chemin = new GraphicsPath();
        chemin.AddString(texte, famille, (int)FontStyle.Regular, taille, PointF.Empty, Typo);
        return chemin.GetBounds();
    }

    /// <summary>La taille a laquelle <paramref name="texte"/> tient dans <paramref name="largeur"/>, sans depasser <paramref name="taille"/>.</summary>
    public static float TailleQuiTient(string texte, FontFamily famille, float taille, float largeur)
    {
        var boite = BoiteDuTexte(texte, famille, taille);
        return boite.Width <= largeur || boite.Width <= 0 ? taille : taille * largeur / boite.Width;
    }

    public static void TexteEspace(Graphics g, string texte, Font police, Color couleur, float x, float y, float espacement)
    {
        using var pinceau = new SolidBrush(couleur);
        foreach (var c in texte)
        {
            var morceau = c.ToString();
            g.DrawString(morceau, police, pinceau, x, y, Typo);
            x += Mesure(g, morceau, police).Width + espacement;
        }
    }

    public static float LargeurEspacee(Graphics g, string texte, Font police, float espacement)
        => texte.Sum(c => Mesure(g, c.ToString(), police).Width) + espacement * Math.Max(0, texte.Length - 1);
}
