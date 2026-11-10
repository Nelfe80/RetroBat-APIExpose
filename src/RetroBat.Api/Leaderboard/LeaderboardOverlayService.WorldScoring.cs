using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LES ECRANS DU WORLD SCORING DANS LA FAMILLE MULTIPLAYER (decision P-D7 du 10/11, choix (a) du user) : ce que le VS et
/// l'ecran de fin n'avaient pas, dessine avec leurs propres traits (meme fond de carte, meme relief, memes temps).
/// <list type="bullet">
/// <item>au depart, la carte du SCORE A BATTRE, face au joueur, a la place de P2 ;</item>
/// <item>au bilan, la BARRE DE PROGRESSION entre le score du joueur et le record (comme « degats infliges »), le score et
/// le rang vises a ses extremites.</item>
/// </list>
/// </summary>
public sealed partial class LeaderboardOverlayService
{
    /// <summary>
    /// La barre de progression du bilan : <c>Gauche</c> et <c>RangGauche</c>, le score du joueur et sa place ; <c>Droite</c>
    /// et <c>RangDroite</c>, le score vise (le record du monde du mode, sinon le haut du classement) et son rang ;
    /// <c>Part</c> de 0 a 1 ; <c>Atteint</c> : le joueur a egale ou battu la cible (la barre est pleine, doree).
    /// </summary>
    public sealed record ProgressionDeFin(string Libelle, string Gauche, string RangGauche, string Droite, string RangDroite, double Part, bool Atteint);

    private sealed partial class Panneau
    {
        // ── Le depart : la carte du joueur, la carte du score a battre ───────

        /// <summary>
        /// La carte du joueur seul (700 x 690, celle de P1) : « P1 » et la pastille de l'etat comme au VS ; sans portrait, son
        /// nom prend la place du score de la carte d'en face, en grand, a l'ombre violette, son drapeau dessous ; record et
        /// place sur la ligne du bas. Les deux cartes se repondent.
        /// </summary>
        private void DessinerLaCarteDuJoueurSeul(Graphics g, JoueurDeLancement j, FontFamily famille, Font secours, PointF coin, double t)
        {
            var (dx, opacite) = Arrivee(t, false, 0);
            if (opacite <= 0.01)
            {
                return;
            }

            var carte = new RectangleF(coin.X + dx, coin.Y, 700, 690);
            var violet = CouleurDe(1);
            DessinerLeFondDUneCarteDeLancement(g, carte, 1, opacite, 20);
            var place = "P" + (j.Place?.ToString(CultureInfo.InvariantCulture) ?? "1");
            var boitePlace = TraitsDesEcrans.BoiteDuTexte(place, famille, 110);
            TraitsDesEcrans.TexteEnRelief(g, place, famille, 110, new PointF(carte.X + 28 - boitePlace.X, carte.Y + 22 - boitePlace.Y), violet, Nuit, 0, [(5, 5, Nuit)], opacite);
            DessinerLEtatDuJoueur(g, j, secours, new PointF(carte.Right - 28, carte.Y + 30), true, 26, opacite, t);

            var milieu = carte.X + carte.Width / 2;
            var y = carte.Y + 196;
            if (j.Personnage.Length > 0)
            {
                using var petit = TraitsDesEcrans.Libelles(22, secours);
                var mot = j.Personnage.ToUpperInvariant();
                var largeur = TraitsDesEcrans.LargeurEspacee(g, mot, petit, 6);
                TraitsDesEcrans.TexteEspace(g, mot, petit, TraitsDesEcrans.Alpha(Color.FromArgb(232, 228, 255), opacite), milieu - largeur / 2, y, 6);
            }

            y += 40;
            var pseudo = j.Pseudo.ToUpperInvariant();
            var taille = TraitsDesEcrans.TailleQuiTient(pseudo, famille, 150, 620);
            var boite = TraitsDesEcrans.BoiteDuTexte(pseudo, famille, taille);
            TraitsDesEcrans.TexteEnRelief(g, pseudo, famille, taille, new PointF(milieu - boite.Width / 2 - boite.X, y - boite.Y), Color.White, Nuit, 6,
                [(8, 8, violet)], opacite);
            y += boite.Height + 30;
            if (j.Pays.Length > 0 && Drapeaux.Dessiner(g, j.Pays, new RectangleF(milieu - 34, y, 68, 46), (int)(255 * opacite)))
            {
                y += 46 + 26;
            }

            var chiffres = new[] { new ChiffreCle(j.IconeVictoires, j.LibelleVictoires, j.Victoires), new ChiffreCle(j.IconeSerie, j.LibelleSerie, j.Serie) };
            var largeurChiffres = DessinerLesChiffres(g, chiffres, secours, new PointF(0, 0), 34, opacite, mesurer: true);
            DessinerLesChiffres(g, chiffres, secours, new PointF(milieu - largeurChiffres / 2, Math.Max(y, carte.Bottom - 104)), 34, opacite);
        }

        /// <summary>
        /// La carte de la cible (700 x 690, comme celle de P2) : « #3 » en grand a la place de « P2 », la pastille « SCORE TO
        /// BEAT » a la place de l'etat, le score en grand avec l'ombre de couleur du pseudo, qui le tient, et la ligne du bas.
        /// Elle arrive de la droite avec le retard de P2.
        /// </summary>
        private void DessinerLaCarteDeLaCible(Graphics g, CarteDeLaCible c, FontFamily famille, Font secours, PointF coin, double t)
        {
            var (dx, opacite) = Arrivee(t, true, 0.08);
            if (opacite <= 0.01)
            {
                return;
            }

            var carte = new RectangleF(coin.X + dx, coin.Y, 700, 690);
            var orange = CouleurDe(2);
            DessinerLeFondDUneCarteDeLancement(g, carte, 2, opacite, 20);
            if (c.Rang.Length > 0)
            {
                var boite = TraitsDesEcrans.BoiteDuTexte(c.Rang, famille, 110);
                TraitsDesEcrans.TexteEnRelief(g, c.Rang, famille, 110, new PointF(carte.Right - 28 - boite.Width - boite.X, carte.Y + 22 - boite.Y),
                    orange, Nuit, 0, [(5, 5, Nuit)], opacite);
            }

            DessinerUnePastilleFixe(g, c.Pastille, "etoile", orange, secours, new PointF(carte.X + 28, carte.Y + 30), 26, opacite);

            var milieu = carte.X + carte.Width / 2;
            var y = carte.Y + 196;
            if (c.Couronne)
            {
                var couronne = new RectangleF(milieu - 54, y - 74, 108, 60);
                IconesPixel.Dessiner(g, "couronne", couronne, TraitsDesEcrans.Alpha(Dore, opacite));
                IconesPixel.Dessiner(g, "joyaux", couronne, TraitsDesEcrans.Alpha(Color.FromArgb(176, 122, 0), opacite));
            }

            using (var petit = TraitsDesEcrans.Libelles(22, secours))
            {
                var mot = c.Libelle.ToUpperInvariant();
                var largeur = TraitsDesEcrans.LargeurEspacee(g, mot, petit, 6);
                TraitsDesEcrans.TexteEspace(g, mot, petit, TraitsDesEcrans.Alpha(Color.FromArgb(232, 228, 255), opacite), milieu - largeur / 2, y, 6);
            }

            y += 40;
            var taille = TraitsDesEcrans.TailleQuiTient(c.Valeur, famille, 150, 620);
            var boiteValeur = TraitsDesEcrans.BoiteDuTexte(c.Valeur, famille, taille);
            TraitsDesEcrans.TexteEnRelief(g, c.Valeur, famille, taille, new PointF(milieu - boiteValeur.Width / 2 - boiteValeur.X, y - boiteValeur.Y), Color.White, Nuit, 6,
                [(8, 8, orange)], opacite);
            y += boiteValeur.Height + 30;

            if (c.Joueur.Length > 0)
            {
                var nom = c.Joueur.ToUpperInvariant();
                var tailleNom = TraitsDesEcrans.TailleQuiTient(nom, famille, 60, 560);
                var boiteNom = TraitsDesEcrans.BoiteDuTexte(nom, famille, tailleNom);
                const float hauteurDrapeau = 34;
                var drapeau = c.Pays.Length > 0 ? hauteurDrapeau * 1.5f + 16 : 0;
                var gauche = milieu - (drapeau + boiteNom.Width) / 2;
                if (drapeau > 0)
                {
                    Drapeaux.Dessiner(g, c.Pays, new RectangleF(gauche, y + (boiteNom.Height - hauteurDrapeau) / 2, hauteurDrapeau * 1.5f, hauteurDrapeau), (int)(255 * opacite));
                }

                TraitsDesEcrans.TexteEnRelief(g, nom, famille, tailleNom, new PointF(gauche + drapeau - boiteNom.X, y - boiteNom.Y), Color.White, Nuit, 0,
                    [(4, 4, Nuit)], opacite);
                y += boiteNom.Height + 26;
            }

            if (c.Chiffres.Count > 0)
            {
                var largeurChiffres = DessinerLesChiffres(g, c.Chiffres, secours, new PointF(0, 0), 34, opacite, mesurer: true);
                DessinerLesChiffres(g, c.Chiffres, secours, new PointF(milieu - largeurChiffres / 2, Math.Max(y, carte.Bottom - 104)), 34, opacite);
            }
        }

        /// <summary>
        /// Une pastille qui ne change pas (« SCORE TO BEAT ») : la forme de la pastille d'etat du VS, cerclee de la couleur de la
        /// carte, son icone puis son texte.
        /// </summary>
        private static void DessinerUnePastilleFixe(Graphics g, string texte, string icone, Color couleur, Font secours, PointF coin, float taille, double opacite)
        {
            if (texte.Length == 0)
            {
                return;
            }

            using var police = TraitsDesEcrans.Chiffres(taille, secours);
            var mot = texte.ToUpperInvariant();
            var mesure = TraitsDesEcrans.Mesure(g, mot, police);
            var hauteur = taille * 1.7f;
            var cote = taille * 0.95f;
            var zone = new RectangleF(coin.X, coin.Y, mesure.Width + cote + taille * 1.5f, hauteur);
            using (var forme = FormesDesEcrans.Arrondi(zone, hauteur / 2))
            using (var fond = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, opacite)))
            using (var bord = new Pen(TraitsDesEcrans.Alpha(couleur, opacite), 3))
            {
                g.FillPath(fond, forme);
                g.DrawPath(bord, forme);
            }

            var rond = new RectangleF(zone.X + taille * 0.6f, zone.Y + (hauteur - cote) / 2, cote, cote);
            IconesPixel.Dessiner(g, icone, rond, TraitsDesEcrans.Alpha(Dore, opacite));
            using var encre = new SolidBrush(TraitsDesEcrans.Alpha(Color.White, opacite));
            g.DrawString(mot, police, encre, rond.Right + taille * 0.35f, zone.Y + (hauteur - mesure.Height) / 2, TraitsDesEcrans.Typo);
        }

        // ── Le bilan : la barre de progression vers le record ────────────────

        /// <summary>
        /// La barre de progression, a la place de la jauge globale du MULTIPLAYER et avec ses temps (apparition 1,2 s, remplissage
        /// de 1,35 a 2,5 s) : le score du joueur et sa place a gauche, en violet ; le score vise et son rang a droite, en orange ;
        /// la part atteinte en violet raye, ce qui reste en orange voile. Rend le haut de la zone des cartes.
        /// </summary>
        private float DessinerLaProgression(Graphics g, ProgressionDeFin p, FontFamily famille, Font secours, double t)
        {
            var apparition = Sortie(Entre(t, 1.2, 1.49));
            if (apparition <= 0)
            {
                return 318;
            }

            const float x = 880;
            const float largeur = 980;
            var y = 178 + 40 * (float)(1 - apparition);
            var remplissage = Sortie(Entre(t, 1.35, 2.5));
            var part = Math.Clamp(p.Part, 0, 1) * remplissage;
            var violet = CouleurDe(1);
            var orange = CouleurDe(2);
            using (var libelle = TraitsDesEcrans.Libelles(20, secours))
            {
                var mot = p.Libelle.ToUpperInvariant();
                var largeurMot = TraitsDesEcrans.LargeurEspacee(g, mot, libelle, 5);
                TraitsDesEcrans.TexteEspace(g, mot, libelle, TraitsDesEcrans.Alpha(Color.FromArgb(232, 228, 255), apparition), x + (largeur - largeurMot) / 2f, y + 14, 5);
            }

            // Les extremites : la valeur en grand, le rang en petit, en or, du cote de la barre.
            using (var rang = TraitsDesEcrans.Chiffres(26, secours, italique: true))
            using (var or = new SolidBrush(TraitsDesEcrans.Alpha(Dore, apparition)))
            {
                var boiteG = TraitsDesEcrans.BoiteDuTexte(p.Gauche, famille, 42);
                var finG = TraitsDesEcrans.TexteEnRelief(g, p.Gauche, famille, 42, new PointF(x - boiteG.X, y + 42 - boiteG.Bottom), violet, Nuit, 0, [(3, 3, Nuit)], apparition);
                if (p.RangGauche.Length > 0)
                {
                    var h = TraitsDesEcrans.Mesure(g, p.RangGauche, rang).Height;
                    g.DrawString(p.RangGauche, rang, or, finG.Right + 10, y + 42 - h * 0.92f, TraitsDesEcrans.Typo);
                }

                var boiteD = TraitsDesEcrans.BoiteDuTexte(p.Droite, famille, 42);
                TraitsDesEcrans.TexteEnRelief(g, p.Droite, famille, 42, new PointF(x + largeur - boiteD.Width - boiteD.X, y + 42 - boiteD.Bottom), p.Atteint ? Dore : orange, Nuit, 0,
                    [(3, 3, Nuit)], apparition);
                if (p.RangDroite.Length > 0)
                {
                    var m = TraitsDesEcrans.Mesure(g, p.RangDroite, rang);
                    g.DrawString(p.RangDroite, rang, or, x + largeur - boiteD.Width - 10 - m.Width, y + 42 - m.Height * 0.92f, TraitsDesEcrans.Typo);
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
                var plein = (float)(part * largeur);
                using var rayures = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb((int)(40 * apparition), 255, 255, 255), Color.Transparent);
                using (var reste = new SolidBrush(TraitsDesEcrans.Alpha(Color.FromArgb(90, orange), apparition)))
                {
                    g.FillRectangle(reste, barre);
                }

                using (var couleur = new SolidBrush(TraitsDesEcrans.Alpha(p.Atteint && remplissage >= 1 ? Dore : violet, apparition)))
                {
                    g.FillRectangle(couleur, barre.X, barre.Y, plein, barre.Height);
                }

                g.FillRectangle(rayures, barre.X, barre.Y, plein, barre.Height);
                if (plein > 2 && plein < largeur - 2)
                {
                    using var separation = new Pen(TraitsDesEcrans.Alpha(Color.White, apparition), 4);
                    g.DrawLine(separation, barre.X + plein, barre.Y, barre.X + plein, barre.Bottom);
                }

                // La part atteinte, dans la barre, quand elle a la place.
                var pourcent = Math.Round(part * 100).ToString(CultureInfo.InvariantCulture) + " %";
                using (var police = TraitsDesEcrans.Libelles(22, secours))
                {
                    var largeurTexte = TraitsDesEcrans.Mesure(g, pourcent, police).Width;
                    if (plein > largeurTexte + 30)
                    {
                        using var sombre = new SolidBrush(TraitsDesEcrans.Alpha(Nuit, apparition));
                        g.DrawString(pourcent, police, sombre, barre.X + plein - largeurTexte - 14, barre.Y + 6, TraitsDesEcrans.Typo);
                    }
                }

                g.Clip = clip;
                using var bord = new Pen(TraitsDesEcrans.Alpha(Nuit, apparition), 3);
                g.DrawPath(bord, forme);
            }

            return 318;
        }
    }
}
