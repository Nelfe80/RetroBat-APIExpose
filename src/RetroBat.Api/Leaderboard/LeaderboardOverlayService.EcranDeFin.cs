using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// L'ECRAN DE FIN MULTIPLAYER v2 (CDC Ecrans v1.0, chapitre 2 ; maquettes validees par le user le 26/10) : plein ecran,
/// dessine dans un repere de 1920 x 1080 mis a l'echelle de l'ecran.
/// <list type="bullet">
/// <item>le fond : le fanart du jeu (sinon sa capture, sinon un fond uni), a peine voile a gauche, assombri et teinte a
/// droite derriere les joueurs ; precalcule une fois ;</item>
/// <item>a gauche le mot de fin, penche, dans la police du type et la couleur tiree du fanart, puis le vainqueur ;</item>
/// <item>a droite la jauge globale et les joueurs : deux cartes en colonnes a deux, quatre cartes a quatre, une ligne par
/// joueur sinon ;</item>
/// <item>l'animation de 3 secondes (sequence du user), puis tout est fixe.</item>
/// </list>
/// </summary>
public sealed partial class LeaderboardOverlayService
{
    /// <summary>
    /// Ce que l'ecran de fin montre. <c>Cle</c> designe la rencontre : une autre cle relance l'animation. <c>Fanart</c> : le
    /// chemin de l'image du jeu sur cette borne, ou null.
    /// </summary>
    public sealed record EcranDeFin(
        string Cle,
        string Type,
        string Titre,
        string Jeu,
        string Duree,
        string? Fanart,
        IReadOnlyList<JoueurDeFin> Joueurs,
        JaugeDeFin? Jauge,
        string Note,
        string Badge,
        TextesDeFin Textes,
        string GlypheRevanche,
        string GlypheQuitter,
        string? Police = null);

    public sealed record TextesDeFin(string FinDePartie, string Vainqueur, string Phrase, string Maintenir, string Revanche, string Quitter);

    /// <summary>
    /// Un joueur a la fin. <c>Rang</c> : 1 pour le vainqueur, null sans vainqueur. <c>Lettre</c>, <c>Cote</c>, <c>Ecart</c> :
    /// vides sans cote. <c>Portrait</c> : l'image du personnage choisi, ou null (le pseudo remonte).
    /// </summary>
    public sealed record JoueurDeFin(
        int? Place,
        string Pseudo,
        string Personnage,
        string? Portrait,
        string Lettre,
        string Cote,
        string Ecart,
        bool Gagnant,
        int? Rang,
        string Distinction,
        IReadOnlyList<MesureDeFin> Mesures);

    /// <summary>Une mesure d'un joueur : <c>Valeur</c> null = non mesuree ; <c>Part</c> de 0 a 1, relative au meilleur.</summary>
    public sealed record MesureDeFin(string Icone, string Libelle, long? Valeur, double Part, bool Meilleure);

    public sealed record JaugeDeFin(string Libelle, IReadOnlyList<PartDeLaJauge> Parts);

    public sealed record PartDeLaJauge(int Place, double Part);

    /// <summary>APERCU : le temps fige de l'animation, en secondes ; null, l'horloge de l'ecran.</summary>
    internal double? _tempsDApercu;

    private sealed partial class Panneau
    {
        /// <summary>Jusqu'ou l'animation redessine : 3 s de sequence, puis la chute des confettis.</summary>
        private const double FinDeLAnimation = 5.4;

        private static readonly Color Nuit = Color.FromArgb(11, 8, 32);
        private static readonly Color Dore = Color.FromArgb(255, 210, 60);
        private static readonly Color Gain = Color.FromArgb(57, 229, 140);
        private static readonly Color Perte = Color.FromArgb(255, 79, 100);

        private readonly Stopwatch _horlogeDeLaFin = new();
        private string? _cleDeLaFin;
        private System.Windows.Forms.Timer? _imagesDeLaFin;
        private Bitmap? _fondDeFin;
        private string? _cleDuFond;
        private Color _accent = Color.FromArgb(212, 255, 58);
        private Color _relief = Dore;
        private readonly Dictionary<string, Image?> _portraits = new(StringComparer.OrdinalIgnoreCase);

        // ── L'horloge ────────────────────────────────────────────────────────

        /// <summary>
        /// Le temps de l'animation. Elle part quand le voile noir commence a se lever (sinon l'impact du titre se jouerait
        /// dans le noir), et repart a zero pour une autre rencontre.
        /// </summary>
        private double TempsDeLaFin(EcranDeFin fin)
        {
            if (_service._tempsDApercu is { } fige)
            {
                return fige;
            }

            if (_cleDeLaFin != fin.Cle)
            {
                _cleDeLaFin = fin.Cle;
                _horlogeDeLaFin.Reset();
            }

            if (!_horlogeDeLaFin.IsRunning && _voile < 255)
            {
                _horlogeDeLaFin.Start();
                if (_imagesDeLaFin is null)
                {
                    _imagesDeLaFin = new System.Windows.Forms.Timer { Interval = 33 };
                    _imagesDeLaFin.Tick += (_, _) =>
                    {
                        Invalidate();
                        if (_horlogeDeLaFin.Elapsed.TotalSeconds > FinDeLAnimation)
                        {
                            _imagesDeLaFin.Stop();
                        }
                    };
                }

                _imagesDeLaFin.Start();
            }

            return _horlogeDeLaFin.IsRunning ? _horlogeDeLaFin.Elapsed.TotalSeconds : 0;
        }

        private static double Entre(double t, double debut, double fin) => fin <= debut ? (t >= fin ? 1 : 0) : Math.Clamp((t - debut) / (fin - debut), 0, 1);

        private static double Sortie(double x) => 1 - Math.Pow(1 - x, 3);

        /// <summary>Une sortie qui depasse un peu sa cible, puis y revient (le « rebond »).</summary>
        private static double Depasse(double x)
        {
            const double c1 = 1.70158;
            const double c3 = c1 + 1;
            return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
        }

        // ── Le dessin ────────────────────────────────────────────────────────

        private void DessinerLEcranDeFin(Graphics g, EcranDeFin fin, Font secours)
        {
            var t = TempsDeLaFin(fin);
            var style = StylesDesEcrans.Pour(fin.Type);
            PreparerLeFond(fin, style);
            if (_fondDeFin is not null)
            {
                g.DrawImageUnscaled(_fondDeFin, 0, 0);
            }
            else
            {
                g.Clear(Nuit);
            }

            var k = Math.Min(Width / 1920f, Height / 1080f);
            var etat = g.Save();
            g.TranslateTransform((Width - 1920 * k) / 2f, (Height - 1080 * k) / 2f);
            g.ScaleTransform(k, k);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            var titre = TraitsDesEcrans.Famille(fin.Police ?? style.Police, secours);
            try
            {
                DessinerLeDecor(g, fin.Type, t);
                DessinerLeLogo(g, k);
                DessinerLEnTete(g, fin, secours);
                DessinerLeTitre(g, fin.Titre, titre, t);
                DessinerLeVainqueur(g, fin, titre, secours, t);
                DessinerLaPhrase(g, fin.Textes.Phrase, secours);
                var haut = DessinerLaJauge(g, fin, titre, secours, t);
                DessinerLesJoueursDeFin(g, fin, style, titre, secours, haut, t);
                DessinerLesConfettis(g, t);
                DessinerLesBoutons(g, fin, titre, secours, k, t);
            }
            finally
            {
                g.Restore(etat);
            }
        }

        // ── Le fond, precalcule ──────────────────────────────────────────────

        /// <summary>La teinte sombre du cote des joueurs, selon le type.</summary>
        private static Color TeinteDuType(string type) => type switch
        {
            StylesDesEcrans.Course => Color.FromArgb(13, 42, 122),
            StylesDesEcrans.Coop => Color.FromArgb(26, 42, 138),
            StylesDesEcrans.Puzzle => Color.FromArgb(58, 26, 110),
            StylesDesEcrans.Sport => Color.FromArgb(90, 34, 10),
            StylesDesEcrans.Strategie => Color.FromArgb(58, 42, 8),
            StylesDesEcrans.Tir => Color.FromArgb(74, 10, 18),
            _ => Color.FromArgb(74, 22, 144),
        };

        private void PreparerLeFond(EcranDeFin fin, StyleDEcran style)
        {
            var places = string.Join(",", fin.Joueurs.Select(j => j.Place));
            var cle = $"{fin.Fanart}|{Width}x{Height}|{fin.Type}|{places}";
            if (cle == _cleDuFond && _fondDeFin is not null)
            {
                return;
            }

            _fondDeFin?.Dispose();
            _fondDeFin = null;
            _cleDuFond = cle;
            Bitmap? fanart = null;
            try
            {
                if (fin.Fanart is { } chemin && File.Exists(chemin))
                {
                    using var flux = File.OpenRead(chemin);
                    using var image = System.Drawing.Image.FromStream(flux);
                    fanart = new Bitmap(image);
                }
            }
            catch (Exception)
            {
                fanart = null;
            }

            try
            {
                var (titre, relief) = CouleurDuTitre.Choisir(fanart, fin.Joueurs.Where(j => j.Place is not null).Select(j => j.Place!.Value), style.Couleur);
                _accent = TraitsDesEcrans.Hex(titre);
                _relief = TraitsDesEcrans.Hex(relief);
                _fondDeFin = DessinerLeFond(fanart, fin.Type, Math.Max(1, Width), Math.Max(1, Height));
            }
            finally
            {
                fanart?.Dispose();
            }
        }

        private static Bitmap DessinerLeFond(Bitmap? fanart, string type, int largeur, int hauteur)
        {
            var fond = new Bitmap(largeur, hauteur, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(fond);
            g.Clear(Color.FromArgb(18, 10, 38));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (fanart is not null)
            {
                // Le fanart couvre l'ecran, un peu adouci (luminosite 0,95).
                var echelle = Math.Max(largeur / (float)fanart.Width, hauteur / (float)fanart.Height);
                var w = fanart.Width * echelle;
                var h = fanart.Height * echelle;
                using var attributs = new ImageAttributes();
                attributs.SetColorMatrix(new ColorMatrix(
                [
                    [0.95f, 0, 0, 0, 0], [0, 0.95f, 0, 0, 0], [0, 0, 0.95f, 0, 0], [0, 0, 0, 1, 0], [0, 0, 0, 0, 1],
                ]));
                g.DrawImage(fanart, Rectangle.Round(new RectangleF((largeur - w) / 2f, (hauteur - h) / 2f, w, h)),
                    0, 0, fanart.Width, fanart.Height, GraphicsUnit.Pixel, attributs);
            }

            // Un voile leger partout : le motif du fanart ne lutte pas avec le titre (retour du user du 26/10).
            using (var voile = new SolidBrush(Color.FromArgb(56, 12, 6, 28)))
            {
                g.FillRectangle(voile, 0, 0, largeur, hauteur);
            }

            // A droite, derriere les joueurs : la teinte du type, puis l'assombrissement.
            var teinte = TeinteDuType(type);
            DegradeHorizontal(g, largeur, hauteur, teinte, [(0, 0), (0.36f, 0), (0.58f, 150), (1, 150)]);
            DegradeHorizontal(g, largeur, hauteur, Color.FromArgb(12, 6, 28), [(0, 0), (0.34f, 0), (0.55f, 200), (1, 236)]);

            // En bas a gauche, sous le vainqueur et la phrase.
            var k = Math.Min(largeur / 1920f, hauteur / 1080f);
            var bas = new RectangleF(0, hauteur - 460 * k, 1100 * k, 460 * k + 1);
            using (var degrade = new LinearGradientBrush(bas, Color.FromArgb(0, 12, 6, 28), Color.FromArgb(200, 12, 6, 28), LinearGradientMode.Vertical))
            {
                g.FillRectangle(degrade, bas);
            }

            // La trame (combat, coop) : des points, seulement du cote des joueurs.
            if (type is StylesDesEcrans.Combat or StylesDesEcrans.Coop)
            {
                var pas = (type == StylesDesEcrans.Coop ? 14 : 9) * k;
                var rayon = (type == StylesDesEcrans.Coop ? 2.2f : 1.4f) * k;
                var couleur = type == StylesDesEcrans.Coop ? Color.FromArgb(255, 79, 163) : Color.White;
                for (var x = largeur * 0.4f; x < largeur; x += pas)
                {
                    var rampe = Math.Clamp((x / largeur - 0.4f) / 0.2f, 0, 1);
                    using var point = new SolidBrush(Color.FromArgb((int)(rampe * (type == StylesDesEcrans.Coop ? 30 : 18)), couleur));
                    for (var y = 0f; y < hauteur; y += pas)
                    {
                        g.FillEllipse(point, x - rayon, y - rayon, rayon * 2, rayon * 2);
                    }
                }
            }

            return fond;
        }

        private static void DegradeHorizontal(Graphics g, int largeur, int hauteur, Color couleur, (float Position, int Alpha)[] arrets)
        {
            var zone = new RectangleF(0, 0, largeur, hauteur);
            using var degrade = new LinearGradientBrush(zone, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
            {
                InterpolationColors = new ColorBlend
                {
                    Positions = arrets.Select(a => a.Position).ToArray(),
                    Colors = arrets.Select(a => Color.FromArgb(a.Alpha, couleur)).ToArray(),
                },
            };
            g.FillRectangle(degrade, zone);
        }

        // ── A gauche : le decor, le titre, le vainqueur ──────────────────────

        private void DessinerLeDecor(Graphics g, string type, double t)
        {
            var x = Entre(t, 0, 0.31);
            var echelle = t < 0.31 ? 0.2 + 0.88 * Sortie(x) : 1.08 - 0.08 * Entre(t, 0.31, 0.5);
            var opacite = Entre(t, 0, 0.12);
            if (opacite <= 0)
            {
                return;
            }

            var etat = g.Save();
            g.TranslateTransform(300, 240);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-300, -240);
            switch (type)
            {
                case StylesDesEcrans.Course:
                    using (var vitesse = new SolidBrush(TraitsDesEcrans.Alpha(_accent, 0.85 * opacite)))
                    using (var bleu = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(47, 213, 255), 0.7 * opacite)))
                    {
                        Trainee(g, vitesse, -20, 132, 760, 22);
                        Trainee(g, bleu, 80, 170, 620, 10);
                        Trainee(g, vitesse, -60, 340, 840, 16);
                    }
                    break;
                case StylesDesEcrans.Puzzle:
                    foreach (var (cx, cy, r, couleur) in new[] { (70f, 104f, 34f, "ff4f64"), (890f, 120f, 30f, "3d7bff"), (940f, 280f, 22f, "ffd23c"), (36f, 370f, 24f, "39e58c") })
                    {
                        using var bille = new SolidBrush(TraitsDesEcrans.Alpha(TraitsDesEcrans.Hex(couleur), opacite));
                        using var reflet = new SolidBrush(TraitsDesEcrans.Alpha(Color.White, 0.7 * opacite));
                        g.FillEllipse(bille, cx - r, cy - r, 2 * r, 2 * r);
                        g.FillEllipse(reflet, cx - r * 0.62f, cy - r * 0.62f, r * 0.54f, r * 0.54f);
                    }
                    break;
                case StylesDesEcrans.Combat:
                    // Les eclaboussures derriere le titre, a la couleur du titre et a celle de son relief.
                    using (var encre = new SolidBrush(TraitsDesEcrans.Alpha(_accent, 0.75 * opacite)))
                    using (var goutte = new SolidBrush(TraitsDesEcrans.Alpha(_relief, 0.8 * opacite)))
                    {
                        foreach (var (cx, cy, r) in new[] { (188f, 198f, 67f), (272f, 182f, 48f), (121f, 221f, 39f), (64f, 155f, 15f), (415f, 270f, 8f), (362f, 159f, 12f) })
                        {
                            g.FillEllipse(encre, cx - r, cy - r, 2 * r, 2 * r);
                        }

                        g.FillRectangle(encre, 239, 249, 11, 41);
                        g.FillRectangle(encre, 152, 258, 8, 34);
                        foreach (var (cx, cy, r) in new[] { (334f, 232f, 19f), (49f, 260f, 12f), (390f, 127f, 9f) })
                        {
                            g.FillEllipse(goutte, cx - r, cy - r, 2 * r, 2 * r);
                        }
                    }
                    break;
            }

            g.Restore(etat);
        }

        private static void Trainee(Graphics g, Brush pinceau, float x, float y, float largeur, float hauteur)
        {
            var biais = hauteur * 0.62f;
            g.FillPolygon(pinceau, [new PointF(x + biais, y), new PointF(x + largeur + biais, y), new PointF(x + largeur, y + hauteur), new PointF(x, y + hauteur)]);
        }

        private void DessinerLeLogo(Graphics g, float k)
        {
            var chemin = Path.Combine(RetroBatPaths.ThemeResourcesRoot, "images", "nelfeplay-multiplayer.png");
            if (!File.Exists(chemin) || _service._glyphes?.Glyphe(chemin, (int)Math.Round(70 * k)) is not { } logo)
            {
                return;
            }

            g.DrawImage(logo, 56, 30, 70f * logo.Width / logo.Height, 70);
        }

        private void DessinerLEnTete(Graphics g, EcranDeFin fin, Font secours)
        {
            using var grand = TraitsDesEcrans.LibellesFins(28, secours);
            using var petit = TraitsDesEcrans.LibellesFins(22, secours);
            const float droite = 1856;
            var mot = fin.Textes.FinDePartie.ToUpperInvariant();
            var largeurMot = TraitsDesEcrans.LargeurEspacee(g, mot, grand, 9);
            var largeurBarres = TraitsDesEcrans.Mesure(g, "  //", grand).Width;
            TraitsDesEcrans.TexteEspace(g, mot, grand, Color.White, droite - largeurBarres - largeurMot, 46, 9);
            using (var accent = new SolidBrush(_accent))
            {
                g.DrawString("  //", grand, accent, droite - largeurBarres, 46, TraitsDesEcrans.Typo);
            }

            var sousTitre = string.Join(" · ", new[] { fin.Jeu, fin.Duree }.Where(x => x.Length > 0));
            using (var gris = new SolidBrush(Color.FromArgb(201, 195, 255)))
            {
                g.DrawString(sousTitre, petit, gris, droite - TraitsDesEcrans.Mesure(g, sousTitre, petit).Width, 88, TraitsDesEcrans.Typo);
            }

            var y = 124f;
            if (fin.Badge.Length > 0)
            {
                using var police = TraitsDesEcrans.Libelles(18, secours);
                var largeur = TraitsDesEcrans.LargeurEspacee(g, fin.Badge.ToUpperInvariant(), police, 3) + 24;
                var zone = new RectangleF(droite - largeur, y, largeur, 30);
                using var forme = FormesDesEcrans.Arrondi(zone, 15);
                using var bord = new Pen(Color.FromArgb(184, 199, 217), 2);
                g.DrawPath(bord, forme);
                TraitsDesEcrans.TexteEspace(g, fin.Badge.ToUpperInvariant(), police, Color.FromArgb(184, 199, 217), zone.X + 12, y + 5, 3);
                y += 40;
            }

            if (fin.Note.Length > 0)
            {
                using var police = TraitsDesEcrans.LibellesFins(20, secours);
                using var encre = new SolidBrush(Color.FromArgb(255, 216, 168));
                using var aDroite = new StringFormat(TraitsDesEcrans.Typo) { Alignment = StringAlignment.Far, Trimming = StringTrimming.Word };
                g.DrawString(fin.Note, police, encre, new RectangleF(droite - 760, y, 760, 60), aDroite);
            }
        }

        private void DessinerLeTitre(Graphics g, string titre, FontFamily famille, double t)
        {
            if (titre.Length == 0 || t <= 0)
            {
                return;
            }

            var taille = TraitsDesEcrans.TailleQuiTient(titre, famille, 184, 860);
            var impact = Entre(t, 0, 0.27);
            var echelle = t < 0.27 ? 2.6 - (2.6 - 0.92) * Sortie(impact) : 0.92 + 0.08 * Entre(t, 0.27, 0.34);
            var secousse = t is >= 0.34 and < 0.5 ? Math.Sin((t - 0.34) * 70) * 8 * (1 - Entre(t, 0.34, 0.5)) : 0;
            var etat = g.Save();
            var pivot = new PointF(60, 100 + taille * 0.85f);
            g.TranslateTransform(pivot.X + (float)secousse, pivot.Y + (float)(secousse * 0.5));
            g.RotateTransform(-5);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-pivot.X, -pivot.Y);
            TraitsDesEcrans.TexteEnRelief(g, titre, famille, taille, new PointF(60, 100), _accent, Nuit, 10,
                [(11, 11, _relief), (16, 16, Nuit)], Entre(t, 0, 0.12));
            g.Restore(etat);
        }

        private void DessinerLeVainqueur(Graphics g, EcranDeFin fin, FontFamily famille, Font secours, double t)
        {
            if (fin.Joueurs.FirstOrDefault(j => j.Gagnant) is not { } vainqueur)
            {
                return;
            }

            var arrivee = Entre(t, 0.5, 0.86);
            var decalage = t < 0.86 ? -140 * (1 - Sortie(arrivee)) + 12 * Sortie(arrivee) : 12 * (1 - Entre(t, 0.86, 1.04));
            var opacite = Sortie(arrivee);
            if (opacite <= 0.01)
            {
                return;
            }

            var etat = g.Save();
            g.TranslateTransform((float)decalage, 0);
            const float gauche = 64;
            const float basDuBloc = 940;
            var boiteUn = TraitsDesEcrans.BoiteDuTexte("1", famille, 300);
            var un = TraitsDesEcrans.TexteEnRelief(g, "1", famille, 300, new PointF(gauche - boiteUn.X, basDuBloc - boiteUn.Bottom), _accent, Nuit, 8,
                [(12, 12, _relief), (17, 17, Nuit)], opacite);
            var xPseudo = un.Right + 26;
            var pseudo = vainqueur.Pseudo.ToUpperInvariant();
            var taillePseudo = TraitsDesEcrans.TailleQuiTient(pseudo, famille, 140, 880 - xPseudo);
            var boitePseudo = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taillePseudo);
            var basDuPseudo = basDuBloc - 96;
            var rendu = TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(xPseudo - boitePseudo.X, basDuPseudo - boitePseudo.Bottom),
                Color.White, Nuit, 6, [(8, 8, Nuit)], opacite);

            // L'etiquette VAINQUEUR, penchee, sous le pseudo.
            var mot = fin.Textes.Vainqueur.ToUpperInvariant();
            var boiteMot = TraitsDesEcrans.BoiteDuTexte(mot, famille, 52);
            var etiquette = new RectangleF(xPseudo, basDuPseudo + 20, boiteMot.Width + 48, 64);
            var etatEtiquette = g.Save();
            g.TranslateTransform(etiquette.X, etiquette.Y + etiquette.Height);
            g.RotateTransform(-6);
            g.TranslateTransform(-etiquette.X, -(etiquette.Y + etiquette.Height));
            using (var ombre = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite)))
            using (var fond = new SolidBrush(TraitsDesEcrans.Alpha(_accent, opacite)))
            using (var formeOmbre = FormesDesEcrans.Arrondi(new RectangleF(etiquette.X + 6, etiquette.Y + 6, etiquette.Width, etiquette.Height), 6))
            using (var forme = FormesDesEcrans.Arrondi(etiquette, 6))
            {
                g.FillPath(ombre, formeOmbre);
                g.FillPath(fond, forme);
            }

            TraitsDesEcrans.TexteEnRelief(g, mot, famille, 52, new PointF(etiquette.X + 24 - boiteMot.X, etiquette.Y + (etiquette.Height - boiteMot.Height) / 2f - boiteMot.Y),
                Nuit, Nuit, 0, [], opacite);
            g.Restore(etatEtiquette);

            // La couronne tombe sur le pseudo, avec un rebond.
            var chute = Entre(t, 0.58, 0.95);
            var yCouronne = t < 0.95 ? -240 * (1 - Sortie(chute)) + 14 * Sortie(chute) : t < 1.08 ? 14 - 24 * Entre(t, 0.95, 1.08) : -10 * (1 - Entre(t, 1.08, 1.2));
            if (chute > 0)
            {
                var couronne = new RectangleF(rendu.X + 8, rendu.Y - 66 + (float)yCouronne, 108, 60);
                var etatCouronne = g.Save();
                g.TranslateTransform(couronne.X + couronne.Width / 2, couronne.Y + couronne.Height / 2);
                g.RotateTransform((float)(t < 0.95 ? -18 + 24 * Sortie(chute) : 6 * (1 - Entre(t, 0.95, 1.2))));
                g.TranslateTransform(-(couronne.X + couronne.Width / 2), -(couronne.Y + couronne.Height / 2));
                IconesPixel.Dessiner(g, "couronne", couronne, TraitsDesEcrans.Alpha(Dore, Math.Min(opacite, Sortie(chute) * 2)));
                IconesPixel.Dessiner(g, "joyaux", couronne, TraitsDesEcrans.Alpha(Color.FromArgb(176, 122, 0), Math.Min(opacite, Sortie(chute) * 2)));
                g.Restore(etatCouronne);
            }

            g.Restore(etat);
        }

        private static void DessinerLaPhrase(Graphics g, string phrase, Font secours)
        {
            if (phrase.Length == 0)
            {
                return;
            }

            using var police = TraitsDesEcrans.LibellesFins(26, secours);
            using var encre = new SolidBrush(Color.FromArgb(232, 228, 255));
            g.DrawString(phrase, police, encre, 64, 1004, TraitsDesEcrans.Typo);
        }

        // ── A droite : la jauge globale et les joueurs ───────────────────────

        /// <summary>La jauge globale (a la Splatoon) au-dessus des joueurs. Rend le haut de la zone des joueurs.</summary>
        private float DessinerLaJauge(Graphics g, EcranDeFin fin, FontFamily famille, Font secours, double t)
        {
            if (fin.Jauge is not { Parts.Count: >= 2 } jauge)
            {
                return 230;
            }

            var apparition = Sortie(Entre(t, 1.2, 1.49));
            if (apparition <= 0)
            {
                return 318;
            }

            const float x = 880;
            const float largeur = 980;
            var y = 178 + 40 * (float)(1 - apparition);
            var remplissage = Sortie(Entre(t, 1.35, 2.5));
            var egale = 1.0 / jauge.Parts.Count;
            var parts = jauge.Parts.Select(p => (p.Place, Part: egale + (p.Part - egale) * remplissage)).ToList();
            using var libelle = TraitsDesEcrans.Libelles(20, secours);
            var mot = jauge.Libelle;
            var largeurMot = TraitsDesEcrans.LargeurEspacee(g, mot, libelle, 5);
            TraitsDesEcrans.TexteEspace(g, mot, libelle, TraitsDesEcrans.Alpha(Color.FromArgb(232, 228, 255), apparition), x + (largeur - largeurMot) / 2f, y + 14, 5);
            if (parts.Count == 2)
            {
                foreach (var (part, aDroite) in new[] { (parts[0], false), (parts[1], true) })
                {
                    var texte = Math.Round(part.Part * 100).ToString(CultureInfo.InvariantCulture) + " %";
                    var boite = TraitsDesEcrans.BoiteDuTexte(texte, famille, 42);
                    var gauche = aDroite ? x + largeur - boite.Width : x;
                    TraitsDesEcrans.TexteEnRelief(g, texte, famille, 42, new PointF(gauche - boite.X, y + 42 - boite.Bottom), TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(part.Place)),
                        Nuit, 0, [(3, 3, Nuit)], apparition);
                }
            }

            var barre = new RectangleF(x, y + 52, largeur, 34);
            using (var halo = new Pen(TraitsDesEcrans.Alpha(Color.FromArgb(51, 255, 255, 255), apparition), 2))
            using (var formeHalo = FormesDesEcrans.Arrondi(RectangleF.Inflate(barre, 4, 4), 21))
            {
                g.DrawPath(halo, formeHalo);
            }

            using (var forme = FormesDesEcrans.Arrondi(barre, 17))
            {
                var clip = g.Clip;
                g.SetClip(forme, CombineMode.Intersect);
                var gaucheDuMorceau = barre.X;
                using var rayures = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb((int)(40 * apparition), 255, 255, 255), Color.Transparent);
                using var separation = new Pen(TraitsDesEcrans.Alpha(Color.White, apparition), 4);
                using var police = TraitsDesEcrans.Libelles(22, secours);
                for (var i = 0; i < parts.Count; i++)
                {
                    var largeurDuMorceau = i == parts.Count - 1 ? barre.Right - gaucheDuMorceau : (float)(parts[i].Part * largeur);
                    var morceau = new RectangleF(gaucheDuMorceau, barre.Y, largeurDuMorceau, barre.Height);
                    using (var couleur = new SolidBrush(TraitsDesEcrans.Alpha(TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(parts[i].Place)), apparition)))
                    {
                        g.FillRectangle(couleur, morceau);
                    }

                    g.FillRectangle(rayures, morceau);
                    if (parts.Count > 2 && largeurDuMorceau > 70)
                    {
                        using var sombre = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, apparition));
                        g.DrawString(Math.Round(parts[i].Part * 100).ToString(CultureInfo.InvariantCulture) + " %", police, sombre, morceau.X + 12, morceau.Y + 6, TraitsDesEcrans.Typo);
                    }

                    if (i > 0)
                    {
                        g.DrawLine(separation, gaucheDuMorceau, barre.Y, gaucheDuMorceau, barre.Bottom);
                    }

                    gaucheDuMorceau += largeurDuMorceau;
                }

                g.Clip = clip;
                using var bord = new Pen(TraitsDesEcrans.Alpha(Nuit, apparition), 3);
                g.DrawPath(bord, forme);
            }

            return 318;
        }

        private void DessinerLesJoueursDeFin(Graphics g, EcranDeFin fin, StyleDEcran style, FontFamily famille, Font secours, float haut, double t)
        {
            var joueurs = fin.Joueurs;
            if (joueurs.Count == 2)
            {
                for (var i = 0; i < 2; i++)
                {
                    DessinerUneCarteDeFin(g, joueurs[i], style, famille, secours, new PointF(840 + i * 530, haut), t, i * 0.25);
                }
            }
            else if (joueurs.Count == 4)
            {
                for (var i = 0; i < 4; i++)
                {
                    DessinerUneCartePetite(g, joueurs[i], style, famille, secours, new PointF(840 + (i % 2) * 530, haut + 2 + (i / 2) * 262), t, i * 0.12);
                }
            }
            else
            {
                DessinerLaListeDeFin(g, joueurs, style, famille, secours, haut - 30, t);
            }
        }

        /// <summary>L'apparition d'un joueur (1,2 a 1,62 s) : il monte de 60 pixels en se devoilant.</summary>
        private static (float Dy, double Opacite) Apparition(double t, double retard)
        {
            var x = Entre(t, 1.2 + retard, 1.49 + retard);
            var dy = x < 1 ? 60 * (1 - Sortie(x)) - 6 * Sortie(x) : -6 * (1 - Entre(t, 1.49 + retard, 1.62 + retard));
            return ((float)dy, Sortie(x));
        }

        private static long Decompte(long valeur, double t) => (long)Math.Round(valeur * Sortie(Entre(t, 1.3, 2.5)));

        private void DessinerLeFondDUneCarte(Graphics g, RectangleF carte, JoueurDeFin j, double opacite, float rayon)
        {
            using var forme = FormesDesEcrans.Arrondi(carte, rayon);
            if (j.Gagnant)
            {
                for (var i = 3; i >= 1; i--)
                {
                    using var halo = new Pen(TraitsDesEcrans.Alpha(Color.FromArgb(28 * i, _accent), opacite), 6 * i + 3);
                    g.DrawPath(halo, forme);
                }
            }

            using (var fond = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(230, 18, 10, 40), opacite)))
            {
                g.FillPath(fond, forme);
            }

            using var bord = new Pen(TraitsDesEcrans.Alpha(j.Gagnant ? _accent : TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place)), opacite), 3);
            g.DrawPath(bord, forme);
        }

        /// <summary>Le portrait du personnage dans la forme du type, agrandi sans lissage. Rend sa boite.</summary>
        private void DessinerLePortraitDeFin(Graphics g, JoueurDeFin j, StyleDEcran style, RectangleF zone, double opacite)
        {
            if (Portrait(j.Portrait) is not { } image || opacite <= 0.01)
            {
                return;
            }

            var miroir = j.Place is { } p && p % 2 == 0;
            using (var cadre = FormesDesEcrans.Portrait(style.Forme, zone, miroir))
            using (var couleur = new SolidBrush(TraitsDesEcrans.Alpha(TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place)), opacite)))
            using (var ombre = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(140, 0, 0, 0), opacite)))
            using (var cadreOmbre = FormesDesEcrans.Portrait(style.Forme, new RectangleF(zone.X + 4, zone.Y + 5, zone.Width, zone.Height), miroir))
            {
                g.FillPath(ombre, cadreOmbre);
                g.FillPath(couleur, cadre);
            }

            var dedans = RectangleF.Inflate(zone, -4, -4);
            using var forme = FormesDesEcrans.Portrait(style.Forme, dedans, miroir);
            var clip = g.Clip;
            var interpolation = g.InterpolationMode;
            var decalage = g.PixelOffsetMode;
            g.SetClip(forme, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var echelle = Math.Max(dedans.Width / image.Width, dedans.Height / image.Height);
            var w = image.Width * echelle;
            var h = image.Height * echelle;
            using (var attributs = new ImageAttributes())
            {
                attributs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)opacite });
                g.DrawImage(image, Rectangle.Round(new RectangleF(dedans.X + (dedans.Width - w) / 2f, dedans.Y + (dedans.Height - h) / 2f, w, h)),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributs);
            }

            g.Clip = clip;
            g.InterpolationMode = interpolation;
            g.PixelOffsetMode = decalage;
        }

        private Image? Portrait(string? chemin)
        {
            if (string.IsNullOrEmpty(chemin))
            {
                return null;
            }

            if (_portraits.TryGetValue(chemin, out var deja))
            {
                return deja;
            }

            Image? image = null;
            try
            {
                if (File.Exists(chemin))
                {
                    using var flux = File.OpenRead(chemin);
                    using var lue = System.Drawing.Image.FromStream(flux);
                    image = new Bitmap(lue);
                }
            }
            catch (Exception)
            {
                image = null;
            }

            _portraits[chemin] = image;
            return image;
        }

        private static void DessinerLaPastilleDeFin(Graphics g, int? place, Font police, PointF coin, double opacite)
        {
            var texte = "P" + (place?.ToString(CultureInfo.InvariantCulture) ?? "?");
            var taille = TraitsDesEcrans.Mesure(g, texte, police);
            var zone = new RectangleF(coin.X, coin.Y, taille.Width + 18, taille.Height + 8);
            using var forme = FormesDesEcrans.Arrondi(zone, 4);
            using var fond = new SolidBrush(TraitsDesEcrans.Alpha(TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(place)), opacite));
            using var encre = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite));
            g.FillPath(fond, forme);
            g.DrawString(texte, police, encre, zone.X + 9, zone.Y + 4, TraitsDesEcrans.Typo);
        }

        /// <summary>La lettre de rang dans son hexagone d'or, puis l'ELO et l'ecart. Rend le bord droit.</summary>
        private static float DessinerLaCoteDeFin(Graphics g, JoueurDeFin j, Font secours, PointF coin, float hauteur, double opacite, double t)
        {
            if (j.Cote.Length == 0)
            {
                return coin.X;
            }

            var x = coin.X;
            if (j.Lettre.Length > 0)
            {
                var hexagone = new RectangleF(x, coin.Y, hauteur * 1.15f, hauteur);
                using var forme = FormesDesEcrans.Hexagone(hexagone);
                using var or = new SolidBrush(TraitsDesEcrans.Alpha(Dore, opacite));
                g.FillPath(or, forme);
                using var police = TraitsDesEcrans.Chiffres(hauteur * 0.72f, secours);
                using var encre = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite));
                var taille = TraitsDesEcrans.Mesure(g, j.Lettre, police);
                g.DrawString(j.Lettre, police, encre, hexagone.X + (hexagone.Width - taille.Width) / 2f, hexagone.Y + (hexagone.Height - taille.Height) / 2f, TraitsDesEcrans.Typo);
                x = hexagone.Right + hauteur * 0.32f;
            }

            using (var police = TraitsDesEcrans.Chiffres(hauteur * 0.78f, secours))
            using (var blanc = new SolidBrush(TraitsDesEcrans.Alpha(Color.White, opacite)))
            {
                var texte = "ELO " + j.Cote;
                var taille = TraitsDesEcrans.Mesure(g, texte, police);
                g.DrawString(texte, police, blanc, x, coin.Y + (hauteur - taille.Height) / 2f, TraitsDesEcrans.Typo);
                x += taille.Width + hauteur * 0.3f;
            }

            if (j.Ecart.Length > 0)
            {
                var montee = Sortie(Entre(t, 1.44, 1.71));
                using var police = TraitsDesEcrans.Chiffres(hauteur * 1.0f, secours);
                using var couleur = new SolidBrush(TraitsDesEcrans.Alpha(j.Ecart.StartsWith('-') ? Perte : Gain, opacite * montee));
                var taille = TraitsDesEcrans.Mesure(g, j.Ecart, police);
                g.DrawString(j.Ecart, police, couleur, x, coin.Y + (hauteur - taille.Height) / 2f + (float)(16 * (1 - montee)), TraitsDesEcrans.Typo);
                x += taille.Width;
            }

            return x;
        }

        private static void DessinerLEtoile(Graphics g, RectangleF zone, double t, double opacite)
        {
            var x = Entre(t, 2.5, 2.65);
            var echelle = t < 2.65 ? 1.6 * Sortie(x) : 1.6 - 0.6 * Entre(t, 2.65, 2.79);
            if (echelle <= 0.01)
            {
                return;
            }

            var etat = g.Save();
            g.TranslateTransform(zone.X + zone.Width / 2, zone.Y + zone.Height / 2);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-(zone.X + zone.Width / 2), -(zone.Y + zone.Height / 2));
            IconesPixel.Dessiner(g, "etoile", zone, TraitsDesEcrans.Alpha(Dore, opacite));
            g.Restore(etat);
        }

        /// <summary>La distinction : une etiquette jaune, penchee, au coin de la carte (2,57 s).</summary>
        private static void DessinerLaDistinction(Graphics g, string distinction, FontFamily famille, float taille, PointF coinHautDroit, double t)
        {
            if (distinction.Length == 0)
            {
                return;
            }

            var x = Entre(t, 2.57, 2.75);
            if (x <= 0)
            {
                return;
            }

            var echelle = t < 2.75 ? 1.6 - (1.6 - 0.92) * Sortie(x) : 0.92 + 0.08 * Entre(t, 2.75, 2.88);
            var mot = distinction.ToUpperInvariant();
            var boite = TraitsDesEcrans.BoiteDuTexte(mot, famille, taille);
            var hauteur = taille * 1.45f;
            var largeur = boite.Width + taille * 2.1f;
            var zone = new RectangleF(coinHautDroit.X - largeur + 22, coinHautDroit.Y - hauteur * 0.62f, largeur, hauteur);
            var etat = g.Save();
            g.TranslateTransform(zone.X + zone.Width / 2, zone.Y + zone.Height / 2);
            g.RotateTransform(9);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-(zone.X + zone.Width / 2), -(zone.Y + zone.Height / 2));
            var opacite = Math.Min(1, x * 3);
            using (var ombre = FormesDesEcrans.Arrondi(new RectangleF(zone.X + 5, zone.Y + 5, zone.Width, zone.Height), 8))
            using (var forme = FormesDesEcrans.Arrondi(zone, 8))
            using (var noir = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite)))
            using (var jaune = new SolidBrush(TraitsDesEcrans.Alpha(Dore, opacite)))
            using (var bord = new Pen(TraitsDesEcrans.Alpha(Nuit, opacite), 3))
            {
                g.FillPath(noir, ombre);
                g.FillPath(jaune, forme);
                g.DrawPath(bord, forme);
            }

            var couronne = new RectangleF(zone.X + taille * 0.45f, zone.Y + (zone.Height - taille * 0.56f) / 2f, taille, taille * 0.56f);
            IconesPixel.Dessiner(g, "couronne", couronne, TraitsDesEcrans.Alpha(Nuit, opacite));
            TraitsDesEcrans.TexteEnRelief(g, mot, famille, taille, new PointF(couronne.Right + taille * 0.3f - boite.X, zone.Y + (zone.Height - boite.Height) / 2f - boite.Y),
                Nuit, Nuit, 0, [], opacite);
            g.Restore(etat);
        }

        /// <summary>Deux joueurs : la grande carte (490 pixels), en colonne.</summary>
        private void DessinerUneCarteDeFin(Graphics g, JoueurDeFin j, StyleDEcran style, FontFamily famille, Font secours, PointF coin, double t, double retard)
        {
            var (dy, opacite) = Apparition(t, retard);
            if (opacite <= 0.01)
            {
                return;
            }

            const float largeur = 490;
            var aPortrait = Portrait(j.Portrait) is not null;
            var hautEntete = aPortrait ? 148f : 104f;
            var hauteur = 26 + hautEntete + 16 + 61 + (j.Cote.Length > 0 ? 16 + 40 : 0) + 16 + 2 + 16 + j.Mesures.Count * 70 + Math.Max(0, j.Mesures.Count - 1) * 16 + 28;
            var carte = new RectangleF(coin.X, coin.Y + dy, largeur, hauteur);
            DessinerLeFondDUneCarte(g, carte, j, opacite, 20);
            var x = carte.X + 28;
            var y = carte.Y + 26;
            if (j.Rang is { } rang)
            {
                var texte = rang.ToString(CultureInfo.InvariantCulture);
                var boite = TraitsDesEcrans.BoiteDuTexte(texte, famille, 128);
                var rendu = TraitsDesEcrans.TexteEnRelief(g, texte, famille, 128, new PointF(x - boite.X, y + hautEntete - boite.Bottom),
                    j.Gagnant ? _accent : TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place)), Nuit, 0, [(5, 5, Nuit)], opacite);
                x = rendu.Right + 18;
            }

            if (aPortrait)
            {
                DessinerLePortraitDeFin(g, j, style, new RectangleF(x, y, 124, 148), opacite);
                x += 124 + 18;
            }

            using (var pastille = TraitsDesEcrans.Chiffres(21, secours))
            {
                DessinerLaPastilleDeFin(g, j.Place, pastille, new PointF(x, y + hautEntete - 76), opacite);
            }

            if (j.Personnage.Length > 0)
            {
                using var police = TraitsDesEcrans.Libelles(22, secours);
                TraitsDesEcrans.TexteEspace(g, j.Personnage.ToUpperInvariant(), police, TraitsDesEcrans.Alpha(Color.FromArgb(201, 195, 255), opacite), x, y + hautEntete - 30, 3);
            }

            y += hautEntete + 16;
            var pseudo = j.Pseudo.ToUpperInvariant();
            var taillePseudo = TraitsDesEcrans.TailleQuiTient(pseudo, famille, 64, largeur - 56);
            var boitePseudo = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taillePseudo);
            TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(carte.X + 28 - boitePseudo.X, y + 58 - boitePseudo.Bottom), Color.White, Nuit, 0,
                [(4, 4, Color.Black)], opacite);
            y += 61;
            if (j.Cote.Length > 0)
            {
                y += 16;
                DessinerLaCoteDeFin(g, j, secours, new PointF(carte.X + 28, y), 36, opacite, t);
                y += 40;
            }

            y += 16;
            using (var trait = new Pen(TraitsDesEcrans.Alpha(Color.FromArgb(31, 255, 255, 255), opacite), 2))
            {
                g.DrawLine(trait, carte.X + 28, y, carte.Right - 28, y);
            }

            y += 18;
            using var libelle = TraitsDesEcrans.Libelles(24, secours);
            using var valeur = TraitsDesEcrans.Chiffres(54, secours, italique: true);
            using var nonMesure = TraitsDesEcrans.Chiffres(40, secours, italique: true);
            var remplissage = Sortie(Entre(t, 1.35, 2.5));
            foreach (var m in j.Mesures)
            {
                var couleur = TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place));
                var rond = new RectangleF(carte.X + 28, y, 52, 52);
                using (var fond = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(13, 8, 32), opacite)))
                using (var bord = new Pen(TraitsDesEcrans.Alpha(couleur, opacite), 2))
                {
                    g.FillEllipse(fond, rond);
                    g.DrawEllipse(bord, rond);
                }

                var rapport = IconesPixel.Rapport(m.Icone);
                IconesPixel.Dessiner(g, m.Icone, RectangleF.Inflate(rond, -12, -12 / Math.Max(1f, rapport)), TraitsDesEcrans.Alpha(Color.White, opacite));
                TraitsDesEcrans.TexteEspace(g, m.Libelle, libelle, TraitsDesEcrans.Alpha(Color.FromArgb(216, 212, 240), opacite), rond.Right + 14, y + 13, 2.4f);
                var texte = m.Valeur is { } v ? Decompte(v, t).ToString(CultureInfo.InvariantCulture) : "-";
                var police = m.Valeur is null ? nonMesure : valeur;
                var taille = TraitsDesEcrans.Mesure(g, texte, police);
                using (var encre = new SolidBrush(TraitsDesEcrans.Alpha(m.Meilleure ? Dore : Color.White, opacite)))
                {
                    g.DrawString(texte, police, encre, carte.Right - 28 - taille.Width, y + 26 - taille.Height / 2f, TraitsDesEcrans.Typo);
                }

                if (m.Meilleure)
                {
                    DessinerLEtoile(g, new RectangleF(carte.Right - 28 - taille.Width - 40, y + 13, 26, 26), t, opacite);
                }

                var piste = new RectangleF(carte.X + 28, y + 58, largeur - 56, 12);
                using (var fondDePiste = FormesDesEcrans.Arrondi(piste, 6))
                using (var gris = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(20, 255, 255, 255), opacite)))
                {
                    g.FillPath(gris, fondDePiste);
                }

                var largeurPleine = (float)(piste.Width * m.Part * remplissage);
                if (largeurPleine > 1)
                {
                    using var plein = FormesDesEcrans.Arrondi(new RectangleF(piste.X, piste.Y, Math.Max(12, largeurPleine), piste.Height), 6);
                    using var pinceau = new SolidBrush(TraitsDesEcrans.Alpha(couleur, opacite));
                    g.FillPath(pinceau, plein);
                }

                y += 70 + 16;
            }

            DessinerLaDistinction(g, j.Distinction, famille, 32, new PointF(carte.Right, carte.Y), t);
        }

        /// <summary>Quatre joueurs : la carte compacte (2 x 2).</summary>
        private void DessinerUneCartePetite(Graphics g, JoueurDeFin j, StyleDEcran style, FontFamily famille, Font secours, PointF coin, double t, double retard)
        {
            var (dy, opacite) = Apparition(t, retard);
            if (opacite <= 0.01)
            {
                return;
            }

            const float largeur = 490;
            var carte = new RectangleF(coin.X, coin.Y + dy, largeur, 232);
            DessinerLeFondDUneCarte(g, carte, j, opacite, 18);
            var x = carte.X + 22;
            var y = carte.Y + 20;
            if (j.Rang is { } rang)
            {
                var texte = rang.ToString(CultureInfo.InvariantCulture);
                var boite = TraitsDesEcrans.BoiteDuTexte(texte, famille, 96);
                TraitsDesEcrans.TexteEnRelief(g, texte, famille, 96, new PointF(x - boite.X, y + 96 - boite.Bottom), j.Gagnant ? _accent : TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place)),
                    Nuit, 0, [(4, 4, Nuit)], opacite);
                x += 70 + 16;
            }

            if (Portrait(j.Portrait) is not null)
            {
                DessinerLePortraitDeFin(g, j, style, new RectangleF(x, y, 104, 96), opacite);
                x += 104 + 16;
            }

            using (var pastille = TraitsDesEcrans.Chiffres(19, secours))
            {
                DessinerLaPastilleDeFin(g, j.Place, pastille, new PointF(x, y), opacite);
            }

            var pseudo = j.Pseudo.ToUpperInvariant();
            var taillePseudo = TraitsDesEcrans.TailleQuiTient(pseudo, famille, 40, carte.Right - 22 - x);
            var boitePseudo = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taillePseudo);
            TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(x - boitePseudo.X, y + 70 - boitePseudo.Bottom), Color.White, Nuit, 0, [(2, 2, Nuit)], opacite);
            DessinerLaCoteDeFin(g, j, secours, new PointF(x, y + 76), 24, opacite, t);
            y += 96 + 16;
            var colonne = (largeur - 44 - 2 * 14) / 3f;
            using var libelle = TraitsDesEcrans.Libelles(15, secours);
            using var valeur = TraitsDesEcrans.Chiffres(40, secours, italique: true);
            var remplissage = Sortie(Entre(t, 1.35, 2.5));
            for (var i = 0; i < Math.Min(3, j.Mesures.Count); i++)
            {
                var m = j.Mesures[i];
                var cx = carte.X + 22 + i * (colonne + 14);
                IconesPixel.Dessiner(g, m.Icone, new RectangleF(cx, y, 20, 20), TraitsDesEcrans.Alpha(Color.FromArgb(195, 205, 245), opacite));
                TraitsDesEcrans.TexteEspace(g, m.Libelle, libelle, TraitsDesEcrans.Alpha(Color.FromArgb(195, 205, 245), opacite), cx + 26, y + 2, 1.2f);
                var texte = m.Valeur is { } v ? Decompte(v, t).ToString(CultureInfo.InvariantCulture) : "-";
                var decalage = m.Meilleure ? 26f : 0f;
                if (m.Meilleure)
                {
                    DessinerLEtoile(g, new RectangleF(cx, y + 34, 20, 20), t, opacite);
                }

                using (var encre = new SolidBrush(TraitsDesEcrans.Alpha(m.Meilleure ? Dore : Color.White, opacite)))
                {
                    g.DrawString(texte, valeur, encre, cx + decalage, y + 24, TraitsDesEcrans.Typo);
                }

                var piste = new RectangleF(cx, y + 76, colonne, 8);
                using (var gris = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(20, 255, 255, 255), opacite)))
                {
                    g.FillRectangle(gris, piste);
                }

                using var pinceau = new SolidBrush(TraitsDesEcrans.Alpha(TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place)), opacite));
                g.FillRectangle(pinceau, piste.X, piste.Y, (float)(piste.Width * m.Part * remplissage), piste.Height);
            }

            DessinerLaDistinction(g, j.Distinction, famille, 24, new PointF(carte.Right, carte.Y), t);
        }

        /// <summary>Trois joueurs, ou cinq et plus : une ligne par joueur, les mesures en colonnes sous leur nom.</summary>
        private void DessinerLaListeDeFin(Graphics g, IReadOnlyList<JoueurDeFin> joueurs, StyleDEcran style, FontFamily famille, Font secours, float haut, double t)
        {
            const float gauche = 840;
            const float largeur = 1020;
            var hauteurDeLigne = Math.Min(140f, (920 - haut - 40 - (joueurs.Count - 1) * 14) / Math.Max(1, joueurs.Count));
            var colonnes = joueurs.FirstOrDefault()?.Mesures ?? [];
            const float largeurColonne = 160;
            var xMesures = gauche + largeur - 26 - colonnes.Count * largeurColonne - Math.Max(0, colonnes.Count - 1) * 18;
            using var libelle = TraitsDesEcrans.Libelles(17, secours);
            var (dyTete, opaciteTete) = Apparition(t, 0);
            for (var i = 0; i < colonnes.Count; i++)
            {
                var cx = xMesures + i * (largeurColonne + 18);
                IconesPixel.Dessiner(g, colonnes[i].Icone, new RectangleF(cx, haut + dyTete, 20, 20), TraitsDesEcrans.Alpha(Color.FromArgb(195, 205, 245), opaciteTete));
                TraitsDesEcrans.TexteEspace(g, colonnes[i].Libelle, libelle, TraitsDesEcrans.Alpha(Color.FromArgb(195, 205, 245), opaciteTete), cx + 26, haut + 2 + dyTete, 1.4f);
            }

            var y = haut + 40;
            using var valeur = TraitsDesEcrans.Chiffres(Math.Min(48, hauteurDeLigne * 0.36f), secours, italique: true);
            var remplissage = Sortie(Entre(t, 1.35, 2.5));
            for (var r = 0; r < joueurs.Count; r++)
            {
                var j = joueurs[r];
                var (dy, opacite) = Apparition(t, Math.Min(0.6, r * 0.12));
                if (opacite > 0.01)
                {
                    var ligne = new RectangleF(gauche, y + dy, largeur, hauteurDeLigne);
                    DessinerLeFondDUneCarte(g, ligne, j, opacite, 18);
                    var x = ligne.X + 26;
                    if (j.Rang is { } rang)
                    {
                        var texte = rang.ToString(CultureInfo.InvariantCulture);
                        var taille = Math.Min(72, hauteurDeLigne * 0.55f);
                        var boite = TraitsDesEcrans.BoiteDuTexte(texte, famille, taille);
                        TraitsDesEcrans.TexteEnRelief(g, texte, famille, taille, new PointF(x - boite.X, ligne.Y + (hauteurDeLigne + boite.Height) / 2f - boite.Bottom),
                            j.Gagnant ? _accent : TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place)), Nuit, 0, [(4, 4, Nuit)], opacite);
                        x += 70;
                    }

                    var cote = hauteurDeLigne - 40;
                    if (Portrait(j.Portrait) is not null)
                    {
                        DessinerLePortraitDeFin(g, j, style, new RectangleF(x, ligne.Y + 20, cote * 0.9f, cote), opacite);
                        x += cote * 0.9f + 18;
                    }

                    var pseudo = j.Pseudo.ToUpperInvariant();
                    var taillePseudo = TraitsDesEcrans.TailleQuiTient(pseudo, famille, Math.Min(40, hauteurDeLigne * 0.3f), xMesures - 24 - x);
                    var boitePseudo = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taillePseudo);
                    TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(x - boitePseudo.X, ligne.Y + hauteurDeLigne * 0.5f - boitePseudo.Bottom), Color.White, Nuit, 0,
                        [(2, 2, Nuit)], opacite);
                    using (var pastille = TraitsDesEcrans.Chiffres(Math.Min(19, hauteurDeLigne * 0.15f), secours))
                    {
                        DessinerLaPastilleDeFin(g, j.Place, pastille, new PointF(x, ligne.Y + hauteurDeLigne * 0.56f), opacite);
                    }

                    if (j.Personnage.Length > 0)
                    {
                        using var police = TraitsDesEcrans.Libelles(Math.Min(20, hauteurDeLigne * 0.15f), secours);
                        TraitsDesEcrans.TexteEspace(g, j.Personnage.ToUpperInvariant(), police, TraitsDesEcrans.Alpha(Color.FromArgb(195, 205, 245), opacite), x + 54, ligne.Y + hauteurDeLigne * 0.58f, 2);
                    }

                    for (var i = 0; i < Math.Min(colonnes.Count, j.Mesures.Count); i++)
                    {
                        var m = j.Mesures[i];
                        var cx = xMesures + i * (largeurColonne + 18);
                        var texte = m.Valeur is { } v ? Decompte(v, t).ToString(CultureInfo.InvariantCulture) : "-";
                        var decalage = m.Meilleure ? 28f : 0f;
                        if (m.Meilleure)
                        {
                            DessinerLEtoile(g, new RectangleF(cx, ligne.Y + hauteurDeLigne * 0.3f, 22, 22), t, opacite);
                        }

                        using (var encre = new SolidBrush(TraitsDesEcrans.Alpha(m.Meilleure ? Dore : Color.White, opacite)))
                        {
                            g.DrawString(texte, valeur, encre, cx + decalage, ligne.Y + hauteurDeLigne * 0.2f, TraitsDesEcrans.Typo);
                        }

                        var piste = new RectangleF(cx, ligne.Y + hauteurDeLigne * 0.7f, largeurColonne, 10);
                        using (var gris = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(20, 255, 255, 255), opacite)))
                        {
                            g.FillRectangle(gris, piste);
                        }

                        using var pinceau = new SolidBrush(TraitsDesEcrans.Alpha(TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(j.Place)), opacite));
                        g.FillRectangle(pinceau, piste.X, piste.Y, (float)(piste.Width * m.Part * remplissage), piste.Height);
                    }

                    DessinerLaDistinction(g, j.Distinction, famille, 20, new PointF(ligne.Right, ligne.Y), t);
                }

                y += hauteurDeLigne + 14;
            }
        }

        // ── Les confettis et les boutons ─────────────────────────────────────

        private static readonly (float X, float Taille, string Couleur, float Retard)[] Confettis =
        [
            (880, 14, "3d7bff", 0), (960, 10, "2fd5ff", 0.2f), (1040, 12, "ff8a3d", 0.1f), (1130, 10, "a259ff", 0.35f), (1220, 14, "ffd23c", 0.05f),
            (1310, 10, "3d7bff", 0.3f), (1400, 12, "ff4fa3", 0.15f), (1490, 10, "2fd5ff", 0.4f), (1580, 14, "a259ff", 0.08f), (1670, 10, "ffd23c", 0.25f),
            (1760, 12, "d4ff3a", 0.18f), (1840, 10, "ff8a3d", 0.32f), (120, 12, "2fd5ff", 0.22f), (330, 10, "ffd23c", 0.12f), (560, 14, "ff4fa3", 0.38f),
            (760, 10, "3d7bff", 0.02f),
        ];

        private static void DessinerLesConfettis(Graphics g, double t)
        {
            foreach (var (x, taille, couleur, retard) in Confettis)
            {
                var avance = (t - 2.5 - retard) / 2.4;
                if (avance is <= 0 or >= 1)
                {
                    continue;
                }

                var opacite = avance < 0.85 ? 1 : (1 - avance) / 0.15;
                var y = -40 + avance * 1180;
                var etat = g.Save();
                g.TranslateTransform(x, (float)y);
                g.RotateTransform((float)(avance * 540));
                using var pinceau = new SolidBrush(TraitsDesEcrans.Alpha(TraitsDesEcrans.Hex(couleur), opacite));
                g.FillRectangle(pinceau, -taille / 2, -taille / 2, taille, taille);
                g.Restore(etat);
            }
        }

        private void DessinerLesBoutons(Graphics g, EcranDeFin fin, FontFamily famille, Font secours, float k, double t)
        {
            const float bas = 1034;
            const float droite = 1856;
            using var petit = TraitsDesEcrans.Libelles(16, secours);
            var motQuitter = fin.Textes.Quitter.ToUpperInvariant();
            var boiteQuitter = TraitsDesEcrans.BoiteDuTexte(motQuitter, famille, 40);
            var quitter = new RectangleF(droite - (boiteQuitter.Width + 14 + 46 + 16 + 34), bas - 70, boiteQuitter.Width + 14 + 46 + 16 + 34, 70);
            using (var forme = FormesDesEcrans.Arrondi(quitter, 35))
            using (var bord = new Pen(Color.FromArgb(115, 255, 255, 255), 3))
            {
                g.DrawPath(bord, forme);
            }

            DessinerUnGlypheDeFin(g, fin.GlypheQuitter, new RectangleF(quitter.X + 14, quitter.Y + 12, 46, 46), Color.White, k);
            TraitsDesEcrans.TexteEnRelief(g, motQuitter, famille, 40, new PointF(quitter.X + 14 + 46 + 16 - boiteQuitter.X, quitter.Y + (70 - boiteQuitter.Height) / 2f - boiteQuitter.Y),
                Color.White, Color.White, 0, []);

            // REVANCHE s'allume a 2,5 s : grise avant, puis une petite pulsation.
            var allume = Entre(t, 2.5, 2.75);
            var echelle = t < 2.75 ? 0.96 + 0.12 * Sortie(allume) : 1.08 - 0.08 * Entre(t, 2.75, 3);
            var motRevanche = fin.Textes.Revanche.ToUpperInvariant();
            var boiteRevanche = TraitsDesEcrans.BoiteDuTexte(motRevanche, famille, 40);
            var motMaintenir = fin.Textes.Maintenir.ToUpperInvariant();
            var largeurMaintenir = TraitsDesEcrans.LargeurEspacee(g, motMaintenir, petit, 2.5f);
            var largeurTexte = Math.Max(boiteRevanche.Width, largeurMaintenir);
            var revanche = new RectangleF(quitter.X - 22 - (14 + 46 + 16 + largeurTexte + 34), bas - 70, 14 + 46 + 16 + largeurTexte + 34, 70);
            var etat = g.Save();
            g.TranslateTransform(revanche.X + revanche.Width / 2, revanche.Y + revanche.Height / 2);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-(revanche.X + revanche.Width / 2), -(revanche.Y + revanche.Height / 2));
            var fond = allume >= 1 ? _accent : Melange(Color.FromArgb(70, 66, 90), _accent, allume);
            using (var forme = FormesDesEcrans.Arrondi(revanche, 35))
            using (var pinceau = new SolidBrush(fond))
            {
                if (allume > 0)
                {
                    using var halo = new Pen(TraitsDesEcrans.Alpha(Color.FromArgb(110, _accent), allume), 14);
                    g.DrawPath(halo, forme);
                }

                g.FillPath(pinceau, forme);
            }

            // Les touches comme dans la barre d'aide d'ES et la barre des reactions : le pictogramme du theme, teinte, rien
            // autour (demande du user du 26/10). Fonce sur le bouton allume, blanc sur le bouton en contour.
            DessinerUnGlypheDeFin(g, fin.GlypheRevanche, new RectangleF(revanche.X + 14, revanche.Y + 12, 46, 46), Nuit, k);
            var xTexte = revanche.X + 14 + 46 + 16;
            TraitsDesEcrans.TexteEspace(g, motMaintenir, petit, Nuit, xTexte, revanche.Y + 11, 2.5f);
            TraitsDesEcrans.TexteEnRelief(g, motRevanche, famille, 36, new PointF(xTexte - TraitsDesEcrans.BoiteDuTexte(motRevanche, famille, 36).X, revanche.Y + 58 - TraitsDesEcrans.BoiteDuTexte(motRevanche, famille, 36).Bottom),
                Nuit, Nuit, 0, []);
            g.Restore(etat);
        }

        private static Color Melange(Color a, Color b, double x)
            => Color.FromArgb(255, (int)(a.R + (b.R - a.R) * x), (int)(a.G + (b.G - a.G) * x), (int)(a.B + (b.B - a.B) * x));

        private void DessinerUnGlypheDeFin(Graphics g, string glyphe, RectangleF zone, Color couleur, float k)
        {
            if (glyphe.Length == 0 || _service._glyphes?.Glyphe(glyphe, (int)Math.Round(zone.Height * k), couleur) is not { } image)
            {
                return;
            }

            var largeur = zone.Height * image.Width / Math.Max(1f, image.Height);
            g.DrawImage(image, zone.X + (zone.Width - largeur) / 2f, zone.Y, largeur, zone.Height);
        }
    }
}
