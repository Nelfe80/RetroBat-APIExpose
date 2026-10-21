using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Les petites cartes MULTIPLAYER en haut a droite de l'ecran d'ES (retour du user du 22/10) : une par
/// jeu de « Votre selection », et celle d'une invitation par-dessus. Meme facture que le cartouche du
/// defi : le gris du menu d'ES, un lisere, les polices du theme, des coins arrondis par une region.
///
/// La fenetre ne prend JAMAIS le focus (WS_EX_NOACTIVATE) : la navigation d'ES n'est pas interrompue, et
/// le panneau MULTIPLAYER ne se croit pas abandonne. Elle ne se redessine qu'a un changement, et chaque
/// seconde seulement pendant un decompte.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiplayerToastOverlay : IDisposable
{
    /// <summary>Une carte : un surtitre en petit, le titre, un detail, et le decompte quand il court.</summary>
    public sealed record Carte(string Surtitre, string Titre, string Detail, DateTime? FinDuDecompteUtc = null, bool Alerte = false);

    private readonly ILogger<MultiplayerToastOverlay>? _journal;
    private readonly object _verrou = new();
    private IReadOnlyList<Carte> _cartes = [];
    private EsMenuStyle _style = new();
    private Thread? _fil;
    private Fenetre? _forme;

    public MultiplayerToastOverlay(ILogger<MultiplayerToastOverlay>? journal = null)
    {
        _journal = journal;
    }

    /// <summary>Montre ces cartes (vide : tout s'efface).</summary>
    public void Montrer(IReadOnlyList<Carte> cartes)
    {
        if (cartes.Count == 0)
        {
            Cacher();
            return;
        }

        Rectangle ecran;
        try
        {
            var es = EmulatorForeground.FenetreDEmulationStation();
            ecran = es != IntPtr.Zero ? Screen.FromHandle(es).Bounds : (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080));
        }
        catch (Exception)
        {
            ecran = new Rectangle(0, 0, 1920, 1080);
        }

        lock (_verrou)
        {
            _cartes = cartes;
            if (_fil is null)
            {
                _style = EsMenuStyle.Lire(_journal);
                _fil = new Thread(() => OverlayUiGuard.Run("MultiplayerToastOverlay", () =>
                {
                    try
                    {
                        _forme = new Fenetre(this, ecran);
                        Application.Run(_forme);
                    }
                    catch (Exception ex)
                    {
                        _journal?.LogWarning(ex, "MULTIPLAYER : les cartes du haut d'ES se sont arretees.");
                    }
                }))
                { IsBackground = true, Name = "multiplayer-toast" };
                _fil.SetApartmentState(ApartmentState.STA);
                _fil.Start();
                return;
            }
        }

        _forme?.Placer(ecran);
        _forme?.Paraitre();
    }

    public void Cacher()
    {
        lock (_verrou)
        {
            _cartes = [];
        }

        _forme?.Disparaitre();
    }

    /// <summary>L'APERCU des cartes, dessine hors ecran (outil de mise au point) ; aucune fenetre n'est montree.</summary>
    internal static byte[] Apercu(EsMenuStyle style, IReadOnlyList<Carte> cartes, Rectangle ecran)
    {
        byte[] png = [];
        Exception? erreur = null;
        var fil = new Thread(() =>
        {
            try
            {
                var service = new MultiplayerToastOverlay();
                service._style = style;
                service._cartes = cartes;
                using var forme = new Fenetre(service, ecran);
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

    private IReadOnlyList<Carte> Lire()
    {
        lock (_verrou) return _cartes;
    }

    private EsMenuStyle Style()
    {
        lock (_verrou) return _style;
    }

    public void Dispose()
    {
        try
        {
            _forme?.BeginInvoke(() => _forme.Close());
        }
        catch (Exception)
        {
        }
    }

    private sealed class Fenetre : Form
    {
        private readonly MultiplayerToastOverlay _service;
        private readonly PrivateFontCollection _polices = new();
        private readonly Dictionary<string, FontFamily> _familles = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Forms.Timer _minuteur;
        private Rectangle _ecran;
        private int _nombre;

        public Fenetre(MultiplayerToastOverlay service, Rectangle ecran)
        {
            _service = service;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Couleur(service.Style().BackgroundColor);
            ChargerLesPolices(service.Style());
            Placer(ecran);

            // Chaque seconde : reaffirmer la place au-dessus d'ES, et redessiner seulement si un decompte court.
            _minuteur = new System.Windows.Forms.Timer { Interval = 1000 };
            _minuteur.Tick += (_, _) =>
            {
                if (!Visible)
                {
                    return;
                }

                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
                if (_service.Lire().Any(c => c.FinDuDecompteUtc is not null))
                {
                    Invalidate();
                }
            };
            _minuteur.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WsExTopmost = 0x00000008;
                var p = base.CreateParams;
                p.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
                return p;
            }
        }

        private float HauteurCarte => _ecran.Height * 0.062f;

        private float Ecart => _ecran.Height * 0.008f;

        /// <summary>En haut a droite de l'ecran d'ES, une carte sous l'autre ; la fenetre a la taille de la pile.</summary>
        public void Placer(Rectangle ecran)
        {
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke(() => Placer(ecran));
                return;
            }

            _ecran = ecran;
            var cartes = _service.Lire();
            _nombre = Math.Max(1, cartes.Count);
            var largeur = (int)(ecran.Width * 0.18);
            var hauteur = (int)Math.Ceiling(_nombre * HauteurCarte + (_nombre - 1) * Ecart);
            var marge = (int)(ecran.Height * 0.018);
            Bounds = new Rectangle(ecran.Right - largeur - marge, ecran.Top + marge, largeur, hauteur);
            var region = new Region(new Rectangle(0, 0, 0, 0));
            for (var i = 0; i < _nombre; i++)
            {
                using var chemin = Arrondi(new RectangleF(0, i * (HauteurCarte + Ecart), largeur, HauteurCarte), HauteurCarte * 0.16f);
                region.Union(chemin);
            }

            Region = region;
            Invalidate();
        }

        public void Paraitre()
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    Placer(_ecran);
                    if (!Visible)
                    {
                        Show();
                    }

                    Invalidate();
                });
            }
        }

        public void Disparaitre()
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    if (Visible)
                    {
                        Hide();
                    }
                });
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_service.Lire().Count == 0)
            {
                Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var s = _service.Style();
            var cartes = _service.Lire();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var fond = new SolidBrush(Color.FromArgb(255, 12, 14, 18)))
            {
                g.FillRectangle(fond, 0, 0, Width, Height);
            }

            var petite = Math.Max(9f, _ecran.Height * 0.0115f);
            var moyenne = Math.Max(11f, _ecran.Height * 0.016f);
            using var policePetite = Police(s.TextFontPath, petite, FontStyle.Regular);
            using var policeTitre = Police(s.TitleFontPath, moyenne, FontStyle.Bold);
            using var policeChiffre = Police(s.GroupFontPath.Length > 0 ? s.GroupFontPath : s.TitleFontPath, moyenne * 1.05f, FontStyle.Bold);
            using var gauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var blanc = new SolidBrush(Couleur(s.TitleColor));
            using var gris = new SolidBrush(Couleur(s.TextColor));
            using var pale = new SolidBrush(Couleur(s.GroupColor));
            var rouge = Color.FromArgb(229, 83, 75);

            for (var i = 0; i < cartes.Count && i < _nombre; i++)
            {
                var carte = cartes[i];
                var zone = new RectangleF(0, i * (HauteurCarte + Ecart), Width, HauteurCarte);
                using (var chemin = Arrondi(zone, HauteurCarte * 0.16f))
                using (var fond = new SolidBrush(Couleur(s.BackgroundColor)))
                using (var lisere = new Pen(carte.Alerte ? Couleur(s.SelectorColor) : Color.FromArgb(255, 60, 60, 60), carte.Alerte ? 2f : 1f))
                {
                    g.FillPath(fond, chemin);
                    g.DrawPath(lisere, new GraphicsPath(chemin.PathPoints, chemin.PathTypes));
                }

                var marge = HauteurCarte * 0.16f;
                var droite = zone.Right - marge;

                // A droite, le decompte : un anneau et ses secondes, rouge aux 5 dernieres (V24-07).
                if (carte.FinDuDecompteUtc is { } fin)
                {
                    var secondes = Math.Max(0, (int)Math.Ceiling((fin - DateTime.UtcNow).TotalSeconds));
                    var diametre = HauteurCarte * 0.62f;
                    var cercle = new RectangleF(droite - diametre, zone.Y + (HauteurCarte - diametre) / 2f, diametre, diametre);
                    var couleur = secondes <= 5 ? rouge : Couleur(s.SelectorColor);
                    using var trait = new Pen(couleur, Math.Max(2f, HauteurCarte * 0.06f));
                    g.DrawEllipse(trait, cercle);
                    using var encre = new SolidBrush(secondes <= 5 ? rouge : Couleur(s.TitleColor));
                    g.DrawString(secondes.ToString(CultureInfo.InvariantCulture), policeChiffre, encre, cercle, centre);
                    droite = cercle.Left - marge;
                }

                var largeur = Math.Max(10f, droite - marge - zone.X);
                g.DrawString(carte.Surtitre.ToUpperInvariant(), policePetite, pale,
                    new RectangleF(zone.X + marge, zone.Y + HauteurCarte * 0.08f, largeur, HauteurCarte * 0.32f), gauche);
                g.DrawString(carte.Titre, policeTitre, blanc,
                    new RectangleF(zone.X + marge, zone.Y + HauteurCarte * 0.34f, largeur, HauteurCarte * 0.36f), gauche);
                if (carte.Detail.Length > 0)
                {
                    g.DrawString(carte.Detail, policePetite, gris,
                        new RectangleF(zone.X + marge, zone.Y + HauteurCarte * 0.66f, largeur, HauteurCarte * 0.3f), gauche);
                }
            }
        }

        private void ChargerLesPolices(EsMenuStyle style)
        {
            foreach (var chemin in new[] { style.TitleFontPath, style.TextFontPath, style.GroupFontPath })
            {
                if (chemin.Length == 0 || _familles.ContainsKey(chemin))
                {
                    continue;
                }

                try
                {
                    var avant = _polices.Families.Length;
                    _polices.AddFontFile(chemin);
                    var famille = _polices.Families.Skip(avant).FirstOrDefault() ?? _polices.Families.LastOrDefault();
                    if (famille is not null)
                    {
                        _familles[chemin] = famille;
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private Font Police(string chemin, float taille, FontStyle variante)
        {
            if (chemin.Length > 0 && _familles.TryGetValue(chemin, out var famille))
            {
                foreach (var essai in new[] { variante, FontStyle.Regular, FontStyle.Bold })
                {
                    if (famille.IsStyleAvailable(essai))
                    {
                        return new Font(famille, taille, essai, GraphicsUnit.Pixel);
                    }
                }
            }

            return new Font("Segoe UI", taille, variante, GraphicsUnit.Pixel);
        }

        private static Color Couleur(string hex)
        {
            var (r, v, b, _) = EsMenuStyle.Couleur(hex);
            return Color.FromArgb(255, r, v, b);
        }

        private static GraphicsPath Arrondi(RectangleF r, float rayon)
        {
            var d = rayon * 2f;
            var chemin = new GraphicsPath();
            chemin.AddArc(r.X, r.Y, d, d, 180, 90);
            chemin.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            chemin.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            chemin.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            chemin.CloseFigure();
            return chemin;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _minuteur.Dispose();
                _polices.Dispose();
            }

            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr apres, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
    }
}
