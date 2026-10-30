using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// L'ECRAN DE LANCEMENT MULTIPLAYER, le VS (maquettes validees par le user le 28/10, toile « Cartes en jeu et ecran de fin »,
/// page « Ecran de lancement ») : il prend la place de « Les joueurs se synchronisent… », du decompte jusqu'au depart.
/// <list type="bullet">
/// <item>la meme declinaison par type que l'ecran de fin et les panneaux : police, forme du portrait, mot central (VS, EN PISTE !,
/// ON FAIT EQUIPE !), couleur de titre tiree du fanart ; jamais de decor derriere le mot (retour du user du 28/10) ;</item>
/// <item>deux joueurs face a face, quatre en 2 x 2 aux places des bandes en jeu, trois ou cinq et plus en lignes ;</item>
/// <item>l'etat REEL de chaque joueur (chargement, synchronisation, pret, deconnecte), l'anneau qui tourne tant qu'il attend ;</item>
/// <item>« A VOUS DE JOUER ! » quand le depart est donne, puis l'ecran s'efface.</item>
/// </list>
/// </summary>
public sealed partial class LeaderboardOverlayService
{
    /// <summary>Ce qu'un joueur fait au lancement : l'anneau tourne pour Chargement, Synchronisation et EnRoute.</summary>
    public enum EtatAuLancement
    {
        Chargement,
        Synchronisation,
        Pret,
        Deconnecte,
        EnRoute,
    }

    /// <summary>
    /// Ce que l'ecran de lancement montre. <c>Cle</c> designe la rencontre : une autre cle relance l'entree. <c>DepartUtc</c> :
    /// le depart est donne (« A VOUS DE JOUER ! »). <c>Etape</c> : 1 pendant la synchronisation, 2 quand tout le monde est pret.
    /// </summary>
    public sealed record EcranDeLancement(
        string Cle,
        string Type,
        string Mot,
        string Jeu,
        string Entete,
        string SousTitre,
        string? Fanart,
        IReadOnlyList<JoueurDeLancement> Joueurs,
        string Etiquette,
        string TitreDuConseil,
        string Conseil,
        string Legende,
        IReadOnlyList<string> Etapes,
        int Etape,
        string AVous,
        DateTime? DepartUtc,
        bool AttenteLongue,
        string Reessayer,
        string Quitter,
        string Maintenir,
        string GlypheReessayer,
        string GlypheQuitter,
        string? Police = null);

    /// <summary>
    /// Un joueur au lancement. <c>Portrait</c> : l'image du personnage choisi, ou null (le pseudo remonte). <c>Lettre</c>,
    /// <c>Cote</c> : vides sans cote. <c>Barres</c> : de 1 a 4, null sans mesure. <c>Coequipiers</c> : les suivants d'une
    /// equipe, en vignettes sur le portrait.
    /// </summary>
    public sealed record JoueurDeLancement(
        int? Place,
        string Pseudo,
        string Personnage,
        string? Portrait,
        string Pays,
        string Lettre,
        string Cote,
        string LibelleVictoires,
        string Victoires,
        string LibelleSerie,
        string Serie,
        EtatAuLancement Etat,
        string TexteDeLEtat,
        int? PingMs,
        int? Barres,
        DateTime ChangeUtc,
        IReadOnlyList<string?>? Coequipiers = null);

    /// <summary>Un mot de plus de huit lettres en deux lignes, coupe a l'espace le plus proche du milieu (ON FAIT / EQUIPE !).</summary>
    internal static string[] MotEnDeuxLignes(string mot)
    {
        if (mot.Length <= 8 || !mot.Contains(' '))
        {
            return [mot];
        }

        var coupe = Enumerable.Range(0, mot.Length).Where(i => mot[i] == ' ').OrderBy(i => Math.Abs(i - mot.Length / 2.0)).First();
        return [mot[..coupe], mot[(coupe + 1)..]];
    }

    /// <summary>L'ecran de lancement reste devant le jeu qui vient d'arriver : il repasse devant, sans prendre la main.</summary>
    public void GarderDevantLeJeu()
    {
        lock (_gate)
        {
            if (!Affiche)
            {
                return;
            }
        }

        _forme?.RefuserLActivation();
        _forme?.Paraitre();
    }

    /// <summary>L'ecran s'estompe en <paramref name="duree"/> ; sa fermeture suit (elle rend l'opacite).</summary>
    public void Estomper(TimeSpan duree) => _forme?.Estomper(duree);

    private sealed partial class Panneau
    {
        public void Estomper(TimeSpan duree)
        {
            if (!IsHandleCreated)
            {
                return;
            }

            BeginInvoke(() =>
            {
                var debut = DateTime.UtcNow;
                var minuteur = new System.Windows.Forms.Timer { Interval = 30 };
                minuteur.Tick += (_, _) =>
                {
                    var x = (DateTime.UtcNow - debut).TotalMilliseconds / Math.Max(1, duree.TotalMilliseconds);
                    if (x >= 1 || !Visible)
                    {
                        minuteur.Stop();
                        minuteur.Dispose();
                        return;
                    }

                    Opacity = Math.Max(0, 1 - x);
                };
                minuteur.Start();
            });
        }

        private readonly Stopwatch _horlogeDuLancement = new();
        private string? _cleDuLancement;
        private System.Windows.Forms.Timer? _imagesDuLancement;
        private Bitmap? _fondDuLancement;
        private string? _cleDuFondDuLancement;

        /// <summary>
        /// Le temps de l'ecran de lancement, depuis son ouverture. Tant qu'il est la, il se redessine 15 fois par seconde :
        /// assez pour les anneaux, peu pour une borne qui charge son jeu en meme temps.
        /// </summary>
        private double TempsDuLancement(EcranDeLancement lancement)
        {
            if (_service._tempsDApercu is { } fige)
            {
                return fige;
            }

            if (_cleDuLancement != lancement.Cle)
            {
                _cleDuLancement = lancement.Cle;
                _horlogeDuLancement.Reset();
            }

            // L'entree se joue quand le noir commence a se lever, pas dessous.
            if (!_horlogeDuLancement.IsRunning && _voile < 255)
            {
                _horlogeDuLancement.Start();
            }

            if (_imagesDuLancement is null)
            {
                _imagesDuLancement = new System.Windows.Forms.Timer { Interval = 66 };
                _imagesDuLancement.Tick += (_, _) =>
                {
                    if (_service.LireMultiplayer()?.Lancement is null || !Visible)
                    {
                        _imagesDuLancement.Stop();
                        return;
                    }

                    Invalidate();
                };
            }

            if (!_imagesDuLancement.Enabled)
            {
                _imagesDuLancement.Start();
            }

            return _horlogeDuLancement.Elapsed.TotalSeconds;
        }

        private void DessinerLEcranDeLancement(Graphics g, EcranDeLancement l, Font secours)
        {
            var t = TempsDuLancement(l);
            var style = StylesDesEcrans.Pour(l.Type);
            PreparerLeFondDuLancement(l, style);
            if (_fondDuLancement is not null)
            {
                g.DrawImageUnscaled(_fondDuLancement, 0, 0);
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
            var famille = TraitsDesEcrans.Famille(l.Police ?? style.Police, secours);
            // Le depart donne : « A VOUS DE JOUER ! » tient 1,2 s, puis tout s'efface en 0,35 s.
            var depuisLeDepart = l.DepartUtc is { } depart ? Math.Max(0, (DateTime.UtcNow - depart).TotalSeconds) : -1;
            if (_service._tempsDApercu is { } fige && l.DepartUtc is not null)
            {
                depuisLeDepart = fige;
            }

            try
            {
                DessinerLeLogo(g, k);
                DessinerLEnTeteDuLancement(g, l, secours);
                var liste = l.Joueurs.Count is not (2 or 4);
                if (l.Joueurs.Count == 2)
                {
                    for (var i = 0; i < 2; i++)
                    {
                        DessinerUneGrandeCarteDeLancement(g, l.Joueurs[i], style, famille, secours, new PointF(i == 0 ? 96 : 1124, 150), i == 1, t);
                    }
                }
                else if (l.Joueurs.Count == 4)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        DessinerUnePetiteCarteDeLancement(g, l.Joueurs[i], style, famille, secours, new PointF(i % 2 == 0 ? 96 : 1184, i < 2 ? 150 : 500), i % 2 == 1, t);
                    }
                }
                else
                {
                    DessinerLesLignesDeLancement(g, l.Joueurs, style, famille, secours, t);
                }

                if (depuisLeDepart < 0 || depuisLeDepart < 0.15)
                {
                    DessinerLeMotDuLancement(g, l, famille, liste, t);
                }

                DessinerLeConseil(g, l, famille, secours, t);
                DessinerLesEtapes(g, l, secours, t);
                if (l.AttenteLongue && l.DepartUtc is null)
                {
                    DessinerLesBoutonsDuLancement(g, l, famille, secours, k);
                }

                if (depuisLeDepart >= 0)
                {
                    DessinerLeDepart(g, l, famille, depuisLeDepart);
                }
            }
            finally
            {
                g.Restore(etat);
            }
        }

        // ── Le fond, precalcule : le fanart a peine voile au centre, assombri et teinte des deux cotes ──

        private void PreparerLeFondDuLancement(EcranDeLancement l, StyleDEcran style)
        {
            var places = string.Join(",", l.Joueurs.Select(j => j.Place));
            var cle = $"{l.Fanart}|{Width}x{Height}|{l.Type}|{places}|{l.Joueurs.Count}";
            if (cle == _cleDuFondDuLancement && _fondDuLancement is not null)
            {
                return;
            }

            _fondDuLancement?.Dispose();
            _fondDuLancement = null;
            _cleDuFondDuLancement = cle;
            Bitmap? fanart = null;
            try
            {
                if (l.Fanart is { } chemin && File.Exists(chemin))
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
                var (titre, relief) = CouleurDuTitre.Choisir(fanart, l.Joueurs.Where(j => j.Place is not null).Select(j => j.Place!.Value), style.Couleur);
                _accent = TraitsDesEcrans.Hex(titre);
                _relief = TraitsDesEcrans.Hex(relief);
                _fondDuLancement = DessinerLeFondDuLancement(fanart, l.Type, l.Joueurs.Count is 2 or 4, Math.Max(1, Width), Math.Max(1, Height));
            }
            finally
            {
                fanart?.Dispose();
            }
        }

        private static Bitmap DessinerLeFondDuLancement(Bitmap? fanart, string type, bool faceAFace, int largeur, int hauteur)
        {
            var fond = new Bitmap(largeur, hauteur, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(fond);
            g.Clear(Color.FromArgb(18, 10, 38));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (fanart is not null)
            {
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

            using (var voile = new SolidBrush(Color.FromArgb(70, 12, 6, 28)))
            {
                g.FillRectangle(voile, 0, 0, largeur, hauteur);
            }

            var teinte = TeinteDuType(type);
            var sombre = Color.FromArgb(12, 6, 28);
            if (faceAFace)
            {
                // Les cartes des deux cotes : teinte et ombre a gauche et a droite, le centre (le mot) reste lumineux.
                DegradeHorizontal(g, largeur, hauteur, teinte, [(0, 140), (0.38f, 60), (0.5f, 20), (0.62f, 60), (1, 140)]);
                DegradeHorizontal(g, largeur, hauteur, sombre, [(0, 220), (0.38f, 140), (0.5f, 76), (0.62f, 140), (1, 220)]);
            }
            else
            {
                // Comme l'ecran de fin : le mot a gauche, les lignes des joueurs a droite.
                DegradeHorizontal(g, largeur, hauteur, teinte, [(0, 0), (0.36f, 0), (0.58f, 150), (1, 150)]);
                DegradeHorizontal(g, largeur, hauteur, sombre, [(0, 0), (0.34f, 0), (0.55f, 200), (1, 236)]);
            }

            var k = Math.Min(largeur / 1920f, hauteur / 1080f);
            var bas = new RectangleF(0, hauteur - 320 * k, largeur, 320 * k + 1);
            using (var degrade = new LinearGradientBrush(bas, Color.FromArgb(0, 12, 6, 28), Color.FromArgb(225, 12, 6, 28), LinearGradientMode.Vertical))
            {
                g.FillRectangle(degrade, bas);
            }

            return fond;
        }

        // ── L'en-tete, le mot, le depart ─────────────────────────────────────

        private void DessinerLEnTeteDuLancement(Graphics g, EcranDeLancement l, Font secours)
        {
            using var grand = TraitsDesEcrans.LibellesFins(28, secours);
            using var petit = TraitsDesEcrans.LibellesFins(22, secours);
            const float droite = 1856;
            var mot = l.Entete.ToUpperInvariant();
            var largeurMot = TraitsDesEcrans.LargeurEspacee(g, mot, grand, 9);
            var largeurBarres = TraitsDesEcrans.Mesure(g, "  //", grand).Width;
            TraitsDesEcrans.TexteEspace(g, mot, grand, Color.White, droite - largeurBarres - largeurMot, 46, 9);
            using (var accent = new SolidBrush(_accent))
            {
                g.DrawString("  //", grand, accent, droite - largeurBarres, 46, TraitsDesEcrans.Typo);
            }

            var sousTitre = string.Join(" · ", new[] { l.Jeu, l.SousTitre }.Where(x => x.Length > 0));
            using var gris = new SolidBrush(Color.FromArgb(201, 195, 255));
            g.DrawString(sousTitre, petit, gris, droite - TraitsDesEcrans.Mesure(g, sousTitre, petit).Width, 88, TraitsDesEcrans.Typo);
        }

        /// <summary>
        /// Le mot central, dans la police du type et la couleur tiree du fanart, penche, avec le relief de l'ecran de fin. Face a
        /// face, il est au centre entre les cartes ; en lignes, en grand a gauche comme le mot de fin. L'etiquette jaune dessous.
        /// </summary>
        private void DessinerLeMotDuLancement(Graphics g, EcranDeLancement l, FontFamily famille, bool aGauche, double t)
        {
            if (l.Mot.Length == 0)
            {
                return;
            }

            var impact = Entre(t, 0.15, 0.42);
            var echelle = t < 0.42 ? 2.6 - (2.6 - 0.92) * Sortie(impact) : 0.92 + 0.08 * Entre(t, 0.42, 0.5);
            var secousse = t is >= 0.5 and < 0.66 ? Math.Sin((t - 0.5) * 70) * 8 * (1 - Entre(t, 0.5, 0.66)) : 0;
            var opacite = Entre(t, 0.15, 0.26);
            if (opacite <= 0)
            {
                return;
            }

            var lignes = aGauche || l.Joueurs.Count == 4 ? MotEnDeuxLignes(l.Mot.ToUpperInvariant()) : [l.Mot.ToUpperInvariant()];
            var largeurMax = aGauche ? 760f : l.Joueurs.Count == 4 ? 420f : 300f;
            var tailleVoulue = aGauche ? 150f : l.Joueurs.Count == 4 ? 124f : 240f;
            var taille = lignes.Min(ligne => TraitsDesEcrans.TailleQuiTient(ligne, famille, tailleVoulue, largeurMax));
            var hauteurLigne = taille * 0.9f;
            var centreX = aGauche ? 60f : 960f;
            var haut = aGauche ? 160f : (l.Joueurs.Count == 4 ? 470f : 440f) - hauteurLigne * lignes.Length / 2f;
            var pivot = new PointF(aGauche ? 60 : 960, haut + hauteurLigne * lignes.Length / 2f);
            var etat = g.Save();
            g.TranslateTransform(pivot.X + (float)secousse, pivot.Y + (float)(secousse * 0.5));
            g.RotateTransform(-5);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-pivot.X, -pivot.Y);
            for (var i = 0; i < lignes.Length; i++)
            {
                var boite = TraitsDesEcrans.BoiteDuTexte(lignes[i], famille, taille);
                var x = aGauche ? centreX - boite.X : centreX - boite.Width / 2f - boite.X;
                TraitsDesEcrans.TexteEnRelief(g, lignes[i], famille, taille, new PointF(x, haut + i * hauteurLigne), _accent, Nuit, 10,
                    [(11, 11, _relief), (16, 16, Nuit)], opacite);
            }

            g.Restore(etat);
            var basDuMot = haut + hauteurLigne * lignes.Length + 24;
            DessinerLEtiquetteDuLancement(g, l.Etiquette, famille, aGauche ? 76 : 960, basDuMot, aGauche, t);
        }

        /// <summary>L'etiquette jaune penchee (« 2 MANCHES GAGNANTES »), comme « VAINQUEUR » a la fin.</summary>
        private static void DessinerLEtiquetteDuLancement(Graphics g, string texte, FontFamily famille, float x, float y, bool aGauche, double t)
        {
            if (texte.Length == 0)
            {
                return;
            }

            var apparition = Entre(t, 0.55, 0.75);
            if (apparition <= 0)
            {
                return;
            }

            var echelle = t < 0.75 ? 2 - Sortie(apparition) : 1;
            var mot = texte.ToUpperInvariant();
            const float taille = 34;
            var boite = TraitsDesEcrans.BoiteDuTexte(mot, famille, taille);
            var zone = new RectangleF(aGauche ? x : x - (boite.Width + 36) / 2f, y, boite.Width + 36, taille * 1.45f);
            var etat = g.Save();
            g.TranslateTransform(zone.X + zone.Width / 2, zone.Y + zone.Height / 2);
            g.RotateTransform(-6);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-(zone.X + zone.Width / 2), -(zone.Y + zone.Height / 2));
            var opacite = Math.Min(1, apparition * 3);
            using (var ombre = FormesDesEcrans.Arrondi(new RectangleF(zone.X + 6, zone.Y + 6, zone.Width, zone.Height), 6))
            using (var forme = FormesDesEcrans.Arrondi(zone, 6))
            using (var noir = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite)))
            using (var jaune = new SolidBrush(TraitsDesEcrans.Alpha(Dore, opacite)))
            {
                g.FillPath(noir, ombre);
                g.FillPath(jaune, forme);
            }

            TraitsDesEcrans.TexteEnRelief(g, mot, famille, taille, new PointF(zone.X + 18 - boite.X, zone.Y + (zone.Height - boite.Height) / 2f - boite.Y), Nuit, Nuit, 0, [], opacite);
            g.Restore(etat);
        }

        /// <summary>« A VOUS DE JOUER ! » : l'impact au centre, par-dessus tout, puis l'ecran s'efface (0,35 s apres 1,2 s).</summary>
        private void DessinerLeDepart(Graphics g, EcranDeLancement l, FontFamily famille, double s)
        {
            var mot = l.AVous.ToUpperInvariant();
            if (mot.Length == 0)
            {
                return;
            }

            var impact = Entre(s, 0, 0.25);
            var echelle = s < 0.25 ? 2.4 - (2.4 - 0.94) * Sortie(impact) : 0.94 + 0.06 * Entre(s, 0.25, 0.32);
            var secousse = s is >= 0.32 and < 0.5 ? Math.Sin((s - 0.32) * 70) * 8 * (1 - Entre(s, 0.32, 0.5)) : 0;
            var taille = TraitsDesEcrans.TailleQuiTient(mot, famille, 170, 1700);
            var boite = TraitsDesEcrans.BoiteDuTexte(mot, famille, taille);
            var pivot = new PointF(960, 470);
            var etat = g.Save();
            g.TranslateTransform(pivot.X + (float)secousse, pivot.Y + (float)(secousse * 0.5));
            g.RotateTransform(-5);
            g.ScaleTransform((float)echelle, (float)echelle);
            g.TranslateTransform(-pivot.X, -pivot.Y);
            TraitsDesEcrans.TexteEnRelief(g, mot, famille, taille, new PointF(960 - boite.Width / 2f - boite.X, 470 - boite.Height / 2f - boite.Y), _accent, Nuit, 10,
                [(11, 11, _relief), (16, 16, Nuit)], Entre(s, 0, 0.1));
            g.Restore(etat);
        }

        // ── Les cartes ───────────────────────────────────────────────────────

        /// <summary>L'arrivee d'une carte (0 a 0,4 s) : elle glisse depuis son cote en se devoilant.</summary>
        private static (float Dx, double Opacite) Arrivee(double t, bool aDroite, double retard)
        {
            var x = Sortie(Entre(t, retard, 0.4 + retard));
            return ((float)((aDroite ? 160 : -160) * (1 - x)), x);
        }

        private static Color CouleurDe(int? place) => TraitsDesEcrans.Hex(StylesDesEcrans.CouleurDeLaPlace(place));

        private void DessinerLeFondDUneCarteDeLancement(Graphics g, RectangleF carte, int? place, double opacite, float rayon)
        {
            using var forme = FormesDesEcrans.Arrondi(carte, rayon);
            var couleur = CouleurDe(place);
            for (var i = 2; i >= 1; i--)
            {
                using var halo = new Pen(TraitsDesEcrans.Alpha(Color.FromArgb(34 * i, couleur), opacite), 6 * i + 3);
                g.DrawPath(halo, forme);
            }

            using (var fond = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(230, 18, 10, 40), opacite)))
            {
                g.FillPath(fond, forme);
            }

            using var bord = new Pen(TraitsDesEcrans.Alpha(couleur, opacite), 3);
            g.DrawPath(bord, forme);
        }

        private static JoueurDeFin CommeALaFin(JoueurDeLancement j)
            => new(j.Place, j.Pseudo, j.Personnage, j.Portrait, j.Lettre, j.Cote, string.Empty, false, null, string.Empty, [], j.Coequipiers);

        private static bool Tourne(EtatAuLancement etat) => etat is EtatAuLancement.Chargement or EtatAuLancement.Synchronisation or EtatAuLancement.EnRoute;

        /// <summary>
        /// Le portrait dans la forme du type, et tant que le joueur attend, une lumiere qui fait le tour de son cadre (l'anneau
        /// de la maquette) ; deconnecte, il s'eteint. Pret, une coche verte au coin, qui pulse a son arrivee.
        /// </summary>
        private void DessinerLePortraitDeLancement(Graphics g, JoueurDeLancement j, StyleDEcran style, RectangleF zone, double opacite, double t)
        {
            var eteint = j.Etat == EtatAuLancement.Deconnecte;
            DessinerLePortraitDeFin(g, CommeALaFin(j), style, zone, eteint ? opacite * 0.4 : opacite);
            var miroir = j.Place is { } p && p % 2 == 0;
            if (Tourne(j.Etat))
            {
                using var dehors = FormesDesEcrans.Portrait(style.Forme, RectangleF.Inflate(zone, 7, 7), miroir);
                using var dedans = FormesDesEcrans.Portrait(style.Forme, RectangleF.Inflate(zone, -4, -4), miroir);
                using var anneau = new Region(dehors);
                anneau.Exclude(dedans);
                var clip = g.Clip;
                g.SetClip(anneau, CombineMode.Intersect);
                var centre = new PointF(zone.X + zone.Width / 2, zone.Y + zone.Height / 2);
                var rayon = Math.Max(zone.Width, zone.Height);
                var angle = (float)(t * 360 / 1.4 % 360);
                using (var lumiere = new SolidBrush(TraitsDesEcrans.Alpha(Color.White, 0.85 * opacite)))
                using (var trainee = new SolidBrush(TraitsDesEcrans.Alpha(CouleurDe(j.Place), 0.9 * opacite)))
                {
                    g.FillPie(trainee, centre.X - rayon, centre.Y - rayon, 2 * rayon, 2 * rayon, angle - 80, 70);
                    g.FillPie(lumiere, centre.X - rayon, centre.Y - rayon, 2 * rayon, 2 * rayon, angle - 12, 12);
                }

                g.Clip = clip;
            }
            else if (j.Etat == EtatAuLancement.Pret)
            {
                var pulse = Entre((DateTime.UtcNow - j.ChangeUtc).TotalSeconds, 0, 0.25);
                var echelle = (float)(pulse < 1 ? 1.3 - 0.3 * Sortie(pulse) : 1);
                var cote = 54f * echelle;
                var rond = new RectangleF(zone.Right - cote * 0.62f, zone.Bottom - cote * 0.7f, cote, cote);
                using var nuit = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite));
                using var vert = new SolidBrush(TraitsDesEcrans.Alpha(Gain, opacite));
                g.FillEllipse(nuit, RectangleF.Inflate(rond, 4, 4));
                g.FillEllipse(vert, rond);
                DessinerUneCoche(g, RectangleF.Inflate(rond, -cote * 0.24f, -cote * 0.24f), TraitsDesEcrans.Alpha(Nuit, opacite), cote * 0.1f);
            }
        }

        private static void DessinerUneCoche(Graphics g, RectangleF zone, Color couleur, float epaisseur)
        {
            using var crayon = new Pen(couleur, epaisseur) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(crayon,
            [
                new PointF(zone.X, zone.Y + zone.Height * 0.55f),
                new PointF(zone.X + zone.Width * 0.38f, zone.Bottom - zone.Height * 0.08f),
                new PointF(zone.Right, zone.Y + zone.Height * 0.1f),
            ]);
        }

        /// <summary>
        /// La pastille d'etat (« PRET ! » en vert, « SYNCHRONISATION… » cerclee de la couleur du joueur avec sa petite roue,
        /// « DECONNECTE » en rouge), puis le ping en barres. <paramref name="aDroite"/> aligne le tout sur son bord droit. Rend
        /// la hauteur occupee.
        /// </summary>
        private static float DessinerLEtatDuJoueur(Graphics g, JoueurDeLancement j, Font secours, PointF coin, bool aDroite, float taille, double opacite, double t)
        {
            using var police = TraitsDesEcrans.Chiffres(taille, secours);
            var texte = j.TexteDeLEtat.ToUpperInvariant();
            var mesure = TraitsDesEcrans.Mesure(g, texte, police);
            var hauteur = taille * 1.7f;
            var icone = taille * 0.95f;
            var largeur = mesure.Width + icone + taille * 1.5f;
            var zone = new RectangleF(aDroite ? coin.X - largeur : coin.X, coin.Y, largeur, hauteur);
            var couleur = CouleurDe(j.Place);
            var (fond, bord, encre) = j.Etat switch
            {
                EtatAuLancement.Pret => (Gain, Gain, Nuit),
                EtatAuLancement.Deconnecte => (Color.FromArgb(255, 59, 79), Color.FromArgb(255, 59, 79), Color.White),
                EtatAuLancement.EnRoute => (Nuit, Color.FromArgb(255, 182, 72), Color.FromArgb(255, 182, 72)),
                _ => (Nuit, couleur, Color.White),
            };
            var pulse = j.Etat == EtatAuLancement.Pret ? Entre((DateTime.UtcNow - j.ChangeUtc).TotalSeconds, 0, 0.25) : 1;
            var etat = g.Save();
            if (pulse < 1)
            {
                var echelle = (float)(1.16 - 0.16 * Sortie(pulse));
                g.TranslateTransform(zone.X + zone.Width / 2, zone.Y + zone.Height / 2);
                g.ScaleTransform(echelle, echelle);
                g.TranslateTransform(-(zone.X + zone.Width / 2), -(zone.Y + zone.Height / 2));
            }

            using (var forme = FormesDesEcrans.Arrondi(zone, hauteur / 2))
            using (var pinceau = new SolidBrush(TraitsDesEcrans.Alpha(fond, opacite)))
            using (var crayon = new Pen(TraitsDesEcrans.Alpha(bord, opacite), 3))
            {
                g.FillPath(pinceau, forme);
                g.DrawPath(crayon, forme);
            }

            var rond = new RectangleF(zone.X + taille * 0.6f, zone.Y + (hauteur - icone) / 2, icone, icone);
            if (j.Etat == EtatAuLancement.Pret)
            {
                DessinerUneCoche(g, RectangleF.Inflate(rond, -icone * 0.12f, -icone * 0.16f), TraitsDesEcrans.Alpha(Nuit, opacite), icone * 0.16f);
            }
            else if (Tourne(j.Etat))
            {
                using var fondRoue = new Pen(TraitsDesEcrans.Alpha(Color.FromArgb(56, 255, 255, 255), opacite), icone * 0.15f);
                using var roue = new Pen(TraitsDesEcrans.Alpha(j.Etat == EtatAuLancement.EnRoute ? bord : couleur, opacite), icone * 0.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawEllipse(fondRoue, RectangleF.Inflate(rond, -icone * 0.08f, -icone * 0.08f));
                g.DrawArc(roue, RectangleF.Inflate(rond, -icone * 0.08f, -icone * 0.08f), (float)(t * 360 / 1.0 % 360), 90);
            }
            else
            {
                using var croix = new Pen(TraitsDesEcrans.Alpha(encre, opacite), icone * 0.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var c = RectangleF.Inflate(rond, -icone * 0.22f, -icone * 0.22f);
                g.DrawLine(croix, c.Left, c.Top, c.Right, c.Bottom);
                g.DrawLine(croix, c.Right, c.Top, c.Left, c.Bottom);
            }

            using (var pinceau = new SolidBrush(TraitsDesEcrans.Alpha(encre, opacite)))
            {
                g.DrawString(texte, police, pinceau, rond.Right + taille * 0.35f, zone.Y + (hauteur - mesure.Height) / 2, TraitsDesEcrans.Typo);
            }

            g.Restore(etat);
            var bas = zone.Bottom + taille * 0.45f;
            if (j.PingMs is { } ms && j.Barres is { } barres)
            {
                using var petite = TraitsDesEcrans.Chiffres(taille * 0.85f, secours);
                var vert = barres >= 3 ? Gain : barres == 2 ? Color.FromArgb(255, 182, 72) : Color.FromArgb(255, 59, 79);
                var ping = ms.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";
                var largeurPing = TraitsDesEcrans.Mesure(g, ping, petite).Width;
                var hBarre = taille * 0.95f;
                var largeurTout = 4 * (taille * 0.24f) + 3 * (taille * 0.13f) + taille * 0.4f + largeurPing;
                var x = aDroite ? coin.X - largeurTout : coin.X + taille * 0.3f;
                for (var i = 0; i < 4; i++)
                {
                    var h = hBarre * (0.36f + 0.213f * i);
                    using var pinceau = new SolidBrush(TraitsDesEcrans.Alpha(i < barres ? vert : Color.FromArgb(64, 255, 255, 255), opacite));
                    g.FillRectangle(pinceau, x + i * (taille * 0.37f), bas + hBarre - h, taille * 0.24f, h);
                }

                using var encrePing = new SolidBrush(TraitsDesEcrans.Alpha(vert, opacite));
                g.DrawString(ping, petite, encrePing, x + 4 * (taille * 0.37f) + taille * 0.25f, bas + hBarre - TraitsDesEcrans.Mesure(g, ping, petite).Height * 0.92f, TraitsDesEcrans.Typo);
                bas += hBarre + taille * 0.2f;
            }

            return bas - coin.Y;
        }

        /// <summary>Le drapeau, puis la lettre de rang et l'ELO. Rend le bord droit.</summary>
        private static float DessinerLeDrapeauEtLaCote(Graphics g, JoueurDeLancement j, Font secours, PointF coin, float hauteur, double opacite)
        {
            var x = coin.X;
            if (j.Pays.Length > 0 && Drapeaux.Dessiner(g, j.Pays, new RectangleF(x, coin.Y + hauteur * 0.2f, hauteur * 0.9f, hauteur * 0.6f), (int)(255 * opacite)))
            {
                x += hauteur * 0.9f + hauteur * 0.38f;
            }

            return DessinerLaCoteDeFin(g, CommeALaFin(j), secours, new PointF(x, coin.Y), hauteur, opacite, 0);
        }

        /// <summary>Les victoires et la serie, chacune avec son icone, a la suite. Rend la largeur.</summary>
        private static float DessinerLesVictoires(Graphics g, JoueurDeLancement j, Font secours, PointF coin, float taille, double opacite, bool mesurer = false)
        {
            using var libelle = TraitsDesEcrans.Libelles(taille * 0.5f, secours);
            using var valeur = TraitsDesEcrans.Chiffres(taille, secours);
            var x = coin.X;
            foreach (var (icone, nom, texte) in new[] { ("trophee", j.LibelleVictoires, j.Victoires), ("flamme", j.LibelleSerie, j.Serie) })
            {
                if (texte.Length == 0)
                {
                    continue;
                }

                var zone = new RectangleF(x, coin.Y + taille * 0.25f, taille * 0.85f * IconesPixel.Rapport(icone), taille * 0.85f);
                if (!mesurer)
                {
                    IconesPixel.Dessiner(g, icone, zone, TraitsDesEcrans.Alpha(icone == "flamme" ? Color.FromArgb(255, 138, 61) : Dore, opacite));
                    TraitsDesEcrans.TexteEspace(g, nom.ToUpperInvariant(), libelle, TraitsDesEcrans.Alpha(Color.FromArgb(216, 212, 240), opacite), zone.Right + taille * 0.3f, coin.Y, 2);
                    using var blanc = new SolidBrush(TraitsDesEcrans.Alpha(Color.White, opacite));
                    g.DrawString(texte, valeur, blanc, zone.Right + taille * 0.26f, coin.Y + taille * 0.42f, TraitsDesEcrans.Typo);
                }

                var largeur = Math.Max(TraitsDesEcrans.LargeurEspacee(g, nom.ToUpperInvariant(), libelle, 2), TraitsDesEcrans.Mesure(g, texte, valeur).Width);
                x = zone.Right + taille * 0.3f + largeur + taille * 0.9f;
            }

            return x - coin.X - taille * 0.9f;
        }

        /// <summary>Deux joueurs : la grande carte (700 x 690), face a face, P1 a gauche.</summary>
        private void DessinerUneGrandeCarteDeLancement(Graphics g, JoueurDeLancement j, StyleDEcran style, FontFamily famille, Font secours, PointF coin, bool aDroite, double t)
        {
            var (dx, opacite) = Arrivee(t, aDroite, aDroite ? 0.08 : 0);
            if (opacite <= 0.01)
            {
                return;
            }

            var carte = new RectangleF(coin.X + dx, coin.Y, 700, 690);
            DessinerLeFondDUneCarteDeLancement(g, carte, j.Place, opacite, 20);
            var place = "P" + (j.Place?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
            var boitePlace = TraitsDesEcrans.BoiteDuTexte(place, famille, 110);
            TraitsDesEcrans.TexteEnRelief(g, place, famille, 110,
                new PointF(aDroite ? carte.Right - 28 - boitePlace.Width - boitePlace.X : carte.X + 28 - boitePlace.X, carte.Y + 22 - boitePlace.Y),
                CouleurDe(j.Place), Nuit, 0, [(5, 5, Nuit)], opacite);
            DessinerLEtatDuJoueur(g, j, secours, new PointF(aDroite ? carte.X + 28 : carte.Right - 28, carte.Y + 30), false == aDroite, 26, opacite, t);

            var aPortrait = Portrait(j.Portrait) is not null;
            var milieu = carte.X + carte.Width / 2;
            var y = carte.Y + 100;
            if (aPortrait)
            {
                DessinerLePortraitDeLancement(g, j, style, new RectangleF(milieu - 126, y, 252, 300), opacite, t);
                y += 300 + 14;
            }
            else
            {
                y += 140;   // sans personnage choisi, le pseudo remonte
            }

            using var petit = TraitsDesEcrans.Libelles(22, secours);
            if (j.Personnage.Length > 0 && aPortrait)
            {
                var nom = j.Personnage.ToUpperInvariant();
                var largeur = TraitsDesEcrans.LargeurEspacee(g, nom, petit, 6);
                TraitsDesEcrans.TexteEspace(g, nom, petit, TraitsDesEcrans.Alpha(Color.FromArgb(232, 228, 255), opacite), milieu - largeur / 2, y, 6);
            }

            y += 30;
            var pseudo = j.Pseudo.ToUpperInvariant();
            var taillePseudo = TraitsDesEcrans.TailleQuiTient(pseudo, famille, aPortrait ? 96 : 124, 640);
            var boitePseudo = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taillePseudo);
            TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(milieu - boitePseudo.Width / 2 - boitePseudo.X, y - boitePseudo.Y), Color.White, Nuit, 6,
                [(6, 6, CouleurDe(j.Place))], opacite);
            y += boitePseudo.Height + 18;

            var largeurCote = DessinerLeDrapeauEtLaCote(g, j, secours, new PointF(-10000, -10000), 46, 0) + 10000;
            DessinerLeDrapeauEtLaCote(g, j, secours, new PointF(milieu - largeurCote / 2, y), 46, opacite);
            y += 46 + 18;
            var largeurVictoires = DessinerLesVictoires(g, j, secours, new PointF(0, 0), 34, opacite, mesurer: true);
            DessinerLesVictoires(g, j, secours, new PointF(milieu - largeurVictoires / 2, y), 34, opacite);
        }

        /// <summary>Quatre joueurs : la carte compacte (640 x 330), le portrait du cote exterieur, aux places des bandes en jeu.</summary>
        private void DessinerUnePetiteCarteDeLancement(Graphics g, JoueurDeLancement j, StyleDEcran style, FontFamily famille, Font secours, PointF coin, bool aDroite, double t)
        {
            var (dx, opacite) = Arrivee(t, aDroite, (j.Place ?? 1) * 0.05);
            if (opacite <= 0.01)
            {
                return;
            }

            var carte = new RectangleF(coin.X + dx, coin.Y, 640, 330);
            DessinerLeFondDUneCarteDeLancement(g, carte, j.Place, opacite, 20);
            var aPortrait = Portrait(j.Portrait) is not null;
            if (aPortrait)
            {
                DessinerLePortraitDeLancement(g, j, style, new RectangleF(aDroite ? carte.Right - 28 - 170 : carte.X + 28, carte.Y + 56, 170, 196), opacite, t);
            }

            var gauche = aPortrait && !aDroite ? carte.X + 220 : carte.X + 28;
            var droite = aPortrait && aDroite ? carte.Right - 220 : carte.Right - 28;
            var place = "P" + (j.Place?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
            var boitePlace = TraitsDesEcrans.BoiteDuTexte(place, famille, 60);
            var pseudo = j.Pseudo.ToUpperInvariant();
            var taillePseudo = TraitsDesEcrans.TailleQuiTient(pseudo, famille, 58, droite - gauche - boitePlace.Width - 18);
            var boitePseudo = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taillePseudo);
            var y = carte.Y + 26;
            var basLigne = y + Math.Max(boitePlace.Height, boitePseudo.Height);
            if (aDroite)
            {
                TraitsDesEcrans.TexteEnRelief(g, place, famille, 60, new PointF(droite - boitePlace.Width - boitePlace.X, basLigne - boitePlace.Bottom), CouleurDe(j.Place), Nuit, 0, [(4, 4, Nuit)], opacite);
                TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(droite - boitePlace.Width - 18 - boitePseudo.Width - boitePseudo.X, basLigne - boitePseudo.Bottom),
                    Color.White, Nuit, 4, [(5, 5, CouleurDe(j.Place))], opacite);
            }
            else
            {
                TraitsDesEcrans.TexteEnRelief(g, place, famille, 60, new PointF(gauche - boitePlace.X, basLigne - boitePlace.Bottom), CouleurDe(j.Place), Nuit, 0, [(4, 4, Nuit)], opacite);
                TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(gauche + boitePlace.Width + 18 - boitePseudo.X, basLigne - boitePseudo.Bottom),
                    Color.White, Nuit, 4, [(5, 5, CouleurDe(j.Place))], opacite);
            }

            y = basLigne + 6;
            if (j.Personnage.Length > 0 && aPortrait)
            {
                using var petit = TraitsDesEcrans.Libelles(19, secours);
                var nom = j.Personnage.ToUpperInvariant();
                var largeur = TraitsDesEcrans.LargeurEspacee(g, nom, petit, 5);
                TraitsDesEcrans.TexteEspace(g, nom, petit, TraitsDesEcrans.Alpha(Color.FromArgb(195, 205, 245), opacite), aDroite ? droite - largeur : gauche, y, 5);
            }

            y += 36;
            var largeurCote = DessinerLeDrapeauEtLaCote(g, j, secours, new PointF(-10000, -10000), 40, 0) + 10000;
            DessinerLeDrapeauEtLaCote(g, j, secours, new PointF(aDroite ? droite - largeurCote : gauche, y), 40, opacite);
            y += 40 + 22;
            DessinerLEtatDuJoueur(g, j, secours, new PointF(aDroite ? droite : gauche, y), aDroite, 23, opacite, t);
        }

        /// <summary>Trois joueurs, ou cinq et plus : une ligne par joueur a droite, le mot en grand a gauche.</summary>
        private void DessinerLesLignesDeLancement(Graphics g, IReadOnlyList<JoueurDeLancement> joueurs, StyleDEcran style, FontFamily famille, Font secours, double t)
        {
            const float gauche = 900;
            const float largeur = 956;
            var hauteur = Math.Min(196f, (700f - (joueurs.Count - 1) * 20f) / Math.Max(1, joueurs.Count));
            for (var i = 0; i < joueurs.Count; i++)
            {
                var j = joueurs[i];
                var (dx, opacite) = Arrivee(t, true, i * 0.06);
                if (opacite <= 0.01)
                {
                    continue;
                }

                var ligne = new RectangleF(gauche + dx, 150 + i * (hauteur + 20), largeur, hauteur);
                DessinerLeFondDUneCarteDeLancement(g, ligne, j.Place, opacite, 20);
                var echelle = hauteur / 196f;
                var place = "P" + (j.Place?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
                var boitePlace = TraitsDesEcrans.BoiteDuTexte(place, famille, 52 * echelle);
                TraitsDesEcrans.TexteEnRelief(g, place, famille, 52 * echelle, new PointF(ligne.X + 26 - boitePlace.X, ligne.Y + 26 * echelle - boitePlace.Y), CouleurDe(j.Place), Nuit, 0, [(4, 4, Nuit)], opacite);
                var x = ligne.X + 130 * echelle;
                if (Portrait(j.Portrait) is not null)
                {
                    DessinerLePortraitDeLancement(g, j, style, new RectangleF(x, ligne.Y + 18 * echelle, 136 * echelle, 160 * echelle), opacite, t);
                    x += 162 * echelle;
                }

                using var petit = TraitsDesEcrans.Libelles(20 * echelle, secours);
                if (j.Personnage.Length > 0 && Portrait(j.Portrait) is not null)
                {
                    TraitsDesEcrans.TexteEspace(g, j.Personnage.ToUpperInvariant(), petit, TraitsDesEcrans.Alpha(Color.FromArgb(232, 228, 255), opacite), x, ligne.Y + 26 * echelle, 5);
                }

                var pseudo = j.Pseudo.ToUpperInvariant();
                var taillePseudo = TraitsDesEcrans.TailleQuiTient(pseudo, famille, 52 * echelle, ligne.Right - 330 - x);
                var boitePseudo = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taillePseudo);
                TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taillePseudo, new PointF(x - boitePseudo.X, ligne.Y + 56 * echelle - boitePseudo.Y), Color.White, Nuit, 0,
                    [(4, 4, CouleurDe(j.Place))], opacite);
                DessinerLeDrapeauEtLaCote(g, j, secours, new PointF(x, ligne.Y + 56 * echelle + boitePseudo.Height + 14 * echelle), 34 * echelle, opacite);
                DessinerLEtatDuJoueur(g, j, secours, new PointF(ligne.Right - 28, ligne.Y + 34 * echelle), true, 23 * echelle, opacite, t);
            }
        }

        // ── En bas : le conseil, les etapes, les boutons ─────────────────────

        private void DessinerLeConseil(Graphics g, EcranDeLancement l, FontFamily famille, Font secours, double t)
        {
            if (l.Conseil.Length == 0)
            {
                return;
            }

            var opacite = Entre(t, 0.3, 0.6);
            if (opacite <= 0)
            {
                return;
            }

            const float gauche = 64;
            using var texte = TraitsDesEcrans.Chiffres(28, secours, italique: true);
            var y = 1036f - 30;
            if (l.TitreDuConseil.Length > 0)
            {
                var titre = l.TitreDuConseil.ToUpperInvariant();
                var boite = TraitsDesEcrans.BoiteDuTexte(titre, famille, 26);
                var zone = new RectangleF(gauche, y - 50, boite.Width + 28, 38);
                using (var forme = FormesDesEcrans.Arrondi(zone, 6))
                using (var jaune = new SolidBrush(TraitsDesEcrans.Alpha(Dore, opacite)))
                {
                    g.FillPath(jaune, forme);
                }

                TraitsDesEcrans.TexteEnRelief(g, titre, famille, 26, new PointF(zone.X + 14 - boite.X, zone.Y + (zone.Height - boite.Height) / 2 - boite.Y), Nuit, Nuit, 0, [], opacite);
            }

            using var encre = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(232, 228, 255), opacite));
            using var aGauche = new StringFormat(TraitsDesEcrans.Typo) { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(l.Conseil, texte, encre, new RectangleF(gauche, y, l.Joueurs.Count == 2 || l.Joueurs.Count == 4 ? 980 : 760, 40), aGauche);
        }

        /// <summary>« On attend AKIRA… », puis les trois etapes : JOUEURS REUNIS, SYNCHRONISATION, C'EST PARTI !</summary>
        private static void DessinerLesEtapes(Graphics g, EcranDeLancement l, Font secours, double t)
        {
            if (l.Etapes.Count < 3)
            {
                return;
            }

            var opacite = Entre(t, 0.25, 0.55);
            if (opacite <= 0)
            {
                return;
            }

            const float droite = 1856;
            const float largeur = 780;
            var gauche = droite - largeur;
            const float haut = 904;
            using (var legende = TraitsDesEcrans.Chiffres(28, secours, italique: true))
            using (var encre = new SolidBrush(TraitsDesEcrans.Alpha(l.Etape >= 2 ? Gain : Color.White, opacite)))
            {
                var taille = TraitsDesEcrans.Mesure(g, l.Legende, legende);
                g.DrawString(l.Legende, legende, encre, droite - taille.Width, haut, TraitsDesEcrans.Typo);
            }

            var y = haut + 58;
            var noeuds = new[] { gauche + 36, gauche + largeur / 2, droite - 36 };
            using (var vert = new SolidBrush(TraitsDesEcrans.Alpha(Gain, opacite)))
            using (var gris = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(46, 255, 255, 255), opacite)))
            using (var or = new SolidBrush(TraitsDesEcrans.Alpha(Dore, opacite)))
            {
                g.FillRectangle(vert, noeuds[0], y + 16, noeuds[1] - noeuds[0], 4);
                g.FillRectangle(l.Etape >= 2 ? or : gris, noeuds[1], y + 16, noeuds[2] - noeuds[1], 4);
            }

            for (var i = 0; i < 3; i++)
            {
                var rond = new RectangleF(noeuds[i] - 18, y, 36, 36);
                var fait = i == 0 || (i == 1 && l.Etape >= 2);
                var courant = i == l.Etape;
                if (fait)
                {
                    using var vert = new SolidBrush(TraitsDesEcrans.Alpha(Gain, opacite));
                    g.FillEllipse(vert, rond);
                    DessinerUneCoche(g, RectangleF.Inflate(rond, -10, -11), TraitsDesEcrans.Alpha(Nuit, opacite), 4);
                }
                else if (i == 2 && l.Etape >= 2)
                {
                    using var or = new SolidBrush(TraitsDesEcrans.Alpha(Dore, opacite));
                    g.FillEllipse(or, rond);
                    using var nuit = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite));
                    g.FillPolygon(nuit, [new PointF(rond.X + 13, rond.Y + 10), new PointF(rond.X + 13, rond.Bottom - 10), new PointF(rond.Right - 10, rond.Y + 18)]);
                }
                else
                {
                    if (courant)
                    {
                        var pulse = t * 1 / 1.2 % 1;
                        using var halo = new Pen(TraitsDesEcrans.Alpha(Color.FromArgb(255, 138, 61), opacite * 0.85 * (1 - pulse)), 3);
                        var r = RectangleF.Inflate(rond, (float)(18 * 1.1 * pulse), (float)(18 * 1.1 * pulse));
                        g.DrawEllipse(halo, r);
                    }

                    using var nuit = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite));
                    using var bord = new Pen(TraitsDesEcrans.Alpha(courant ? Color.FromArgb(255, 138, 61) : Color.FromArgb(77, 255, 255, 255), opacite), 4);
                    g.FillEllipse(nuit, rond);
                    g.DrawEllipse(bord, RectangleF.Inflate(rond, -2, -2));
                    if (courant)
                    {
                        using var point = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(255, 138, 61), opacite));
                        g.FillEllipse(point, RectangleF.Inflate(rond, -12, -12));
                    }
                }

                using var libelle = TraitsDesEcrans.Libelles(19, secours);
                var mot = l.Etapes[i].ToUpperInvariant();
                var largeurMot = TraitsDesEcrans.LargeurEspacee(g, mot, libelle, 3);
                TraitsDesEcrans.TexteEspace(g, mot, libelle, TraitsDesEcrans.Alpha(courant || fait ? Color.White : Color.FromArgb(201, 195, 255), opacite),
                    Math.Min(droite - largeurMot + 20, noeuds[i] - largeurMot / 2), y + 46, 3);
            }
        }

        /// <summary>
        /// L'ATTENTE TROP LONGUE : l'anneau tourne depuis trop longtemps, on propose de reessayer ou de quitter, avec les
        /// pictogrammes d'ES comme les boutons de l'ecran de fin (demande du user du 28/10).
        /// </summary>
        private void DessinerLesBoutonsDuLancement(Graphics g, EcranDeLancement l, FontFamily famille, Font secours, float k)
        {
            using var petit = TraitsDesEcrans.Libelles(16, secours);
            const float haut = 760;
            var boutons = new List<(string Glyphe, string Mot, string Dessus, bool Plein)>
            {
                (l.GlypheReessayer, l.Reessayer.ToUpperInvariant(), string.Empty, true),
                (l.GlypheQuitter, l.Quitter.ToUpperInvariant(), l.Maintenir.ToUpperInvariant(), false),
            };
            var largeurs = boutons.Select(b => Math.Max(TraitsDesEcrans.BoiteDuTexte(b.Mot, famille, 36).Width, TraitsDesEcrans.LargeurEspacee(g, b.Dessus, petit, 2.5f)) + 14 + 46 + 16 + 34).ToList();
            var x = 960 - (largeurs.Sum() + 22) / 2;
            for (var i = 0; i < boutons.Count; i++)
            {
                var (glyphe, mot, dessus, plein) = boutons[i];
                var zone = new RectangleF(x, haut, largeurs[i], 70);
                using (var forme = FormesDesEcrans.Arrondi(zone, 35))
                {
                    if (plein)
                    {
                        using var pinceau = new SolidBrush(_accent);
                        g.FillPath(pinceau, forme);
                    }
                    else
                    {
                        using var fond = new SolidBrush(Color.FromArgb(220, 18, 10, 40));
                        using var bord = new Pen(Color.FromArgb(115, 255, 255, 255), 3);
                        g.FillPath(fond, forme);
                        g.DrawPath(bord, forme);
                    }
                }

                var encre = plein ? Nuit : Color.White;
                DessinerUnGlypheDeFin(g, glyphe, new RectangleF(zone.X + 14, zone.Y + 12, 46, 46), encre, k);
                var xTexte = zone.X + 14 + 46 + 16;
                var boite = TraitsDesEcrans.BoiteDuTexte(mot, famille, 36);
                if (dessus.Length > 0)
                {
                    TraitsDesEcrans.TexteEspace(g, dessus, petit, encre, xTexte, zone.Y + 11, 2.5f);
                    TraitsDesEcrans.TexteEnRelief(g, mot, famille, 36, new PointF(xTexte - boite.X, zone.Y + 58 - boite.Bottom), encre, encre, 0, []);
                }
                else
                {
                    TraitsDesEcrans.TexteEnRelief(g, mot, famille, 36, new PointF(xTexte - boite.X, zone.Y + (70 - boite.Height) / 2f - boite.Y), encre, encre, 0, []);
                }

                x += largeurs[i] + 22;
            }
        }
    }
}
