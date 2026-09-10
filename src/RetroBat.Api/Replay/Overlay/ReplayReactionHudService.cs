using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Input;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Overlay;

/// <summary>
/// HUD des RÉACTIONS (R4, affichage) : fenêtre plein-cadre TRANSPARENTE par pixel (layered,
/// click-through, sans focus) au-dessus de la zone de jeu. Deux effets :
///   • JAUGE DE CHARGE pendant qu'on tient un bouton : anneau qui se remplit, emoji + mot du
///     niveau atteint, couleur de la famille ;
///   • au RELÂCHÉ, la réaction est « envoyée » en TUNNEL : une nuée d'emojis jaillit GROS des
///     bords de l'écran et converge vers le CENTRE en rétrécissant et s'estompant (point de
///     fuite) — le centre (l'action) reste lisible. Un mot central pulse en surimpression.
/// La fenêtre ne rend QUE quand quelque chose est actif (jauge, particules ou mots vivants).
/// </summary>
public sealed class ReplayReactionHudService : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly ReplayReactionService _reactions;
    private readonly ReplayPlaybackService _playback;
    private readonly ReplayStore _store;
    private readonly RetroBat.Api.Replay.Social.ReplaySocialStore _social;
    private readonly ILogger<ReplayReactionHudService> _logger;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly NelfePlayScoringSessionService? _session;

    private readonly object _sync = new();
    private Thread? _uiThread;
    private ApplicationContext? _appContext;
    private Control? _dispatcher;
    private HudForm? _form;
    private ReplayReactionSprites? _sprites; // créé sur le thread UI du HUD (pas de partage cross-thread GDI+)
    private IDisposable? _sub;

    private readonly RetroBat.Api.Netplay.LiveSpectateState _direct;
    private readonly RetroBat.Api.Netplay.LiveCrowdModel _foule;

    public ReplayReactionHudService(IEventBus bus, ReplayReactionService reactions,
        ReplayPlaybackService playback, ReplayStore store,
        RetroBat.Api.Replay.Social.ReplaySocialStore social, ILogger<ReplayReactionHudService> logger,
        IOptionsMonitor<ApiExposeOptions> options,
        RetroBat.Api.Netplay.LiveSpectateState direct,
        RetroBat.Api.Netplay.LiveCrowdModel foule,
        NelfePlayScoringSessionService? session = null)
    {
        _bus = bus; _reactions = reactions; _playback = playback; _store = store; _social = social;
        _logger = logger; _options = options; _session = session;
        _direct = direct; _foule = foule;
    }

    /// <summary>
    /// La langue des mots de réaction, résolue par le mécanisme EXISTANT de la borne :
    /// le joueur identifié d'abord, la borne ensuite, l'anglais en dernier recours.
    /// Relue à chaque affichage — un joueur peut arriver entre deux réactions.
    /// </summary>
    private string Locale() => CabinetAnnounceText.Resolve(
        _session?.Get()?.Locale,
        RetroBat.Domain.Services.ApiExposeProfileResolver.ResolveLanguageCode(
            null, _options.CurrentValue.ApiSettings.LanguageProfile));

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        EnsureUiThreadStarted(stoppingToken);
        _sub = _bus.Subscribe<EventEnvelope>(OnBusEvent);
        _logger.LogInformation("Replay HUD réactions : service initialisé.");
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sub?.Dispose();
        CloseUiThread();
        await base.StopAsync(cancellationToken);
    }

    private void OnBusEvent(EventEnvelope e)
    {
        Control? dispatcher; HudForm? form;
        lock (_sync) { dispatcher = _dispatcher; form = _form; }
        if (dispatcher is null || form is null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;

        if (string.Equals(e.Type, "replay.reaction", StringComparison.Ordinal) && e.Payload is ReplayReaction r)
        {
            try { dispatcher.BeginInvoke(new Action(() => form.AddReaction(r.Reaction, r.Level, r.Chord))); } catch { }
        }
        else if (string.Equals(e.Type, "replay.finished", StringComparison.Ordinal))
        {
            try { dispatcher.BeginInvoke(new Action(form.ClearAll)); } catch { }
        }
    }

    private void EnsureUiThreadStarted(CancellationToken ct)
    {
        lock (_sync) { if (_uiThread != null) return; }
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var context = new ApplicationContext();
            var dispatcher = new Control(); dispatcher.CreateControl();
            var sprites = new ReplayReactionSprites(_logger); // sur CE thread UI
            var form = new HudForm(() => _reactions.GetCharge(), sprites,
                () => _playback.GetState(),
                () => _reactions.GetAvailability(),
                // La FOULE : « regarde-t-on un direct ? » et « qu'y a-t-il a dessiner ? ». Deux
                // delegues plutot qu'une reference : la fenetre vit sur son propre thread UI, et
                // ne doit rien detenir qu'elle pourrait lire a contretemps.
                () => _direct.Actif,
                maintenant => _foule.Relever(maintenant),
                // R9 : le journal d'ici, PLUS les evenements signes recus d'ailleurs. C'est ce qui
                // fait apparaitre les reactions des autres spectateurs pendant la lecture, sans
                // qu'aucune d'elles n'ait a etre crue sur parole.
                id => _social.Display(id, _store.ReadReactions(id)), Locale);
            lock (_sync) { _appContext = context; _dispatcher = dispatcher; _form = form; _sprites = sprites; }
            ready.Set();
            Application.Run(context);
            lock (_sync) { _form?.Dispose(); _form = null; _dispatcher?.Dispose(); _dispatcher = null; _appContext = null; _sprites?.Dispose(); _sprites = null; }
        })
        { IsBackground = true, Name = "APIExpose.ReplayReactionHud" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        lock (_sync) { _uiThread = thread; }
        try { ready.Wait(ct); } catch (OperationCanceledException) { }
    }

    private void CloseUiThread()
    {
        ApplicationContext? context;
        lock (_sync) context = _appContext;
        try { context?.ExitThread(); } catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    private sealed class HudForm : Form
    {
        // La planche = 2 DESIGNS de mascotte × 3 niveaux : colonnes 0-2 = design gauche, 3-5 = lapin.
        // On n'utilise QU'UN design (ses 3 colonnes = les 3 niveaux). 0 = gauche, 3 = lapin.
        private const int DesignBase = 0;

        private const int BarHeight = 118;    // la barre d'info occupe le bas ; on pose la jauge au-dessus
        private const int ActiveTickMs = 40;  // 25 fps : uniquement pendant une animation (nuée/jauge/bulle)
        private const int IdleTickMs = 500;   // 2 fps : repos et lecture-sans-réaction (rien à animer)
        private const int PartLifeMs = 620;   // vie COURTE d'un sprite du tunnel (vagues répétées = dynamique)
        private const int LabelLifeMs = 1250; // durée du mot central
        private const int MaxParticles = 320;

        private const int BarSidePadding = 30; // == SidePadding de la barre (mapping timeline identique)
        private const long MinBubbleMs = 1500; // debounce : durée mini avant de changer de bulle
        private const long BubbleLifeMs = 2600; // durée d'affichage d'une bulle

        private readonly Func<ReplayReactionService.ChargeSnapshot> _charge;
        private readonly ReplayReactionSprites? _sprites;
        private readonly Func<ReplayPlaybackService.StateSnapshot> _state;
        private readonly Func<ReplayReactionService.Availability> _avail;
        private readonly Func<string, IReadOnlyList<ReplayReaction>> _loadReactions;
        private readonly Func<string> _locale; // langue résolue (joueur→borne→en), relue à chaque réaction
        private readonly System.Windows.Forms.Timer _timer;
        private long _lastRenderMs;   // throttle du compositing : 1 fps au repos, 25 fps si animation

        // bulle « réaction des autres » au passage du curseur (debounce, une seule à la fois)
        private string? _bubbleReplayId;
        private bool _bMarkersLoaded;   // latch : une seule lecture disque des réactions par replay
        private IReadOnlyList<ReactionMarker> _bMarkers = Array.Empty<ReactionMarker>();
        private ReactionMarker? _activeBubble;
        private long _bubbleShownMs;
        private long _bubbleFrame, _bubbleEnd;
        private readonly List<Particle> _parts = new();
        private readonly List<Label> _labels = new();
        private readonly Random _rng = new();
        private readonly Dictionary<int, Font> _emojiFonts = new();
        private readonly Dictionary<int, Font> _wordFonts = new();
        private Bitmap? _buffer;          // GDI+ dessine dedans (mémoire partagée avec le DIB)
        private IntPtr _memDc, _dib, _oldSel;
        private Rectangle _region;
        private readonly Func<bool> _enDirect;
        private readonly Func<long, RetroBat.Api.Netplay.LiveCrowdModel.Instantane> _foule;

        private sealed class Particle { public string Family = ""; public int Col; public string Emoji = ""; public Color Color; public int Level; public long Born; public double Angle; public float Radial; }
        private sealed class Label { public string Word = ""; public Color Color; public int Level; public long Born; }

        public HudForm(Func<ReplayReactionService.ChargeSnapshot> charge, ReplayReactionSprites? sprites,
            Func<ReplayPlaybackService.StateSnapshot> state, Func<ReplayReactionService.Availability> avail,
            Func<bool> enDirect,
            Func<long, RetroBat.Api.Netplay.LiveCrowdModel.Instantane> foule,
            Func<string, IReadOnlyList<ReplayReaction>> loadReactions, Func<string> locale)
        {
            _charge = charge;
            _sprites = sprites;
            _state = state;
            _avail = avail;
            _enDirect = enDirect;
            _foule = foule;
            _loadReactions = loadReactions;
            _locale = locale;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            _timer = new System.Windows.Forms.Timer { Interval = IdleTickMs }; // démarre lent ; monte à 25 fps si animation
            _timer.Tick += (_, _) => OnTick();
            _timer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020,
                    WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void AddReaction(string family, int level, bool chord)
        {
            _timer.Interval = ActiveTickMs; // une réaction arrive → repasse à 25 fps tout de suite (nuée fluide)
            var (emoji, word) = ReplayReactionText.Resolve(family, level, _locale());
            var color = ReplayReactionText.ColorOf(family);
            // Une réaction = l'icône du design choisi au niveau atteint (col = base + niveau).
            var col = DesignBase + Math.Clamp(level - 1, 0, 2);

            var waves = chord ? 3 : 2;                 // plusieurs vagues = pulsé, façon anime
            var perWave = chord ? 7 : 4 + level;       // moins nombreux (car plus gros)
            var now = NowMs();
            for (var w = 0; w < waves; w++)
            {
                var wborn = now + w * 150;             // vagues décalées
                for (var i = 0; i < perWave; i++)
                {
                    var ang = i / (double)perWave * Math.PI * 2 + w * 0.35 + (_rng.NextDouble() * 0.3 - 0.15);
                    _parts.Add(new Particle
                    {
                        Family = family, Col = col, Emoji = emoji, Color = color, Level = level, Angle = ang,
                        Radial = 1.12f + (float)_rng.NextDouble() * 0.14f,  // > 1 = DÉMARRE HORS ÉCRAN
                        Born = wborn + _rng.Next(0, 60),
                    });
                }
            }
            _labels.Add(new Label { Word = word, Color = color, Level = level, Born = now });
            if (_parts.Count > MaxParticles) _parts.RemoveRange(0, _parts.Count - MaxParticles);
            if (_labels.Count > 6) _labels.RemoveRange(0, _labels.Count - 6);
        }

        public void ClearAll() { _parts.Clear(); _labels.Clear(); }

        private void OnTick()
        {
            try
            {
                var charge = SafeCharge();
                var now = NowMs();
                _parts.RemoveAll(p => now - p.Born > PartLifeMs);
                _labels.RemoveAll(l => now - l.Born > LabelLifeMs);

                var st = SafeState();
                var playing = string.Equals(st.Mode, "replay", StringComparison.Ordinal);
                PumpBubble(st, playing);

                // Pendant un DIRECT, la facade sert aussi : la legende doit se voir, et la foule
                // avec elle. Sans ca le HUD ne paraissait que le temps d'une animation, donc la
                // legende clignotait et la foule n'existait pas entre deux reactions.
                var enDirect = SafeEnDirect();

                var active = playing || enDirect || charge.Active || _parts.Count > 0 || _labels.Count > 0;
                if (!active)
                {
                    if (Visible) Hide();
                    if (_timer.Interval != IdleTickMs) _timer.Interval = IdleTickMs; // repos : le timer ralentit (pas 25 fps pour rien)
                    return;
                }

                if (!EnsureRegion()) return;
                if (!Visible) Show();

                // Le coût = compositing DWM d'une fenêtre layered PLEIN ÉCRAN par-dessus RetroArch.
                // On ne recompose donc à 25 fps QUE quand quelque chose BOUGE (nuée/mot/jauge/bulle) ;
                // sinon 1 fps suffit (légende/attente quasi statiques) → RetroArch garde le GPU, fin
                // du son haché. (Diagnostic borne i3-N305 : 2 overlays layered 25 fps = coupable.)
                // La foule ne compte comme animation que quand elle BOUGE (saut, vol, etiquette).
                // Des silhouettes immobiles n'ont aucune raison de couter 25 images par seconde :
                // c'est le compositing DWM d'une fenetre layered plein ecran qui coute, et il a
                // deja hache le son sur une borne modeste.
                var fouleInstantanee = enDirect ? SafeFoule(now) : null;
                var fouleAnime = fouleInstantanee is not null
                    && (fouleInstantanee.Vols.Count > 0
                        || fouleInstantanee.Etiquettes.Count > 0
                        || fouleInstantanee.Sauts.Count > 0);

                var animating = charge.Active || _parts.Count > 0 || _labels.Count > 0
                    || _activeBubble is not null || fouleAnime;
                if (animating || now - _lastRenderMs >= 1000)
                {
                    RenderFrame(charge, now, playing, fouleInstantanee);
                    PushLayered(_region.Location, _region.Size);
                    _lastRenderMs = now;
                }
                // Cadence dynamique : 25 fps SEULEMENT pendant une animation (nuée fluide) ; sinon
                // 2 fps même en pleine lecture (rien ne bouge, seul le rendu 1 fps de la légende compte).
                var wantMs = animating ? ActiveTickMs : IdleTickMs;
                if (_timer.Interval != wantMs) _timer.Interval = wantMs;
                AssertTopmost();
            }
            catch
            {
                // Un pépin de rendu ne doit JAMAIS faire tomber l'appli : on saute la frame.
            }
        }

        private ReplayReactionService.ChargeSnapshot SafeCharge()
        {
            try { return _charge(); } catch { return new ReplayReactionService.ChargeSnapshot(false, "", 0, 0, false, 0); }
        }

        private ReplayPlaybackService.StateSnapshot SafeState()
        {
            try { return _state(); } catch { return new ReplayPlaybackService.StateSnapshot("none", "idle", null, 0, null, null, null, false, null, 60, null, null); }
        }

        // Bulle « réaction des autres » : au passage du curseur sur un cluster majoritaire, une seule
        // bulle (nom + icône + mot), avec debounce (durée mini avant de changer, priorité au dernier).
        private void PumpBubble(ReplayPlaybackService.StateSnapshot st, bool playing)
        {
            if (!string.Equals(st.ReplayId, _bubbleReplayId, StringComparison.Ordinal))
            {
                _bubbleReplayId = st.ReplayId;
                _bMarkers = Array.Empty<ReactionMarker>();
                _bMarkersLoaded = false;
                _activeBubble = null;
            }
            var end = st.ReplayEndFrame ?? 0;
            // Latch : UNE seule lecture disque (ReadReactions JSONL) par replay — même si 0 réaction
            // (sinon _bMarkers.Count==0 relançait la lecture 25×/s).
            if (!_bMarkersLoaded && st.ReplayId is not null && end > 0)
            {
                _bMarkersLoaded = true;
                try { _bMarkers = ReplayReactionText.Clusterize(_loadReactions(st.ReplayId), end, 24); }
                catch { _bMarkers = Array.Empty<ReactionMarker>(); }
            }
            if (!playing || end <= 0 || _bMarkers.Count == 0) { _activeBubble = null; return; }

            var now = NowMs();
            var frame = st.Frame;
            var window = Math.Max(1, end / 48);
            ReactionMarker? near = null; var bestD = long.MaxValue;
            foreach (var m in _bMarkers) { var d = Math.Abs(m.Frame - frame); if (d < window && d < bestD) { bestD = d; near = m; } }

            if (near is ReactionMarker nm &&
                (_activeBubble is not ReactionMarker ab || (ab.Frame != nm.Frame && now - _bubbleShownMs > MinBubbleMs)))
            {
                // FIXE la bulle sur la frame du marqueur (elle ne bouge plus une fois affichée = lisible).
                _activeBubble = nm; _bubbleShownMs = now; _bubbleFrame = nm.Frame;
            }
            if (_activeBubble is not null && now - _bubbleShownMs > BubbleLifeMs) _activeBubble = null;

            _bubbleEnd = end;
        }

        private void DrawBubble(Graphics g, long now)
        {
            if (_activeBubble is not ReactionMarker m || _sprites is not { Ok: true } || _bubbleEnd <= 0) return;
            var t = (now - _bubbleShownMs) / (float)BubbleLifeMs;
            if (t is < 0 or >= 1) return;
            var alpha = t < 0.85f ? 1f : 1f - (t - 0.85f) / 0.15f;
            var a = (int)(255 * Math.Clamp(alpha, 0f, 1f));

            var (_, word) = ReplayReactionText.Resolve(m.Family, m.Level, _locale());
            var color = ReplayReactionText.ColorOf(m.Family);
            using var nameF = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var wordF = new Font("Segoe UI Semibold", 14f, FontStyle.Regular, GraphicsUnit.Pixel);

            const int icon = 34, padX = 14, padY = 9, gap = 12;
            var nameW = g.MeasureString(m.Name, nameF).Width;
            var wordW = g.MeasureString(word, wordF).Width;
            var textW = Math.Max(nameW, wordW);
            var bw = padX + icon + gap + (int)textW + padX;
            const int bh = 52;

            var pad = BarSidePadding;
            var cx = pad + (_region.Width - 2 * pad) * (float)Math.Clamp(_bubbleFrame / (double)_bubbleEnd, 0, 1);
            var bx = Math.Clamp(cx - bw / 2f, 8f, _region.Width - bw - 8f);
            var by = _region.Height - BarHeight - bh - 12f;

            using (var bg = new SolidBrush(Color.FromArgb((int)(a * 0.92f), 12, 20, 34)))
                FillRounded(g, bg, bx, by, bw, bh, 12);
            using (var accent = new SolidBrush(Color.FromArgb(a, color)))
                FillRounded(g, accent, bx, by, 5, bh, 2); // liseré couleur de famille
            // petite pointe vers le curseur
            var tipX = Math.Clamp(cx, bx + 16, bx + bw - 16);
            using (var tail = new SolidBrush(Color.FromArgb((int)(a * 0.92f), 12, 20, 34)))
                g.FillPolygon(tail, new[] { new PointF(tipX - 9, by + bh - 1), new PointF(tipX + 9, by + bh - 1), new PointF(tipX, by + bh + 10) });

            _sprites.Draw(g, m.Family, DesignBase + Math.Clamp(m.Level - 1, 0, 2), bx + padX + icon / 2f, by + bh / 2f, icon, alpha);
            var tx = bx + padX + icon + gap;
            using (var nb = new SolidBrush(Color.FromArgb(a, color)))
                g.DrawString(m.Name, nameF, nb, tx, by + padY);
            using (var wb = new SolidBrush(Color.FromArgb(a, 220, 230, 245)))
                g.DrawString(word, wordF, wb, tx, by + padY + 22);
        }

        private bool EnsureRegion()
        {
            var screen = ResolveGameScreen();
            if (screen is null) return false;
            var region = screen.Value; // plein écran de jeu (le tunnel part des bords)
            if (region == _region && _buffer != null) return true;

            DisposeBuffer();
            _region = region;
            Bounds = region;

            // DIB section top-down 32 bpp : GDI+ dessine dedans, on présente le MÊME memDC à chaque
            // frame (pas de GetHbitmap plein écran par frame = fluide). PArgb = premultiplié correct
            // pour UpdateLayeredWindow.
            var bmi = new BITMAPINFOHEADER
            {
                biSize = 40, biWidth = region.Width, biHeight = -region.Height,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            _dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
            _memDc = CreateCompatibleDC(IntPtr.Zero);
            _oldSel = SelectObject(_memDc, _dib);
            _buffer = new Bitmap(region.Width, region.Height, region.Width * 4,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bits);
            return true;
        }

        private void DisposeBuffer()
        {
            _buffer?.Dispose(); _buffer = null;
            if (_memDc != IntPtr.Zero) { if (_oldSel != IntPtr.Zero) SelectObject(_memDc, _oldSel); DeleteDC(_memDc); }
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            _memDc = _dib = _oldSel = IntPtr.Zero;
        }

        private bool SafeEnDirect()
        {
            try { return _enDirect(); } catch { return false; }
        }

        private RetroBat.Api.Netplay.LiveCrowdModel.Instantane? SafeFoule(long now)
        {
            try { return _foule(now); } catch { return null; }
        }

        private void RenderFrame(
            ReplayReactionService.ChargeSnapshot charge, long now, bool playing,
            RetroBat.Api.Netplay.LiveCrowdModel.Instantane? foule)
        {
            using var g = Graphics.FromImage(_buffer!);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.Bilinear;      // rapide pour les sprites mis à l'échelle
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            var av = SafeAvail();
            // La foule EN PREMIER : elle est le fond de scene, tout le reste passe devant.
            if (foule is not null) DrawCrowd(g, foule, now);
            if (playing || foule is not null) DrawLegend(g, av);
            foreach (var p in _parts) DrawParticle(g, p, now);
            foreach (var l in _labels) DrawLabel(g, l, now);
            // Jauge de charge seulement s'il reste du budget (sinon la maintenir ne mène à rien).
            if (charge.Active && av.Budget > 0) DrawGauge(g, charge);
            if (playing) DrawBubble(g, now);
        }

        /// <summary>
        /// La FOULE, en bas de l'ecran, comme sur le replay.
        ///
        /// Trois rangees de silhouettes pixel qui se chevauchent, comme un public vu de loin.
        /// C'est le REMPLISSAGE qui est la metrique : on lit la taille de l'audience sans qu'un
        /// chiffre soit ecrit. Les rangees du fond sont plus petites et plus sombres, ce qui
        /// donne la profondeur.
        ///
        /// Le placement, la teinte, la duree des etiquettes et la hauteur des sauts viennent
        /// tous de LiveCrowdModel, jumeau du module web : les deux doivent rendre les memes
        /// nombres, sinon la meme foule se placerait autrement selon l'ecran.
        /// </summary>
        private void DrawCrowd(Graphics g, RetroBat.Api.Netplay.LiveCrowdModel.Instantane f, long now)
        {
            var sol = _region.Height - 14;

            // Le pas est ADAPTATIF, calcule par rangee sur son effectif REEL.
            //
            // Avec un pas fixe d'un centieme de largeur, soixante spectateurs occupaient les
            // soixante premiers pour cents de l'ecran et laissaient le reste vide : la foule
            // paraissait tassee a gauche. En adaptant le pas, les presents s'etalent sur toute
            // la largeur quel que soit leur nombre, et c'est la DENSITE qui devient la mesure
            // au lieu de l'etendue. C'est aussi ce qui ressemble a un vrai public.
            var effectifs = new int[RetroBat.Api.Netplay.LiveCrowdModel.Rangees];
            foreach (var place in f.Places.Values)
            {
                if (place.Rangee >= 0 && place.Rangee < effectifs.Length)
                {
                    effectifs[place.Rangee]++;
                }
            }

            float PasDe(int rangee)
            {
                var n = rangee >= 0 && rangee < effectifs.Length ? effectifs[rangee] : 0;
                return _region.Width / (float) Math.Max(1, n);
            }

            // Chaque spectateur au CENTRE de sa case : la foule est donc centree, et le bord
            // droit n'a pas une demi-case de vide de plus que le gauche.
            float XDe(int rangee, int index, int taille)
                => (index + 0.5f) * PasDe(rangee) - taille / 2f;

            // Les silhouettes, de l'arriere vers l'avant : les rangees de devant recouvrent.
            for (var rangee = RetroBat.Api.Netplay.LiveCrowdModel.Rangees - 1; rangee >= 0; rangee--)
            {
                // TAILLE ENTIERE et RECUL ENTIER : c'est ce qui garde les pixels carres.
                var taille = RetroBat.Api.Netplay.LiveCrowdModel.TailleRangee(rangee);
                var alpha = RetroBat.Api.Netplay.LiveCrowdModel.Opacites[rangee];
                var baseY = sol - rangee * RetroBat.Api.Netplay.LiveCrowdModel.ReculParRangee;

                foreach (var (acteur, place) in f.Places)
                {
                    if (place.Rangee != rangee) { continue; }

                    var x = XDe(rangee, place.Index, taille)
                        + (rangee % 2 == 1 ? PasDe(rangee) * 0.22f : 0f);
                    var saut = 0f;
                    if (f.Sauts.TryGetValue(acteur, out var s) && s.Fin > now)
                    {
                        var avancement = 1.0 - (s.Fin - now) / (double) RetroBat.Api.Netplay.LiveCrowdModel.DureeSaut;
                        // Le saut est ARRONDI au pixel, et divise comme la rangee : une hauteur
                        // fractionnaire ferait vibrer les bords du sprite d'une image a l'autre.
                        var diviseur = RetroBat.Api.Netplay.LiveCrowdModel.Diviseurs[rangee];
                        saut = (float) Math.Round(
                            Math.Sin(Math.Clamp(avancement, 0, 1) * Math.PI) * s.Hauteur / diviseur);
                    }

                    var teinte = RetroBat.Api.Netplay.LiveCrowdModel.Teinte(acteur);
                    using var pinceau = new SolidBrush(DeTeinte(teinte, alpha));
                    // Coordonnees ARRONDIES : un sprite pose sur une demi-position se retrouve
                    // interpole, et un pixel interpole n'est plus un pixel.
                    Creature(g, pinceau, (int) Math.Round(x), (int) Math.Round(baseY - taille - saut), taille);
                }
            }

            // Les emoji en vol, par la MEME planche de sprites que les particules : deux chemins
            // de rendu pour le meme dessin finiraient par ne plus se ressembler.
            if (_sprites is { Ok: true })
            {
                foreach (var vol in f.Vols)
                {
                    var age = (now - vol.Depuis) / (float) RetroBat.Api.Netplay.LiveCrowdModel.DureeVol;
                    if (age is < 0 or >= 1) { continue; }
                    var tv = RetroBat.Api.Netplay.LiveCrowdModel.TailleRangee(vol.Rangee);
                    var x = XDe(vol.Rangee, vol.Index, tv)
                        + (vol.Rangee % 2 == 1 ? PasDe(vol.Rangee) * 0.22f : 0f) + tv / 2f;
                    var y = sol - vol.Rangee * RetroBat.Api.Netplay.LiveCrowdModel.ReculParRangee
                        - tv - age * 90f;
                    _sprites.Draw(g, vol.Famille, Math.Clamp(vol.Niveau - 1, 0, 2), x, y, 26f + vol.Niveau * 4f, 1f - age);
                }
            }

            // Les etiquettes par-dessus tout, sur une plaque sombre : sans elle, un pseudo clair
            // sur une foule claire ne se lit pas.
            using var police = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var encre = new SolidBrush(Color.FromArgb(235, 232, 236, 246));
            using var plaque = new SolidBrush(Color.FromArgb(200, 11, 16, 32));
            foreach (var e in f.Etiquettes)
            {
                var taille = g.MeasureString(e.Texte, police);
                var te = RetroBat.Api.Netplay.LiveCrowdModel.TailleRangee(e.Rangee);
                var x = XDe(e.Rangee, e.Index, te)
                    + (e.Rangee % 2 == 1 ? PasDe(e.Rangee) * 0.22f : 0f) + te / 2f;
                var cx = Math.Clamp(x, taille.Width / 2f + 4f, _region.Width - taille.Width / 2f - 4f);
                var y = sol - e.Rangee * RetroBat.Api.Netplay.LiveCrowdModel.ReculParRangee
                    - te - 24f;
                FillRounded(g, plaque, cx - taille.Width / 2f - 5f, y, taille.Width + 10f, taille.Height + 2f, 6);
                g.DrawString(e.Texte, police, encre, cx - taille.Width / 2f, y + 1f);
            }
        }

        /// <summary>
        /// Le spectateur, dessine dans un carre de `taille` pixels.
        ///
        /// PROVISOIRE : la grille de huit sur huit tient la place des creatures uniques
        /// derivees du pseudo. Elle se divise proprement dans trente-deux comme dans seize,
        /// donc ce provisoire respecte deja la contrainte du pixel carre.
        ///
        /// Tout est en entiers, positions comme tailles. Quand la creature arrivera, il
        /// suffira de remplacer ce corps : la geometrie autour n'aura pas a changer.
        /// </summary>
        private static void Creature(Graphics g, Brush pinceau, int x, int y, int taille)
        {
            var gabarit = RetroBat.Api.Netplay.LiveCrowdModel.Gabarit;
            var bloc = taille / gabarit.Length;   // 4 a trente-deux pixels, 2 a seize
            if (bloc < 1)
            {
                return;
            }
            for (var ligne = 0; ligne < gabarit.Length; ligne++)
            {
                var bits = gabarit[ligne];
                for (var colonne = 0; colonne < 8; colonne++)
                {
                    if ((bits & (1 << (7 - colonne))) == 0)
                    {
                        continue;
                    }
                    g.FillRectangle(pinceau, x + colonne * bloc, y + ligne * bloc, bloc, bloc);
                }
            }
        }

        /// <summary>Une teinte en degres vers une couleur, saturation et clarte fixes.</summary>
        private static Color DeTeinte(int teinte, int alpha)
        {
            var h = (teinte % 360) / 60.0;
            const double s = 0.62, l = 0.58;
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs(h % 2 - 1));
            var m = l - c / 2;
            var (r, v, b) = (int) h switch
            {
                0 => (c, x, 0.0),
                1 => (x, c, 0.0),
                2 => (0.0, c, x),
                3 => (0.0, x, c),
                4 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            return Color.FromArgb(
                Math.Clamp(alpha, 0, 255),
                (int) Math.Round((r + m) * 255),
                (int) Math.Round((v + m) * 255),
                (int) Math.Round((b + m) * 255));
        }

        private ReplayReactionService.Availability SafeAvail()
        {
            try { return _avail(); } catch { return new ReplayReactionService.Availability(1, 1, true, 1); }
        }

        // ── légende (bandeau haut) : bouton -> petite icône de réaction (niveau 1) ──
        private static readonly (string Btn, string Fam)[] LegendItems =
        {
            ("A", "hype"), ("B", "wow"), ("X", "respect"), ("Y", "laugh"),
            ("L", "tension"), ("R", "ouch"), ("L2", "love"), ("R2", "rage"), ("×3", "celebrate"),
        };

        private void DrawLegend(Graphics g, ReplayReactionService.Availability av)
        {
            if (_sprites is not { Ok: true }) return;
            var can = av.CanReact;
            var rowAlpha = can ? 1f : 0.4f; // légende ESTOMPÉE quand on ne peut pas réagir

            const int icon = 44, gap = 20, padX = 20, padY = 8, statusW = 96;
            using var btnF = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var gold = new SolidBrush(Color.FromArgb((int)(245 * rowAlpha), 245, 200, 90));

            float total = statusW; // pastille de statut à gauche
            var bw = new float[LegendItems.Length];
            for (var i = 0; i < LegendItems.Length; i++) { bw[i] = g.MeasureString(LegendItems[i].Btn, btnF).Width; total += bw[i] + 6 + icon + gap; }
            total -= gap;

            var rowH = icon + padY * 2;
            var x = (_region.Width - total) / 2f;
            var y = 16f;
            using (var bg = new SolidBrush(Color.FromArgb(150, 8, 16, 28)))
                FillRounded(g, bg, x - padX, y - padY, total + padX * 2, rowH, 16);
            var mid = y + rowH / 2f - padY;

            // pastille de statut : anneau de cooldown + compteur de budget restant.
            DrawStatus(g, x + 24, mid, av);
            x += statusW;

            for (var i = 0; i < LegendItems.Length; i++)
            {
                var (btn, fam) = LegendItems[i];
                var bh = g.MeasureString(btn, btnF);
                g.DrawString(btn, btnF, gold, x, mid - bh.Height / 2f);
                x += bw[i] + 6;
                _sprites.Draw(g, fam, DesignBase, x + icon / 2f, mid, icon, rowAlpha);
                x += icon + gap;
            }
        }

        private void DrawStatus(Graphics g, float cx, float cy, ReplayReactionService.Availability av)
        {
            const float r = 20f;
            var empty = av.Budget <= 0;
            var color = empty ? Color.FromArgb(235, 90, 90) : av.CanReact ? Color.FromArgb(90, 220, 120) : Color.FromArgb(240, 190, 70);

            using (var ring = new Pen(Color.FromArgb(90, 200, 214, 235), 4f)) g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
            // remplissage cooldown (ou plein si prêt)
            using (var arc = new Pen(color, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, -90, (float)(360 * (empty ? 1 : av.CooldownProgress)));

            // compteur de budget — chiffre GROS avec CONTOUR (effet typo), centré, déborde l'anneau.
            using var fam = new FontFamily("Segoe UI");
            using var path = new GraphicsPath();
            path.AddString(av.Budget.ToString(), fam, (int)FontStyle.Bold, 38f, PointF.Empty, StringFormat.GenericTypographic);
            var bnd = path.GetBounds();
            using (var mat = new Matrix()) { mat.Translate(cx - bnd.X - bnd.Width / 2f, cy - bnd.Y - bnd.Height / 2f); path.Transform(mat); }
            using (var outline = new Pen(Color.FromArgb(235, 6, 12, 22), 5f) { LineJoin = LineJoin.Round }) g.DrawPath(outline, path);
            using (var fill = new SolidBrush(color)) g.FillPath(fill, path);
        }

        private static void FillRounded(Graphics g, Brush b, float x, float y, float w, float h, float r)
        {
            using var path = new GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(b, path);
        }

        // ── tunnel : emoji gros au bord → petit/transparent au centre ──
        private void DrawParticle(Graphics g, Particle p, long now)
        {
            var age = now - p.Born;
            if (age < 0) return; // départ différé (effet de vague)
            var t = age / (float)PartLifeMs;
            if (t >= 1f) return;
            var e = EaseIn(t);   // accélère vers le point de fuite (aspiration)

            float cx = _region.Width / 2f, cy = _region.Height / 2f;
            double dx = Math.Cos(p.Angle), dy = Math.Sin(p.Angle);
            var rx = dx != 0 ? _region.Width / 2f / (float)Math.Abs(dx) : 1e6f;
            var ry = dy != 0 ? _region.Height / 2f / (float)Math.Abs(dy) : 1e6f;
            var r = Math.Min(rx, ry) * p.Radial;         // > bord = hors écran
            float sx = cx + (float)dx * r, sy = cy + (float)dy * r;

            var travel = e * 0.99f;                       // va quasiment jusqu'au centre
            var x = Lerp(sx, cx, travel);
            var y = Lerp(sy, cy, travel);

            var big = 190f + p.Level * 42f;               // ~2x plus gros au départ (bord de l'écran)
            var size = Lerp(big, big * 0.06f, e);         // gros au bord -> minuscule au point de fuite

            // 100 % au bord, fondu RAPIDE en approchant du centre (vagues qui se succèdent).
            var alpha = Math.Clamp(1.12f - e * 1.12f, 0f, 1f);

            if (_sprites is { Ok: true }) _sprites.Draw(g, p.Family, p.Col, x, y, size, alpha);
            else DrawEmoji(g, p.Emoji, x, y, size * 0.7f, p.Color, alpha); // fallback glyphe
        }

        // ── mot central qui pulse ──
        private void DrawLabel(Graphics g, Label l, long now)
        {
            var t = (now - l.Born) / (float)LabelLifeMs;
            if (t < 0f || t >= 1f) return;
            float cx = _region.Width / 2f, cy = _region.Height / 2f; // pile au centre
            float scale = t < 0.16f ? Lerp(0.6f, 1.06f, t / 0.16f)
                : t < 0.30f ? Lerp(1.06f, 1f, (t - 0.16f) / 0.14f) : 1f;
            var alpha = t < 0.74f ? 1f : 1f - (t - 0.74f) / 0.26f;

            // Le mot occupe ~70 % de la LARGEUR de l'écran : on calcule la taille de police pour ça.
            var word = l.Word.ToUpperInvariant();
            var targetW = _region.Width * 0.70f;
            var mw = g.MeasureString(word, WordFont(48)).Width;
            var size = mw > 1f ? 48f * targetW / mw : 48f;
            DrawWord(g, word, cx, cy, size * scale, l.Color, Math.Clamp(alpha, 0f, 1f), true);
        }

        // ── jauge de charge (bas-centre, au-dessus de la barre) ──
        private void DrawGauge(Graphics g, ReplayReactionService.ChargeSnapshot c)
        {
            var (emoji, word) = ReplayReactionText.Resolve(c.Family, c.Level, _locale());
            var color = ReplayReactionText.ColorOf(c.Family);
            float cx = _region.Width / 2f;
            float cy = _region.Height - BarHeight - 150f; // au-dessus de la barre d'info (sinon le texte
                                                          // clignote : 2 fenêtres topmost se disputent la zone)
            const float R = 54f;

            using (var back = new SolidBrush(Color.FromArgb(150, 8, 16, 28)))
                g.FillEllipse(back, cx - R - 14, cy - R - 14, (R + 14) * 2, (R + 14) * 2);
            using (var bp = new Pen(Color.FromArgb(90, 200, 214, 235), 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(bp, cx - R, cy - R, R * 2, R * 2, 135, 270);
            using (var pp = new Pen(Color.FromArgb(235, color), 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pp, cx - R, cy - R, R * 2, R * 2, 135, (float)(270 * c.Progress));

            // sprite central : escalade à travers les 6 colonnes selon la charge (le maintien fait « monter » l'icône)
            var eSize = 66f + c.Level * 10f;
            if (_sprites is { Ok: true })
            {
                var col = DesignBase + Math.Clamp(c.Level - 1, 0, 2); // icône du design au niveau courant
                _sprites.Draw(g, c.Family, col, cx, cy - 2, eSize, 1f);
            }
            else DrawEmoji(g, emoji, cx, cy - 4, eSize * 0.7f, color, 1f);
            DrawWord(g, word, cx, cy + R + 22, 22f, color, 1f, true);
            for (var i = 0; i < 3; i++)
            {
                using var b = new SolidBrush(i < c.Level ? color : Color.FromArgb(120, 150, 165, 190));
                g.FillEllipse(b, cx - 16 + i * 14, cy + R + 46, 8, 8);
            }
        }

        // ── primitives (polices en cache pour la perf) ──
        private Font EmojiFont(float size)
        {
            var key = Math.Clamp((int)Math.Round(size / 2f) * 2, 8, 220);
            if (!_emojiFonts.TryGetValue(key, out var f)) { f = new Font("Segoe UI Emoji", key, FontStyle.Regular, GraphicsUnit.Pixel); _emojiFonts[key] = f; }
            return f;
        }

        private Font WordFont(float size)
        {
            var key = Math.Clamp((int)Math.Round(size / 4f) * 4, 10, 760);
            if (!_wordFonts.TryGetValue(key, out var f)) { f = new Font("Segoe UI Semibold", key, FontStyle.Bold, GraphicsUnit.Pixel); _wordFonts[key] = f; }
            return f;
        }

        private void DrawEmoji(Graphics g, string emoji, float cx, float cy, float size, Color color, float alpha)
        {
            if (alpha <= 0.02f) return;
            var f = EmojiFont(size);
            using var b = new SolidBrush(Color.FromArgb((int)(255 * Math.Clamp(alpha, 0f, 1f)), color));
            var sz = g.MeasureString(emoji, f);
            g.DrawString(emoji, f, b, cx - sz.Width / 2f, cy - sz.Height / 2f);
        }

        private void DrawWord(Graphics g, string word, float cx, float cy, float size, Color color, float alpha, bool bold)
        {
            if (alpha <= 0.02f) return;
            var f = WordFont(size);
            var sz = g.MeasureString(word, f);
            var a = (int)(255 * Math.Clamp(alpha, 0f, 1f));
            var off = Math.Max(2f, f.Size * 0.03f); // ombre proportionnelle à la taille
            using (var sh = new SolidBrush(Color.FromArgb((int)(a * 0.55f), 0, 0, 0)))
                g.DrawString(word, f, sh, cx - sz.Width / 2f + off, cy - sz.Height / 2f + off);
            using var br = new SolidBrush(Color.FromArgb(a, color));
            g.DrawString(word, f, br, cx - sz.Width / 2f, cy - sz.Height / 2f);
        }

        private static float EaseOut(float t) => 1f - (float)Math.Pow(1 - t, 3);
        private static float EaseIn(float t) => t * t; // accélère (aspiration vers le centre)
        private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
        /// <summary>
        /// L'heure du HUD : millisecondes depuis le demarrage.
        ///
        /// La foule compare ses horodatages a CELLE-CI (voir `LiveCrowdModel.Maintenant`).
        /// Changer de base ici sans changer la-bas rend les emoji invisibles et les pseudos
        /// eternels, sans lever la moindre erreur.
        /// </summary>
        private static long NowMs() => Environment.TickCount64;

        // ── layered window ──
        private void PushLayered(Point pos, Size sz)
        {
            if (_memDc == IntPtr.Zero) return;
            var screenDc = GetDC(IntPtr.Zero);
            var size = new SIZE { cx = sz.Width, cy = sz.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = pos.X, y = pos.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        private void AssertTopmost()
        {
            if (!IsHandleCreated) return;
            SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose(); DisposeBuffer();
                foreach (var f in _emojiFonts.Values) f.Dispose();
                foreach (var f in _wordFonts.Values) f.Dispose();
            }
            base.Dispose(disposing);
        }

        private static Rectangle? ResolveGameScreen()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("retroarch"))
                    using (p) { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero && GetWindowRect(p.MainWindowHandle, out var rr))
                        return Screen.FromRectangle(Rectangle.FromLTRB(rr.Left, rr.Top, rr.Right, rr.Bottom)).Bounds; }
            }
            catch { }
            var h = FindWindowW(null, "EmulationStation");
            if (h != IntPtr.Zero && GetWindowRect(h, out var r))
                return Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).Bounds;
            return Screen.PrimaryScreen?.Bounds;
        }
    }

    // ── P/Invoke ──
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? c, string? n);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, ref POINT dst, ref SIZE size,
        IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
}
