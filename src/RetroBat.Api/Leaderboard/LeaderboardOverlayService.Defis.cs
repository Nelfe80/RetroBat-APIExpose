using System.Drawing;
using System.Drawing.Drawing2D;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LE PANNEAU DES DEFIS (lot P5 du CDC du panneau, maquettes 1 a 5) : la meme fenetre que le classement et MULTIPLAYER, un
/// autre contenu. Logo, trait, onglets, contenu, fiche calee en bas, actions, aide. Deux formes de contenu : une liste
/// (SPEEDRUNS, TRAINING, RECORDS) ou des bandeaux (CHALLENGES), toujours sur une colonne.
/// </summary>
public sealed partial class LeaderboardOverlayService
{
    /// <summary>
    /// Une ligne du panneau. <c>Groupe</c> : un surtitre (Niveaux, Boss), jamais choisi. <c>Marque</c> : « reussi », « tente »
    /// ou vide. Les bandeaux lisent aussi <c>Regle</c>, <c>Mesure</c>, <c>Image</c> (chemin local) et <c>Badge</c>.
    /// </summary>
    public sealed record LigneDeDefi(
        string Nom,
        string Colonne1 = "",
        string Colonne2 = "",
        bool Groupe = false,
        string Marque = "",
        string Regle = "",
        string Mesure = "",
        string? Image = null,
        string Badge = "");

    public sealed record ChampDeFiche(string Etiquette, string Valeur);

    /// <summary>Une ligne du selecteur de reglages : son nom, sa valeur, et si elle joue (inactive : grisee).</summary>
    public sealed record LigneDeReglage(string Etiquette, string Valeur, bool Active = true);

    /// <summary>
    /// (12/11) Le selecteur de reglages, en surimpression de la liste : un titre, des lignes generiques, la ligne choisie. La
    /// fiche reste visible dessous (son resume suit les changements).
    /// </summary>
    public sealed record SelecteurAffiche(string Titre, IReadOnlyList<LigneDeReglage> Lignes, int Courante);

    /// <summary>
    /// Ce que le panneau des defis montre. <c>LigneCourante</c> : l'index dans <c>Lignes</c>, surtitres compris. <c>Types</c> :
    /// la rangee de RECORDS (GLOBAL, ANY%...), vide ailleurs. <c>LigneEpinglee</c> : la ligne du joueur, qui reste en bas de la
    /// liste quand elle n'est pas a l'ecran. <c>Bandeau</c> : une ligne au-dessus de la liste (regle d'un type
    /// de record, « hors classement » de TRAINING). <c>Action</c> : le bouton de la barre du bas, null s'il n'y a rien a lancer.
    /// </summary>
    public sealed record ContenuDefis(
        IReadOnlyList<string> Onglets,
        int OngletCourant,
        bool NousAvonsLaMain,
        bool Bandeaux,
        IReadOnlyList<string> Colonnes,
        IReadOnlyList<LigneDeDefi> Lignes,
        int LigneCourante,
        IReadOnlyList<ChampDeFiche> Fiche,
        Aide? Action,
        IReadOnlyList<Aide> Aides,
        string Bandeau = "",
        string Message = "",
        IReadOnlyList<string>? Types = null,
        int TypeCourant = 0,
        string Pseudo = "",
        bool EnLigne = false,
        int? LigneEpinglee = null,
        SalleMassRun? MassRun = null,
        SelecteurAffiche? Selecteur = null);

    /// <summary>
    /// L'onglet MASS RUN (style B des maquettes, sans image du niveau, D23) : deux grands compteurs, une ligne de salle, un
    /// pied. Polices et couleurs du theme d'ES, comme les autres panneaux (retour du user du 04/11).
    /// </summary>
    public sealed record SalleMassRun(string Etat, bool Direct, string Ligne, string EtiquetteGauche, string ValeurGauche,
        string EtiquetteDroite, string ValeurDroite, string Pied);

    private ContenuDefis? _defis;

    public bool AfficheDefis
    {
        get { lock (_gate) return Affiche && _defis is not null; }
    }

    public void OuvrirDefis(EsMenuStyle style, ContenuDefis contenu)
        => OuvrirAvec(style, Lire() with { Attente = "", AttenteTitre = "", AttenteDetail = "" }, null, contenu);

    public void MontrerDefis(ContenuDefis contenu)
    {
        lock (_gate)
        {
            if (_defis is null) return;   // ferme ou rendu a un autre panneau entre-temps
            _defis = contenu;
            _dernierGeste = DateTime.UtcNow;
        }

        _forme?.Rafraichir();
    }

    private ContenuDefis? LireDefis()
    {
        lock (_gate) return _defis;
    }

    internal static byte[] ApercuDefis(EsMenuStyle style, ContenuDefis contenu, ILogger<LeaderboardOverlayService> logger)
    {
        byte[] png = [];
        Exception? erreur = null;
        var fil = new Thread(() =>
        {
            try
            {
                var service = new LeaderboardOverlayService(logger);
                service._style = style;
                service._defis = contenu;
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
        if (erreur is not null) throw new InvalidOperationException("apercu impossible", erreur);
        return png;
    }

    private sealed partial class Panneau
    {
        private void DessinerLesDefis(Graphics g, ContenuDefis d, Contenu socle, EsMenuStyle s, Font policeTitre, Font policeTexte,
            Font policePetite, Font policeGras, Font policeGroupe, Font policePied, float tailleTitre, float tailleTexte,
            float taillePetite, float tailleGroupe, float taillePied)
        {
            var marge = Math.Max(12f, _largeurEcran * 0.014f);
            var hauteurTitre = tailleTitre * 1.5f;
            var y = _hauteurEcran * 0.0637f - hauteurTitre / 2f;
            DessinerLeLogoDesDefis(g, s, policeTitre, hauteurTitre, y);
            y = _hauteurEcran * 0.0637f * 2f - hauteurTitre / 2f;
            Bordure(g, s, y);
            y = DessinerLesOnglets(g, socle with { Onglets = d.Onglets, OngletCourant = d.OngletCourant, NousAvonsLaMain = d.NousAvonsLaMain, Pastilles = null },
                s, policeTexte, tailleTexte, y);

            // Le bas du panneau, de bas en haut : aide, barre d'action, fiche. Les memes rangees que le classement et MULTIPLAYER.
            var basDeLAide = Height - taillePied * 1.5f - tailleTexte * 1.9f - _hauteurEcran * 0.012f;
            var basDuContenu = basDeLAide - tailleTexte * 0.4f;
            if (d.Fiche.Count > 0)
            {
                var hauteurFiche = (tailleTexte * 2.6f) * ((d.Fiche.Count + 1) / 2) + tailleTexte * 0.8f;
                var fiche = new RectangleF(marge, basDuContenu - hauteurFiche, Width - 2 * marge, hauteurFiche);
                DessinerLaFiche(g, d.Fiche, s, policeGroupe, policeTexte, tailleTexte, fiche);
                basDuContenu = fiche.Y - tailleTexte * 0.4f;
            }

            if (d.Types is { Count: > 0 } types)
            {
                y = DessinerLesTypes(g, types, d.TypeCourant, s, policeGroupe, tailleGroupe, marge, y + tailleTexte * 0.3f);
            }

            if (d.Bandeau.Length > 0)
            {
                using var encre = new SolidBrush(Teinte(s.TitleColor));
                using var aGauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.Character };
                var zone = new RectangleF(marge, y + tailleTexte * 0.2f, Width - 2 * marge, taillePetite * 2.2f);
                Aplat(g, Color.FromArgb(120, 8, 10, 14), zone);
                DessinerCoupe(g, d.Bandeau, policePetite, encre, new RectangleF(zone.X + taillePetite * 0.6f, zone.Y, zone.Width - taillePetite * 1.2f, zone.Height), aGauche);
                y = zone.Bottom + tailleTexte * 0.2f;
            }

            if (d.MassRun is { } salle)
            {
                DessinerLaSalleMassRun(g, salle, s, policeTitre, policeTexte, policePetite, policeGroupe, tailleTitre, tailleTexte,
                    taillePetite, marge, y, basDuContenu);
            }
            else if (d.Lignes.Count == 0)
            {
                using var encre = new SolidBrush(Teinte(s.TextColor));
                using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(d.Message.ToUpperInvariant(), policeTexte, encre, new RectangleF(marge, y, Width - 2 * marge, basDuContenu - y), centre);
            }
            else if (d.Bandeaux)
            {
                DessinerLesBandeaux(g, d, s, policeGras, policePetite, policeGroupe, tailleTexte, taillePetite, tailleGroupe, marge, y, basDuContenu);
            }
            else
            {
                DessinerLaListeDesDefis(g, d, s, policeTexte, policeGras, policeGroupe, policePetite, tailleTexte, tailleGroupe, marge, y, basDuContenu);
            }

            if (d.Selecteur is { } selecteur)
            {
                DessinerLeSelecteur(g, selecteur, s, policeGroupe, policeTexte, policeGras, tailleTexte, marge, y, basDuContenu);
            }

            DessinerLaBarre(g, socle with { Defier = d.Action?.Mot ?? "", GlypheDefier = d.Action?.Glyphe ?? "", DefierMode = "", MaPlace = "" },
                s, policeTexte, policePetite, tailleTexte, basDeLAide);
            DessinerLAide(g, socle with { Aides = d.Aides, Pseudo = d.Pseudo, EnLigne = d.EnLigne }, s, policePied, taillePied, basDeLAide + tailleTexte * 1.9f);
        }

        /// <summary>
        /// La salle MASS RUN : l'etat (pastille rouge en direct), deux cartes aux grands chiffres dans la police du titre du
        /// theme (inscrits, depart), la ligne de la salle (format, niveau, regle de mort), un pied.
        /// </summary>
        private void DessinerLaSalleMassRun(Graphics g, SalleMassRun salle, EsMenuStyle s, Font policeTitre, Font policeTexte,
            Font policePetite, Font policeGroupe, float tailleTitre, float tailleTexte, float taillePetite, float marge, float y, float bas)
        {
            using var encreTitre = new SolidBrush(Teinte(s.TitleColor));
            using var encreTexte = new SolidBrush(Teinte(s.TextColor));
            using var encreGroupe = new SolidBrush(Teinte(s.GroupColor));
            using var encreAccent = new SolidBrush(Teinte(s.SelectorColor));
            using var aGauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // l'etat, avec la pastille rouge quand une course est ouverte ou en cours
            var haut = y + tailleTexte * 0.5f;
            var xEtat = marge;
            if (salle.Direct)
            {
                using var rouge = new SolidBrush(Color.FromArgb(255, 255, 59, 59));
                var d = taillePetite * 0.8f;
                g.FillEllipse(rouge, xEtat, haut + (tailleTexte * 1.4f - d) / 2f, d, d);
                xEtat += d * 1.6f;
            }
            g.DrawString(salle.Etat.ToUpperInvariant(), policeGroupe, encreGroupe, new RectangleF(xEtat, haut, Width - xEtat - marge, tailleTexte * 1.4f), aGauche);
            haut += tailleTexte * 1.9f;

            // deux cartes : etiquette en haut, grand chiffre dessous
            var ecart = marge * 0.8f;
            var largeur = (Width - 2 * marge - ecart) / 2f;
            var hauteur = Math.Min((bas - haut) * 0.62f, largeur * 0.62f);
            var taille = hauteur * 0.62f;
            using var grand = new Font(policeTitre.FontFamily, Math.Max(8f, taille), policeTitre.Style, GraphicsUnit.Pixel);
            for (var i = 0; i < 2; i++)
            {
                var carte = new RectangleF(marge + i * (largeur + ecart), haut, largeur, hauteur);
                Aplat(g, Color.FromArgb(120, 8, 10, 14), carte);
                var etiquette = i == 0 ? salle.EtiquetteGauche : salle.EtiquetteDroite;
                var valeur = i == 0 ? salle.ValeurGauche : salle.ValeurDroite;
                g.DrawString(etiquette.ToUpperInvariant(), policePetite, encreTexte,
                    new RectangleF(carte.X + taillePetite, carte.Y + taillePetite * 0.4f, carte.Width - 2 * taillePetite, taillePetite * 2f), aGauche);
                g.DrawString(valeur, grand, i == 0 ? encreTitre : encreAccent,
                    new RectangleF(carte.X, carte.Y + taillePetite * 2f, carte.Width, carte.Height - taillePetite * 2.2f), centre);
            }
            haut += hauteur + tailleTexte * 0.8f;

            // la ligne de la salle, puis le pied
            Bordure(g, s, haut);
            g.DrawString(salle.Ligne, policeTexte, encreTitre, new RectangleF(marge, haut + tailleTexte * 0.3f, Width - 2 * marge, tailleTexte * 1.8f), aGauche);
            haut += tailleTexte * 2.3f;
            if (salle.Pied.Length > 0 && haut + tailleTexte * 1.5f < bas)
            {
                g.DrawString(salle.Pied, policePetite, encreTexte, new RectangleF(marge, haut, Width - 2 * marge, taillePetite * 2f), aGauche);
            }
        }

        private void DessinerLeLogoDesDefis(Graphics g, EsMenuStyle s, Font policeTitre, float hauteurTitre, float y)
        {
            var chemin = Path.Combine(RetroBatPaths.ThemeResourcesRoot, "images", "nelfeplay-challenges.png");
            var logo = File.Exists(chemin) ? _service._glyphes?.Glyphe(chemin, (int)Math.Round(hauteurTitre * 1.75f)) : null;
            if (logo is not null && logo.Width > Width * 0.75f)
            {
                logo = _service._glyphes?.Glyphe(chemin, (int)Math.Round(hauteurTitre * 1.75f * Width * 0.75f / logo.Width));
            }

            if (logo is not null)
            {
                g.DrawImage(logo, (Width - logo.Width) / 2f, y + (hauteurTitre - logo.Height) / 2f - hauteurTitre * 0.22f, logo.Width, logo.Height);
                return;
            }

            using var encre = new SolidBrush(Teinte(s.TitleColor));
            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("CHALLENGES & SPEEDRUNS", policeTitre, encre, new RectangleF(0, y, Width, hauteurTitre), centre);
        }

        /// <summary>La rangee des types de RECORDS, en pastilles : le type choisi a la couleur de selection, X passe au suivant.</summary>
        private float DessinerLesTypes(Graphics g, IReadOnlyList<string> types, int courant, EsMenuStyle s, Font police, float taille, float marge, float y)
        {
            var hauteur = taille * 1.7f;
            var x = marge;
            using var milieu = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (var i = 0; i < types.Count; i++)
            {
                var largeur = g.MeasureString(types[i], police, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 1.4f;
                var zone = new RectangleF(x, y, largeur, hauteur);
                using (var forme = FormesDesEcrans.Arrondi(zone, Math.Max(3f, taille * 0.3f)))
                using (var fond = new SolidBrush(i == courant ? Teinte(s.SelectorColor) : Color.FromArgb(60, Teinte(s.TextColor))))
                {
                    g.FillPath(fond, forme);
                }

                using var encre = new SolidBrush(i == courant ? Teinte(s.SelectedTextColor) : Teinte(s.TextColor));
                g.DrawString(types[i], police, encre, zone, milieu);
                x += largeur + taille * 0.5f;
            }

            return y + hauteur;
        }

        /// <summary>
        /// La liste : surtitres de groupe, coche ou rond devant les epreuves deja tentees, deux colonnes a droite. Elle defile
        /// pour garder la ligne choisie a l'ecran, et la barre de droite dit ou l'on en est, comme dans le classement.
        /// </summary>
        private void DessinerLaListeDesDefis(Graphics g, ContenuDefis d, EsMenuStyle s, Font police, Font gras, Font groupe, Font petite,
            float taille, float tailleGroupe, float marge, float y, float bas)
        {
            var hauteur = taille * 1.5f;
            var hauteurGroupe = tailleGroupe * 2.0f;
            var largeurColonne = Width * 0.17f;
            var couleurTexte = Teinte(s.TextColor);
            var couleurChoisi = Teinte(s.SelectedTextColor);
            using var aGauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.Character, FormatFlags = StringFormatFlags.NoWrap };
            using var aDroite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

            if (d.Colonnes.Count > 0)
            {
                using var etiquette = new SolidBrush(Teinte(s.GroupColor));
                for (var i = 0; i < d.Colonnes.Count && i < 2; i++)
                {
                    var droite = Width - marge - (1 - i) * largeurColonne;
                    g.DrawString(d.Colonnes[i], petite, etiquette, new RectangleF(droite - largeurColonne, y, largeurColonne, taille), aDroite);
                }

                y += taille;
            }

            // La ligne du joueur garde sa place en bas tant que la liste ne la montre pas.
            var epinglee = d.LigneEpinglee is { } e && e >= 0 && e < d.Lignes.Count ? e : -1;
            var basDeLaListe = epinglee >= 0 ? bas - hauteur * 1.15f : bas;

            float HauteurDe(LigneDeDefi l) => l.Groupe ? hauteurGroupe : hauteur;
            var premiere = 0;
            float Etendue(int de, int a) { var h = 0f; for (var i = de; i <= a; i++) h += HauteurDe(d.Lignes[i]); return h; }
            while (premiere < d.LigneCourante && Etendue(premiere, Math.Min(d.LigneCourante + 1, d.Lignes.Count - 1)) > basDeLaListe - y) premiere++;

            var yDepart = y;
            var derniere = premiere - 1;
            for (var i = premiere; i < d.Lignes.Count && y + HauteurDe(d.Lignes[i]) <= basDeLaListe; i++)
            {
                var l = d.Lignes[i];
                derniere = i;
                if (l.Groupe)
                {
                    using var encreGroupe = new SolidBrush(Teinte(s.GroupColor));
                    g.DrawString(l.Nom.ToUpperInvariant(), groupe, encreGroupe, new RectangleF(marge, y, Width - 2 * marge, hauteurGroupe),
                        new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Far });
                    y += hauteurGroupe;
                    continue;
                }

                var choisie = i == d.LigneCourante && d.NousAvonsLaMain;
                if (choisie)
                {
                    using var degrade = new LinearGradientBrush(new RectangleF(0, y, Width, hauteur), Teinte(s.SelectorColor), Teinte(s.SelectorColorEnd), 0f);
                    g.FillRectangle(degrade, 0, y, Width, hauteur);
                }

                var xNom = marge + taille * 1.3f;
                DessinerLaMarque(g, l.Marque, s, new RectangleF(marge, y, taille * 1.1f, hauteur), taille);
                using var encre = new SolidBrush(choisie ? couleurChoisi : couleurTexte);
                var police2 = choisie ? gras : police;
                DessinerCoupe(g, l.Nom.ToUpperInvariant(), police2, encre, new RectangleF(xNom, y, Width - marge - 2 * largeurColonne - xNom, hauteur), aGauche);
                g.DrawString(l.Colonne1, police2, encre, new RectangleF(Width - marge - 2 * largeurColonne, y, largeurColonne, hauteur), aDroite);
                g.DrawString(l.Colonne2, police2, encre, new RectangleF(Width - marge - largeurColonne, y, largeurColonne, hauteur), aDroite);
                y += hauteur;
            }

            DessinerLaBarreDeDefilement(g, s, d.Lignes.Count, premiere, derniere, d.LigneCourante, yDepart, basDeLaListe, taille);

            if (epinglee >= 0 && (epinglee < premiere || epinglee > derniere))
            {
                var l = d.Lignes[epinglee];
                var zone = new RectangleF(0, bas - hauteur, Width, hauteur);
                Aplat(g, Color.FromArgb(150, 8, 10, 14), zone);
                using var encre = new SolidBrush(Teinte(s.TitleColor));
                var xNom = marge + taille * 1.3f;
                DessinerCoupe(g, l.Nom.ToUpperInvariant(), gras, encre, new RectangleF(xNom, zone.Y, Width - marge - 2 * largeurColonne - xNom, hauteur), aGauche);
                g.DrawString(l.Colonne1, gras, encre, new RectangleF(Width - marge - 2 * largeurColonne, zone.Y, largeurColonne, hauteur), aDroite);
                g.DrawString(l.Colonne2, gras, encre, new RectangleF(Width - marge - largeurColonne, zone.Y, largeurColonne, hauteur), aDroite);
            }
        }

        /// <summary>
        /// Les bandeaux de CHALLENGES (decision du user du 31/10 : une colonne, ◄ ► gardent leur sens) : image a gauche, nom,
        /// regle, puis mesure, record et temps du joueur ; le badge RÉUSSI ou TENTÉ en haut a droite.
        /// </summary>
        private void DessinerLesBandeaux(Graphics g, ContenuDefis d, EsMenuStyle s, Font gras, Font petite, Font groupe,
            float taille, float taillePetite, float tailleGroupe, float marge, float y, float bas)
        {
            var hauteur = taille * 3.4f;
            var ecart = taille * 0.3f;
            var hauteurGroupe = tailleGroupe * 2.0f;
            using var aGauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.Character, FormatFlags = StringFormatFlags.NoWrap };
            float HauteurDe(LigneDeDefi l) => l.Groupe ? hauteurGroupe : hauteur + ecart;
            var premiere = 0;
            float Etendue(int de, int a) { var h = 0f; for (var i = de; i <= a; i++) h += HauteurDe(d.Lignes[i]); return h; }
            while (premiere < d.LigneCourante && Etendue(premiere, d.LigneCourante) > bas - y) premiere++;

            var yDepart = y;
            var derniere = premiere - 1;
            for (var i = premiere; i < d.Lignes.Count && y + HauteurDe(d.Lignes[i]) <= bas + ecart; i++)
            {
                var l = d.Lignes[i];
                derniere = i;
                if (l.Groupe)
                {
                    using var encreGroupe = new SolidBrush(Teinte(s.GroupColor));
                    g.DrawString(l.Nom.ToUpperInvariant(), groupe, encreGroupe, new RectangleF(marge, y, Width - 2 * marge, hauteurGroupe),
                        new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Far });
                    y += hauteurGroupe;
                    continue;
                }

                var choisie = i == d.LigneCourante && d.NousAvonsLaMain;
                var zone = new RectangleF(marge, y, Width - 2 * marge, hauteur);
                using (var forme = FormesDesEcrans.Arrondi(zone, taille * 0.3f))
                {
                    if (choisie)
                    {
                        using var degrade = new LinearGradientBrush(zone, Teinte(s.SelectorColor), Teinte(s.SelectorColorEnd), 0f);
                        g.FillPath(degrade, forme);
                    }
                    else
                    {
                        using var fond = new SolidBrush(Color.FromArgb(150, 8, 10, 14));
                        g.FillPath(fond, forme);
                    }
                }

                var image = new RectangleF(zone.X, zone.Y, hauteur * 16f / 9f, hauteur);
                if (l.Image is { } chemin && File.Exists(chemin) && _service._glyphes?.Vignette(chemin, (int)hauteur) is { } vignette)
                {
                    g.DrawImage(vignette, image);
                }
                else
                {
                    Aplat(g, Color.FromArgb(70, Teinte(s.TextColor)), image);
                }

                var x = image.Right + taille * 0.6f;
                var largeur = zone.Right - x - taille * 0.6f;
                using var encreNom = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TitleColor));
                using var encreTexte = new SolidBrush(choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TextColor));
                var largeurBadge = l.Badge.Length > 0 ? g.MeasureString(l.Badge, petite, PointF.Empty, StringFormat.GenericTypographic).Width + taillePetite : 0f;
                DessinerCoupe(g, l.Nom.ToUpperInvariant(), gras, encreNom, new RectangleF(x, zone.Y + hauteur * 0.06f, largeur - largeurBadge, hauteur * 0.36f), aGauche);
                DessinerCoupe(g, l.Regle, petite, encreTexte, new RectangleF(x, zone.Y + hauteur * 0.40f, largeur, hauteur * 0.26f), aGauche);
                var bas2 = string.Join("    ", new[] { l.Mesure, l.Colonne1, l.Colonne2 }.Where(t => t.Length > 0));
                DessinerCoupe(g, bas2, petite, encreTexte, new RectangleF(x, zone.Y + hauteur * 0.66f, largeur, hauteur * 0.28f), aGauche);
                if (l.Badge.Length > 0)
                {
                    var etiquette = new RectangleF(zone.Right - largeurBadge - taille * 0.4f, zone.Y + hauteur * 0.1f, largeurBadge, taillePetite * 1.4f);
                    using (var forme = FormesDesEcrans.Arrondi(etiquette, taillePetite * 0.25f))
                    using (var fond = new SolidBrush(l.Marque == "reussi" ? Color.FromArgb(245, 185, 64) : Color.FromArgb(70, 74, 82)))
                    {
                        g.FillPath(fond, forme);
                    }

                    using var encreBadge = new SolidBrush(l.Marque == "reussi" ? Color.FromArgb(36, 36, 36) : Color.White);
                    g.DrawString(l.Badge, petite, encreBadge, etiquette, new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }

                y += hauteur + ecart;
            }

            DessinerLaBarreDeDefilement(g, s, d.Lignes.Count, premiere, derniere, d.LigneCourante, yDepart, bas, taille);
        }

        /// <summary>Coche doree si reussi, rond s'il a ete tente, rien sinon.</summary>
        private static void DessinerLaMarque(Graphics g, string marque, EsMenuStyle s, RectangleF zone, float taille)
        {
            if (marque.Length == 0) return;
            var c = taille * 0.42f;
            var cx = zone.X + zone.Width / 2f;
            var cy = zone.Y + zone.Height / 2f;
            if (marque == "reussi")
            {
                using var plume = new Pen(Color.FromArgb(245, 185, 64), Math.Max(2f, taille * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(plume, [new PointF(cx - c, cy), new PointF(cx - c * 0.25f, cy + c * 0.7f), new PointF(cx + c, cy - c * 0.8f)]);
            }
            else
            {
                using var plume = new Pen(Teinte(s.TextColor), Math.Max(1.5f, taille * 0.1f));
                g.DrawEllipse(plume, cx - c * 0.75f, cy - c * 0.75f, c * 1.5f, c * 1.5f);
            }
        }

        private void DessinerLaBarreDeDefilement(Graphics g, EsMenuStyle s, int total, int premiere, int derniere, int courante, float haut, float bas, float taille)
        {
            if (total <= 0 || (premiere == 0 && derniere >= total - 1)) return;
            var piste = new RectangleF(Width - 3f, haut, 3f, Math.Max(1, bas - haut));
            Aplat(g, Color.FromArgb(40, Teinte(s.TextColor)), piste);
            var part = Math.Min(1f, (float) Math.Max(1, derniere - premiere + 1) / total);
            var hauteurCurseur = Math.Max(taille, piste.Height * part);
            var progres = total <= 1 ? 0f : (float) courante / (total - 1);
            Aplat(g, Teinte(s.SelectorColor), new RectangleF(piste.X, piste.Y + (piste.Height - hauteurCurseur) * progres, piste.Width, hauteurCurseur));
        }

        /// <summary>
        /// (12/11) Le selecteur de reglages : la liste assombrie, une carte au milieu, une ligne par reglage (nom a gauche,
        /// valeur entre deux fleches a droite), la ligne choisie dans le degrade du curseur d'ES, une ligne inactive grisee sans
        /// fleches. Les fleches sont dessinees : la police du theme n'a pas toujours les triangles.
        /// </summary>
        private void DessinerLeSelecteur(Graphics g, SelecteurAffiche sel, EsMenuStyle s, Font titre, Font police, Font gras,
            float taille, float marge, float haut, float bas)
        {
            if (bas <= haut) return;
            Aplat(g, Color.FromArgb(170, 8, 10, 14), new RectangleF(0, haut, Width, bas - haut));
            var hauteurLigne = taille * 2.0f;
            var enTete = taille * 2.4f;
            var hauteur = Math.Min(bas - haut, enTete + sel.Lignes.Count * hauteurLigne + taille * 0.8f);
            var zone = new RectangleF(marge * 2f, haut + Math.Max(0f, (bas - haut - hauteur) / 2f), Width - marge * 4f, hauteur);
            using (var forme = FormesDesEcrans.Arrondi(zone, taille * 0.4f))
            using (var fond = new SolidBrush(Color.FromArgb(240, 16, 18, 24)))
            using (var bord = new Pen(Teinte(s.SelectorColor), Math.Max(1.5f, taille * 0.08f)))
            {
                g.FillPath(fond, forme);
                g.DrawPath(bord, forme);
            }

            using var aGauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.Character, FormatFlags = StringFormatFlags.NoWrap };
            using var aDroite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            using (var encreTitre = new SolidBrush(Teinte(s.GroupColor)))
            {
                g.DrawString(sel.Titre.ToUpperInvariant(), titre, encreTitre, new RectangleF(zone.X + taille * 0.8f, zone.Y, zone.Width - taille * 1.6f, enTete), aGauche);
            }

            var interieur = zone.Width - taille * 1.6f;
            for (var i = 0; i < sel.Lignes.Count; i++)
            {
                var l = sel.Lignes[i];
                var y = zone.Y + enTete + i * hauteurLigne;
                if (y + hauteurLigne > zone.Bottom + 0.5f) break;
                var choisie = i == sel.Courante && l.Active;
                var rangee = new RectangleF(zone.X + taille * 0.3f, y, zone.Width - taille * 0.6f, hauteurLigne);
                if (choisie)
                {
                    using var degrade = new LinearGradientBrush(rangee, Teinte(s.SelectorColor), Teinte(s.SelectorColorEnd), 0f);
                    g.FillRectangle(degrade, rangee);
                }

                var couleur = !l.Active ? Teinte(s.TextColor, 90) : choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TextColor);
                using var encre = new SolidBrush(couleur);
                var p = choisie ? gras : police;
                var x = zone.X + taille * 0.8f;
                DessinerCoupe(g, l.Etiquette.ToUpperInvariant(), p, encre, new RectangleF(x, y, interieur * 0.34f, hauteurLigne), aGauche);

                // la valeur, entre deux fleches quand elle se change
                var fleche = taille * 0.55f;
                var droite = zone.Right - taille * 0.8f;
                var largeurValeur = Math.Min(interieur * 0.6f, g.MeasureString(l.Valeur, p, PointF.Empty, StringFormat.GenericTypographic).Width);
                var cy = y + hauteurLigne / 2f;
                if (l.Active)
                {
                    using var pointe = new SolidBrush(couleur);
                    g.FillPolygon(pointe, [new PointF(droite - fleche, cy - fleche * 0.6f), new PointF(droite, cy), new PointF(droite - fleche, cy + fleche * 0.6f)]);
                    var gaucheFleche = droite - fleche - taille * 0.5f - largeurValeur - taille * 0.5f;
                    g.FillPolygon(pointe, [new PointF(gaucheFleche, cy - fleche * 0.6f), new PointF(gaucheFleche - fleche, cy), new PointF(gaucheFleche, cy + fleche * 0.6f)]);
                }

                var finValeur = droite - fleche - taille * 0.5f;
                DessinerCoupe(g, l.Valeur, p, encre, new RectangleF(finValeur - interieur * 0.6f, y, interieur * 0.6f, hauteurLigne), aDroite);
            }
        }

        /// <summary>La fiche calee en bas (maquette 1) : deux colonnes d'etiquettes et de valeurs.</summary>
        private void DessinerLaFiche(Graphics g, IReadOnlyList<ChampDeFiche> champs, EsMenuStyle s, Font etiquette, Font valeur, float taille, RectangleF zone)
        {
            using (var forme = FormesDesEcrans.Arrondi(zone, taille * 0.3f))
            using (var fond = new SolidBrush(Color.FromArgb(150, 8, 10, 14)))
            {
                g.FillPath(fond, forme);
            }

            using var encreEtiquette = new SolidBrush(Teinte(s.GroupColor));
            using var encreValeur = new SolidBrush(Teinte(s.TitleColor));
            using var aGauche = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.Character, FormatFlags = StringFormatFlags.NoWrap };
            var colonne = (zone.Width - taille * 1.2f) / 2f;
            for (var i = 0; i < champs.Count; i++)
            {
                var x = zone.X + taille * 0.6f + (i % 2) * colonne;
                var y = zone.Y + taille * 0.4f + (i / 2) * taille * 2.6f;
                DessinerCoupe(g, champs[i].Etiquette.ToUpperInvariant(), etiquette, encreEtiquette, new RectangleF(x, y, colonne - taille * 0.4f, taille), aGauche);
                DessinerCoupe(g, champs[i].Valeur, valeur, encreValeur, new RectangleF(x, y + taille * 1.05f, colonne - taille * 0.4f, taille * 1.4f), aGauche);
            }
        }
    }

    /// <summary>
    /// Un texte coupe a la largeur de sa case, termine par trois points (Couper) : la police du theme n'a pas « … », que GDI+
    /// mettait en fin de ligne coupee (03/11, le resume du defi facile de Metal Slug 3 finissait en trois carres).
    /// </summary>
    private static void DessinerCoupe(Graphics g, string texte, Font police, Brush encre, RectangleF zone, StringFormat format)
        => g.DrawString(Couper(g, texte, police, zone.Width), police, encre, zone, format);
}
