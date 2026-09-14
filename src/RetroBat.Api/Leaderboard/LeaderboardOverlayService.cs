using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Le panneau de classement, a gauche du menu d'EmulationStation, HABILLE COMME LUI.
///
/// Il s'affiche par-dessus ES sans lui prendre le focus tant que c'est ES qui navigue, et prend
/// le focus quand le joueur entre chez nous - ce qui fige la navigation d'ES (SDL ne livre plus
/// la manette a une fenetre inactive) pendant que nous continuons de lire le pad par le canal
/// panel. Mesure sur la borne : le menu d'ES RESTE OUVERT, il devient seulement inerte.
///
/// QUATRE REGLES APPRISES A L'ECRAN, qu'on ne peut pas deviner :
///   1. `Form.TopMost` ne suffit pas sur une fenetre `WS_EX_NOACTIVATE` : Windows ne l'applique
///      pas et le panneau passe DERRIERE ES. Il faut la bascule NOTOPMOST -> TOPMOST en
///      SWP_NOACTIVATE, repetee (comme l'overlay de replay).
///   2. Une fenetre `WS_EX_NOACTIVATE` ne peut PAS etre activee : on retire le drapeau le temps
///      de prendre la main, et on le remet des qu'on la rend.
///   3. `SetForegroundWindow` seul est refuse a un processus d'arriere-plan : passage par la
///      methode maison (attachement au fil de la fenetre de devant).
///   4. LE RETOUR DU FOCUS EST UN CONTRAT. Sur une borne il n'y a pas de clavier : un panneau
///      qui garde le focus sans le rendre laisse le joueur sans aucun controle.
///
/// L'habillage ne s'invente pas non plus : police, tailles et couleurs viennent du theme actif
/// (<see cref="EsMenuStyle"/>), le cadre est le nine-patch des menus d'ES, et les pictogrammes
/// sont ses propres glyphes de boutons.
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
    private EsMenuStyle _style = new();
    private EsGlyphCache? _glyphes;
    private readonly PrivateFontCollection _polices = new();
    private readonly Dictionary<string, FontFamily> _familles = new(StringComparer.OrdinalIgnoreCase);
    private FontFamily? _policeSymboles;
    /// <summary>Les poignees suivies, pour marquer les lignes sans interroger le reseau.</summary>
    internal IReadOnlySet<string> _suivis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public void PoserLesSuivis(IReadOnlySet<string> suivis) => _suivis = suivis;
    private DateTime _dernierGeste = DateTime.UtcNow;

    public LeaderboardOverlayService(ILogger<LeaderboardOverlayService> logger)
    {
        _logger = logger;
    }

    public bool Affiche { get; private set; }

    private System.Diagnostics.Stopwatch? _depuisLAppui;

    /// <summary>Le chrono de l'appui, pour dire QUAND le panneau est reellement apparu.</summary>
    public void ChronometrerLePremierDessin(System.Diagnostics.Stopwatch? chrono)
    {
        _depuisLAppui = chrono;
        _forme?.Rafraichir();
    }

    /// <summary>
    /// Prepare TOUS les pictogrammes du panneau, une fois, en arriere-plan. Chaque conversion
    /// lance un processus ImageMagick ; les faire a la premiere ouverture, glyphe par glyphe,
    /// laissait le panneau vide pres d'une minute. Une fois ecrits dans <c>.cache</c>, ils
    /// servent pour toujours : plus aucune conversion tant que le theme ne change pas.
    /// </summary>
    public void Prechauffer(EsMenuStyle style)
    {
        lock (_gate)
        {
            _style = style;
            ChargerLesPolices(style);
            // La fenetre nait MAINTENANT, cachee : la creer a la premiere ouverture (fil STA,
            // polices du theme) coutait presque une seconde, montre en main.
            if (_fil is null)
            {
                _fil = new Thread(Boucle) { IsBackground = true, Name = "leaderboard-overlay" };
                _fil.SetApartmentState(ApartmentState.STA);
                _fil.Start();
            }
        }
        _glyphes ??= new EsGlyphCache(style, _logger);
        var glyphes = _glyphes;
        var hauteurEcran = HauteurDeLEcranDEs();
        _ = Task.Run(() =>
        {
            try
            {
                int Taille(double fraction, double facteur) => (int) Math.Max(9, fraction * hauteurEcran * facteur);
                var texte = style.TextFontSize;
                var couleurs = new[]
                {
                    EsMenuStyle.Couleur(style.TextColor), EsMenuStyle.Couleur(style.SelectedTextColor),
                    EsMenuStyle.Couleur(style.HelpIconColor), EsMenuStyle.Couleur(style.TitleColor),
                };
                var noms = new List<string> { "dpad_updown", "dpad_leftright", "dpad_left", "dpad_right", "star_filled", "star_unfilled" };
                noms.AddRange(style.HelpIcons.Values);
                foreach (var identite in new[] { "a", "b", "x", "y", "l", "r" })
                {
                    var chemin = style.HelpIcon(identite);
                    if (chemin.Length > 0) noms.Add(chemin);
                }
                foreach (var nom in noms.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var facteur in new[] { 0.9d, 1.0d, 1.1d, 1.2d })
                    {
                        foreach (var (r, v, b, _) in couleurs)
                        {
                            glyphes.Preparer(nom, Taille(texte, facteur), Color.FromArgb(r, v, b));
                        }
                    }
                }
                // Les images qui gardent leurs couleurs : sceau, logo, sabliers.
                glyphes.Preparer("nelfe-verified", Taille(texte, 1.0), null);
                glyphes.Preparer("nelfeplay", (int) Math.Max(9, style.TitleFontSize * hauteurEcran * 1.15), null);
                foreach (var i in new[] { 0, 1, 2, 3 })
                {
                    foreach (var (r, v, b, _) in couleurs.Take(1))
                    {
                        glyphes.Preparer("busy_" + i, Taille(texte, 1.1), Color.FromArgb(r, v, b));
                    }
                }
                _logger.LogInformation("Classement : pictogrammes prets ({Combien} en cache).", glyphes.Combien);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Classement : preparation des pictogrammes interrompue.");
            }
        });
    }

    private static int HauteurDeLEcranDEs()
    {
        try
        {
            var es = Process.GetProcessesByName("emulationstation").FirstOrDefault();
            if (es?.MainWindowHandle is { } h && h != IntPtr.Zero) return Screen.FromHandle(h).Bounds.Height;
        }
        catch (Exception) { }
        return Screen.PrimaryScreen?.Bounds.Height ?? 1080;
    }
    public bool ANousLaMain { get; private set; }

    /// <summary>Une ligne de l'onglet LIVE & CONTEST : quoi, qui, ou, et ce qu'on peut faire.</summary>
    public sealed record Evenement(string Etiquette, string Qui, string Detail, bool EnDirect);

    /// <summary>Un glyphe d'ES et son mot, deja traduit : une consigne d'aide ou une action.</summary>
    public sealed record Aide(string Glyphe, string Mot);

    /// <summary>Ce que le panneau montre. Remplace en bloc : jamais d'etat a moitie mis a jour.</summary>
    public sealed record Contenu(
        string Jeu,
        IReadOnlyList<string> Onglets,
        int OngletCourant,
        IReadOnlyList<LeaderboardClient.Ligne> Lignes,
        int LigneCourante,
        string Message,
        bool SurLaPorte,
        bool NousAvonsLaMain,
        IReadOnlyList<Aide> Aides,
        IReadOnlyList<Aide> ActionsDeLaLigne,
        string Titre,
        string EtiquetteReplay,
        string SurtitrePodium,
        string SurtitreClassement,
        IReadOnlyList<string> Ordinaux,
        string Defier,
        string MaPlace,
        string SuivreMot,
        string SuiviMot,
        bool SuitLaLigne,
        string GlypheDefier,
        string GlypheSuivre,
        IReadOnlyList<Evenement>? Evenements = null,
        string Rejoindre = "",
        string GlypheRejoindre = "",
        string Attente = "",
        string AttenteTitre = "",
        string AttenteDetail = "",
        IReadOnlySet<long>? ReplaysEnPreparation = null,
        IReadOnlyDictionary<string, int>? RangsPrecedents = null);

    private Contenu _contenu = new("", Array.Empty<string>(), 0, Array.Empty<LeaderboardClient.Ligne>(), 0, "", true, false,
        Array.Empty<Aide>(), Array.Empty<Aide>(), "", "", "", "", Array.Empty<string>(),
        "", "", "", "", false, "", "");

    // ── Ouvrir, montrer, fermer ──────────────────────────────────────────────

    public void Ouvrir(EsMenuStyle style, Contenu contenu)
    {
        lock (_gate)
        {
            _style = style;
            _contenu = contenu;
            _dernierGeste = DateTime.UtcNow;
            if (_glyphes is null)
            {
                _glyphes = new EsGlyphCache(style, _logger);
                _glyphes.Pret += () => _forme?.Rafraichir();   // un glyphe pret : on redessine
            }
            ChargerLesPolices(style);
            _forme?.SEtendre(false);   // jamais rouvrir en plein ecran d'attente

            if (_fil is null)
            {
                _fil = new Thread(Boucle) { IsBackground = true, Name = "leaderboard-overlay" };
                _fil.SetApartmentState(ApartmentState.STA);
                _fil.Start();
            }
            Affiche = true;
        }
        // Sans cela, la fenetre n'apparaissait qu'au prochain tour du minuteur : jusqu'a une
        // demi-seconde d'attente apres que tout etait pret.
        _forme?.Paraitre();
    }

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
        _forme?.SEtendre(false);
        lock (_gate) { _contenu = _contenu with { Attente = "", AttenteTitre = "", AttenteDetail = "" }; }
    }

    /// <summary>
    /// Fermer parce qu'un jeu demarre : on ne rend PAS la main a EmulationStation, c'est
    /// l'emulateur qui la prend. Refocaliser ES ici volerait le premier plan a RetroArch.
    /// </summary>
    public void FermerPourLeJeu()
    {
        lock (_gate)
        {
            if (!Affiche) return;
            Affiche = false;
            _contenu = _contenu with { Attente = "", AttenteTitre = "", AttenteDetail = "" };
        }
        ANousLaMain = false;
        _forme?.RefuserLActivation();
        _forme?.Cacher();
        _forme?.SEtendre(false);
    }

    /// <summary>La boite d'attente est-elle a l'ecran ?</summary>
    public bool EnAttente
    {
        get { lock (_gate) return Affiche && _contenu.Attente.Length > 0; }
    }

    /// <summary>
    /// L'attente d'un replay : la boite « WORKING… » d'ES, plein ecran floute. Le panneau
    /// s'etend a tout l'ecran d'ES le temps que le jeu demarre.
    /// </summary>
    public void Attendre(string message, string titre = "", string detail = "")
    {
        lock (_gate)
        {
            _contenu = _contenu with { Attente = message, AttenteTitre = titre, AttenteDetail = detail };
            _dernierGeste = DateTime.UtcNow;
        }
        _forme?.SEtendre(true);
    }

    /// <summary>Prend la manette. Faux si Windows a refuse : on ne fait alors rien croire.</summary>
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

    /// <summary>Rend la manette a EmulationStation. Verifie, et reessaye.</summary>
    public bool RendreLeFocus()
    {
        ANousLaMain = false;
        _forme?.RefuserLActivation();
        for (var essai = 0; essai < 3; essai++)
        {
            if (EmulatorForeground.FocusEmulationStation()) return true;
            Thread.Sleep(120);
        }
        _logger.LogError("Classement : EmulationStation n'a pas repris le premier plan. Le joueur peut se retrouver sans controle.");
        return false;
    }

    public bool TropLongSilence()
    {
        if (!ANousLaMain) return false;
        if (DateTime.UtcNow - _dernierGeste < SilenceMax) return false;
        _logger.LogInformation("Classement : {Secondes} s sans geste, on rend la main a EmulationStation.", (int) SilenceMax.TotalSeconds);
        return true;
    }

    /// <summary>Les polices du theme, chargees une fois. Sans elles, la police systeme.</summary>
    private void ChargerLesPolices(EsMenuStyle style)
    {
        // Les pictogrammes de FontAwesome, la police d'icones d'ES elle-meme.
        if (_policeSymboles is null && style.ResourcesRoot.Length > 0)
        {
            var fa = Path.Combine(style.ResourcesRoot, "fontawesome-webfont.ttf");
            if (File.Exists(fa))
            {
                try
                {
                    var avant = _polices.Families.Select(f => f.Name).ToHashSet();
                    _polices.AddFontFile(fa);
                    _policeSymboles = _polices.Families.FirstOrDefault(f => !avant.Contains(f.Name)) ?? _polices.Families.LastOrDefault();
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Classement : FontAwesome illisible."); }
            }
        }
        foreach (var chemin in new[] { style.TitleFontPath, style.TextFontPath, style.GroupFontPath, style.SmallFontPath, style.FooterFontPath })
        {
            if (chemin.Length == 0 || _familles.ContainsKey(chemin)) continue;
            try
            {
                var avant = _polices.Families.Length;
                _polices.AddFontFile(chemin);
                var famille = _polices.Families.Skip(avant).FirstOrDefault() ?? _polices.Families.LastOrDefault();
                if (famille is not null) _familles[chemin] = famille;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Classement : police {Police} illisible.", Path.GetFileName(chemin));
            }
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

    private EsMenuStyle Style()
    {
        lock (_gate) return _style;
    }

    public void Dispose()
    {
        try { _forme?.Fermer(); } catch { }
        _glyphes?.Dispose();
        _polices.Dispose();
    }

    // ── La fenetre ───────────────────────────────────────────────────────────

    private sealed class Panneau : Form
    {
        private readonly LeaderboardOverlayService _service;
        private readonly System.Windows.Forms.Timer _minuteur;
        private readonly int _hauteurEcran;
        private readonly int _largeurEcran;
        /// <summary>La camera des onglets, comme ComponentTab : l'onglet courant reste visible.</summary>
        private float _cameraOnglets;
        private int _largeurNormale;
        private int _imageAttente;
        private int _imageSablier;

        private static bool AUnReplayEnPreparation(Contenu c)
            => c.ReplaysEnPreparation is { Count: > 0 } attente
               && c.Lignes.Any(l => l.CestMoi && l.ReplayId is not { Length: > 0 } && attente.Contains(l.Valeur));

        public Panneau(LeaderboardOverlayService service)
        {
            _service = service;
            var (ecranDEs, ecran, largeur) = EcranDEs();
            _hauteurEcran = ecran;
            _largeurEcran = largeur;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Opacity = 1d;
            // WinForms refuse une couleur de fond avec alpha : le fond translucide (242424D0 de
            // blurfx) se peint dans OnPaint, la propriete ne recoit que l'opaque.
            BackColor = Color.FromArgb(255, Teinte(service.Style().BackgroundColor));

            // La largeur du menu de jeu d'ES (GuiGameOptions) : max(W/3, min(W/2, 33 x « S »)).
            // Il est colle au bord droit sur toute la hauteur ; nous, au bord gauche, mais on
            // s'arrete AU-DESSUS de la barre d'aide d'ES (HelpStyle : 0,9515 x H) pour ne jamais
            // cacher ses consignes.
            var tailleTexte = (float) Math.Max(9, service.Style().TextFontSize * _hauteurEcran);
            float largeurDeS;
            using (var police = Police(service.Style().TextFontPath, tailleTexte, FontStyle.Regular))
            using (var g = CreateGraphics())
            {
                largeurDeS = g.MeasureString("S", police, PointF.Empty, StringFormat.GenericTypographic).Width;
            }
            var largeurMenuEs = Math.Min(_largeurEcran * 0.5f, largeurDeS * 33f);
            largeurMenuEs = Math.Max(largeurMenuEs, _largeurEcran / 3f);
            // Le panneau prend TOUT ce qui reste une fois le menu d'ES servi et les 15 % d'ecart
            // gardes entre les deux volets : c'est le plus large possible sans rien empieter.
            var w = _largeurEcran - largeurMenuEs - _largeurEcran * 0.15f;
            _largeurNormale = (int) w;
            Bounds = new Rectangle(ecranDEs.X, ecranDEs.Y, (int) w, BasDuPanneau(_hauteurEcran));

            _minuteur = new System.Windows.Forms.Timer { Interval = 500 };
            _minuteur.Tick += (_, _) =>
            {
                if (!_service.Affiche) { Cacher(); return; }
                if (!Visible) Show();
                PasserDevant();
                var contenu = _service.Lire();
                // Le sablier tourne : attente d'un lancement, ou replay en cours d'envoi.
                if (contenu.Attente.Length > 0 || AUnReplayEnPreparation(contenu))
                {
                    _imageSablier++;
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
                const int WsExToolWindow = 0x00000080;
                var p = base.CreateParams;
                p.ExStyle |= WsExNoActivate | WsExToolWindow;
                return p;
            }
        }

        /// <summary>Le noir pur ne sert plus de trou vers le flou : le fond est un degrade opaque.</summary>
        private Color Opaque(Color c) => c;

        public void Rafraichir()
        {
            if (IsHandleCreated) BeginInvoke(Invalidate);
        }

        /// <summary>Se montrer immediatement, et passer devant.</summary>
        public void Paraitre()
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (!Visible) Show();
                PasserDevant();
                Invalidate();
            });
        }

        /// <summary>Plein ecran (boite d'attente) ou le volet normal.</summary>
        public void SEtendre(bool pleinEcran)
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                var (ecran, _, _) = EcranDEs();
                Bounds = pleinEcran
                    ? ecran
                    : new Rectangle(ecran.X, ecran.Y, _largeurNormale, BasDuPanneau(_hauteurEcran));
                Invalidate();
            });
        }

        public void Cacher()
        {
            if (IsHandleCreated) BeginInvoke(() => { if (Visible) Hide(); });
        }

        public void Fermer()
        {
            if (IsHandleCreated) BeginInvoke(Close);
        }

        /// <summary>
        /// Le premier plan impose. PIEGE MESURE : une fenetre `WS_EX_NOACTIVATE` ne peut pas
        /// etre activee (Windows refuse en silence), d'ou le retrait du drapeau ici.
        /// </summary>
        public bool Imposer()
        {
            if (!IsHandleCreated) return false;
            var ok = false;
            Invoke(() =>
            {
                SetWindowLong(Handle, GwlExStyle, GetWindowLong(Handle, GwlExStyle) & ~WsExNoActivate);
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

        /// <summary>Le panneau redevient inactivable : il ne prendra plus le focus par accident.</summary>
        public void RefuserLActivation()
        {
            if (!IsHandleCreated) return;
            Invoke(() => SetWindowLong(Handle, GwlExStyle, GetWindowLong(Handle, GwlExStyle) | WsExNoActivate));
        }

        /// <summary>
        /// Rester devant, sans clignoter.
        ///
        /// MESURE : apres avoir ete cachee puis remontree, la fenetre garde le BIT
        /// WS_EX_TOPMOST mais perd sa PLACE dans la bande topmost - elle se retrouve sous
        /// EmulationStation. Le bit de style ne prouve donc rien ; seule une position mesuree
        /// le fait. Une reaffirmation simple de HWND_TOPMOST est idempotente et ne clignote
        /// pas ; la bascule NOTOPMOST -> TOPMOST, elle, fait clignoter l'ecran face a un
        /// emulateur plein ecran, donc on ne la garde que pour le cas ou l'on est REELLEMENT
        /// passe derriere la fenetre d'un autre processus.
        /// </summary>
        private void PasserDevant()
        {
            SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            if (!QuelquUnNousPasseDevant()) return;
            SetWindowPos(Handle, HwndNoTopmost, Left, Top, Width, Height, SwpNoActivate);
            SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpNoActivate | SwpShowWindow);
        }

        /// <summary>Une fenetre visible d'un AUTRE processus se trouve-t-elle au-dessus de nous ?</summary>
        private bool QuelquUnNousPasseDevant()
        {
            var moi = GetCurrentProcessId();
            for (var h = GetWindow(Handle, GwHwndPrev); h != IntPtr.Zero; h = GetWindow(h, GwHwndPrev))
            {
                if (!IsWindowVisible(h)) continue;
                GetWindowThreadProcessId(h, out var pid);
                if (pid != moi) return true;
            }
            return false;
        }

        /// <summary>
        /// Ou s'arrete le panneau. La barre d'aide d'ES commence a 0,9515 x H (HelpStyle) : on
        /// s'arrete AU-DESSUS, en laissant de l'air, sinon les deux lignes d'aide se touchent
        /// et le panneau a l'air de deborder sur celle d'ES.
        /// </summary>
        private static int BasDuPanneau(int hauteurEcran) => (int) (hauteurEcran * (0.9515f - 0.022f));

        /// <summary>L'ecran QUI PORTE ES, et ses dimensions.</summary>
        private static (Rectangle Ecran, int Hauteur, int Largeur) EcranDEs()
        {
            var ecran = Screen.PrimaryScreen;
            try
            {
                var es = Process.GetProcessesByName("emulationstation").FirstOrDefault();
                if (es?.MainWindowHandle is { } h && h != IntPtr.Zero) ecran = Screen.FromHandle(h);
            }
            catch { }
            var b = ecran?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            return (b, b.Height, b.Width);
        }

        private static Color Teinte(string hex, int alpha = 255)
        {
            var (r, g, b, a) = EsMenuStyle.Couleur(hex, alpha);
            return Color.FromArgb(a, r, g, b);
        }

        // ── Le dessin, aux mesures d'ES ──────────────────────────────────────
        //
        // Tout vient de la source d'ES, pas d'un jugement d'oeil :
        //   MenuComponent : titre en gras, CENTRE, TITLE_VERT_PADDING = 0,0637 x hauteur ;
        //   ComponentTab  : onglets en police d'entree, marge 0,015 x LARGEUR de chaque cote,
        //                   bloc degrade pleine hauteur quand ils ont le focus, SOULIGNEMENT de
        //                   0,0025 x largeur sinon, separateurs d'1 px, texte choisi en couleur
        //                   de titre ;
        //   ComponentList : lignes hautes de 1,5 x la taille de police, barre de selection
        //                   pleine largeur en degrade horizontal, texte choisi en selectedColor,
        //                   la valeur ou l'action a DROITE de la ligne ;
        //   ComponentGrid : une bordure d'1 px (menugrid) sous l'en-tete et sous les onglets.

        protected override void OnPaint(PaintEventArgs e)
        {
            var s = _service.Style();
            var c = _service.Lire();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // FreeType chez ES : anticrenelage en niveaux de gris, pas de hinting, glyphes
            // filtres. ClearType « grid fit » epaissit et durcit tout ; AntiAlias s'en approche.
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // Les tailles d'un theme sont des FRACTIONS DE LA HAUTEUR D'ECRAN, pas des pixels.
            float Taille(double fraction) => (float) Math.Max(9, fraction * _hauteurEcran);
            var tailleTitre = Taille(s.TitleFontSize);
            var tailleTexte = Taille(s.TextFontSize);
            var taillePetite = Taille(s.SmallFontSize);
            var taillePied = Taille(s.FooterFontSize);
            var tailleGroupe = Taille(s.GroupFontSize);
            using var policeGroupe = Police(s.GroupFontPath, tailleGroupe, FontStyle.Bold);
            using var policeGras = Police(s.GroupFontPath.Length > 0 ? s.GroupFontPath : s.TitleFontPath, tailleTexte, FontStyle.Bold);
            using var policeTitre = Police(s.TitleFontPath, tailleTitre, FontStyle.Bold);
            using var policeTexte = Police(s.TextFontPath, tailleTexte, FontStyle.Regular);
            using var policePetite = Police(s.SmallFontPath, taillePetite, FontStyle.Regular);
            using var policePied = Police(s.FooterFontPath, taillePied, FontStyle.Regular);

            if (_service._depuisLAppui is { } chrono)
            {
                _service._logger.LogInformation("Classement : panneau VISIBLE {Ms} ms apres l'appui.", chrono.ElapsedMilliseconds);
                _service._depuisLAppui = null;
            }

            DessinerLeCadre(g);

            if (c.Attente.Length > 0)
            {
                DessinerLAttente(g, c, s, policeTexte, tailleTexte);
                return;
            }

            // Le titre : centre, gras, en majuscules, avec l'aeration d'ES (TITLE_VERT_PADDING).
            var hauteurTitre = tailleTitre * 1.5f;
            var y = _hauteurEcran * 0.0637f - hauteurTitre / 2f;
            using (var encre = new SolidBrush(Teinte(s.TitleColor)))
            using (var centre = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            {
                // Le logo NelfePlay puis le titre, centres ensemble : c'est la plateforme qui
                // parle ici, a cote du jeu qu'ES presente.
                var titre = c.Titre.ToUpperInvariant();
                var largeurTitre = g.MeasureString(titre, policeTitre, PointF.Empty, StringFormat.GenericTypographic).Width;
                var logo = _service._glyphes?.Glyphe("nelfeplay", (int) (tailleTitre * 1.15f));
                var largeurLogo = logo is null ? 0 : logo.Width + tailleTitre * 0.45f;
                var x = (Width - largeurTitre - largeurLogo) / 2f;
                if (logo is not null)
                {
                    g.DrawImage(logo, x, y + (hauteurTitre - logo.Height) / 2f, logo.Width, logo.Height);
                    x += largeurLogo;
                }
                g.DrawString(titre, policeTitre, encre, new RectangleF(x, y, largeurTitre + 4, hauteurTitre), centre);
            }
            y = _hauteurEcran * 0.0637f * 2f - hauteurTitre / 2f;
            Bordure(g, s, y);

            y = DessinerLesOnglets(g, c, s, policeTexte, tailleTexte, y);

            // La barre d'actions, puis la ligne d'aide : deux rangees en bas du panneau.
            var basDeLAide = Height - taillePied * 1.5f - tailleTexte * 1.9f - _hauteurEcran * 0.012f;
            if (c.Evenements is { Count: > 0 } evenements)
            {
                DessinerLesEvenements(g, c, s, evenements, policeTexte, policePetite, policeGras, tailleTexte, y, basDeLAide);
            }
            else if (c.Lignes.Count == 0)
            {
                // Le message d'attente ou d'absence, en entree de menu ordinaire.
                using var encre = new SolidBrush(Teinte(s.TextColor));
                using var gauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
                g.DrawString(c.Message.ToUpperInvariant(), policeTexte, encre,
                    new RectangleF(10f, y, Width, tailleTexte * 1.5f), gauche);
            }
            else
            {
                DessinerLesLignes(g, c, s, policeTexte, policePetite, policeGroupe, policeGras, tailleTexte, tailleGroupe, y, basDeLAide);
            }

            DessinerLaBarre(g, c, s, policeTexte, policePetite, tailleTexte, basDeLAide);
            DessinerLAide(g, c, s, policePied, taillePied, basDeLAide + tailleTexte * 1.9f);
        }

        /// <summary>
        /// Le fond du menu. ES pose un nine-patch (frame.png) TEINTE de la couleur de fond, ce
        /// qui le rend invisible sur carbon : un aplat de cette couleur est exactement ce que
        /// l'oeil voit. Le dessiner sans teinte donnait des bords blancs.
        /// </summary>
        /// <summary>
        /// Le fond : un degrade vertical, du presque noir en haut vers le gris du menu d'ES en
        /// bas. Les deux teintes viennent du theme (menubg), assombrie puis telle quelle, pour
        /// que le panneau reste de la meme famille que le menu d'en face.
        /// </summary>
        private void DessinerLeCadre(Graphics g)
        {
            var (r, v, b, _) = EsMenuStyle.Couleur(_service.Style().BackgroundColor);
            Color Teintee(double facteur) => Color.FromArgb(255,
                Math.Clamp((int) (r * facteur), 0, 255), Math.Clamp((int) (v * facteur), 0, 255), Math.Clamp((int) (b * facteur), 0, 255));

            // Le gris du menu au centre, le noir aux deux bords : le panneau ne devient JAMAIS
            // plus clair que le menu d'ES en face, et il se fond dans l'ecran en haut et en bas.
            var sombre = Teintee(0.22);
            var zone = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));
            using var degrade = new LinearGradientBrush(zone, sombre, sombre, LinearGradientMode.Vertical);
            degrade.InterpolationColors = new ColorBlend
            {
                Colors = new[] { sombre, Teintee(1.0), sombre },
                Positions = new[] { 0f, 0.45f, 1f },
            };
            var avant = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(degrade, zone);
            g.SmoothingMode = avant;
        }


        /// <summary>
        /// La boite d'attente d'ES (BusyComponent) : un cadre aux couleurs du menu, centre,
        /// le spinner busy_0..3 qui tourne, et le message en entree de menu.
        /// </summary>
        private void DessinerLAttente(Graphics g, Contenu c, EsMenuStyle s, Font police, float taille)
        {
            var texte = c.Attente.ToUpperInvariant();
            var largeurTexte = g.MeasureString(texte, police, PointF.Empty, StringFormat.GenericTypographic).Width;
            var hauteur = taille * 2.2f;
            var icone = _service._glyphes?.Glyphe("busy_" + (_imageAttente % 4), (int) (taille * 1.1f), Teinte(s.TextColor));
            var largeurIcone = icone?.Width ?? 0;
            var marge = taille * 1.2f;
            var largeur = marge + largeurIcone + (largeurIcone > 0 ? taille * 0.6f : 0) + largeurTexte + marge;
            var zone = new RectangleF((Width - largeur) / 2f, (Height - hauteur) / 2f, largeur, hauteur);

            // Le rappel du DEFI au-dessus de la boite : le mot, puis l'objectif. Le joueur sait ce
            // qu'il va jouer pendant que l'emulateur demarre.
            if (c.AttenteTitre.Length > 0)
            {
                using var policeTitre = new Font(police.FontFamily, taille * 1.6f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var enBas = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
                using var encreTitre = new SolidBrush(Teinte(s.GroupColor));
                using var encreDetail = new SolidBrush(Teinte(s.TitleColor));
                var yDetail = zone.Y - taille * 1.2f;
                if (c.AttenteDetail.Length > 0)
                {
                    g.DrawString(c.AttenteDetail.ToUpperInvariant(), police, encreDetail, new RectangleF(0, yDetail - taille * 1.6f, Width, taille * 1.6f), enBas);
                    yDetail -= taille * 1.9f;
                }
                g.DrawString(c.AttenteTitre.ToUpperInvariant(), policeTitre, encreTitre, new RectangleF(0, yDetail - taille * 2.2f, Width, taille * 2.2f), enBas);
            }

            using (var fond = new SolidBrush(Color.FromArgb(255, Teinte(s.BackgroundColor))))
            using (var chemin = Arrondi(zone, (float) Math.Clamp(s.ButtonCornerSize * _hauteurEcran / 1080d, 4, 24)))
            {
                g.FillPath(fond, chemin);
            }
            var x = zone.X + marge;
            if (icone is not null)
            {
                g.DrawImage(icone, x, zone.Y + (hauteur - icone.Height) / 2f, icone.Width, icone.Height);
                x += icone.Width + taille * 0.6f;
            }
            using var encre = new SolidBrush(Teinte(s.TextColor));
            using var centre = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
            g.DrawString(texte, police, encre, new RectangleF(x, zone.Y, largeurTexte + 4, hauteur), centre);
            _imageAttente++;
        }

        /// <summary>La bordure d'une grille d'ES : 1 px, couleur menugrid. Transparente = rien.</summary>
        private void Bordure(Graphics g, EsMenuStyle s, float y)
            => Aplat(g, Teinte(s.GridSeparatorColor), new RectangleF(0, y, Width, 1f));

        /// <summary>
        /// La rangee d'onglets, comme ComponentTab la dessine. Elle prend TOUTE la largeur du
        /// panneau ; chaque onglet a sa largeur de texte plus les marges d'ES. Quand les onglets
        /// ont le focus, l'onglet courant porte le bloc degrade pleine hauteur ; sinon, le
        /// soulignement. Le texte choisi passe en couleur de titre.
        /// </summary>
        private float DessinerLesOnglets(Graphics g, Contenu c, EsMenuStyle s, Font police, float taille, float y)
        {
            var hauteur = taille * 1.5f;
            var margeOnglet = _largeurEcran * 0.015f;
            var soulignement = Math.Max(2f, _largeurEcran * 0.0025f);
            var couleurTexte = Teinte(s.TextColor);
            var couleurChoisi = Teinte(s.TitleColor);
            var separateur = Teinte(s.SeparatorColor);
            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // Les largeurs d'abord, pour placer la camera : l'onglet courant doit etre visible
            // en entier, et on ne laisse pas de vide a gauche si tout tient.
            var largeurs = c.Onglets.Select(n => g.MeasureString(n.ToUpperInvariant(), police, PointF.Empty, StringFormat.GenericTypographic).Width + 2 * margeOnglet).ToArray();
            var total = largeurs.Sum();
            var debutCourant = largeurs.Take(c.OngletCourant).Sum();
            var finCourant = debutCourant + (c.OngletCourant < largeurs.Length ? largeurs[c.OngletCourant] : 0);
            if (total <= Width) _cameraOnglets = 0;
            else
            {
                if (finCourant - _cameraOnglets > Width) _cameraOnglets = finCourant - Width;
                if (debutCourant < _cameraOnglets) _cameraOnglets = debutCourant;
                _cameraOnglets = Math.Clamp(_cameraOnglets, 0, total - Width);
            }
            g.SetClip(new RectangleF(0, y, Width, hauteur));

            var x = -_cameraOnglets;
            for (var i = 0; i < c.Onglets.Count; i++)
            {
                var nom = c.Onglets[i].ToUpperInvariant();
                var largeur = largeurs[i];
                var actif = i == c.OngletCourant;
                if (actif)
                {
                    // ComponentTab remplit en selectorColor UNI (drawRect couleur -> meme couleur) ;
                    // le degrade, c'est la barre de ligne, pas l'onglet.
                    Aplat(g, Teinte(s.SelectorColor), c.NousAvonsLaMain
                        ? new RectangleF(x, y, largeur, hauteur)
                        : new RectangleF(x, y + hauteur - soulignement, largeur, soulignement));
                }
                using (var encre = new SolidBrush(actif ? couleurChoisi : couleurTexte))
                {
                    g.DrawString(nom, police, encre, new RectangleF(x, y, largeur, hauteur), centre);
                }
                Aplat(g, separateur, new RectangleF(x, y, 1f, hauteur));
                x += largeur;
            }
            Aplat(g, separateur, new RectangleF(x, y, 1f, hauteur));
            g.ResetClip();

            // La porte vers le menu d'ES, a droite : le glyphe d'ES lui-meme.
            if (c.SurLaPorte && c.NousAvonsLaMain)
            {
                var icone = _service._glyphes?.Glyphe("dpad_right", (int) (taille * 1.1f), couleurTexte);
                if (icone is not null)
                {
                    g.DrawImage(icone, Width - margeOnglet - icone.Width, y + (hauteur - icone.Height) / 2f, icone.Width, icone.Height);
                }
            }

            y = (float) Math.Floor(y + hauteur);
            if (c.NousAvonsLaMain)
            {
                // Le trait de couleur qui dit « c'est ici que vous etes » : la couleur de
                // selection, sur toute la largeur, sous la rangee d'onglets - colle a l'onglet,
                // sans le pixel sombre que laissait un remplissage fractionnaire.
                Aplat(g, Teinte(s.SelectorColor), new RectangleF(0, y, Width, soulignement));
                return y + soulignement;
            }
            Bordure(g, s, y);
            return y + 1f;
        }

        /// <summary>
        /// La barre de selection d'ES : un degrade horizontal du theme.
        ///
        /// Les bords sont ARRONDIS A L'ENTIER et l'anticrenelage coupe : un rectangle
        /// antialiase a coordonnees fractionnaires laisse une couture sombre d'un pixel entre
        /// deux surfaces qui se touchent - c'est ce qu'on voyait entre l'onglet et son trait.
        /// </summary>
        private void BarreDeSelection(Graphics g, EsMenuStyle s, RectangleF zone)
        {
            var r = Entier(zone);
            if (r.Width <= 0 || r.Height <= 0) return;
            using var degrade = new LinearGradientBrush(
                new Rectangle(r.X, r.Y, r.Width + 1, r.Height),
                Teinte(s.SelectorColor), Teinte(s.SelectorColorEnd), LinearGradientMode.Horizontal);
            var avant = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(degrade, r);
            g.SmoothingMode = avant;
        }

        /// <summary>Un rectangle cale sur la grille de pixels : pas de couture entre deux aplats.</summary>
        private static Rectangle Entier(RectangleF z)
            => Rectangle.FromLTRB((int) Math.Floor(z.Left), (int) Math.Floor(z.Top), (int) Math.Ceiling(z.Right), (int) Math.Ceiling(z.Bottom));

        /// <summary>Un aplat net, sans anticrenelage : traits, bordures, fonds de section.</summary>
        private static void Aplat(Graphics g, Color couleur, RectangleF zone)
        {
            var r = Entier(zone);
            if (r.Width <= 0 || r.Height <= 0 || couleur.A == 0) return;
            var avant = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using var encre = new SolidBrush(couleur);
            g.FillRectangle(encre, r);
            g.SmoothingMode = avant;
        }

        /// <summary>
        /// Les lignes, comme ComponentList : hautes de 1,5 x la police, la ligne choisie sous la
        /// barre pleine largeur et en selectedColor. Chaque ligne ouvre sur une icone, comme le
        /// menu principal d'ES (etoile pleine sur le podium) ; puis le rang, le joueur et son
        /// origine en petit ; a droite le score, precede du sceau s'il est scelle. Sur la ligne
        /// CHOISIE, les actions se presentent en vrais boutons d'ES avec leur touche. Des
        /// surtitres de section (menugroup) separent le podium du reste.
        /// </summary>
        private void DessinerLesLignes(Graphics g, Contenu c, EsMenuStyle s, Font police, Font petite, Font groupe, Font gras, float taille, float tailleGroupe, float y, float bas)
        {
            var hauteur = taille * 1.5f;
            var hauteurGroupe = tailleGroupe * 2.0f;             // menugroup : lineSpacing 2.0
            var marge = Math.Max(12f, _largeurEcran * 0.014f);  // TEXT_PADDING d'ES
            var couleurTexte = Teinte(s.TextColor);
            var couleurChoisi = Teinte(s.SelectedTextColor);
            var couleurMoi = Teinte(s.TitleColor);
            var couleurGroupe = Teinte(s.GroupColor);
            using var gauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var droite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            // Ce que chaque ligne occupe, surtitre compris, pour faire defiler juste.
            float Occupe(int i) => hauteur + (Surtitre(c, i).Length > 0 ? hauteurGroupe : 0);
            var premier = 0;
            {
                var place = bas - y;
                var fin = c.LigneCourante;
                var somme = 0f;
                for (var i = fin; i >= 0; i--) { somme += Occupe(i); if (somme > place) { premier = i + 1; break; } }
            }
            var yDepart = y;
            var largeurRang = g.MeasureString("#000", police, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.4f;
            var hauteurIcone = (int) (taille * 0.9f);            // 1,25 x la hauteur de lettre
            var etiquette = c.EtiquetteReplay.ToUpperInvariant();
            var premiereLigne = premier;

            for (var i = premier; i < c.Lignes.Count; i++)
            {
                var surtitre = Surtitre(c, i);
                if (surtitre.Length > 0)
                {
                    if (y + hauteurGroupe + hauteur > bas) break;
                    Aplat(g, Color.FromArgb(16, 0, 0, 0), new RectangleF(0, y, Width, hauteurGroupe));
                    using var encreGroupe = new SolidBrush(couleurGroupe);
                    g.DrawString(surtitre.ToUpperInvariant(), groupe, encreGroupe, new RectangleF(marge, y, Width - 2 * marge, hauteurGroupe), gauche);
                    y += hauteurGroupe;
                }
                if (y + hauteur > bas) break;

                var l = c.Lignes[i];
                var choisie = i == c.LigneCourante && c.NousAvonsLaMain;
                if (choisie) BarreDeSelection(g, s, new RectangleF(0, y, Width, hauteur));
                var encre = choisie ? couleurChoisi : (l.CestMoi ? couleurMoi : couleurTexte);
                using var pinceau = new SolidBrush(encre);

                // A droite : le score, le sceau s'il y a lieu, puis les boutons de la ligne choisie.
                // Le score en GRAS : c'est la donnee que l'oeil cherche en premier.
                var valeur = l.Valeur.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                var xDroite = Width - marge;
                g.DrawString(valeur, gras, pinceau, new RectangleF(0, y, xDroite, hauteur), droite);
                xDroite -= g.MeasureString(valeur, gras, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.5f;
                if (l.Scelle)
                {
                    var sceau = _service._glyphes?.Glyphe("nelfe-verified", (int) (taille * 1.0f));
                    if (sceau is not null)
                    {
                        g.DrawImage(sceau, xDroite - sceau.Width, y + (hauteur - sceau.Height) / 2f, sceau.Width, sceau.Height);
                        xDroite -= sceau.Width + taille * 0.5f;
                    }
                }
                xDroite -= taille * 0.7f;

                if (choisie)
                {
                    foreach (var action in c.ActionsDeLaLigne)
                    {
                        xDroite = Bouton(g, s, petite, taille * 0.8f, action.Mot.ToUpperInvariant(), xDroite, y, hauteur, enFocus: true, discret: true);
                        var icone = _service._glyphes?.Glyphe(action.Glyphe, (int) (taille * 1.2f), choisie ? encre : Teinte(s.HelpIconColor));
                        if (icone is not null)
                        {
                            xDroite -= taille * 0.35f;
                            g.DrawImage(icone, xDroite - icone.Width, y + (hauteur - icone.Height) / 2f, icone.Width, icone.Height);
                            xDroite -= icone.Width + taille * 0.8f;
                        }
                    }
                }
                if (etiquette.Length > 0 && l.CestMoi && l.ReplayId is not { Length: > 0 }
                    && c.ReplaysEnPreparation is { } enAttente && enAttente.Contains(l.Valeur))
                {
                    // Le replay est enregistre ICI mais pas encore sur la plateforme : « REPLAY » en
                    // gris et le sablier d'ES qui tourne. Rien a lancer tant qu'il n'est pas arrive.
                    var gris = Color.FromArgb(choisie ? 200 : 150, Teinte(s.TextColor));
                    var sablier = _service._glyphes?.Glyphe("busy_" + (_imageSablier % 4), (int) (taille * 0.9f), gris);
                    if (sablier is not null)
                    {
                        g.DrawImage(sablier, xDroite - sablier.Width, y + (hauteur - sablier.Height) / 2f, sablier.Width, sablier.Height);
                        xDroite -= sablier.Width + taille * 0.3f;
                    }
                    using var encreGrise = new SolidBrush(gris);
                    g.DrawString(etiquette, petite, encreGrise, new RectangleF(0, y, xDroite, hauteur), droite);
                    xDroite -= g.MeasureString(etiquette, petite, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.8f;
                }
                else if (!choisie && l.ReplayId is { Length: > 0 } && etiquette.Length > 0)
                {
                    using var encreEtiquette = new SolidBrush(couleurGroupe);
                    g.DrawString(etiquette, petite, encreEtiquette, new RectangleF(0, y, xDroite, hauteur), droite);
                    xDroite -= g.MeasureString(etiquette, petite, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.8f;
                }

                // A gauche : la coupe du vainqueur, le rang, le joueur, son origine.
                var x = marge;
                if (l.Rang == 1 && _service._policeSymboles is { } coupe)
                {
                    // La coupe de la premiere place, par la police d'icones d'ES (FontAwesome).
                    using var policeCoupe = new Font(coupe, taille * 0.95f, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var encreCoupe = new SolidBrush(choisie ? couleurChoisi : Teinte(s.TitleColor));
                    g.DrawString("\uF091", policeCoupe, encreCoupe, new RectangleF(x, y, hauteurIcone * 1.6f, hauteur), gauche);
                }
                x += hauteurIcone + 10f;

                // Le rang s'ecrit comme sur nelfeplay.com : la colonne « # ». Les trois
                // premiers portent en plus leur ordinal, le meme mot que le podium du site.
                var rang = "#" + l.Rang;
                var policeRang = l.Rang <= 3 ? gras : police;
                g.DrawString(rang, policeRang, pinceau, new RectangleF(x, y, largeurRang, hauteur), gauche);

                // Le mouvement depuis la derniere consultation : fleche BLEUE vers le haut quand le
                // joueur est monte, ORANGE vers le bas quand il est descendu. Couleurs de sens, pas du
                // theme : elles doivent se lire pareil quel que soit le colorset.
                var mouvement = LeaderboardRankHistory.Mouvement(l, c.RangsPrecedents);
                if (mouvement != 0)
                {
                    var cote = taille * 0.42f;
                    var xFleche = x + g.MeasureString(rang, policeRang, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 0.22f;
                    var yMilieu = y + hauteur / 2f;
                    var couleur = mouvement > 0
                        ? (choisie ? Color.White : Color.FromArgb(255, 60, 140, 255))
                        : Color.FromArgb(255, 245, 160, 50);
                    using var encreFleche = new SolidBrush(couleur);
                    var avant = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPolygon(encreFleche, mouvement > 0
                        ? new[] { new PointF(xFleche + cote / 2f, yMilieu - cote * 0.55f), new PointF(xFleche + cote, yMilieu + cote * 0.45f), new PointF(xFleche, yMilieu + cote * 0.45f) }
                        : new[] { new PointF(xFleche, yMilieu - cote * 0.45f), new PointF(xFleche + cote, yMilieu - cote * 0.45f), new PointF(xFleche + cote / 2f, yMilieu + cote * 0.55f) });
                    g.SmoothingMode = avant;
                }
                x += largeurRang;

                // L'etat de suivi, juste avant le pseudo : sur la ligne choisie il annonce ce
                // que fait la touche, sur les autres il rappelle qui l'on suit deja.
                if (l.Poignee.Length > 0)
                {
                    var suivi = choisie ? (c.SuitLaLigne ? c.SuiviMot : c.SuivreMot) : (c.SuitLaLigne && false ? c.SuiviMot : "");
                    var etiquetteSuivi = (choisie ? suivi : (_service._suivis.Contains(l.Poignee) ? c.SuiviMot : "")).ToUpperInvariant();
                    if (etiquetteSuivi.Length > 0)
                    {
                        using var encreSuivi = new SolidBrush(choisie ? couleurChoisi : couleurGroupe);
                        var largeurSuivi = g.MeasureString(etiquetteSuivi, petite, PointF.Empty, StringFormat.GenericTypographic).Width;
                        if (choisie && c.GlypheSuivre.Length > 0)
                        {
                            var touche = _service._glyphes?.Glyphe(c.GlypheSuivre, (int) (taille * 0.9f), couleurChoisi);
                            if (touche is not null)
                            {
                                g.DrawImage(touche, x, y + (hauteur - touche.Height) / 2f, touche.Width, touche.Height);
                                x += touche.Width + taille * 0.2f;
                            }
                        }
                        g.DrawString(etiquetteSuivi, petite, encreSuivi, new RectangleF(x, y, largeurSuivi + 4, hauteur), gauche);
                        x += largeurSuivi + taille * 0.5f;
                    }
                }

                // Le pseudo en CAPITALES, comme les entrees d'ES.
                var nom = (l.Joueur.Length > 0 ? l.Joueur : "?").ToUpperInvariant();
                var largeurNom = Math.Min(g.MeasureString(nom, police, PointF.Empty, StringFormat.GenericTypographic).Width + 2, Math.Max(10, xDroite - x));
                g.DrawString(nom, police, pinceau, new RectangleF(x, y, largeurNom, hauteur), gauche);
                x += largeurNom + taille * 0.6f;

                // Le monde du record, par la police FontAwesome d'ES : maison, immeuble (salle
                // verifiee) ou trophee (contest). Un pictogramme que le joueur lit sans legende.
                var monde = l.Monde switch { "station" => "\uF1AD", "stream" => "\uF091", "home" => "\uF015", _ => "" };
                if (monde.Length > 0 && _service._policeSymboles is { } symboles && xDroite - x > taille * 2)
                {
                    using var policeSymboles = new Font(symboles, taille * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var encreMonde = new SolidBrush(choisie ? couleurChoisi : couleurTexte);
                    g.DrawString(monde, policeSymboles, encreMonde, new RectangleF(x, y, taille * 1.2f, hauteur), gauche);
                    x += taille * 1.1f;
                }

                y += hauteur;
            }

            // La barre de defilement : elle dit qu'il y a plus de scores que l'ecran n'en
            // montre, et ou l'on se trouve. ES en dessine une pour les memes raisons.
            var affichees = Math.Max(1, c.LigneCourante - premiereLigne + 1);
            if (c.Lignes.Count > affichees || premiereLigne > 0)
            {
                var hautDeListe = bas - (bas - yDepart);
                var piste = new RectangleF(Width - 3f, yDepart, 3f, Math.Max(1, bas - yDepart));
                Aplat(g, Color.FromArgb(40, Teinte(s.TextColor)), piste);
                var part = Math.Min(1f, (float) affichees / c.Lignes.Count);
                var hauteurCurseur = Math.Max(taille, piste.Height * part);
                var progres = c.Lignes.Count <= 1 ? 0f : (float) c.LigneCourante / (c.Lignes.Count - 1);
                var yCurseur = piste.Y + (piste.Height - hauteurCurseur) * progres;
                Aplat(g, Teinte(s.SelectorColor), new RectangleF(piste.X, yCurseur, piste.Width, hauteurCurseur));
                _ = hautDeListe;
            }
        }

        /// <summary>
        /// Les lignes de l'onglet LIVE & CONTEST, sur la meme grille que les scores : l'etiquette
        /// (LIVE en couleur de selection, ou le nom du contest), le joueur en capitales, la machine
        /// en petit, et sur la ligne choisie le bouton REJOINDRE avec sa touche.
        /// </summary>
        private void DessinerLesEvenements(Graphics g, Contenu c, EsMenuStyle s, IReadOnlyList<Evenement> evenements,
            Font police, Font petite, Font gras, float taille, float y, float bas)
        {
            var hauteur = taille * 1.5f;
            var marge = Math.Max(12f, _largeurEcran * 0.014f);
            var place = Math.Max(1, (int) ((bas - y) / hauteur));
            var premier = Math.Clamp(c.LigneCourante - place + 1, 0, Math.Max(0, evenements.Count - place));
            using var gauche = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var droite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var largeurEtiquette = Math.Min(Width * 0.34f, evenements.Max(e => g.MeasureString(e.Etiquette.ToUpperInvariant(), gras, PointF.Empty, StringFormat.GenericTypographic).Width) + taille);

            for (var i = premier; i < evenements.Count && i < premier + place; i++)
            {
                var e = evenements[i];
                var choisie = i == c.LigneCourante && c.NousAvonsLaMain;
                if (choisie) BarreDeSelection(g, s, new RectangleF(0, y, Width, hauteur));
                var encre = choisie ? Teinte(s.SelectedTextColor) : Teinte(s.TextColor);
                using var pinceau = new SolidBrush(encre);

                var xDroite = Width - marge;
                if (choisie && c.Rejoindre.Length > 0)
                {
                    xDroite = Bouton(g, s, petite, taille * 0.8f, c.Rejoindre.ToUpperInvariant(), xDroite, y, hauteur, enFocus: true, discret: true);
                    var touche = c.GlypheRejoindre.Length > 0 ? _service._glyphes?.Glyphe(c.GlypheRejoindre, (int) (taille * 1.1f), encre) : null;
                    if (touche is not null)
                    {
                        xDroite -= taille * 0.35f;
                        g.DrawImage(touche, xDroite - touche.Width, y + (hauteur - touche.Height) / 2f, touche.Width, touche.Height);
                        xDroite -= touche.Width;
                    }
                    xDroite -= taille * 0.8f;
                }

                // L'etiquette : LIVE en couleur de selection (rouge-bleu du theme), sinon le contest.
                using (var encreEtiquette = new SolidBrush(choisie ? encre : (e.EnDirect ? Teinte(s.SelectorColor) : Teinte(s.GroupColor))))
                {
                    g.DrawString(e.Etiquette.ToUpperInvariant(), gras, encreEtiquette, new RectangleF(marge, y, largeurEtiquette, hauteur), gauche);
                }
                var x = marge + largeurEtiquette;
                var qui = e.Qui.ToUpperInvariant();
                var largeurQui = Math.Min(g.MeasureString(qui, police, PointF.Empty, StringFormat.GenericTypographic).Width + 2, Math.Max(10, xDroite - x));
                g.DrawString(qui, police, pinceau, new RectangleF(x, y, largeurQui, hauteur), gauche);
                x += largeurQui + taille * 0.6f;
                if (e.Detail.Length > 0 && xDroite - x > taille * 2)
                {
                    g.DrawString(e.Detail, petite, pinceau, new RectangleF(x, y, xDroite - x, hauteur), gauche);
                }
                y += hauteur;
            }
        }

        /// <summary>Le surtitre qui precede la ligne i : PODIUM avant le rang 1, CLASSEMENT avant le 4e.</summary>
        private static string Surtitre(Contenu c, int i)
        {
            if (i < 0 || i >= c.Lignes.Count) return "";
            var rang = c.Lignes[i].Rang;
            if (i == 0 && rang <= 3) return c.SurtitrePodium;
            if (rang == 4 || (i == 0 && rang > 3)) return c.SurtitreClassement;
            return "";
        }

        /// <summary>
        /// Un bouton d'ES (ButtonComponent) : un contour arrondi aux coins du theme, le texte
        /// avec TEXT_PADDING de chaque cote, la largeur d'au moins « DELETE ». Gris au repos ;
        /// en focus, contour et texte en blanc (le fond bleu de la ligne porte deja la couleur
        /// de selection). Dessine a droite de xDroite, rend le nouveau bord gauche.
        /// </summary>
        private float Bouton(Graphics g, EsMenuStyle s, Font police, float taille, string texte, float xDroite, float y, float hauteurLigne, bool enFocus, bool discret = false, float? padding = null)
        {
            // Un bouton DE LIGNE reste discret : il accompagne un score, il ne le domine pas.
            // Les autres gardent la construction d'ES (TEXT_PADDING, largeur minimale de « DELETE »),
            // a l'echelle de la taille demandee.
            var marge = padding ?? (discret ? taille * 0.6f : Math.Max(12f, _largeurEcran * 0.014f));
            var largeurTexte = g.MeasureString(texte, police, PointF.Empty, StringFormat.GenericTypographic).Width;
            var largeurMin = discret ? 0f : g.MeasureString("DELETE", police, PointF.Empty, StringFormat.GenericTypographic).Width + marge;
            var largeur = Math.Max(largeurTexte + marge, largeurMin);
            var hauteur = discret ? taille * 1.15f : taille * 1.4f;
            var zone = new RectangleF(xDroite - largeur, y + (hauteurLigne - hauteur) / 2f, largeur, hauteur);
            var couleur = enFocus ? Opaque(Teinte(s.SelectedTextColor)) : Opaque(Teinte(s.TextColor));
            var rayon = (float) Math.Clamp(s.ButtonCornerSize * _hauteurEcran / 1080d, 4, 24);

            using (var chemin = Arrondi(zone, rayon))
            using (var contour = new Pen(couleur, Math.Max(1.5f, _hauteurEcran / 720f)))
            {
                g.DrawPath(contour, chemin);
            }
            using var encre = new SolidBrush(couleur);
            using var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(texte, police, encre, zone, centre);
            return zone.Left;
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

        /// <summary>
        /// La barre d'actions du panneau : a gauche le defi, qui vaut pour le jeu entier et non
        /// pour une ligne ; a droite MA PLACE et ce qu'il manque pour gagner un rang. Les deux
        /// se lisent d'un coup d'oeil, sans quitter le classement.
        /// </summary>
        private void DessinerLaBarre(Graphics g, Contenu c, EsMenuStyle s, Font police, Font petite, float taille, float y)
        {
            if (c.Defier.Length == 0 && c.MaPlace.Length == 0) return;
            var hauteur = taille * 1.9f;
            var marge = Math.Max(12f, _largeurEcran * 0.014f);
            Bordure(g, s, y);

            if (c.Defier.Length > 0)
            {
                // Un cran plus petit que les boutons d'ES : le defi invite, il ne doit pas ecraser le
                // classement qu'il surplombe.
                var x = marge;
                var tailleBouton = taille * 0.82f;
                var touche = _service._glyphes?.Glyphe(c.GlypheDefier, (int) (taille * 1.05f), Teinte(s.HelpIconColor));
                if (touche is not null)
                {
                    g.DrawImage(touche, x, y + (hauteur - touche.Height) / 2f, touche.Width, touche.Height);
                    x += touche.Width + taille * 0.3f;
                }
                var texte = c.Defier.ToUpperInvariant();
                var largeur = Math.Max(
                    g.MeasureString(texte, petite, PointF.Empty, StringFormat.GenericTypographic).Width,
                    g.MeasureString("DELETE", petite, PointF.Empty, StringFormat.GenericTypographic).Width) + tailleBouton * 1.2f;
                Bouton(g, s, petite, tailleBouton, texte, x + largeur, y + (hauteur - tailleBouton * 1.4f) / 2f, tailleBouton * 1.4f, enFocus: true, discret: false, padding: tailleBouton * 1.2f);
            }

            if (c.MaPlace.Length > 0)
            {
                using var encre = new SolidBrush(Teinte(s.GroupColor));
                using var droite = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                g.DrawString(c.MaPlace.ToUpperInvariant(), petite, encre, new RectangleF(0, y, Width - marge, hauteur), droite);
            }
        }

        /// <summary>
        /// Notre ligne d'aide, dans le style de la barre d'aide d'ES et alignee sur elle
        /// (HelpStyle : 0,012 x W) : un glyphe, un mot, et on avance. Elle se tient juste
        /// AU-DESSUS de la barre d'ES, jamais a sa place.
        /// </summary>
        private void DessinerLAide(Graphics g, Contenu c, EsMenuStyle s, Font police, float taille, float y)
        {
            var hauteur = taille * 1.5f;
            var x = _largeurEcran * 0.012f;
            var couleur = Teinte(s.FooterColor);
            var couleurIcone = Teinte(s.HelpIconColor);   // la barre d'aide d'ES colore ses pictogrammes a part
            using var encre = new SolidBrush(couleur);
            using var centre = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
            foreach (var aide in c.Aides)
            {
                var icone = _service._glyphes?.Glyphe(aide.Glyphe, (int) (taille * 1.2f), couleurIcone);
                if (icone is not null)
                {
                    g.DrawImage(icone, x, y + (hauteur - icone.Height) / 2f, icone.Width, icone.Height);
                    x += icone.Width + taille * 0.35f;
                }
                var mot = aide.Mot.ToUpperInvariant();
                g.DrawString(mot, police, encre, new RectangleF(x, y, Width, hauteur), centre);
                x += g.MeasureString(mot, police, PointF.Empty, StringFormat.GenericTypographic).Width + taille * 1.2f;
            }
        }

        private Font Police(string chemin, float taille, FontStyle variante)
        {
            if (chemin.Length > 0 && _service._familles.TryGetValue(chemin, out var famille))
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
                _minuteur.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr apres, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attacher);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr h, int index, int valeur);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private static readonly IntPtr HwndTopmost = new(-1);
        private static readonly IntPtr HwndNoTopmost = new(-2);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const int GwlExStyle = -20;
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint commande);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint GwHwndPrev = 3;   // la fenetre juste AU-DESSUS dans l'ordre z
        private const int WsExNoActivate = 0x08000000;
    }
}
