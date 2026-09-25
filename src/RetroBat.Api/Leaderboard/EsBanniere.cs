using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LE BANDEAU DU HAUT, AUX COULEURS D'EMULATIONSTATION (demande user 2026-09-26).
///
/// L'annonce au lancement (« Partie certifiable »), les consignes de challenge et les messages du
/// replay s'affichaient dans une petite fenetre noire en police systeme : une piece rapportee sur
/// l'ecran d'un jeu lance depuis ES. Le panneau de classement, lui, se lit comme un menu d'ES,
/// parce qu'il en prend la charte dans le theme actif (<see cref="EsMenuStyle"/>). Le bandeau
/// prend la meme :
///   - le fond du panneau : degrade vertical de la couleur des menus (menubg), assombrie aux bords ;
///   - l'en-tete : le logo NelfePlay et la marque (« SCORING ») en police et couleur des en-tetes
///     de section (menugroup), sous un filet d'un pixel (menugrid) ;
///   - le titre en majuscules, police de titre (menutitle), sur la barre de SELECTION d'ES
///     (son degrade, son texte choisi) : ce qu'on doit lire d'un coup d'oeil ;
///   - le detail en petite police des menus (menutextsmall), sur deux lignes au plus.
/// Les tailles sont des fractions de la hauteur d'ecran, comme dans ES. La fenetre est topmost et
/// ne prend jamais le focus : elle ne sort pas le jeu du premier plan.
/// </summary>
internal sealed class EsBanniereForm : Form
{
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly PrivateFontCollection _polices = new();
    private readonly Dictionary<string, FontFamily> _familles = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _masquer = new();
    private readonly System.Windows.Forms.Timer _fondu = new() { Interval = 25 };
    private EsMenuStyle _style = new();
    private EsGlyphCache? _glyphes;
    private string _marque = "";
    private string _titre = "";
    private string _detail = "";
    private bool _alerte;
    private float _hauteurEcran = 1080f;
    private int _pas;

    public EsBanniereForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _masquer.Tick += (_, _) => Masquer();
        _fondu.Tick += (_, _) =>
        {
            _pas++;
            Opacity = Math.Min(0.96, 0.12 + _pas * 0.08);
            if (_pas >= 12) _fondu.Stop();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTopmost | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var arrondi = 2;   // DWMWCP_ROUND : les boites de dialogue d'ES ont des coins adoucis
        _ = DwmSetWindowAttribute(Handle, 33, ref arrondi, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Montre le bandeau en haut de l'ecran, centre, pour la duree donnee.</summary>
    public void Presenter(string marque, string titre, string detail, int? dureeMs, bool alerte = false)
    {
        _alerte = alerte;
        _style = EsMenuStyle.Lire();
        ChargerLesPolices(_style);
        if (_glyphes is null)
        {
            _glyphes = new EsGlyphCache(_style);
            _glyphes.Pret += () => { if (IsHandleCreated) BeginInvoke(Invalidate); };
        }
        _marque = marque.ToUpperInvariant();
        _titre = titre.ToUpperInvariant();   // ES met ses entrees de menu en majuscules
        _detail = detail;

        var ecran = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        _hauteurEcran = ecran.Height;
        var largeur = (int) Math.Clamp(ecran.Width * 0.46f, 560f, 1100f);
        var (hGroupe, hTitre, tPetit) = (Taille(_style.GroupFontSize) * 1.9f, TailleTitre() * 1.5f, Taille(_style.SmallFontSize));
        var lignesDetail = 0;
        if (_detail.Length > 0)
        {
            using var police = Police(_style.SmallFontPath.Length > 0 ? _style.SmallFontPath : _style.TextFontPath, tPetit, FontStyle.Regular);
            using var mesure = CreateGraphics();
            var taille = mesure.MeasureString(_detail, police, (int) (largeur - Marge * 2));
            lignesDetail = Math.Clamp((int) Math.Ceiling(taille.Height / (police.GetHeight(mesure) * 0.98f)), 1, 2);
        }
        var hauteur = (int) (hGroupe + hTitre + (lignesDetail == 0 ? Marge * 0.6f : lignesDetail * tPetit * 1.45f + Marge));
        Size = new Size(largeur, hauteur);
        Location = new Point(ecran.Left + (ecran.Width - largeur) / 2, ecran.Top + (int) (_hauteurEcran * 0.02f));

        Invalidate();
        _pas = 0;
        Opacity = 0.12;
        _fondu.Stop();
        _fondu.Start();
        if (!Visible) Show();
        _masquer.Stop();
        if (dureeMs is > 0)
        {
            _masquer.Interval = dureeMs.Value;
            _masquer.Start();
        }
    }

    public void Masquer()
    {
        _masquer.Stop();
        _fondu.Stop();
        if (Visible) Hide();
    }

    private float Marge => Math.Max(12f, _hauteurEcran * 0.018f);

    private float Taille(double fraction) => (float) Math.Max(10, fraction * _hauteurEcran);

    /// <summary>Le titre, un cran sous la police de titre d'ES : il annonce, il ne crie pas (demande user).</summary>
    private float TailleTitre() => Taille(_style.TitleFontSize) * 0.78f;

    // L'ALERTE EN ORANGE (demande user 2026-09-26) : une partie qui ne sera pas classee ne doit
    // pas se lire avec la couleur d'une selection ordinaire.
    private static readonly Color OrangeDebut = Color.FromArgb(230, 126, 34);
    private static readonly Color OrangeFin = Color.FromArgb(140, 62, 0);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = _style;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;   // FreeType chez ES : niveaux de gris
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Le fond du panneau de classement : la couleur des menus au centre, assombrie aux bords.
        var (r, v, b, _) = EsMenuStyle.Couleur(s.BackgroundColor);
        Color Teintee(double f) => Color.FromArgb(255, Math.Clamp((int) (r * f), 0, 255), Math.Clamp((int) (v * f), 0, 255), Math.Clamp((int) (b * f), 0, 255));
        var zone = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        using (var fond = new LinearGradientBrush(zone, Teintee(0.35), Teintee(0.35), LinearGradientMode.Vertical))
        {
            fond.InterpolationColors = new ColorBlend { Colors = new[] { Teintee(0.35), Teintee(1.0), Teintee(0.35) }, Positions = new[] { 0f, 0.5f, 1f } };
            g.FillRectangle(fond, zone);
        }

        var marge = Marge;
        var tGroupe = Taille(s.GroupFontSize);
        var hGroupe = tGroupe * 1.9f;

        // L'en-tete : le logo, puis la marque en police et couleur des en-tetes de section.
        var x = marge;
        var logo = _glyphes?.Glyphe("nelfeplay", (int) (tGroupe * 1.35f));
        if (logo is not null)
        {
            g.DrawImage(logo, x, (hGroupe - logo.Height) / 2f, logo.Width, logo.Height);
            x += logo.Width + tGroupe * 0.5f;
        }
        using (var policeGroupe = Police(s.GroupFontPath, tGroupe, FontStyle.Bold))
        using (var encre = new SolidBrush(Teinte(s.GroupColor)))
        using (var milieu = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center })
        {
            g.DrawString(_marque, policeGroupe, encre, new RectangleF(x, 0, Width - x - marge, hGroupe), milieu);
        }
        using (var filet = new SolidBrush(Teinte(s.GridSeparatorColor)))
        {
            g.FillRectangle(filet, 0, hGroupe - 1, Width, 1);
        }

        // Le titre sur la barre de selection d'ES : ce qu'on lit d'un coup d'oeil.
        var tTitre = TailleTitre();
        var hTitre = tTitre * 1.5f;
        var bande = new RectangleF(0, hGroupe, Width, hTitre);
        using (var selection = new LinearGradientBrush(new RectangleF(0, hGroupe, Width + 1, hTitre),
                   _alerte ? OrangeDebut : Teinte(s.SelectorColor), _alerte ? OrangeFin : Teinte(s.SelectorColorEnd), LinearGradientMode.Horizontal))
        {
            g.FillRectangle(selection, bande);
        }
        using (var policeTitre = Police(s.TitleFontPath, tTitre, FontStyle.Bold))
        using (var encre = new SolidBrush(_alerte ? Color.White : Teinte(s.SelectedTextColor)))
        using (var centre = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(_titre, policeTitre, encre, new RectangleF(marge, bande.Top, Width - marge * 2, hTitre), centre);
        }

        // Le detail, en petite police des menus.
        if (_detail.Length > 0)
        {
            var tPetit = Taille(s.SmallFontSize);
            using var policePetite = Police(s.SmallFontPath.Length > 0 ? s.SmallFontPath : s.TextFontPath, tPetit, FontStyle.Regular);
            using var encre = new SolidBrush(Teinte(s.TextColor));
            using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
            var haut = bande.Bottom + marge * 0.35f;
            g.DrawString(_detail, policePetite, encre, new RectangleF(marge, haut, Width - marge * 2, Height - haut - marge * 0.35f), centre);
        }
    }

    private static Color Teinte(string hex)
    {
        var (r, g, b, a) = EsMenuStyle.Couleur(hex);
        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>Les polices du theme, chargees une fois ; sans elles, la police systeme.</summary>
    private void ChargerLesPolices(EsMenuStyle style)
    {
        foreach (var chemin in new[] { style.TitleFontPath, style.TextFontPath, style.GroupFontPath, style.SmallFontPath })
        {
            if (chemin.Length == 0 || _familles.ContainsKey(chemin) || !File.Exists(chemin)) continue;
            try
            {
                var avant = _polices.Families.Length;
                _polices.AddFontFile(chemin);
                var famille = _polices.Families.Skip(avant).FirstOrDefault() ?? _polices.Families.LastOrDefault();
                if (famille is not null) _familles[chemin] = famille;
            }
            catch (Exception)
            {
                // Une police illisible : la police systeme prend le relais.
            }
        }
    }

    private Font Police(string chemin, float taille, FontStyle variante)
    {
        if (chemin.Length > 0 && _familles.TryGetValue(chemin, out var famille))
        {
            foreach (var essai in new[] { variante, FontStyle.Regular, FontStyle.Bold })
            {
                if (famille.IsStyleAvailable(essai)) return new Font(famille, taille, essai, GraphicsUnit.Pixel);
            }
        }
        return new Font("Segoe UI", taille, variante, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _masquer.Dispose();
            _fondu.Dispose();
            _glyphes?.Dispose();
            _polices.Dispose();
        }
        base.Dispose(disposing);
    }
}
