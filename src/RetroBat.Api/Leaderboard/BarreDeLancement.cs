using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.Versioning;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LA BARRE DISCRETE DU BAS DES ECRANS DE LANCEMENT (demande du user du 13/11) : une ligne fine et, juste au-dessus, ce qui se
/// passe (« LOADING PLAYER DECK… ») et le compte des etapes (« 3/6 »). Un seul dessin pour tous les ecrans : l'ecran de
/// lancement plein ecran (speedrun, challenge, World Scoring, MULTIPLAYER, MASS RUN) et la boite d'attente d'ES. Elle lit la
/// <see cref="ProgressionDuLancement"/> a chaque image ; elle ne decide rien et n'attend rien.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BarreDeLancement
{
    /// <summary>Les couleurs d'un ecran : le texte, la piste (le fond de la ligne), le plein, le plein d'un echec.</summary>
    internal sealed record Teintes(Color Encre, Color Piste, Color Plein, Color Echec);

    /// <summary>La charte des ecrans de lancement (ecran de fin, VS) : lavande pale, vert des etapes faites, rouge des pertes.</summary>
    internal static readonly Teintes DesEcrans = new(
        Color.FromArgb(201, 195, 255),
        Color.FromArgb(38, 255, 255, 255),
        Color.FromArgb(57, 229, 140),
        Color.FromArgb(229, 83, 75));

    /// <summary>Sur l'ecran de lancement (repere 1920 x 1080) : sous le conseil et les etapes (qui finissent vers 1040), a la marge des cartes.</summary>
    internal static readonly RectangleF ZoneDesEcrans = new(64, 1050, 1792, 26);

    /// <summary>L'epaisseur de la ligne, en pixels du repere : fine.</summary>
    internal const float Epaisseur = 4;

    /// <summary>
    /// Dessine la barre dans <paramref name="zone"/> : le texte en haut a gauche, le compte en haut a droite, la ligne en bas.
    /// <paramref name="part"/> : la part a montrer (la vraie, ou la vraie lissee sur quelques images) ; <paramref name="t"/> :
    /// le temps de l'ecran, pour le point qui bat en tete de ligne tant que tout n'est pas fait.
    /// </summary>
    internal static void Dessiner(Graphics g, ProgressionDuLancement.Vue vue, double part, RectangleF zone, Font police, Teintes teintes,
        double opacite, double t, float epaisseur = Epaisseur, float espacement = 2.5f)
    {
        if (opacite <= 0.01)
        {
            return;
        }

        var texte = vue.Texte.ToUpperInvariant();
        var compte = vue.Faites.ToString(CultureInfo.InvariantCulture) + "/" + vue.Total.ToString(CultureInfo.InvariantCulture);
        var encre = TraitsDesEcrans.Alpha(teintes.Encre, opacite * 0.9);
        var largeurCompte = TraitsDesEcrans.LargeurEspacee(g, compte, police, espacement);
        TraitsDesEcrans.TexteEspace(g, compte, police, encre, zone.Right - largeurCompte, zone.Y, espacement);
        // Un texte trop long pour la zone (boite d'attente etroite) se coupe proprement plutot que de passer sous le compte.
        var place = zone.Width - largeurCompte - 24;
        while (texte.Length > 4 && TraitsDesEcrans.LargeurEspacee(g, texte, police, espacement) > place)
        {
            texte = texte[..^2].TrimEnd() + "…";
        }

        TraitsDesEcrans.TexteEspace(g, texte, police, encre, zone.X, zone.Y, espacement);

        var y = zone.Bottom - epaisseur;
        var piste = new RectangleF(zone.X, y, zone.Width, epaisseur);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using (var fond = new SolidBrush(TraitsDesEcrans.Alpha(teintes.Piste, opacite)))
            using (var forme = FormesDesEcrans.Arrondi(piste, epaisseur / 2))
            {
                g.FillPath(fond, forme);
            }

            var largeur = (float)(Math.Clamp(part, 0, 1) * zone.Width);
            var couleur = vue.Echec ? teintes.Echec : teintes.Plein;
            if (largeur >= epaisseur)
            {
                using var plein = new SolidBrush(TraitsDesEcrans.Alpha(couleur, opacite));
                using var forme = FormesDesEcrans.Arrondi(new RectangleF(zone.X, y, largeur, epaisseur), epaisseur / 2);
                g.FillPath(plein, forme);
            }

            // Le point qui bat en tete : le lancement vit, sans rien faire croire de plus que la part reelle.
            if (!vue.Finie)
            {
                var battement = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 1.1);
                var rayon = epaisseur * (1.1f + 0.5f * (float)battement);
                var centre = new PointF(zone.X + Math.Max(largeur, epaisseur / 2), y + epaisseur / 2);
                using var halo = new SolidBrush(TraitsDesEcrans.Alpha(couleur, opacite * (0.25 + 0.35 * battement)));
                g.FillEllipse(halo, centre.X - rayon * 1.8f, centre.Y - rayon * 1.8f, rayon * 3.6f, rayon * 3.6f);
                using var point = new SolidBrush(TraitsDesEcrans.Alpha(couleur, opacite));
                g.FillEllipse(point, centre.X - rayon, centre.Y - rayon, rayon * 2, rayon * 2);
            }
        }
        finally
        {
            g.SmoothingMode = mode;
        }
    }

    /// <summary>
    /// La part montree, lissee : elle rejoint la vraie en quelques images (jamais au-dela, jamais en avance). Pure.
    /// </summary>
    internal static double Lisser(double montree, double vraie, double secondes)
    {
        if (vraie <= montree || secondes <= 0) return vraie;
        var pas = 1 - Math.Exp(-secondes / 0.12);
        var suivante = montree + (vraie - montree) * pas;
        return vraie - suivante < 0.002 ? vraie : suivante;
    }
}
