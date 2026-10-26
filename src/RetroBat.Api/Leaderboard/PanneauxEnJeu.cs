using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using RetroBat.Domain.Paths;
using static RetroBat.Api.Leaderboard.TraitsDesEcrans;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LES PANNEAUX EN JEU (CDC Ecrans v1.0, chapitre 3, lot E3) : un panneau par joueur dans les bandes du decor, rendu UNE
/// FOIS en image a plat a la taille de l'ecran, le centre transparent. RetroArch l'affiche comme un decor (overlay) : rien
/// n'est pose par-dessus le jeu, donc ni saccade ni question de focus. La meme image servira aux directs et aux replays.
///
/// Les places impaires a gauche, les paires a droite ; deux joueurs par bande au-dela de deux. Le portrait est le
/// personnage choisi dans le jeu, dans la forme de son type ; sans personnage, le pseudo remonte sous « P1 ».
/// </summary>
[SupportedOSPlatform("windows")]
public static class PanneauxEnJeu
{
    /// <summary>Un joueur : <c>Palier</c> va de 0 a 1 vers la lettre suivante ; <c>PingMs</c>, la latence vers le relais.</summary>
    public sealed record Joueur(int Place, string Pseudo, string Personnage, string? Portrait, string Lettre, string Cote, double? Palier, int? PingMs);

    /// <summary>Les bandes de part et d'autre du jeu, en pixels de l'ecran.</summary>
    public sealed record Bandes(int Gauche, int Droite, int Jeu);

    /// <summary>
    /// Les bandes d'un jeu de <paramref name="formatDuJeu"/> (largeur sur hauteur) affiche sur toute la hauteur de l'ecran, ou
    /// null si elles sont trop etroites pour un panneau (moins d'un dixieme de l'ecran chacune).
    /// </summary>
    public static Bandes? Calculer(int largeur, int hauteur, double formatDuJeu)
    {
        var jeu = (int)Math.Round(hauteur * formatDuJeu);
        var gauche = (largeur - jeu) / 2;
        var droite = largeur - jeu - gauche;
        return largeur <= 0 || hauteur <= 0 || Math.Min(gauche, droite) < largeur * 0.1 ? null : new(gauche, droite, jeu);
    }

    public static Bitmap Dessiner(int largeur, int hauteur, Bandes bandes, string type, string? fanart, IReadOnlyList<Joueur> joueurs)
    {
        var image = new Bitmap(largeur, hauteur, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var secours = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        var style = StylesDesEcrans.Pour(type);
        var famille = Famille(style.Police, secours);
        using var fond = Charger(fanart);
        using var logo = Charger(Path.Combine(RetroBatPaths.ThemeResourcesRoot, "images", "nelfeplay-multiplayer.png"));
        foreach (var (gaucheDeLaBande, largeurDeLaBande, cote) in new[] { (0, bandes.Gauche, 1), (largeur - bandes.Droite, bandes.Droite, 0) })
        {
            var ici = joueurs.Where(j => j.Place % 2 == cote).OrderBy(j => j.Place).ToList();
            var bande = new Rectangle(gaucheDeLaBande, 0, largeurDeLaBande, hauteur);
            DessinerLaBande(g, bande, fond, largeur, hauteur, ici, cote == 1);
            var k = hauteur / 1080f;
            var etat = g.Save();
            g.SetClip(bande);
            g.TranslateTransform(bande.X, 0);
            g.ScaleTransform(k, k);
            var largeurUtile = bande.Width / k;
            if (ici.Count == 1)
            {
                DessinerUnPanneau(g, ici[0], style, famille, secours, logo, largeurUtile, 0, 1080, cote == 1, compact: false);
            }
            else
            {
                for (var i = 0; i < ici.Count && i < 2; i++)
                {
                    DessinerUnPanneau(g, ici[i], style, famille, secours, i == 0 ? logo : null, largeurUtile, i * 540, 540, cote == 1, compact: true);
                }
            }

            g.Restore(etat);
        }

        return image;
    }

    private static Bitmap? Charger(string? chemin)
    {
        try
        {
            if (chemin is null || !File.Exists(chemin))
            {
                return null;
            }

            using var flux = File.OpenRead(chemin);
            using var lue = System.Drawing.Image.FromStream(flux);
            return new Bitmap(lue);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Le fond d'une bande : le fanart net, opaque en haut puis efface vers le bas, sous un voile de la couleur de la place
    /// (le premier joueur de la bande) et un voile sombre ; un filet de la couleur du joueur borde le jeu.
    /// </summary>
    private static void DessinerLaBande(Graphics g, Rectangle bande, Bitmap? fanart, int largeur, int hauteur, IReadOnlyList<Joueur> ici, bool aGauche)
    {
        var couleur = Hex(StylesDesEcrans.CouleurDeLaPlace(ici.FirstOrDefault()?.Place));
        using (var nuit = new SolidBrush(Nuit))
        {
            g.FillRectangle(nuit, bande);
        }

        if (fanart is not null)
        {
            var echelle = Math.Max(largeur / (float)fanart.Width, hauteur / (float)fanart.Height);
            var w = fanart.Width * echelle;
            var h = fanart.Height * echelle;
            var clip = g.Clip;
            g.SetClip(bande);
            g.DrawImage(fanart, (largeur - w) / 2f, (hauteur - h) / 2f, w, h);
            g.Clip = clip;
            // Le fanart s'efface vers le bas : le fond sombre le recouvre progressivement.
            using var efface = new LinearGradientBrush(bande, Color.Transparent, Color.Transparent, LinearGradientMode.Vertical)
            {
                InterpolationColors = new ColorBlend
                {
                    Positions = [0f, 0.34f, 0.6f, 0.84f, 1f],
                    Colors = [Color.FromArgb(0, Nuit), Color.FromArgb(0, Nuit), Color.FromArgb(166, Nuit), Color.FromArgb(255, Nuit), Color.FromArgb(255, Nuit)],
                },
            };
            g.FillRectangle(efface, bande);
        }

        // Le voile : la teinte de la place, assombrie, puis un voile sombre (le motif ne lutte pas avec le texte).
        using (var teinte = new SolidBrush(Color.FromArgb(118, couleur.R / 3, couleur.G / 3, couleur.B / 3)))
        using (var voile = new SolidBrush(Color.FromArgb(82, 10, 6, 24)))
        {
            g.FillRectangle(teinte, bande);
            g.FillRectangle(voile, bande);
        }

        using (var lueur = new LinearGradientBrush(bande, Color.Transparent, Color.Transparent, LinearGradientMode.Vertical)
        {
            InterpolationColors = new ColorBlend
            {
                Positions = [0f, 0.4f, 1f],
                Colors = [Color.FromArgb(0, couleur), Color.FromArgb(0, couleur), Color.FromArgb(70, couleur)],
            },
        })
        {
            g.FillRectangle(lueur, bande);
        }

        var k = hauteur / 1080f;
        var filet = Math.Max(2, (int)Math.Round(4 * k));
        using var trait = new SolidBrush(couleur);
        g.FillRectangle(trait, aGauche ? bande.Right - filet : bande.X, 0, filet, hauteur);
    }

    /// <summary>
    /// Un panneau, en pixels d'un ecran de 1080 de haut : le logo, « P1 », le personnage dans sa forme (ou le pseudo
    /// remonte), le pseudo, la lettre de rang et l'ELO, et le ping en bas. Compact a deux joueurs par bande.
    /// </summary>
    private static void DessinerUnPanneau(Graphics g, Joueur j, StyleDEcran style, FontFamily famille, Font secours, Bitmap? logo,
        float largeur, float haut, float hauteur, bool aGauche, bool compact)
    {
        var couleur = Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place));
        var marge = 16f;
        var utile = largeur - 2 * marge;
        var milieu = largeur / 2f;
        var y = haut + (compact ? 14 : 20);
        if (logo is not null)
        {
            var w = Math.Min(utile, compact ? 150 : 176);
            var h = w * logo.Height / logo.Width;
            g.DrawImage(logo, milieu - w / 2f, y, w, h);
            y += h + 4;
        }

        var place = "P" + j.Place.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var taillePlace = compact ? 64f : 92f;
        var boitePlace = BoiteDuTexte(place, famille, taillePlace);
        TexteEnRelief(g, place, famille, taillePlace, new PointF(milieu - boitePlace.Width / 2f - boitePlace.X, y - boitePlace.Y), couleur, Nuit, 6,
            [(5, 5, Nuit)]);
        y += boitePlace.Height + (compact ? 12 : 20);

        if (Charger(j.Portrait) is { } portrait)
        {
            using (portrait)
            {
                var forme = new RectangleF(milieu - (compact ? 48 : 75), y, compact ? 96 : 150, compact ? 114 : 178);
                DessinerLePortrait(g, portrait, style.Forme, forme, couleur, miroir: !aGauche);
                y = forme.Bottom + 8;
            }

            if (j.Personnage.Length > 0)
            {
                using var police = Libelles(compact ? 15 : 19, secours);
                var mot = j.Personnage.ToUpperInvariant();
                var largeurMot = LargeurEspacee(g, mot, police, 3);
                TexteEspace(g, mot, police, Color.FromArgb(232, 228, 255), milieu - largeurMot / 2f, y, 3);
                y += Mesure(g, mot, police).Height + 6;
            }
        }

        var pseudo = j.Pseudo.ToUpperInvariant();
        var taillePseudo = TailleQuiTient(pseudo, famille, compact ? 40 : (j.Portrait is null ? 70 : 60), utile);
        var boitePseudo = BoiteDuTexte(pseudo, famille, taillePseudo);
        TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(milieu - boitePseudo.Width / 2f - boitePseudo.X, y - boitePseudo.Y), Color.White, Nuit, 5,
            [(4, 4, couleur)]);
        y += boitePseudo.Height + (compact ? 14 : 26);

        if (j.Cote.Length > 0)
        {
            var hauteurLigne = compact ? 30f : 44f;
            var hexagone = new RectangleF(marge, y, hauteurLigne * 1.15f, hauteurLigne);
            if (j.Lettre.Length > 0)
            {
                using var formeHexa = FormesDesEcrans.Hexagone(hexagone);
                using var or = new SolidBrush(Color.FromArgb(255, 210, 60));
                g.FillPath(or, formeHexa);
                using var policeLettre = Chiffres(hauteurLigne * 0.72f, secours);
                using var nuit = new SolidBrush(Nuit);
                var taille = Mesure(g, j.Lettre, policeLettre);
                g.DrawString(j.Lettre, policeLettre, nuit, hexagone.X + (hexagone.Width - taille.Width) / 2f, hexagone.Y + (hexagone.Height - taille.Height) / 2f, Typo);
            }

            using (var petite = Libelles(compact ? 13 : 17, secours))
            using (var grande = Chiffres(compact ? 30 : 44, secours))
            using (var blanc = new SolidBrush(Color.White))
            {
                var x = hexagone.Right + 12;
                if (!compact)
                {
                    TexteEspace(g, "ELO", petite, Color.FromArgb(216, 212, 240), x, y - 4, 2.5f);
                }

                g.DrawString(j.Cote, grande, blanc, x, compact ? y - 2 : y + 8, Typo);
            }

            y += hauteurLigne + (compact ? 8 : 12);
            if (j.Palier is { } palier && !compact)
            {
                var piste = new RectangleF(marge, y, utile, 8);
                using var gris = new SolidBrush(Color.FromArgb(31, 255, 255, 255));
                using var plein = new SolidBrush(couleur);
                using var formePiste = FormesDesEcrans.Arrondi(piste, 4);
                g.FillPath(gris, formePiste);
                if (palier > 0.02)
                {
                    using var formePleine = FormesDesEcrans.Arrondi(new RectangleF(piste.X, piste.Y, (float)(piste.Width * Math.Clamp(palier, 0, 1)), piste.Height), 4);
                    g.FillPath(plein, formePleine);
                }
            }
        }

        if (j.PingMs is { } ping && ping > 0)
        {
            DessinerLePing(g, ping, secours, milieu, haut + hauteur - (compact ? 22 : 30), compact);
        }
    }

    /// <summary>Le portrait du personnage choisi dans la forme du type, agrandi sans lissage, avec son ombre.</summary>
    private static void DessinerLePortrait(Graphics g, Bitmap portrait, FormeDuPortrait forme, RectangleF zone, Color couleur, bool miroir)
    {
        using (var ombre = FormesDesEcrans.Portrait(forme, new RectangleF(zone.X + 5, zone.Y + 7, zone.Width, zone.Height), miroir))
        using (var noir = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
        {
            g.FillPath(noir, ombre);
        }

        using (var cadre = FormesDesEcrans.Portrait(forme, zone, miroir))
        using (var pinceau = new SolidBrush(couleur))
        {
            g.FillPath(pinceau, cadre);
        }

        var dedans = RectangleF.Inflate(zone, -5, -5);
        using var chemin = FormesDesEcrans.Portrait(forme, dedans, miroir);
        var clip = g.Clip;
        var interpolation = g.InterpolationMode;
        var decalage = g.PixelOffsetMode;
        g.SetClip(chemin, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var echelle = Math.Max(dedans.Width / portrait.Width, dedans.Height / portrait.Height);
        var w = portrait.Width * echelle;
        var h = portrait.Height * echelle;
        g.DrawImage(portrait, dedans.X + (dedans.Width - w) / 2f, dedans.Y + (dedans.Height - h) / 2f, w, h);
        g.Clip = clip;
        g.InterpolationMode = interpolation;
        g.PixelOffsetMode = decalage;
    }

    /// <summary>La couleur d'etat de la latence : vert, orange, rouge. Jamais une couleur de place.</summary>
    public static Color CouleurDuPing(int ms) => ms <= 50 ? Color.FromArgb(57, 229, 140) : ms <= 100 ? Color.FromArgb(255, 176, 46) : Color.FromArgb(255, 79, 100);

    /// <summary>Le nombre de barres pleines (sur quatre) d'une latence.</summary>
    public static int BarresDuPing(int ms) => ms < 40 ? 4 : ms < 80 ? 3 : ms < 150 ? 2 : 1;

    private static void DessinerLePing(Graphics g, int ms, Font secours, float milieu, float bas, bool compact)
    {
        var couleur = CouleurDuPing(ms);
        var barres = BarresDuPing(ms);
        using var police = Libelles(compact ? 17 : 22, secours);
        var texte = ms.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";
        var taille = Mesure(g, texte, police);
        var largeurBarres = 4 * 5 + 3 * 3;
        var x = milieu - (largeurBarres + 10 + taille.Width) / 2f;
        for (var i = 0; i < 4; i++)
        {
            var h = 8 + i * 5;
            using var pinceau = new SolidBrush(i < barres ? couleur : Color.FromArgb(64, 255, 255, 255));
            g.FillRectangle(pinceau, x + i * 8, bas - h, 5, h);
        }

        using var encre = new SolidBrush(couleur);
        g.DrawString(texte, police, encre, x + largeurBarres + 10, bas - taille.Height + 2, Typo);
    }
}
