using System.Drawing;
using System.Drawing.Drawing2D;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LE PANNEAU MULTIPLAYER (CDC MULTIPLAYER v2.5, lot M3) dans la fenetre du classement : meme place a
/// gauche du menu d'ES, meme habillage du theme, memes onglets et meme barre d'aide. Seul le contenu
/// change : une ligne par joueur (avatar, nom, pays dessous, connexion, pret), et un decompte en
/// anneau qui passe au rouge aux 5 dernieres secondes (V24-07, V24-18).
///
/// Ni le titre de l'onglet ni le nom du jeu ne se repetent dans la liste, et aucun texte
/// d'explication ne s'ecrit dessous (V24-18). Le niveau Classique se voit tout de suite : son badge
/// prend la place de la rotation (00quater.10).
/// </summary>
public sealed partial class LeaderboardOverlayService
{
    /// <summary>Une ligne de joueur. <c>Barres</c> : 0 a 4 selon la latence, null si elle est inconnue.</summary>
    public sealed record JoueurMultiplayer(
        string Nom,
        string Pays,
        string Couleur,
        bool Hote,
        bool Moi,
        bool Present,
        int? Barres,
        string Etat,
        string MotHote = "HOST",
        string Personnage = "",
        string? Avatar = null,
        string? Portrait = null,
        int? Place = null,
        bool Libre = false,
        string Cote = "",
        string Provisoire = "",
        string Evolution = "",
        string Issue = "",
        bool Gagnant = false,
        IReadOnlyList<StatDuJoueur>? Stats = null);

    /// <summary>Une statistique d'un joueur au resultat : son libelle et sa valeur (« COUPS », « 12 »).</summary>
    public sealed record StatDuJoueur(string Libelle, string Valeur);

    /// <summary>
    /// Comment les places se rangent (retour du user du 22/10) : deux cartes cote a cote a deux places, une grille 2 x 2 a
    /// trois ou quatre, une ligne par joueur au-dela (et pour les amis).
    /// </summary>
    public enum DispositionDesJoueurs
    {
        Liste,
        Colonnes,
        Grille,
    }

    /// <summary>
    /// Un personnage de la bande de choix : son portrait carre (badge RetroAchievements en cache), ou rien encore, et son nom.
    /// <c>Avatar</c> d'un joueur : la planche NelfeNet (PNG 320 x 192), dont on dessine la tete de face au repos.
    /// </summary>
    public sealed record PortraitDeChoix(string? Chemin, string Nom);

    /// <summary>Ce que le panneau MULTIPLAYER montre. Remplace en bloc, comme le classement.</summary>
    public sealed record ContenuMultiplayer(
        IReadOnlyList<string> Onglets,
        int OngletCourant,
        bool NousAvonsLaMain,
        string Badge,
        string Info,
        string Compte,
        DateTime? DecompteFinUtc,
        IReadOnlyList<JoueurMultiplayer> Lignes,
        int LigneCourante,
        string Message,
        string Bandeau,
        IReadOnlyList<Aide> Aides,
        string Pseudo = "",
        bool EnLigne = false,
        IReadOnlyList<PortraitDeChoix>? Selection = null,
        int SelectionCourante = -1,
        Partage? PartageDesDegats = null,
        DispositionDesJoueurs Disposition = DispositionDesJoueurs.Liste);

    /// <summary>
    /// La barre de partage du resultat (00quater.3) : la part des degats infliges par chaque cote d'un duel, a ses
    /// couleurs. <c>PartGauche</c> va de 0 a 1.
    /// </summary>
    public sealed record Partage(double PartGauche, string CouleurGauche, string CouleurDroite, string Libelle);

    /// <summary>La teinte du badge CLASSIQUE (toile, ecrans 13 a 15).</summary>
    internal const string TeinteClassique = "B8C7D9";

    private ContenuMultiplayer? _multiplayer;

    /// <summary>Le panneau montre-t-il MULTIPLAYER ?</summary>
    public bool AfficheMultiplayer
    {
        get { lock (_gate) return Affiche && _multiplayer is not null; }
    }

    public void OuvrirMultiplayer(EsMenuStyle style, ContenuMultiplayer contenu)
        => OuvrirAvec(style, Lire() with { Attente = "", AttenteTitre = "", AttenteDetail = "" }, contenu);

    public void MontrerMultiplayer(ContenuMultiplayer contenu)
    {
        lock (_gate)
        {
            if (_multiplayer is null)
            {
                return;   // le panneau a ete ferme ou rendu au classement entre-temps
            }

            _multiplayer = contenu;
            _dernierGeste = DateTime.UtcNow;
        }

        _forme?.Rafraichir();
    }

    private ContenuMultiplayer? LireMultiplayer()
    {
        lock (_gate) return _multiplayer;
    }

    private sealed partial class Panneau
    {
        private void DessinerLeMultiplayer(
            Graphics g,
            ContenuMultiplayer c,
            Contenu socle,
            EsMenuStyle s,
            Font policeTitre,
            Font policeTexte,
            Font policePetite,
            Font policeGras,
            Font policePied,
            float tailleTitre,
            float tailleTexte,
            float taillePetite,
            float taillePied)
        {
            // Le logo MULTIPLAYER tient le titre, comme celui de World Scoring.
            var hauteurTitre = tailleTitre * 1.5f;
            var y = _hauteurEcran * 0.0637f - hauteurTitre / 2f;
            var chemin = Path.Combine(RetroBatPaths.ThemeResourcesRoot, "images", "nelfeplay-multiplayer.png");
            var logo = File.Exists(chemin) ? _service._glyphes?.Glyphe(chemin, (int)Math.Round(hauteurTitre * 1.75f)) : null;
            if (logo is not null && logo.Width > Width * 0.75f)
            {
                logo = _service._glyphes?.Glyphe(chemin, (int)Math.Round(hauteurTitre * 1.75f * Width * 0.75f / logo.Width));
            }

            if (logo is not null)
            {
                g.DrawImage(logo, (Width - logo.Width) / 2f, y + (hauteurTitre - logo.Height) / 2f - hauteurTitre * 0.22f, logo.Width, logo.Height);
            }
            else
            {
                using var encre = new SolidBrush(Teinte(s.TitleColor));
                using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("MULTIPLAYER", policeTitre, encre, new RectangleF(0, y, Width, hauteurTitre), centre);
            }

            y = _hauteurEcran * 0.0637f * 2f - hauteurTitre / 2f;
            var marge = _largeurEcran * 0.012f;

            // L'information (badge Classique, nombre de joueurs, decompte) dans une carte au-dessus des onglets : la liste
            // dessous gagne sa ligne (retour du user du 22/10).
            if (c.Badge.Length > 0 || c.Info.Length > 0 || c.Compte.Length > 0 || c.DecompteFinUtc is not null)
            {
                var carte = new RectangleF(marge, y, Width - 2 * marge, tailleTexte * 1.9f);
                using (var contour = Arrondi(carte, tailleTexte * 0.3f))
                using (var fond = new SolidBrush(Color.FromArgb(120, 8, 10, 14)))
                {
                    g.FillPath(fond, contour);
                }

                DessinerLInfoMultiplayer(g, c, s, policeTexte, policePetite, tailleTexte, marge + tailleTexte * 0.5f, y);
                y = carte.Bottom + tailleTexte * 0.45f;
            }

            Bordure(g, s, y);

            // Les onglets, dessines par le meme code que le classement.
            var onglets = socle with { Onglets = c.Onglets, OngletCourant = c.OngletCourant, NousAvonsLaMain = c.NousAvonsLaMain, Pastilles = null };
            y = DessinerLesOnglets(g, onglets, s, policeTexte, tailleTexte, y);
            y += tailleTexte * 0.6f;

            if (c.PartageDesDegats is { } partage)
            {
                y = DessinerLePartage(g, partage, s, policePetite, tailleTexte, taillePetite, marge, y);
            }

            if (c.Bandeau.Length > 0)
            {
                using var encre = new SolidBrush(Teinte(s.GroupColor));
                using var gauche = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.Word };
                var zone = new RectangleF(marge, y, Width - 2 * marge, taillePetite * 2.8f);
                g.DrawString(c.Bandeau, policePetite, encre, zone, gauche);
                y += taillePetite * 3f;
            }

            var basDeLAide = Height - taillePied * 1.5f - tailleTexte * 1.9f - _hauteurEcran * 0.012f;
            var basDesJoueurs = basDeLAide;
            if (c.Selection is { Count: > 0 } selection)
            {
                // NIVEAU NELFEPLAY : la bande des personnages au-dessus de l'aide, le choix courant en grand.
                basDesJoueurs = DessinerLaSelection(g, selection, c.SelectionCourante, s, policePetite, tailleTexte, taillePetite, marge, basDeLAide);
            }

            if (c.Lignes.Count == 0)
            {
                using var encre = new SolidBrush(Teinte(s.TextColor));
                using var gauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
                g.DrawString(c.Message.ToUpperInvariant(), policeTexte, encre, new RectangleF(marge, y, Width - 2 * marge, tailleTexte * 1.5f), gauche);
            }
            else if (c.Disposition == DispositionDesJoueurs.Liste)
            {
                DessinerLesJoueurs(g, c, s, policeGras, policePetite, tailleTexte, taillePetite, marge, y, basDesJoueurs);
            }
            else
            {
                DessinerLesCartes(g, c, s, policeGras, policePetite, tailleTexte, marge, y, basDesJoueurs);
            }

            DessinerLAide(g, socle with { Aides = c.Aides, Pseudo = c.Pseudo, EnLigne = c.EnLigne }, s, policePied, taillePied, basDeLAide + tailleTexte * 1.9f);
        }

        /// <summary>
        /// La ligne d'information : le badge du niveau Classique (ou rien), le texte qui l'accompagne,
        /// puis a droite le nombre de joueurs et l'anneau du decompte.
        /// </summary>
        private float DessinerLInfoMultiplayer(Graphics g, ContenuMultiplayer c, EsMenuStyle s, Font police, Font petite, float taille, float marge, float y)
        {
            var hauteur = taille * 1.9f;
            var x = marge;
            using var centre = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
            if (c.Badge.Length > 0)
            {
                using var policeBadge = new Font(petite.FontFamily, petite.Size * 0.95f, FontStyle.Bold, petite.Unit);
                var largeur = g.MeasureString(c.Badge, policeBadge, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.9f;
                var zone = new RectangleF(x, y + (hauteur - taille * 1.15f) / 2f, largeur, taille * 1.15f);
                using (var chemin = Arrondi(zone, taille * 0.25f))
                using (var fond = new SolidBrush(Teinte(TeinteClassique)))
                {
                    g.FillPath(fond, chemin);
                }

                using var encreBadge = new SolidBrush(Color.FromArgb(20, 26, 34));
                using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(c.Badge, policeBadge, encreBadge, zone, milieu);
                x += largeur + taille * 0.6f;
            }

            // A droite : l'anneau, puis le compte a sa gauche.
            var diametre = taille * 1.7f;
            var droite = Width - marge;
            var reste = c.DecompteFinUtc is { } fin ? Math.Max(0, (int)Math.Ceiling((fin - DateTime.UtcNow).TotalSeconds)) : (int?)null;
            if (reste is { } secondes)
            {
                var rouge = secondes <= 5;
                var cercle = new RectangleF(droite - diametre, y + (hauteur - diametre) / 2f, diametre, diametre);
                using var trait = new Pen(rouge ? Color.FromArgb(229, 83, 75) : Teinte(s.SelectorColor), Math.Max(2f, taille * 0.16f));
                g.DrawEllipse(trait, cercle);
                using var encre = new SolidBrush(rouge ? Color.FromArgb(229, 83, 75) : Teinte(s.TitleColor));
                using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(secondes.ToString(System.Globalization.CultureInfo.InvariantCulture), police, encre, cercle, milieu);
                droite -= diametre + taille * 0.6f;
            }

            if (c.Compte.Length > 0)
            {
                using var encre = new SolidBrush(Teinte(s.TextColor));
                using var aDroite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                var largeur = g.MeasureString(c.Compte, police, PointF.Empty, StringFormat.GenericTypographic).Width + 4f;
                g.DrawString(c.Compte, police, encre, new RectangleF(droite - largeur, y, largeur, hauteur), aDroite);
                droite -= largeur + taille * 0.6f;
            }

            if (c.Info.Length > 0 && droite > x)
            {
                using var encre = new SolidBrush(Teinte(s.GroupColor));
                g.DrawString(Couper(g, c.Info, petite, droite - x), petite, encre, new RectangleF(x, y, droite - x, hauteur), centre);
            }

            return y + hauteur + taille * 0.4f;
        }

        /// <summary>Une ligne par joueur : avatar, nom et pays dessous, puis hote, connexion et pret a droite.</summary>
        private void DessinerLesJoueurs(
            Graphics g, ContenuMultiplayer c, EsMenuStyle s, Font gras, Font petite, float taille, float taillePetite, float marge, float y, float bas)
        {
            var hauteur = taille * 2.7f;
            var ecart = taille * 0.35f;
            for (var i = 0; i < c.Lignes.Count && y + hauteur <= bas; i++)
            {
                var j = c.Lignes[i];
                var zone = new RectangleF(marge, y, Width - 2 * marge, hauteur);
                if (j.Libre)
                {
                    DessinerUnePlaceLibre(g, j, s, gras, petite, taille, zone);
                    y += hauteur + ecart;
                    continue;
                }

                // Sur la ligne choisie, tout le texte prend la couleur des textes selectionnes d'ES : le pays et
                // l'etat disparaissaient sur le bleu de la selection (apercu du 22/10).
                var choisie = c.NousAvonsLaMain && i == c.LigneCourante;
                if (choisie)
                {
                    using var chemin = Arrondi(zone, taille * 0.3f);
                    using var fond = new SolidBrush(Teinte(s.SelectorColor));
                    g.FillPath(fond, chemin);
                }
                else
                {
                    using var chemin = Arrondi(zone, taille * 0.3f);
                    using var fond = new SolidBrush(Color.FromArgb(j.Present ? 120 : 60, 8, 10, 14));
                    g.FillPath(fond, chemin);
                }

                // Le numero de la place (la manette du netplay) sur le bord gauche, le drapeau en petit dessous.
                var gaucheAvatar = DessinerLeNumeroEtLeDrapeau(g, j, s, petite, taille, zone, choisie);

                // Le carre de gauche : au niveau NelfePlay, le portrait du personnage choisi (demande du user du 22/10) ;
                // sinon la tete de face de sa planche ; a defaut, un carre a sa couleur et son initiale.
                var cote = hauteur * 0.7f;
                var avatar = new RectangleF(gaucheAvatar, y + (hauteur - cote) / 2f, cote, cote);
                var alpha = j.Present ? 255 : 130;
                if (Image(j.Portrait) is { } portrait)
                {
                    DessinerUnPortrait(g, portrait, avatar, s, choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha), Math.Max(1.5f, _hauteurEcran / 700f));
                }
                else
                {
                    using (var chemin = Arrondi(avatar, cote * 0.22f))
                    using (var fond = new SolidBrush(Teinte(j.Couleur, j.Present ? 255 : 110)))
                    {
                        g.FillPath(fond, chemin);
                    }

                    if (Image(j.Avatar) is { } planche && planche.Width >= 64 && planche.Height >= 64)
                    {
                        var etatAvatar = g.Save();
                        using (var masque = Arrondi(avatar, cote * 0.22f))
                        {
                            g.SetClip(masque);
                        }

                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        using var attenuation = j.Present ? null : Transparence(0.5f);
                        g.DrawImage(planche, Rectangle.Round(avatar), 0, 0, 64, 64, GraphicsUnit.Pixel, attenuation);
                        g.Restore(etatAvatar);
                    }
                    else
                    {
                        using var encre = new SolidBrush(Color.FromArgb(j.Present ? 255 : 150, 12, 12, 12));
                        using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        var initiale = j.Nom.Length > 0 ? j.Nom[..1].ToUpperInvariant() : "?";
                        g.DrawString(initiale, gras, encre, avatar, milieu);
                    }
                }

                // A droite, de l'exterieur vers l'interieur : pret (ou absent), connexion, hote.
                var droite = zone.Right - taille * 0.5f;
                using (var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : CouleurDeLEtat(j, s)))
                using (var aDroite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                {
                    var etat = j.Issue.Length > 0 ? j.Issue : j.Etat;
                    var largeur = g.MeasureString(etat, petite, PointF.Empty, StringFormat.GenericTypographic).Width + 4f;
                    g.DrawString(etat, petite, encre, new RectangleF(droite - largeur, y, largeur, hauteur), aDroite);
                    droite -= largeur + taille * 0.7f;
                }

                // Pas de mesure, pas de barres : des barres grises pour un ami dont on ignore la latence ne disaient rien.
                if (j.Barres is not null)
                {
                    droite = DessinerLaConnexion(g, s, j.Barres, droite, y, hauteur, taille, alpha);
                }

                if (j.Hote)
                {
                    using var policeHote = new Font(petite.FontFamily, petite.Size * 0.85f, FontStyle.Bold, petite.Unit);
                    var largeur = g.MeasureString(j.MotHote, policeHote, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.6f;
                    var etiquette = new RectangleF(droite - largeur, y + (hauteur - taille) / 2f, largeur, taille);
                    using (var chemin = Arrondi(etiquette, taille * 0.22f))
                    using (var trait = new Pen(Teinte(s.TitleColor, alpha), Math.Max(1.2f, _hauteurEcran / 900f)))
                    {
                        g.DrawPath(trait, chemin);
                    }

                    using var encre = new SolidBrush(Teinte(s.TitleColor, alpha));
                    using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(j.MotHote, policeHote, encre, etiquette, milieu);
                    droite -= largeur + taille * 0.6f;
                }

                // Le nom, puis sa cote a la suite (retour du user du 22/10). Pas de ville (V24-18).
                var x = avatar.Right + taille * 0.6f;
                var dessous = string.Join("  ·  ", new[] { j.Personnage }.Concat((j.Stats ?? []).Select(st => st.Libelle + " " + st.Valeur)).Where(t => t.Length > 0));
                var mesure = g.MeasureString("Ag", gras, PointF.Empty, StringFormat.GenericTypographic);
                // Sans rien dessous (niveau Classique), le nom se centre sur la carte.
                var hautDuNom = dessous.Length > 0 ? y + hauteur * 0.14f : y + (hauteur - mesure.Height) / 2f;
                DessinerLeNomEtLaCote(g, j, s, gras, petite, taille, x, hautDuNom, droite - x, mesure.Height, choisie, alpha, centre: false);

                // NIVEAU NELFEPLAY : le personnage du joueur sous son nom (▲▼ le change), et ses statistiques au resultat.
                if (dessous.Length > 0)
                {
                    using var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha));
                    g.DrawString(Couper(g, dessous, petite, Math.Max(0, droite - x)), petite, encre,
                        new PointF(x, y + hauteur * 0.56f), StringFormat.GenericTypographic);
                }

                y += hauteur + ecart;
            }
        }

        /// <summary>
        /// La barre de partage des degats : deux barres qui se rejoignent au point d'equilibre, chacune a la couleur de son
        /// joueur, les pourcentages a leurs bouts. Rend le bas de la barre.
        /// </summary>
        private float DessinerLePartage(Graphics g, Partage partage, EsMenuStyle s, Font petite, float taille, float taillePetite, float marge, float y)
        {
            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };
            using (var encre = new SolidBrush(Teinte(s.GroupColor)))
            {
                g.DrawString(partage.Libelle, petite, encre, new RectangleF(marge, y, Width - 2 * marge, taillePetite * 1.4f), centre);
            }

            y += taillePetite * 1.5f;
            var hauteur = taille * 1.1f;
            var zone = new RectangleF(marge, y, Width - 2 * marge, hauteur);
            var part = (float)Math.Clamp(partage.PartGauche, 0, 1);
            var milieu = zone.X + zone.Width * part;
            var etat = g.Save();
            using (var forme = Arrondi(zone, hauteur * 0.5f))
            {
                g.SetClip(forme);
            }

            using (var gauche = new SolidBrush(Teinte(partage.CouleurGauche)))
            using (var droite = new SolidBrush(Teinte(partage.CouleurDroite)))
            {
                g.FillRectangle(gauche, zone.X, zone.Y, milieu - zone.X, hauteur);
                g.FillRectangle(droite, milieu, zone.Y, zone.Right - milieu, hauteur);
            }

            // L'eclair du point de rencontre : un trait clair, comme le choc des deux barres.
            using (var eclair = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(2f, _hauteurEcran / 500f)))
            {
                g.DrawLine(eclair, milieu, zone.Y, milieu, zone.Bottom);
            }

            g.Restore(etat);
            using var police = new Font(petite.FontFamily, petite.Size, FontStyle.Bold, petite.Unit);
            using var sombre = new SolidBrush(Color.FromArgb(20, 22, 28));
            using var aGauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
            using var aDroite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var pourcentGauche = (int)Math.Round(part * 100);
            g.DrawString(pourcentGauche.ToString(System.Globalization.CultureInfo.InvariantCulture) + " %", police, sombre,
                new RectangleF(zone.X + taille * 0.5f, zone.Y, zone.Width / 2f, hauteur), aGauche);
            g.DrawString((100 - pourcentGauche).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %", police, sombre,
                new RectangleF(zone.X + zone.Width / 2f, zone.Y, zone.Width / 2f - taille * 0.5f, hauteur), aDroite);
            return zone.Bottom + taille * 0.6f;
        }

        private static readonly Color Or = Color.FromArgb(255, 214, 102);
        private static readonly Color Vert = Color.FromArgb(120, 229, 163);
        private static readonly Color Rouge = Color.FromArgb(229, 83, 75);

        /// <summary>La couleur de l'etat d'un joueur : la victoire en or, pret en vert, le reste discret.</summary>
        private Color CouleurDeLEtat(JoueurMultiplayer j, EsMenuStyle s)
            => j.Issue.Length > 0 ? (j.Gagnant ? Or : Teinte(s.GroupColor, 220))
                : j.Present ? Vert : Teinte(s.GroupColor, 200);

        /// <summary>
        /// La colonne de gauche d'une ligne : la pastille du numero, le drapeau en petit dessous (retour du user du 22/10) ;
        /// sans numero (les amis), le drapeau seul. Rend le bord gauche de l'avatar.
        /// </summary>
        private float DessinerLeNumeroEtLeDrapeau(Graphics g, JoueurMultiplayer j, EsMenuStyle s, Font petite, float taille, RectangleF zone, bool choisie)
        {
            var diametre = taille * 1.15f;
            var hauteurDrapeau = taille * 0.5f;
            var avecDrapeau = j.Pays.Length > 0;
            if (j.Place is { } place)
            {
                var bloc = diametre + (avecDrapeau ? taille * 0.22f + hauteurDrapeau : 0f);
                var pastille = new RectangleF(zone.X + taille * 0.4f, zone.Y + (zone.Height - bloc) / 2f, diametre, diametre);
                DessinerLaPastille(g, place, s, petite, pastille, choisie, libre: false);
                if (avecDrapeau)
                {
                    DessinerLeDrapeau(g, j.Pays, s, petite, new RectangleF(pastille.X + (diametre - hauteurDrapeau * 1.5f) / 2f, pastille.Bottom + taille * 0.22f,
                        hauteurDrapeau * 1.5f, hauteurDrapeau), choisie, j.Present ? 255 : 130);
                }

                return pastille.Right + taille * 0.45f;
            }

            if (avecDrapeau)
            {
                var drapeau = new RectangleF(zone.X + taille * 0.4f, zone.Y + (zone.Height - hauteurDrapeau) / 2f, hauteurDrapeau * 1.5f, hauteurDrapeau);
                DessinerLeDrapeau(g, j.Pays, s, petite, drapeau, choisie, j.Present ? 255 : 130);
                return drapeau.Right + taille * 0.45f;
            }

            return zone.X + taille * 0.45f;
        }

        /// <summary>Un drapeau ; un pays qu'on ne sait pas dessiner garde son code.</summary>
        private void DessinerLeDrapeau(Graphics g, string pays, EsMenuStyle s, Font petite, RectangleF drapeau, bool choisie, int alpha)
        {
            if (Drapeaux.Dessiner(g, pays, drapeau, alpha))
            {
                return;
            }

            using var police = new Font(petite.FontFamily, petite.Size * 0.8f, petite.Style, petite.Unit);
            using var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.GroupColor, alpha));
            using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(pays, police, encre, new RectangleF(drapeau.X - drapeau.Width, drapeau.Y, drapeau.Width * 3f, drapeau.Height), milieu);
        }

        /// <summary>
        /// Le pseudo, puis a sa suite la cote (« ELO 1516 »), son ecart au resultat (« +16 », en vert ou en rouge) et la
        /// mention « provisoire » tant qu'elle l'est (retour du user du 22/10 : le « ? » ne disait rien). Centre dans une
        /// carte ; sinon a partir de <paramref name="x"/>.
        /// </summary>
        private void DessinerLeNomEtLaCote(Graphics g, JoueurMultiplayer j, EsMenuStyle s, Font gras, Font petite, float taille,
            float x, float haut, float largeur, float hauteurLigne, bool choisie, int alpha, bool centre)
        {
            using var policeCote = new Font(petite.FontFamily, petite.Size, FontStyle.Bold, petite.Unit);
            var morceaux = new List<(string Texte, Font Police, Color Couleur)>();
            var ecart = taille * 0.45f;
            float Largeur(string t, Font f) => g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width;
            var suite = 0f;
            if (j.Cote.Length > 0)
            {
                morceaux.Add((j.Cote, policeCote, choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha)));
                if (j.Evolution.Length > 0)
                {
                    morceaux.Add((j.Evolution, policeCote, choisie ? Teinte(s.SelectedTextColor) : j.Evolution.StartsWith('-') ? Rouge : Vert));
                }

                if (j.Provisoire.Length > 0)
                {
                    morceaux.Add((j.Provisoire, petite, choisie ? Teinte(s.SelectedTextColor) : Teinte(s.GroupColor, alpha)));
                }

                suite = morceaux.Sum(m => Largeur(m.Texte, m.Police) + ecart * 0.6f) + ecart;
            }

            var nom = Couper(g, j.Nom, gras, Math.Max(0, largeur - suite));
            var total = Largeur(nom, gras) + suite;
            var depart = centre ? x + Math.Max(0, (largeur - total) / 2f) : x;
            using (var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : j.Moi ? Teinte(s.TitleColor, alpha) : Teinte(s.TextColor, alpha)))
            {
                g.DrawString(nom, gras, encre, new PointF(depart, haut), StringFormat.GenericTypographic);
            }

            var curseur = depart + Largeur(nom, gras) + ecart;
            using var basDeLigne = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
            foreach (var (texte, police, couleur) in morceaux)
            {
                using var encre = new SolidBrush(couleur);
                var w = Largeur(texte, police);
                g.DrawString(texte, police, encre, new RectangleF(curseur, haut, w + 2f, hauteurLigne), basDeLigne);
                curseur += w + ecart * 0.6f;
            }
        }

        /// <summary>
        /// Les places en cartes (retour du user du 22/10) : deux cartes cote a cote a deux places, une grille 2 x 2 a trois
        /// ou quatre. Chaque carte : le numero et le drapeau en haut a gauche, la connexion et l'hote en haut a droite, le
        /// portrait (ou l'avatar) au milieu, puis le pseudo et sa cote, le personnage, l'etat, et au resultat les statistiques.
        /// </summary>
        private void DessinerLesCartes(Graphics g, ContenuMultiplayer c, EsMenuStyle s, Font gras, Font petite, float taille, float marge, float y, float bas)
        {
            var rangees = c.Disposition == DispositionDesJoueurs.Grille ? 2 : 1;
            var ecart = taille * 0.45f;
            var largeur = (Width - 2 * marge - ecart) / 2f;
            var dispo = (bas - y - taille * 0.3f - (rangees - 1) * ecart) / rangees;
            var cote = rangees == 1 ? largeur * 0.45f : Math.Min(largeur * 0.34f, dispo * 0.4f);
            var cartes = c.Lignes.Take(2 * rangees).ToList();
            // Toutes les cartes a la meme hauteur : celle de la plus remplie, sans depasser la place.
            var hauteur = Math.Min(dispo, cartes.Max(j => HauteurDUneCarte(g, j, gras, petite, taille, cote)));
            for (var i = 0; i < cartes.Count; i++)
            {
                var zone = new RectangleF(marge + i % 2 * (largeur + ecart), y + i / 2 * (hauteur + ecart), largeur, hauteur);
                if (cartes[i].Libre)
                {
                    DessinerUneCarteLibre(g, cartes[i], s, gras, petite, taille, zone, cote);
                }
                else
                {
                    DessinerUneCarteDeJoueur(g, cartes[i], s, gras, petite, taille, zone, cote, c.NousAvonsLaMain && i == c.LigneCourante);
                }
            }
        }

        /// <summary>La hauteur qu'il faut a une carte pour tout montrer, avec un portrait de ce cote.</summary>
        private float HauteurDUneCarte(Graphics g, JoueurMultiplayer j, Font gras, Font petite, float taille, float cote)
        {
            float H(Font f) => g.MeasureString("Ag", f, PointF.Empty, StringFormat.GenericTypographic).Height;
            var pad = taille * 0.45f;
            var hauteur = pad + cote + taille * 0.45f + H(gras) * 1.25f;
            if (j.Libre)
            {
                return hauteur + H(gras) + pad;
            }

            if (j.Personnage.Length > 0)
            {
                hauteur += H(petite) * 1.35f;
            }

            using (var policeEtat = new Font(gras.FontFamily, j.Issue.Length > 0 ? gras.Size * 1.05f : petite.Size, FontStyle.Bold, gras.Unit))
            {
                hauteur += H(policeEtat) * 1.3f;
            }

            if (j.Stats is { Count: > 0 } stats)
            {
                using var policeValeur = new Font(gras.FontFamily, gras.Size * 0.95f, FontStyle.Bold, gras.Unit);
                hauteur += taille * 0.45f + (stats.Count + 1) / 2 * (H(petite) + H(policeValeur) + taille * 0.25f);
            }

            return hauteur + pad;
        }

        private void DessinerUneCarteDeJoueur(Graphics g, JoueurMultiplayer j, EsMenuStyle s, Font gras, Font petite, float taille, RectangleF zone, float cote, bool choisie)
        {
            var alpha = j.Present ? 255 : 130;
            using (var chemin = Arrondi(zone, taille * 0.35f))
            using (var fond = new SolidBrush(choisie ? Teinte(s.SelectorColor) : Color.FromArgb(j.Present ? 120 : 60, 8, 10, 14)))
            {
                g.FillPath(fond, chemin);
                if (j.Gagnant)
                {
                    using var trait = new Pen(Or, Math.Max(2f, _hauteurEcran / 450f));
                    g.DrawPath(trait, chemin);
                }
            }

            var pad = taille * 0.45f;
            var diametre = taille * 1.15f;
            var haut = zone.Y + pad;

            // En haut a gauche : le numero, le drapeau en petit dessous.
            if (j.Place is { } place)
            {
                var pastille = new RectangleF(zone.X + pad, haut, diametre, diametre);
                DessinerLaPastille(g, place, s, petite, pastille, choisie, libre: false);
                if (j.Pays.Length > 0)
                {
                    var h = taille * 0.5f;
                    DessinerLeDrapeau(g, j.Pays, s, petite, new RectangleF(pastille.X + (diametre - h * 1.5f) / 2f, pastille.Bottom + taille * 0.25f, h * 1.5f, h), choisie, alpha);
                }
            }

            // En haut a droite : la connexion, l'hote dessous (a cote, il mordait sur le portrait).
            var droite = zone.Right - pad;
            if (j.Barres is not null)
            {
                DessinerLaConnexion(g, s, j.Barres, droite, haut, diametre, taille, alpha);
            }

            if (j.Hote)
            {
                using var policeHote = new Font(petite.FontFamily, petite.Size * 0.85f, FontStyle.Bold, petite.Unit);
                var largeurHote = g.MeasureString(j.MotHote, policeHote, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.6f;
                var etiquette = new RectangleF(droite - largeurHote, haut + diametre + taille * 0.25f, largeurHote, taille);
                using (var chemin = Arrondi(etiquette, taille * 0.22f))
                using (var trait = new Pen(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha), Math.Max(1.2f, _hauteurEcran / 900f)))
                {
                    g.DrawPath(trait, chemin);
                }

                using var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha));
                using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(j.MotHote, policeHote, encre, etiquette, milieu);
            }

            // Au milieu : le portrait du personnage (niveau NelfePlay), sinon la tete de l'avatar, sinon l'initiale.
            var carre = new RectangleF(zone.X + (zone.Width - cote) / 2f, haut, cote, cote);
            DessinerLeVisage(g, j, s, gras, carre, choisie, alpha);

            // Dessous, centres : le pseudo et sa cote, le personnage, l'etat (ou la victoire).
            var largeur = zone.Width - 2 * pad;
            var y = carre.Bottom + taille * 0.45f;
            var hauteurNom = g.MeasureString("Ag", gras, PointF.Empty, StringFormat.GenericTypographic).Height;
            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };
            var limite = zone.Bottom - pad;
            DessinerLeNomEtLaCote(g, j, s, gras, petite, taille, zone.X + pad, y, largeur, hauteurNom, choisie, alpha, centre: true);
            y += hauteurNom * 1.25f;
            var hauteurPetite = g.MeasureString("Ag", petite, PointF.Empty, StringFormat.GenericTypographic).Height;
            if (j.Personnage.Length > 0 && y + hauteurPetite <= limite)
            {
                using var policePerso = new Font(petite.FontFamily, petite.Size, FontStyle.Bold, petite.Unit);
                using var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha));
                g.DrawString(Couper(g, j.Personnage, policePerso, largeur), policePerso, encre, new RectangleF(zone.X + pad, y, largeur, hauteurPetite * 1.3f), centre);
                y += hauteurPetite * 1.35f;
            }

            var etat = j.Issue.Length > 0 ? j.Issue : j.Etat;
            using (var policeEtat = new Font(gras.FontFamily, j.Issue.Length > 0 ? gras.Size * 1.05f : petite.Size, FontStyle.Bold, gras.Unit))
            {
                var h = g.MeasureString("Ag", policeEtat, PointF.Empty, StringFormat.GenericTypographic).Height;
                if (etat.Length > 0 && y + h <= limite)
                {
                    using var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : CouleurDeLEtat(j, s));
                    g.DrawString(Couper(g, etat, policeEtat, largeur), policeEtat, encre, new RectangleF(zone.X + pad, y, largeur, h * 1.2f), centre);
                    y += h * 1.3f;
                }
            }

            // Au resultat : les statistiques, deux par rangee, sous un trait fin.
            if (j.Stats is { Count: > 0 } stats && y + hauteurPetite * 2.4f <= limite)
            {
                using (var trait = new Pen(choisie ? Teinte(s.SelectedTextColor, 120) : Teinte(s.GroupColor, 90), 1f))
                {
                    g.DrawLine(trait, zone.X + pad, y + taille * 0.15f, zone.Right - pad, y + taille * 0.15f);
                }

                y += taille * 0.45f;
                var demi = largeur / 2f;
                using var policeValeur = new Font(gras.FontFamily, gras.Size * 0.95f, FontStyle.Bold, gras.Unit);
                var hValeur = g.MeasureString("Ag", policeValeur, PointF.Empty, StringFormat.GenericTypographic).Height;
                for (var k = 0; k < stats.Count; k++)
                {
                    var rangee = y + k / 2 * (hauteurPetite + hValeur + taille * 0.25f);
                    if (rangee + hauteurPetite + hValeur > limite)
                    {
                        break;
                    }

                    var case_ = new RectangleF(zone.X + pad + k % 2 * demi, rangee, demi, hauteurPetite + hValeur);
                    using var pale = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.GroupColor, alpha));
                    using var clair = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha));
                    g.DrawString(stats[k].Libelle, petite, pale, new RectangleF(case_.X, case_.Y, demi, hauteurPetite * 1.2f), centre);
                    g.DrawString(stats[k].Valeur, policeValeur, clair, new RectangleF(case_.X, case_.Y + hauteurPetite * 1.05f, demi, hValeur * 1.2f), centre);
                }
            }
        }

        /// <summary>Le carre d'un joueur : le portrait du personnage, sinon la tete de sa planche, sinon son initiale.</summary>
        private void DessinerLeVisage(Graphics g, JoueurMultiplayer j, EsMenuStyle s, Font gras, RectangleF carre, bool choisie, int alpha)
        {
            if (Image(j.Portrait) is { } portrait)
            {
                DessinerUnPortrait(g, portrait, carre, s, choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor, alpha), Math.Max(1.5f, _hauteurEcran / 700f));
                return;
            }

            using (var chemin = Arrondi(carre, carre.Width * 0.18f))
            using (var fond = new SolidBrush(Teinte(j.Couleur, j.Present ? 255 : 110)))
            {
                g.FillPath(fond, chemin);
            }

            if (Image(j.Avatar) is { } planche && planche.Width >= 64 && planche.Height >= 64)
            {
                var etat = g.Save();
                using (var masque = Arrondi(carre, carre.Width * 0.18f))
                {
                    g.SetClip(masque);
                }

                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                using var attenuation = j.Present ? null : Transparence(0.5f);
                g.DrawImage(planche, Rectangle.Round(carre), 0, 0, 64, 64, GraphicsUnit.Pixel, attenuation);
                g.Restore(etat);
                return;
            }

            using var police = new Font(gras.FontFamily, Math.Max(gras.Size, carre.Height * 0.42f), FontStyle.Bold, gras.Unit);
            using var encre = new SolidBrush(Color.FromArgb(j.Present ? 255 : 150, 12, 12, 12));
            using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(j.Nom.Length > 0 ? j.Nom[..1].ToUpperInvariant() : "?", police, encre, carre, milieu);
        }

        /// <summary>Une place libre en carte : en pointilles, son numero, une silhouette vide, « PLACE LIBRE » dessous.</summary>
        private void DessinerUneCarteLibre(Graphics g, JoueurMultiplayer j, EsMenuStyle s, Font gras, Font petite, float taille, RectangleF zone, float cote)
        {
            var epaisseur = Math.Max(1.2f, _hauteurEcran / 900f);
            using (var chemin = Arrondi(zone, taille * 0.35f))
            using (var trait = new Pen(Teinte(s.GroupColor, 150), epaisseur) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            {
                g.DrawPath(trait, chemin);
            }

            var pad = taille * 0.45f;
            var diametre = taille * 1.15f;
            DessinerLaPastille(g, j.Place ?? 0, s, petite, new RectangleF(zone.X + pad, zone.Y + pad, diametre, diametre), choisie: false, libre: true);
            var silhouette = new RectangleF(zone.X + (zone.Width - cote) / 2f, zone.Y + pad, cote, cote);
            using (var chemin = Arrondi(silhouette, cote * 0.18f))
            using (var trait = new Pen(Teinte(s.GroupColor, 120), epaisseur) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
            {
                g.DrawPath(trait, chemin);
            }

            using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var police = new Font(gras.FontFamily, Math.Max(gras.Size, cote * 0.42f), FontStyle.Bold, gras.Unit))
            using (var encre = new SolidBrush(Teinte(s.GroupColor, 140)))
            {
                g.DrawString("?", police, encre, silhouette, milieu);
            }

            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };
            var largeur = zone.Width - 2 * pad;
            var y = silhouette.Bottom + taille * 0.45f;
            var hNom = g.MeasureString("Ag", gras, PointF.Empty, StringFormat.GenericTypographic).Height;
            using (var encre = new SolidBrush(Teinte(s.GroupColor, 220)))
            {
                g.DrawString(Couper(g, j.Nom, gras, largeur), gras, encre, new RectangleF(zone.X + pad, y, largeur, hNom * 1.2f), centre);
            }

            using (var encre = new SolidBrush(Teinte(s.GroupColor, 160)))
            {
                g.DrawString(Couper(g, j.Etat, petite, largeur), petite, encre, new RectangleF(zone.X + pad, y + hNom * 1.25f, largeur, hNom), centre);
            }
        }

        /// <summary>Le numero d'une place, dans une pastille sur le bord gauche de la carte. Rend ou commence l'avatar.</summary>
        private float DessinerLeNumero(Graphics g, int place, EsMenuStyle s, Font petite, float taille, RectangleF zone, bool choisie, bool libre)
        {
            var diametre = taille * 1.15f;
            var pastille = new RectangleF(zone.X + taille * 0.4f, zone.Y + (zone.Height - diametre) / 2f, diametre, diametre);
            DessinerLaPastille(g, place, s, petite, pastille, choisie, libre);
            return pastille.Right + taille * 0.45f;
        }

        /// <summary>La pastille d'un numero de place : pleine pour un joueur, en pointilles pour une place libre.</summary>
        private void DessinerLaPastille(Graphics g, int place, EsMenuStyle s, Font petite, RectangleF pastille, bool choisie, bool libre)
        {
            var diametre = pastille.Width;
            using (var chemin = Arrondi(pastille, diametre * 0.3f))
            {
                if (libre)
                {
                    using var trait = new Pen(Teinte(s.GroupColor, 170), Math.Max(1.2f, _hauteurEcran / 900f)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                    g.DrawPath(trait, chemin);
                }
                else
                {
                    using var fond = new SolidBrush(choisie ? Color.FromArgb(70, 255, 255, 255) : Teinte(s.TitleColor, 60));
                    g.FillPath(fond, chemin);
                }
            }

            using var police = new Font(petite.FontFamily, petite.Size, FontStyle.Bold, petite.Unit);
            using var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : libre ? Teinte(s.GroupColor, 200) : Teinte(s.TitleColor));
            using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(place.ToString(System.Globalization.CultureInfo.InvariantCulture), police, encre, pastille, milieu);
        }

        /// <summary>Une place qui attend son joueur : une carte en pointilles, son numero, une silhouette vide.</summary>
        private void DessinerUnePlaceLibre(Graphics g, JoueurMultiplayer j, EsMenuStyle s, Font gras, Font petite, float taille, RectangleF zone)
        {
            var epaisseur = Math.Max(1.2f, _hauteurEcran / 900f);
            using (var chemin = Arrondi(zone, taille * 0.3f))
            using (var trait = new Pen(Teinte(s.GroupColor, 150), epaisseur) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            {
                g.DrawPath(trait, chemin);
            }

            var gauche = DessinerLeNumero(g, j.Place ?? 0, s, petite, taille, zone, choisie: false, libre: true);
            var cote = zone.Height * 0.7f;
            var silhouette = new RectangleF(gauche, zone.Y + (zone.Height - cote) / 2f, cote, cote);
            using (var chemin = Arrondi(silhouette, cote * 0.22f))
            using (var trait = new Pen(Teinte(s.GroupColor, 120), epaisseur) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
            {
                g.DrawPath(trait, chemin);
            }

            using (var encre = new SolidBrush(Teinte(s.GroupColor, 140)))
            using (var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("?", gras, encre, silhouette, milieu);
            }

            var x = silhouette.Right + taille * 0.6f;
            var largeur = Math.Max(0, zone.Right - taille * 0.5f - x);
            using var encreNom = new SolidBrush(Teinte(s.GroupColor, 220));
            g.DrawString(Couper(g, j.Nom, gras, largeur), gras, encreNom, new PointF(x, zone.Y + zone.Height * 0.14f), StringFormat.GenericTypographic);
            using var encreEtat = new SolidBrush(Teinte(s.GroupColor, 160));
            g.DrawString(Couper(g, j.Etat, petite, largeur), petite, encreEtat, new PointF(x, zone.Y + zone.Height * 0.56f), StringFormat.GenericTypographic);
        }

        /// <summary>Un portrait carre, en gros pixels, dans un cadre arrondi.</summary>
        private void DessinerUnPortrait(Graphics g, Image portrait, RectangleF carre, EsMenuStyle s, Color cadre, float epaisseur)
        {
            var etat = g.Save();
            using (var masque = Arrondi(carre, carre.Width * 0.14f))
            {
                g.SetClip(masque);
            }

            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(portrait, carre);
            g.Restore(etat);
            using var chemin = Arrondi(carre, carre.Width * 0.14f);
            using var trait = new Pen(cadre, epaisseur);
            g.DrawPath(trait, chemin);
        }

        /// <summary>
        /// La bande des personnages (V24-04, ▲▼) : tous les portraits a la suite, le choix courant agrandi sous le cadre de
        /// selection d'ES, son nom dessous. Rend le haut de la bande : les joueurs s'arretent au-dessus.
        /// </summary>
        private float DessinerLaSelection(Graphics g, IReadOnlyList<PortraitDeChoix> selection, int courant, EsMenuStyle s, Font petite,
            float taille, float taillePetite, float marge, float bas)
        {
            var largeurUtile = Width - 2 * marge;
            var pas = Math.Min(largeurUtile / (selection.Count + 0.6f), taille * 2.4f);
            var petit = pas * 0.84f;
            var grand = pas * 1.32f;
            var hauteurBande = grand + taillePetite * 1.9f;
            var haut = bas - hauteurBande - taille * 0.3f;
            var milieuY = haut + grand / 2f;
            var depart = marge + (largeurUtile - pas * (selection.Count - 1) - grand) / 2f;
            var x = depart;
            for (var i = 0; i < selection.Count; i++)
            {
                var actuel = i == courant;
                var cote = actuel ? grand : petit;
                var carre = new RectangleF(x + (actuel ? 0 : (pas - petit) / 2f), milieuY - cote / 2f, cote, cote);
                if (Image(selection[i].Chemin) is { } portrait)
                {
                    using var attenuation = actuel ? null : Transparence(0.55f);
                    var etat = g.Save();
                    using (var masque = Arrondi(carre, cote * 0.14f))
                    {
                        g.SetClip(masque);
                    }

                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(portrait, Rectangle.Round(carre), 0, 0, portrait.Width, portrait.Height, GraphicsUnit.Pixel, attenuation);
                    g.Restore(etat);
                }
                else
                {
                    using var chemin = Arrondi(carre, cote * 0.14f);
                    using var fond = new SolidBrush(Color.FromArgb(actuel ? 140 : 70, 8, 10, 14));
                    g.FillPath(fond, chemin);
                }

                if (actuel)
                {
                    using var chemin = Arrondi(carre, cote * 0.14f);
                    using var trait = new Pen(Teinte(s.SelectorColor), Math.Max(2.5f, _hauteurEcran / 380f));
                    g.DrawPath(trait, chemin);
                    using var police = new Font(petite.FontFamily, petite.Size, FontStyle.Bold, petite.Unit);
                    using var encre = new SolidBrush(Teinte(s.TitleColor));
                    using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };
                    g.DrawString(selection[i].Nom, police, encre, new RectangleF(carre.X - pas, carre.Bottom + taillePetite * 0.35f, cote + 2 * pas, taillePetite * 1.5f), centre);
                }

                x += actuel ? grand + (pas - petit) / 2f : pas;
            }

            return haut;
        }

        private readonly Dictionary<string, Image?> _images = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Une image du disque, lue une fois (sans garder le fichier ouvert) ; null si elle manque encore.</summary>
        private Image? Image(string? chemin)
        {
            if (string.IsNullOrEmpty(chemin))
            {
                return null;
            }

            if (_images.TryGetValue(chemin, out var deja) && deja is not null)
            {
                return deja;
            }

            try
            {
                if (!File.Exists(chemin))
                {
                    return null;   // pas encore telechargee : on reessaiera au prochain dessin
                }

                var image = System.Drawing.Image.FromStream(new MemoryStream(File.ReadAllBytes(chemin)));
                _images[chemin] = image;
                return image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
            {
                return null;
            }
        }

        private static System.Drawing.Imaging.ImageAttributes Transparence(float opacite)
        {
            var attributs = new System.Drawing.Imaging.ImageAttributes();
            attributs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacite });
            return attributs;
        }

        /// <summary>Quatre barres de connexion, pleines selon la latence ; grises quand elle est inconnue.</summary>
        private float DessinerLaConnexion(Graphics g, EsMenuStyle s, int? barres, float droite, float y, float hauteur, float taille, int alpha)
        {
            var largeurBarre = taille * 0.22f;
            var ecart = taille * 0.12f;
            var total = 4 * largeurBarre + 3 * ecart;
            var x = droite - total;
            var pleine = barres switch
            {
                >= 3 => Color.FromArgb(alpha, 120, 229, 163),
                2 => Color.FromArgb(alpha, 255, 211, 122),
                1 => Color.FromArgb(alpha, 229, 83, 75),
                _ => Teinte(s.GroupColor, alpha / 2),
            };
            var vide = Teinte(s.GroupColor, alpha / 3);
            for (var i = 0; i < 4; i++)
            {
                var h = taille * (0.35f + 0.22f * i);
                using var fond = new SolidBrush(barres is { } n && i < n ? pleine : vide);
                g.FillRectangle(fond, x + i * (largeurBarre + ecart), y + (hauteur + taille * 1.1f) / 2f - h, largeurBarre, h);
            }

            return x - taille * 0.7f;
        }
    }
}

public sealed partial class LeaderboardOverlayService
{
    /// <summary>
    /// L'APERCU du panneau MULTIPLAYER, dessine hors ecran dans une image (outil de mise au point : juger le rendu
    /// sans borne devant soi). Aucune fenetre n'est montree. <paramref name="attente"/> dessine la presentation.
    /// </summary>
    internal static byte[] ApercuMultiplayer(EsMenuStyle style, ContenuMultiplayer contenu, string attente, string attenteTitre,
        string attenteDetail, ILogger<LeaderboardOverlayService> logger)
    {
        byte[] png = [];
        Exception? erreur = null;
        var fil = new Thread(() =>
        {
            try
            {
                var service = new LeaderboardOverlayService(logger);
                service._style = style;
                service._contenu = service._contenu with { Attente = attente, AttenteTitre = attenteTitre, AttenteDetail = attenteDetail };
                service._multiplayer = contenu;
                service._glyphes = new EsGlyphCache(style, logger);
                service.ChargerLesPolices(style);
                using var forme = new Panneau(service);
                forme.CreateControl();
                using var image = new Bitmap(Math.Max(1, forme.Width), Math.Max(1, forme.Height));
                forme.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                using var flux = new MemoryStream();
                image.Save(flux, System.Drawing.Imaging.ImageFormat.Png);
                png = flux.ToArray();
            }
            catch (Exception ex)
            {
                erreur = ex;
            }
        });
        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();
        fil.Join(TimeSpan.FromSeconds(20));
        if (erreur is not null)
        {
            throw new InvalidOperationException("apercu impossible", erreur);
        }

        return png;
    }
}
