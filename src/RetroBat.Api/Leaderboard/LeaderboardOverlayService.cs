using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Le panneau de classement, a gauche du menu d'EmulationStation.
///
/// Il s'affiche PAR-DESSUS ES sans jamais lui prendre le focus, tant que c'est ES qui navigue ;
/// et il prend le focus quand le joueur entre chez nous, ce qui fige la navigation d'ES (SDL ne
/// livre plus les evenements de manette a une fenetre inactive) pendant que nous, nous
/// continuons de lire le pad par le canal panel. Mesure a l'appui sur la borne : le menu d'ES
/// RESTE OUVERT pendant ce temps, il devient seulement inerte.
///
/// TROIS REGLES APPRISES A L'ECRAN, qu'on ne peut pas deviner :
///   1. `Form.TopMost` ne suffit pas sur une fenetre `WS_EX_NOACTIVATE` : Windows ne l'applique
///      pas et le panneau passe DERRIERE ES. Il faut la bascule NOTOPMOST -> TOPMOST en
///      SWP_NOACTIVATE, repetee (c'est ce que fait deja l'overlay de replay).
///   2. `SetForegroundWindow` seul est REFUSE a un processus d'arriere-plan. Prendre et rendre
///      le focus passe par la methode maison (attachement au fil de la fenetre de devant).
///   3. LE RETOUR DU FOCUS EST UN CONTRAT. Sur une borne il n'y a pas de clavier : un panneau
///      qui garde le focus sans le rendre laisse le joueur sans aucun controle. D'ou le chien de
///      garde ci-dessous, qui rend la main si plus rien ne se passe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LeaderboardOverlayService : IDisposable
{
    /// <summary>Au-dela de ce silence, on rend la main a ES de nous-memes.</summary>
    public static readonly TimeSpan SilenceMax = TimeSpan.FromSeconds(45);

    private readonly ILogger<LeaderboardOverlayService> _logger;
    private readonly object _gate = new();

    private Thread? _fil;
    private Panneau? _forme;
    private EsThemeStyle _style = new();
    private EsGlyphCache? _glyphes;
    private PrivateFontCollection? _polices;
    private FontFamily? _famille;
    private DateTime _dernierGeste = DateTime.UtcNow;

    public LeaderboardOverlayService(ILogger<LeaderboardOverlayService> logger)
    {
        _logger = logger;
    }

    /// <summary>Vrai quand le panneau est a l'ecran.</summary>
    public bool Affiche { get; private set; }

    /// <summary>Vrai quand c'est NOUS qui avons la manette.</summary>
    public bool ANousLaMain { get; private set; }

    /// <summary>Ce que le panneau montre. Remplace en bloc : pas d'etat a moitie mis a jour.</summary>
    public sealed record Contenu(
        string Jeu,
        IReadOnlyList<string> Onglets,
        int OngletCourant,
        IReadOnlyList<LeaderboardClient.Ligne> Lignes,
        int LigneCourante,
        string Etat,
        bool SurLaPorte,
        bool NousAvonsLaMain);

    private Contenu _contenu = new("", Array.Empty<string>(), 0, Array.Empty<LeaderboardClient.Ligne>(), 0, "", true, false);

    // ── Ouvrir, montrer, fermer ──────────────────────────────────────────────

    /// <summary>Affiche le panneau sur l'ecran d'EmulationStation, sans lui prendre le focus.</summary>
    public void Ouvrir(EsThemeStyle style, Contenu contenu)
    {
        lock (_gate)
        {
            _style = style;
            _contenu = contenu;
            _dernierGeste = DateTime.UtcNow;
            _glyphes ??= new EsGlyphCache(style, _logger);
            ChargerLaPolice(style);

            if (_fil is null)
            {
                _fil = new Thread(Boucle) { IsBackground = true, Name = "leaderboard-overlay" };
                _fil.SetApartmentState(ApartmentState.STA);
                _fil.Start();
            }
            Affiche = true;
        }
    }

    /// <summary>Remplace ce qui est affiche. Sans effet si le panneau est ferme.</summary>
    public void Montrer(Contenu contenu)
    {
        lock (_gate)
        {
            _contenu = contenu;
            _dernierGeste = DateTime.UtcNow;
        }
        _forme?.Rafraichir();
    }

    public void Fermer()
    {
        lock (_gate)
        {
            if (!Affiche) return;
            Affiche = false;
        }
        if (ANousLaMain) RendreLeFocus();
        _forme?.Cacher();
    }

    /// <summary>
    /// Prend la manette : ES se fige, nous continuons a lire le pad. Rend faux si Windows a
    /// refuse - dans ce cas on ne fait RIEN croire, on reste en mode « ES navigue ».
    /// </summary>
    public bool PrendreLeFocus()
    {
        var forme = _forme;
        if (forme is null || !Affiche) return false;
        var pris = forme.Imposer();
        ANousLaMain = pris;
        _dernierGeste = DateTime.UtcNow;
        if (!pris) _logger.LogWarning("Classement : le premier plan a ete refuse, ES garde la main.");
        return pris;
    }

    /// <summary>
    /// Rend la manette a EmulationStation. VERIFIE, et reessaye : c'est le geste qui sauve la
    /// borne d'un panneau qui ne rendrait jamais la main.
    /// </summary>
    public bool RendreLeFocus()
    {
        ANousLaMain = false;
        for (var essai = 0; essai < 3; essai++)
        {
            if (EmulatorForeground.FocusEmulationStation()) return true;
            Thread.Sleep(120);
        }
        _logger.LogError("Classement : EmulationStation n'a pas repris le premier plan. Le joueur peut se retrouver sans controle.");
        return false;
    }

    /// <summary>
    /// Le chien de garde : appele au fil du temps par le service d'entree. Si nous tenons la
    /// manette et que plus rien n'arrive, on rend la main tout seuls.
    /// </summary>
    public bool TropLongSilence()
    {
        if (!ANousLaMain) return false;
        if (DateTime.UtcNow - _dernierGeste < SilenceMax) return false;
        _logger.LogInformation("Classement : {Secondes} s sans geste, on rend la main a EmulationStation.", (int) SilenceMax.TotalSeconds);
        return true;
    }

    private void ChargerLaPolice(EsThemeStyle style)
    {
        if (_famille is not null || style.FontPath.Length == 0) return;
        try
        {
            _polices = new PrivateFontCollection();
            _polices.AddFontFile(style.FontPath);
            _famille = _polices.Families.FirstOrDefault();
            _logger.LogInformation("Classement : police du theme {Theme} chargee ({Police}).", style.ThemeSet, Path.GetFileName(style.FontPath));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Classement : police du theme illisible, police systeme.");
            _famille = null;
        }
    }

    private void Boucle()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            _forme = new Panneau(this);
            Application.Run(_forme);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Classement : la fenetre du panneau s'est arretee.");
        }
    }

    private Contenu Lire()
    {
        lock (_gate) return _contenu;
    }

    private EsThemeStyle Style()
    {
        lock (_gate) return _style;
    }

    public void Dispose()
    {
        try { _forme?.Fermer(); } catch { }
        _glyphes?.Dispose();
        _polices?.Dispose();
    }

    // ── La fenetre ───────────────────────────────────────────────────────────

    private sealed class Panneau : Form
    {
        private readonly LeaderboardOverlayService _service;
        private readonly System.Windows.Forms.Timer _minuteur;

        public Panneau(LeaderboardOverlayService service)
        {
            _service = service;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Opacity = 0.96d;
            Bounds = ZoneSurEcranDEs();
            BackColor = Teinte(service.Style().BackgroundColor, 255);

            _minuteur = new System.Windows.Forms.Timer { Interval = 500 };
            _minuteur.Tick += (_, _) =>
            {
                if (!_service.Affiche) { Cacher(); return; }
                if (!Visible) { Show(); }
                // On se remet en tete regulierement : une autre fenetre « toujours au-dessus »
                // peut repasser devant, et on ne veut pas dependre de la chance.
                PasserDevant();
            };
            _minuteur.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WsExComposited = 0x02000000;
                var p = base.CreateParams;
                p.ExStyle |= WsExNoActivate | WsExToolWindow | WsExComposited;
                return p;
            }
        }

        public void Rafraichir()
        {
            if (IsHandleCreated) BeginInvoke(Invalidate);
        }

        public void Cacher()
        {
            if (IsHandleCreated) BeginInvoke(() => { if (Visible) Hide(); });
        }

        public void Fermer()
        {
            if (IsHandleCreated) BeginInvoke(Close);
        }

        /// <summary>Le premier plan impose, comme APIExpose le fait pour ES et l'emulateur.</summary>
        public bool Imposer()
        {
            if (!IsHandleCreated) return false;
            var ok = false;
            Invoke(() =>
            {
                var devant = GetForegroundWindow();
                var filDevant = devant == IntPtr.Zero ? 0u : GetWindowThreadProcessId(devant, out _);
                var monFil = GetCurrentThreadId();
                var attache = filDevant != 0 && filDevant != monFil && AttachThreadInput(filDevant, monFil, true);
                try
                {
                    BringWindowToTop(Handle);
                    SetForegroundWindow(Handle);
                }
                finally
                {
                    if (attache) AttachThreadInput(filDevant, monFil, false);
                }
                ok = GetForegroundWindow() == Handle;
            });
            return ok;
        }

        private void PasserDevant()
        {
            SetWindowPos(Handle, HwndNoTopmost, Left, Top, Width, Height, SwpNoActivate);
            SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpNoActivate | SwpShowWindow);
        }

        /// <summary>La moitie gauche de l'ecran QUI PORTE EMULATIONSTATION, pas l'ecran principal.</summary>
        private static Rectangle ZoneSurEcranDEs()
        {
            var ecran = Screen.PrimaryScreen;
            try
            {
                var es = Process.GetProcessesByName("emulationstation").FirstOrDefault();
                if (es?.MainWindowHandle is { } h && h != IntPtr.Zero) ecran = Screen.FromHandle(h);
            }
            catch { }
            var b = ecran?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var marge = (int) (b.Height * 0.055);
            return new Rectangle(b.X + marge, b.Y + marge, b.Width / 2 - marge * 2, b.Height - marge * 2);
        }

        private static Color Teinte(string hex, int alpha)
        {
            var (r, g, bl, a) = EsThemeStyle.Couleur(hex, alpha);
            return Color.FromArgb(a, r, g, bl);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var style = _service.Style();
            var c = _service.Lire();
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var texte = Teinte(style.TextColor, 255);
            var marque = Teinte(style.BaseColor, 255);
            var vif = Color.FromArgb(250, 250, 255);

            using var cadre = new Pen(Color.FromArgb(_service.ANousLaMain ? 230 : 90, marque), _service.ANousLaMain ? 3f : 2f);
            g.DrawRectangle(cadre, 1, 1, Width - 3, Height - 3);

            var echelle = Height / 1056f;
            using var policeTitre = Police(style, 21f * echelle, FontStyle.Bold);
            using var policeOnglet = Police(style, 14f * echelle, FontStyle.Regular);
            using var policeLigne = Police(style, 16f * echelle, FontStyle.Regular);
            using var policeAide = Police(style, 12.5f * echelle, FontStyle.Regular);

            var marge = 26f * echelle;
            var y = marge;

            // Le jeu, en tete : on doit toujours savoir de quel classement on parle.
            using (var encre = new SolidBrush(vif))
            {
                g.DrawString(c.Jeu.Length > 0 ? c.Jeu.ToUpperInvariant() : "CLASSEMENT", policeTitre, encre, marge, y);
            }
            y += policeTitre.Height + 10f * echelle;

            // La bande d'onglets : du plus proche au plus lointain, celui de droite touche la porte.
            y = DessinerLesOnglets(g, c, policeOnglet, marge, y, echelle, marque, texte, vif);

            // Les lignes, ou ce qui tient lieu de lignes.
            if (c.Lignes.Count == 0)
            {
                using var encre = new SolidBrush(texte);
                g.DrawString(Message(c.Etat), policeLigne, encre, marge, y + 12f * echelle);
            }
            else
            {
                DessinerLesLignes(g, c, policeLigne, marge, y, echelle, marque, texte, vif);
            }

            DessinerLAide(g, c, policeAide, marge, echelle, texte);
        }

        private float DessinerLesOnglets(Graphics g, Contenu c, Font police, float marge, float y, float echelle,
            Color marque, Color texte, Color vif)
        {
            var x = marge;
            var hauteur = police.Height + 10f * echelle;
            for (var i = 0; i < c.Onglets.Count; i++)
            {
                var nom = c.Onglets[i].ToUpperInvariant();
                var largeur = g.MeasureString(nom, police).Width + 18f * echelle;
                var actif = i == c.OngletCourant;
                if (actif)
                {
                    using var fond = new SolidBrush(Color.FromArgb(_service.ANousLaMain ? 210 : 120, marque));
                    g.FillRectangle(fond, x, y, largeur, hauteur);
                }
                using (var encre = new SolidBrush(actif ? Color.FromArgb(255, 255, 255) : texte))
                {
                    g.DrawString(nom, police, encre, x + 9f * echelle, y + 5f * echelle);
                }
                x += largeur + 6f * echelle;
            }

            // La porte : quand l'onglet courant est le dernier, un appui a droite sort vers ES.
            // On l'annonce avec le glyphe d'ES lui-meme, a l'endroit ou le geste mene.
            if (c.SurLaPorte && c.NousAvonsLaMain)
            {
                var icone = _service._glyphes?.Glyphe("dpad_right", (int) hauteur, texte);
                if (icone is not null) g.DrawImage(icone, Width - marge - icone.Width, y, icone.Width, icone.Height);
            }
            return y + hauteur + 12f * echelle;
        }

        private void DessinerLesLignes(Graphics g, Contenu c, Font police, float marge, float y, float echelle,
            Color marque, Color texte, Color vif)
        {
            var hauteur = police.Height + 12f * echelle;
            var place = (int) ((Height - y - 70f * echelle) / hauteur);
            var premier = Math.Max(0, Math.Min(c.LigneCourante - place / 2, c.Lignes.Count - place));
            if (premier < 0) premier = 0;

            for (var i = premier; i < c.Lignes.Count && i < premier + place; i++)
            {
                var l = c.Lignes[i];
                var choisie = i == c.LigneCourante && c.NousAvonsLaMain;
                if (choisie)
                {
                    using var fond = new SolidBrush(Color.FromArgb(150, marque));
                    g.FillRectangle(fond, marge - 8f * echelle, y, Width - 2 * marge + 16f * echelle, hauteur);
                }

                var encre = l.CestMoi ? vif : (choisie ? Color.FromArgb(255, 255, 255) : texte);
                using var pinceau = new SolidBrush(encre);
                g.DrawString($"{l.Rang}.", police, pinceau, marge, y + 4f * echelle);
                g.DrawString(l.Joueur.Length > 0 ? l.Joueur : "—", police, pinceau, marge + 52f * echelle, y + 4f * echelle);

                var valeur = l.Valeur.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                var largeurValeur = g.MeasureString(valeur, police).Width;
                g.DrawString(valeur, police, pinceau, Width - marge - largeurValeur - 60f * echelle, y + 4f * echelle);

                // Le replay se signale par le glyphe du bouton qui le lance, pas par un mot.
                if (l.ReplayId is { Length: > 0 })
                {
                    var icone = _service._glyphes?.Glyphe("button_y", (int) (police.Height * 0.95f), encre);
                    if (icone is not null) g.DrawImage(icone, Width - marge - icone.Width, y + 4f * echelle, icone.Width, icone.Height);
                }
                y += hauteur;
            }
        }

        private void DessinerLAide(Graphics g, Contenu c, Font police, float marge, float echelle, Color texte)
        {
            var y = Height - marge - police.Height;
            var x = marge;
            var hauteur = (int) (police.Height * 1.15f);

            void Aide(string glyphe, string mot)
            {
                var icone = _service._glyphes?.Glyphe(glyphe, hauteur, texte);
                if (icone is not null)
                {
                    g.DrawImage(icone, x, y, icone.Width, icone.Height);
                    x += icone.Width + 6f * echelle;
                }
                using var encre = new SolidBrush(texte);
                g.DrawString(mot, police, encre, x, y);
                x += g.MeasureString(mot, police).Width + 18f * echelle;
            }

            if (c.NousAvonsLaMain)
            {
                Aide("dpad_updown", "CHOISIR");
                Aide("dpad_leftright", "VUE");
                if (c.Lignes.Count > 0) Aide("button_y", "REPLAY");
                Aide("button_b", "RETOUR");
            }
            else
            {
                Aide("dpad_left", "CLASSEMENT");
            }
        }

        private Font Police(EsThemeStyle style, float taille, FontStyle variante)
        {
            var famille = _service._famille;
            try
            {
                if (famille is not null) return new Font(famille, taille, variante, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
                // Une famille qui ne connait pas cette variante : on retombe sur la police systeme.
            }
            return new Font("Segoe UI", taille, variante, GraphicsUnit.Pixel);
        }

        private static string Message(string etat) => etat switch
        {
            LeaderboardClient.EtatHorsLigne => "SERVICE HORS LIGNE",
            LeaderboardClient.EtatAucunScore => "AUCUN SCORE ENREGISTRE",
            _ => "CHARGEMENT DU CLASSEMENT…",
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing) _minuteur.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr apres, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attacher);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private static readonly IntPtr HwndTopmost = new(-1);
        private static readonly IntPtr HwndNoTopmost = new(-2);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
    }
}
