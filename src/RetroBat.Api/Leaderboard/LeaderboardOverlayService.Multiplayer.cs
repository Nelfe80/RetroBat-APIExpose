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
        string MotHote = "HOST");

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
        bool EnLigne = false);

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
            Bordure(g, s, y);

            // Les onglets, dessines par le meme code que le classement.
            var onglets = socle with { Onglets = c.Onglets, OngletCourant = c.OngletCourant, NousAvonsLaMain = c.NousAvonsLaMain, Pastilles = null };
            y = DessinerLesOnglets(g, onglets, s, policeTexte, tailleTexte, y);

            var marge = _largeurEcran * 0.012f;
            y += tailleTexte * 0.6f;
            y = DessinerLInfoMultiplayer(g, c, s, policeTexte, policePetite, tailleTexte, marge, y);

            if (c.Bandeau.Length > 0)
            {
                using var encre = new SolidBrush(Teinte(s.GroupColor));
                using var gauche = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.Word };
                var zone = new RectangleF(marge, y, Width - 2 * marge, taillePetite * 2.8f);
                g.DrawString(c.Bandeau, policePetite, encre, zone, gauche);
                y += taillePetite * 3f;
            }

            var basDeLAide = Height - taillePied * 1.5f - tailleTexte * 1.9f - _hauteurEcran * 0.012f;
            if (c.Lignes.Count == 0)
            {
                using var encre = new SolidBrush(Teinte(s.TextColor));
                using var gauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
                g.DrawString(c.Message.ToUpperInvariant(), policeTexte, encre, new RectangleF(marge, y, Width - 2 * marge, tailleTexte * 1.5f), gauche);
            }
            else
            {
                DessinerLesJoueurs(g, c, s, policeGras, policePetite, tailleTexte, taillePetite, marge, y, basDeLAide);
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

                // L'avatar : un carre arrondi a la couleur du joueur, son initiale dedans.
                var cote = hauteur * 0.7f;
                var avatar = new RectangleF(zone.X + taille * 0.45f, y + (hauteur - cote) / 2f, cote, cote);
                using (var chemin = Arrondi(avatar, cote * 0.22f))
                using (var fond = new SolidBrush(Teinte(j.Couleur, j.Present ? 255 : 110)))
                {
                    g.FillPath(fond, chemin);
                }

                using (var encre = new SolidBrush(Color.FromArgb(j.Present ? 255 : 150, 12, 12, 12)))
                using (var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    var initiale = j.Nom.Length > 0 ? j.Nom[..1].ToUpperInvariant() : "?";
                    g.DrawString(initiale, gras, encre, avatar, milieu);
                }

                // A droite, de l'exterieur vers l'interieur : pret (ou absent), connexion, hote.
                var droite = zone.Right - taille * 0.5f;
                var alpha = j.Present ? 255 : 130;
                using (var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : j.Present ? Color.FromArgb(120, 229, 163) : Teinte(s.GroupColor, 200)))
                using (var aDroite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                {
                    var largeur = g.MeasureString(j.Etat, petite, PointF.Empty, StringFormat.GenericTypographic).Width + 4f;
                    g.DrawString(j.Etat, petite, encre, new RectangleF(droite - largeur, y, largeur, hauteur), aDroite);
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

                // Le nom, puis le pays dessous. Pas de ville (V24-18).
                var x = avatar.Right + taille * 0.6f;
                var largeurNom = Math.Max(0, droite - x);
                using (var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : j.Moi ? Teinte(s.TitleColor, alpha) : Teinte(s.TextColor, alpha)))
                {
                    g.DrawString(Couper(g, j.Nom, gras, largeurNom), gras, encre, new PointF(x, y + hauteur * 0.14f), StringFormat.GenericTypographic);
                }

                if (j.Pays.Length > 0)
                {
                    // Le drapeau sous le nom (V24-18) ; un pays qu'on ne sait pas dessiner garde son code.
                    var hauteurDrapeau = hauteur * 0.24f;
                    var drapeau = new RectangleF(x, y + hauteur * 0.6f, hauteurDrapeau * 1.5f, hauteurDrapeau);
                    if (!Drapeaux.Dessiner(g, j.Pays, drapeau, alpha))
                    {
                        using var encre = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.GroupColor, alpha));
                        g.DrawString(j.Pays, petite, encre, new PointF(x, y + hauteur * 0.56f), StringFormat.GenericTypographic);
                    }
                }

                y += hauteur + ecart;
            }
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
