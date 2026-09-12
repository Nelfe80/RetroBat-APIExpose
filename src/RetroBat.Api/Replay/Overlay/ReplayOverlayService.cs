using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Overlay;

/// <summary>
/// Overlay Replay baseline (étape 1) — barre en bas de l'écran de JEU pendant une lecture.
/// Modèle : mêmes fenêtres WinForms topmost/no-activate que les overlays existants
/// (Toast/Claim…). N'affiche RIEN d'autre qu'une timeline + l'état lecture/pause + les
/// contrôles panel ; se montre quand une lecture démarre et se cache à la fin. Aucune
/// dépendance à MarqueeManager. Piloté par un timer qui LIT <see cref="ReplayPlaybackService.GetState"/>
/// en direct (in-process, pas de HTTP) — l'overlay ne fait que CONSOMMER l'état.
/// </summary>
public sealed class ReplayOverlayService : BackgroundService
{
    private readonly ReplayPlaybackService _playback;
    private readonly ReplayStore _store;
    private readonly RetroBat.Api.Replay.Social.ReplaySocialStore _social;
    private readonly ILogger<ReplayOverlayService> _logger;

    private readonly object _sync = new();
    private Thread? _uiThread;
    private ApplicationContext? _appContext;
    private ReplayOverlayForm? _form;
    private ReplayReactionSprites? _sprites; // créé sur le thread UI de la barre

    public ReplayOverlayService(ReplayPlaybackService playback, ReplayStore store,
        RetroBat.Api.Replay.Social.ReplaySocialStore social, ILogger<ReplayOverlayService> logger,
        RetroBat.Api.Replay.Social.SocialIssuerPin pin)
    {
        _playback = playback;
        _store = store;
        _social = social;
        _logger = logger;
        _pin = pin;
    }

    private readonly RetroBat.Api.Replay.Social.SocialIssuerPin _pin;

    /// <summary>
    /// La barre est PLEINE (piste, temps, aides du panel, carte du record) ou REDUITE a un filet
    /// (piste, chaleur, curseur, marqueurs), au CHOIX du spectateur : un double appui sur START
    /// pendant la lecture bascule, et le choix tient jusqu'au prochain double appui (pas de
    /// minuteur qui deciderait a sa place). Le HUD lit la hauteur courante pour poser ses bulles
    /// et ses cameos.
    /// </summary>
    public const int HauteurPleine = 118;
    public const int HauteurReduite = 24;
    public static volatile int HauteurCourante = HauteurPleine;
    public static volatile bool ReduiteVoulue;

    /// <summary>Le double appui sur START : on bascule. Appelable de tout fil.</summary>
    public static void BasculerReduite() => ReduiteVoulue = !ReduiteVoulue;

    private RetroBat.Api.Replay.Social.SocialSummary? LireResume(string replayId)
    {
        var epingle = _pin.Current;
        return epingle is null ? null : _social.ReadSummary(replayId, epingle.Spki, epingle.KeyId);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        EnsureUiThreadStarted(stoppingToken);
        _logger.LogInformation("Replay overlay : service initialisé (thread UI prêt).");

        // Rien à pomper ici : le form gère lui-même son cycle via son timer interne.
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CloseUiThread();
        await base.StopAsync(cancellationToken);
    }

    private void EnsureUiThreadStarted(CancellationToken ct)
    {
        lock (_sync) { if (_uiThread != null) return; }

        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var context = new ApplicationContext();
            var sprites = new ReplayReactionSprites(_logger); // sur CE thread UI
            var form = new ReplayOverlayForm(() => _playback.GetState(), id => _social.Display(id, _store.ReadReactions(id)), sprites, _logger, LireResume);

            lock (_sync) { _appContext = context; _form = form; _sprites = sprites; }

            ready.Set();
            Application.Run(context);

            lock (_sync)
            {
                _form?.Dispose();
                _form = null;
                _appContext = null;
                _sprites?.Dispose();
                _sprites = null;
            }
        })
        {
            IsBackground = true,
            Name = "APIExpose.ReplayOverlay"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        lock (_sync) { _uiThread = thread; }

        try { ready.Wait(ct); } catch (OperationCanceledException) { }
    }

    private void CloseUiThread()
    {
        ApplicationContext? context;
        lock (_sync) context = _appContext;
        if (context == null) return;
        try { context.ExitThread(); } catch { /* arrêt best-effort */ }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fenêtre overlay : bande en bas de l'écran de jeu, topmost, sans focus, sans barre des tâches.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    private sealed class ReplayOverlayForm : Form
    {
        private const int BarHeight = HauteurPleine;
        private bool _reduite;
        private const int SidePadding = 30;
        private const int TrackTop = 16;
        private const int TrackHeight = 8;
        private const int RowTop = 42;   // haut de la rangée de contrôles ES
        private const int GlyphSize = 40; // taille d'un glyphe de touche
        private const double AutoHideLingerSeconds = 1.2; // petit délai avant de cacher en fin de lecture

        private static readonly Color BackColorDark = ColorTranslator.FromHtml("#0B1524");
        private static readonly Color TrackColor = ColorTranslator.FromHtml("#1B2B45");
        private static readonly Color RunColor = ColorTranslator.FromHtml("#2E63B2");
        private static readonly Color CheckpointColor = ColorTranslator.FromHtml("#3A4E70");
        private static readonly Color CursorColor = ColorTranslator.FromHtml("#F4F7FF");
        private static readonly Color TextColor = ColorTranslator.FromHtml("#DCE6F5");
        private static readonly Color TextDimColor = ColorTranslator.FromHtml("#8AA0C2");
        private static readonly Color AccentColor = ColorTranslator.FromHtml("#5EA0FF");
        private static readonly Color NelfeWhite = ColorTranslator.FromHtml("#FFFFFF");   // logo NelfePlay
        private static readonly Color GoldColor = ColorTranslator.FromHtml("#F5B940");    // badge « vérifié/certifié »

        private readonly Func<ReplayPlaybackService.StateSnapshot> _snapshotProvider;
        private readonly Func<string, IReadOnlyList<ReplayReaction>> _loadReactions;
        private readonly ReplayReactionSprites _sprites;
        private readonly ILogger _logger;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly OverlaySurface _surface;

        private ReplayPlaybackService.StateSnapshot _snapshot =
            new("none", "idle", null, 0, null, null, null, false, null, 60, null, null);
        private DateTime? _inactiveSince;
        private int _ticks;
        private bool _shownLogged;

        // courbe + marqueurs de réactions le long du replay
        private readonly Func<string, RetroBat.Api.Replay.Social.SocialSummary?>? _lireResume;
        private RetroBat.Api.Replay.Social.SocialSummary? _resume;
        private DateTime _resumeProchainEssai = DateTime.MinValue;
        private string? _curveReplayId;
        private IReadOnlyList<ReplayReaction> _curveReactions = Array.Empty<ReplayReaction>();
        private float[]? _curve;
        private IReadOnlyList<ReactionMarker> _markers = Array.Empty<ReactionMarker>();

        // curseur INTERPOLÉ (la frame réelle n'arrive que ~toutes les 500 ms) : on prédit à vitesse estimée.
        private long _baseFrame, _baseTime;
        private double _rate;         // frames par ms
        private long _displayFrame;   // position lissée du curseur

        public ReplayOverlayForm(Func<ReplayPlaybackService.StateSnapshot> snapshotProvider,
            Func<string, IReadOnlyList<ReplayReaction>> loadReactions, ReplayReactionSprites sprites, ILogger logger,
            Func<string, RetroBat.Api.Replay.Social.SocialSummary?>? lireResume = null)
        {
            _snapshotProvider = snapshotProvider;
            _lireResume = lireResume;
            _loadReactions = loadReactions;
            _sprites = sprites;
            _logger = logger;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = BackColorDark;
            Opacity = 0d;
            Size = new Size(800, BarHeight); // recalculé à l'affichage

            _surface = new OverlaySurface(() => _snapshot, () => _curve, () => _markers, () => _displayFrame, _sprites, () => _reduite) { Dock = DockStyle.Fill };
            Controls.Add(_surface);

            _timer = new System.Windows.Forms.Timer { Interval = 250 }; // ~4 fps : la barre est layered
            // (compositing DWM par-dessus RetroArch) → on limite les repeints pour ne pas hacher l'audio
            // sur borne faible (curseur moins fluide, mais la lecture prime). Cf. diagnostic i3-N305.
            _timer.Tick += (_, _) => OnTick();
            // Démarre TOUT DE SUITE : la fenêtre naît cachée et s'auto-affiche via le poll.
            // (Ne PAS attacher au 'Shown' : il ne se déclenche jamais tant qu'on ne montre rien.)
            _timer.Start();
        }

        // Recharge les réactions au changement de replay ; calcule les bacs d'intensité dès que la durée est connue.
        private void UpdateCurve()
        {
            if (!string.Equals(_snapshot.ReplayId, _curveReplayId, StringComparison.Ordinal))
            {
                _curveReplayId = _snapshot.ReplayId;
                try { _curveReactions = _snapshot.ReplayId is null ? Array.Empty<ReplayReaction>() : _loadReactions(_snapshot.ReplayId); }
                catch { _curveReactions = Array.Empty<ReplayReaction>(); }
                try { _resume = _snapshot.ReplayId is null || _lireResume is null ? null : _lireResume(_snapshot.ReplayId); }
                catch { _resume = null; }
                _curve = null;
            }
            // Le resume arrive quelques secondes apres le lancement : tant qu'il manque, on le relit
            // toutes les deux secondes, et la courbe se refait avec lui.
            if (_resume is null && _lireResume is not null && _snapshot.ReplayId is not null && DateTime.UtcNow >= _resumeProchainEssai)
            {
                _resumeProchainEssai = DateTime.UtcNow.AddSeconds(2);
                try { _resume = _lireResume(_snapshot.ReplayId); } catch { _resume = null; }
                if (_resume is not null) _curve = null;
            }
            if (_curve is null && _snapshot.ReplayEndFrame is long end && end > 0)
            {
                // Le resume signe d'abord : sa chaleur compte TOUS les spectateurs, sans plafond, et ses
                // moments forts grossissent avec le nombre de gens qui ont reagi ensemble. Sans resume
                // (replay jamais commente, ou plateforme injoignable), les reactions locales font foi.
                if (_resume is { } r && r.Chaleur.Count > 0)
                {
                    _curve = CurveFromSummary(r);
                    _markers = MarkersFromSummary(r);
                }
                else
                {
                    _curve = BuildCurve(_curveReactions, end);
                    _markers = ReplayReactionText.Clusterize(_curveReactions, end, 24);
                }
            }
        }

        // Interpolation du curseur : estime la vitesse depuis les frames réelles et prédit entre deux.
        private void UpdateDisplayFrame()
        {
            var now = Environment.TickCount64;
            var f = _snapshot.Frame;
            var end = _snapshot.ReplayEndFrame ?? 0;
            var playing = string.Equals(_snapshot.Mode, "replay", StringComparison.Ordinal) && !_snapshot.Paused;

            if (!playing) { _rate = 0; _baseFrame = f; _baseTime = now; _displayFrame = f; return; }

            if (f != _baseFrame)
            {
                var dt = now - _baseTime;
                var delta = f - _baseFrame;
                if (delta > 0 && delta < 400 && dt > 0) // avance normale : (ré)estime la vitesse
                {
                    var nr = delta / (double)dt;
                    _rate = _rate <= 0 ? nr : _rate * 0.5 + nr * 0.5;
                }
                else _rate = 0; // seek (arrière ou saut) : NE PLUS prédire → évite le va-et-vient du curseur
                _baseFrame = f; _baseTime = now;
            }

            const long capMs = 700; // ne pas prédire au-delà de ~un intervalle de mesure
            var pred = _baseFrame + _rate * Math.Min(now - _baseTime, capMs);
            _displayFrame = (long)Math.Clamp(pred, 0, end <= 0 ? pred : end);
        }

        private static float[] CurveFromSummary(RetroBat.Api.Replay.Social.SocialSummary r)
        {
            var acc = r.Chaleur.Select(t => (float) t.Intensite).ToArray();
            var max = acc.Length == 0 ? 0f : acc.Max();
            if (max <= 0f) return acc;
            for (var i = 0; i < acc.Length; i++) acc[i] /= max;
            return acc;
        }

        /// <summary>Un marqueur par moment fort, dont la taille dit combien ont reagi ensemble.</summary>
        private static IReadOnlyList<ReactionMarker> MarkersFromSummary(RetroBat.Api.Replay.Social.SocialSummary r)
            => r.Moments
                .Where(m => m.Reaction.Length > 0)
                .Select(m => new ReactionMarker(m.Frame, m.Reaction, m.Reactions >= 5 ? 3 : m.Reactions >= 2 ? 2 : 1, ""))
                .ToList();

        private static float[] BuildCurve(IReadOnlyList<ReplayReaction> reactions, long end)
        {
            const int bins = 160;
            var acc = new float[bins];
            foreach (var r in reactions)
            {
                var b = (int)Math.Clamp(r.Frame / (double)end * bins, 0, bins - 1);
                acc[b] += Math.Clamp(r.Level, 1, 3);
            }
            var max = acc.Length == 0 ? 0f : acc.Max();
            if (max <= 0f) return acc; // tout à zéro
            for (var i = 0; i < bins; i++) acc[i] = acc[i] / max; // normalisé 0-1
            return acc;
        }

        private void OnTick()
        {
            try { _snapshot = _snapshotProvider(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay overlay : lecture d'état échouée"); return; }

            // Vérité terrain : prouve que le timer tourne et ce que voit le form.
            if (_ticks < 3) _logger.LogInformation("Replay overlay : tick {N}, mode={Mode}, visible={Vis}", _ticks, _snapshot.Mode, Visible);
            _ticks++;

            UpdateCurve();
            UpdateDisplayFrame();

            var active = string.Equals(_snapshot.Mode, "replay", StringComparison.Ordinal);
            if (active)
            {
                var reduite = ReduiteVoulue;
                if (reduite != _reduite)
                {
                    _reduite = reduite;
                    HauteurCourante = reduite ? HauteurReduite : HauteurPleine;
                    if (Visible) PlacerBarre();
                }

                _inactiveSince = null;
                if (!Visible || Opacity <= 0d) ShowOverlay();
                else ForceTopmostNoActivate(); // ré-affirme le premier plan à chaque tick (150 ms)
                _surface.Invalidate();
            }
            else
            {
                // Petit délai anti-clignotement (transitions launching/finished).
                _inactiveSince ??= DateTime.UtcNow;
                if (Visible && (DateTime.UtcNow - _inactiveSince.Value).TotalSeconds >= AutoHideLingerSeconds)
                    HideOverlay();
            }
        }

        private void PlacerBarre()
        {
            var (screen, _) = ResolveGameScreenWithSource();
            var area = screen?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var hauteur = _reduite ? HauteurReduite : HauteurPleine;
            Size = new Size(area.Width, hauteur);
            Location = new Point(area.Left, area.Bottom - hauteur);
        }

        private void ShowOverlay()
        {
            _reduite = ReduiteVoulue;
            HauteurCourante = _reduite ? HauteurReduite : HauteurPleine;
            var (screen, src) = ResolveGameScreenWithSource();
            var area = screen?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Size = new Size(area.Width, HauteurCourante);
            Location = new Point(area.Left, area.Bottom - HauteurCourante);
            Opacity = 0.9d;
            if (!Visible) Show();
            ForceTopmostNoActivate();
            _surface.Invalidate();
            if (!_shownLogged)
            {
                _shownLogged = true;
                _logger.LogInformation("Replay overlay : barre affichée (écran={Src} {W}x{H} @ {L},{T}) → barre à {X},{Y} taille {BW}x{BH}",
                    src, area.Width, area.Height, area.Left, area.Top, Location.X, Location.Y, Width, Height);
            }
        }

        private void HideOverlay()
        {
            Opacity = 0d;
            if (Visible) Hide();
        }

        private void ForceTopmostNoActivate()
        {
            if (!IsHandleCreated) return;
            // Bascule NoTopMost -> TopMost : force la fenêtre en TÊTE de la bande topmost, même si
            // RetroArch (topmost lui aussi) vient de repasser devant. Un simple HWND_TOPMOST sur une
            // fenêtre déjà topmost ne re-trie pas. L'écart entre les deux appels est infime (même
            // thread), donc pas de scintillement à l'échelle d'une frame DWM. Sans focus (NOACTIVATE).
            SetWindowPos(Handle, HWND_NOTOPMOST, Left, Top, Width, Height, SWP_NOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, Left, Top, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // Ré-affirme le topmost à chaque passage visible (comme l'overlay « réclame ton score »).
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) ForceTopmostNoActivate();
        }

        // Ne prend jamais le focus (le jeu doit garder l'entrée).
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_COMPOSITED = 0x02000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_COMPOSITED;
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        private static (Screen? Screen, string Source) ResolveGameScreenWithSource()
        {
            // Le jeu (RetroArch) est la référence pendant une lecture ; on s'aligne sur SA fenêtre
            // en priorité (sinon EmulationStation, sinon l'écran principal).
            foreach (var p in SafeProcesses("retroarch"))
                using (p) { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero && GetWindowRect(p.MainWindowHandle, out var rr))
                    return (Screen.FromRectangle(Rectangle.FromLTRB(rr.Left, rr.Top, rr.Right, rr.Bottom)), "retroarch"); }

            var handle = FindWindowW(null, "EmulationStation");
            if (handle != IntPtr.Zero && GetWindowRect(handle, out var r))
                return (Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)), "emulationstation");

            return (Screen.PrimaryScreen, "primary");
        }

        private static Process[] SafeProcesses(string name)
        {
            try { return Process.GetProcessesByName(name); } catch { return Array.Empty<Process>(); }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Surface peinte (double-buffered) : timeline + état + rappels de contrôles.
        // ─────────────────────────────────────────────────────────────────────────────────────
        private sealed class OverlaySurface : Panel
        {
            private readonly Func<ReplayPlaybackService.StateSnapshot> _get;
            private readonly Func<float[]?> _curve;
            private readonly Func<IReadOnlyList<ReactionMarker>> _markers;
            private readonly Func<bool> _reduite;
            private readonly Func<long> _displayFrame;
            private readonly ReplayReactionSprites _sprites;

            public OverlaySurface(Func<ReplayPlaybackService.StateSnapshot> get, Func<float[]?> curve,
                Func<IReadOnlyList<ReactionMarker>> markers, Func<long> displayFrame, ReplayReactionSprites sprites,
                Func<bool>? reduite = null)
            {
                _reduite = reduite ?? (() => false);
                _get = get;
                _curve = curve;
                _markers = markers;
                _displayFrame = displayFrame;
                _sprites = sprites;
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var s = _get();
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using (var bg = new SolidBrush(BackColorDark)) g.FillRectangle(bg, ClientRectangle);
                // liseré supérieur accent
                using (var top = new Pen(AccentColor, 2f)) g.DrawLine(top, 0, 0, Width, 0);

                if (_reduite())
                {
                    // Le filet : la piste, la chaleur, le curseur, les marqueurs. Rien qui se lise.
                    DrawTimeline(g, s, 12, 5, 11f, 10f);
                    return;
                }
                DrawTimeline(g, s);
                DrawStateAndHints(g, s);
                if (s.Card is not null) DrawRecordCard(g, s.Card);
            }

            private void DrawTimeline(Graphics g, ReplayPlaybackService.StateSnapshot s)
                => DrawTimeline(g, s, TrackTop, TrackHeight, 20f, 13f);

            private void DrawTimeline(Graphics g, ReplayPlaybackService.StateSnapshot s, int y, int hauteurPiste, float tailleMarqueur, float hauteurCourbe)
            {
                var left = SidePadding;
                var right = Width - SidePadding;
                var w = Math.Max(1, right - left);

                // piste
                using (var track = new SolidBrush(TrackColor))
                    FillRounded(g, track, new Rectangle(left, y, w, hauteurPiste), hauteurPiste / 2);

                var end = s.ReplayEndFrame ?? 0;
                if (end <= 0) return;

                double Norm(long f) => Math.Clamp(f / (double)end, 0d, 1d);
                int X(long f) => left + (int)Math.Round(w * Norm(f));

                // courbe de réactions (aire au-dessus de la piste)
                DrawReactionCurve(g, left, w, y, hauteurCourbe);

                // segment de RUN (la partie « officielle » du replay)
                if (s.RunStartFrame is long rs && s.RunEndFrame is long re && re > rs)
                {
                    var x0 = X(rs); var x1 = X(re);
                    using var run = new SolidBrush(RunColor);
                    FillRounded(g, run, new Rectangle(x0, y, Math.Max(2, x1 - x0), hauteurPiste), hauteurPiste / 2);
                }

                // ticks de checkpoint (intervalle 5 s — cf. replay_checkpoint_interval)
                var fps = s.NominalFps <= 0 ? 60 : s.NominalFps;
                var stepFrames = (long)Math.Round(5 * fps);
                if (stepFrames > 0)
                {
                    using var tick = new Pen(CheckpointColor, 1f);
                    for (var f = stepFrames; f < end; f += stepFrames)
                    {
                        var x = X(f);
                        g.DrawLine(tick, x, y - 2, x, y + hauteurPiste + 2);
                    }
                }

                // marqueurs de réactions majoritaires (petites icônes sur la timeline)
                if (_sprites.Ok)
                {
                    var mmid = y + hauteurPiste / 2;
                    foreach (var m in _markers())
                        _sprites.Draw(g, m.Family, Math.Clamp(m.Level - 1, 0, 2), X(m.Frame), mmid, tailleMarqueur, 1f);
                }

                // curseur de lecture (position INTERPOLÉE pour un mouvement fluide)
                var cx = X(Math.Clamp(_displayFrame(), 0, end));
                using (var cur = new Pen(CursorColor, 2.5f))
                    g.DrawLine(cur, cx, y - 5, cx, y + hauteurPiste + 5);
                using (var knob = new SolidBrush(CursorColor))
                    g.FillEllipse(knob, cx - 4, y + (hauteurPiste / 2) - 4, 8, 8);
            }

            // Aire d'intensité des réactions, au-dessus de la piste (les données sont normalisées 0-1).
            private void DrawReactionCurve(Graphics g, int left, int w, int trackTop, float H = 13f)
            {
                var curve = _curve();
                if (curve is null || curve.Length < 2) return;
                var baseY = trackTop - 2f;
                var pts = new PointF[curve.Length + 2];
                pts[0] = new PointF(left, baseY);
                for (var i = 0; i < curve.Length; i++)
                {
                    var x = left + w * (i + 0.5f) / curve.Length;
                    pts[i + 1] = new PointF(x, baseY - curve[i] * H);
                }
                pts[^1] = new PointF(left + w, baseY);
                using var fill = new SolidBrush(Color.FromArgb(140, 255, 150, 70)); // aire chaude
                g.FillPolygon(fill, pts);
                using var line = new Pen(Color.FromArgb(220, 255, 176, 90), 1.5f);
                g.DrawLines(line, pts[1..^1]);
            }

            private enum Dir { Up, Down, LeftRight }

            private void DrawStateAndHints(Graphics g, ReplayPlaybackService.StateSnapshot s)
            {
                var mid = RowTop + GlyphSize / 2; // centre vertical de la rangée
                var x = SidePadding;

                using var label = new Font("Segoe UI Semibold", 19f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var timeFont = new Font("Segoe UI", 22f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(TextColor);

                // état ▶/⏸ + temps mm:ss / mm:ss
                DrawStateGlyph(g, new Rectangle(x, mid - 15, 24, 30), s.Paused);
                x += 24 + 16;
                var fps = s.NominalFps <= 0 ? 60 : s.NominalFps;
                var time = $"{Fmt(s.Frame, fps)} / {Fmt(s.ReplayEndFrame ?? 0, fps)}";
                var tsz = g.MeasureString(time, timeFont);
                g.DrawString(time, timeFont, textBrush, x, mid - tsz.Height / 2);
                x += (int)Math.Ceiling(tsz.Width) + 30;

                // séparateur vertical
                using (var sep = new Pen(TextDimColor)) g.DrawLine(sep, x - 15, RowTop + 4, x - 15, RowTop + GlyphSize - 4);

                // rappels ES-style : glyphe de touche + libellé MAJUSCULE
                x = DrawHint(g, x, mid, label, textBrush, Dir.Up, "LECTURE / PAUSE");
                x = DrawHint(g, x, mid, label, textBrush, Dir.LeftRight, "RECUL / AVANCE");
                x = DrawHint(g, x, mid, label, textBrush, Dir.Down, "CHECKPOINT");
                DrawHintStart(g, x, mid, label, textBrush, "QUITTER");
            }

            /// <summary>Un rappel = glyphe de croix directionnelle (direction active en bleu) + libellé.</summary>
            private static int DrawHint(Graphics g, int x, int mid, Font label, Brush textBrush, Dir dir, string text)
            {
                DrawDpad(g, new Rectangle(x, mid - GlyphSize / 2, GlyphSize, GlyphSize), dir);
                x += GlyphSize + 12;
                var sz = g.MeasureString(text, label);
                g.DrawString(text, label, textBrush, x, mid - sz.Height / 2);
                return x + (int)Math.Ceiling(sz.Width) + 30;
            }

            /// <summary>Rappel « START (maintenir) » avec badge de touche.</summary>
            private static void DrawHintStart(Graphics g, int x, int mid, Font label, Brush textBrush, string text)
            {
                var w = DrawStartBadge(g, x, mid);
                x += w + 12;
                g.DrawString(text, label, textBrush, x, mid - g.MeasureString(text, label).Height / 2);
            }

            // ── Fiche « performance NelfePlay » (droite de la barre), façon record esport ──
            private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

            private void DrawRecordCard(Graphics g, ReplayPlaybackService.ReplayCard card)
            {
                var midY = RowTop + GlyphSize / 2;

                // logo NelfePlay (branding, extrême droite)
                const int logoSize = 60;
                var logoBox = new Rectangle(Width - SidePadding - logoSize, midY - logoSize / 2, logoSize, logoSize);
                DrawNelfePlayLogo(g, logoBox);

                using var labelFont = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var scoreFont = new Font("Segoe UI", 25f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var rankFont = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var playerFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var infoFont = new Font("Segoe UI Semibold", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var white = new SolidBrush(TextColor);
                using var dim = new SolidBrush(TextDimColor);
                using var gold = new SolidBrush(GoldColor);

                var score = card.Score is long v ? v.ToString("N0", Fr) : "—";
                var rank = card.Rank is int r ? $"#{r}" : "#—";

                // Ligne 1 (EN HAUT) = le SCORE, titre du record, en or + rang.
                var line1 = new (string t, Font f, Brush b)[]
                {
                    ("SCORE ", labelFont, dim),
                    (score, scoreFont, gold),
                    ("    ", rankFont, dim),
                    (rank, rankFont, white),
                };
                // Ligne 2 = joueur · jeu · date (secondaire).
                var line2 = new (string t, Font f, Brush b)[]
                {
                    (card.Player.ToUpperInvariant(), playerFont, white),
                    ("  ·  ", infoFont, dim),
                    (card.Game.ToUpperInvariant(), infoFont, dim),
                    ("  ·  ", infoFont, dim),
                    (card.DateText, infoFont, dim),
                };

                float LineWidth((string t, Font f, Brush b)[] parts)
                {
                    float s = 0; foreach (var p in parts) s += g.MeasureString(p.t, p.f).Width; return s;
                }
                var w1 = LineWidth(line1);
                var w2 = LineWidth(line2);
                var textRight = logoBox.Left - 18f;
                var textLeft = textRight - Math.Max(w1, w2);

                // Badge « certifié » EN TÊTE (sceau de validation), à gauche du bloc texte.
                const int badgeSize = 54;
                var badgeBox = new Rectangle((int)(textLeft - 18 - badgeSize), midY - badgeSize / 2, badgeSize, badgeSize);
                DrawVerifiedBadge(g, badgeBox, card.Certified);

                void DrawLine((string t, Font f, Brush b)[] parts, float width, float centerY)
                {
                    var x = textRight - width;
                    foreach (var p in parts)
                    {
                        var sz = g.MeasureString(p.t, p.f);
                        g.DrawString(p.t, p.f, p.b, x, centerY - sz.Height / 2);
                        x += sz.Width;
                    }
                }
                DrawLine(line1, w1, midY - 12);
                DrawLine(line2, w2, midY + 16);
            }

            // Marque « N » NelfePlay = 3 polygones à segments droits (viewBox 1000x1000, avant transform).
            private static readonly PointF[][] NMark =
            {
                new PointF[]{ new(62.5f,6.9f), new(81.3f,6.9f), new(937.5f,820.9f), new(937.5f,993.1f), new(723.2f,993.1f), new(62.5f,358f) },
                new PointF[]{ new(923.5f,0f), new(937.5f,0f), new(937.5f,700.2f), new(565.3f,343.3f) },
                new PointF[]{ new(63.6f,475.1f), new(347.2f,747.3f), new(87.5f,1000f), new(61.8f,1000f) },
            };

            private static readonly PointF[] Hex =
            {
                new(500f,20f), new(915.7f,260f), new(915.7f,740f), new(500f,980f), new(84.3f,740f), new(84.3f,260f),
            };

            private static void DrawNMark(Graphics g, Rectangle box, float translate, float scale, Brush brush)
            {
                var s = box.Width / 1000f;
                foreach (var path in NMark)
                {
                    var pts = new PointF[path.Length];
                    for (var i = 0; i < path.Length; i++)
                        pts[i] = new PointF(box.Left + (translate + path[i].X * scale) * s,
                                            box.Top + (translate + path[i].Y * scale) * s);
                    g.FillPolygon(brush, pts);
                }
            }

            private static void DrawNelfePlayLogo(Graphics g, Rectangle box)
            {
                var s = box.Width / 1000f;
                using (var pen = new Pen(NelfeWhite, Math.Max(1.5f, 40f * s)))
                    g.DrawEllipse(pen, box.Left + 20f * s, box.Top + 20f * s, 960f * s, 960f * s);
                using var b = new SolidBrush(NelfeWhite);
                DrawNMark(g, box, 230f, 0.54f, b);
            }

            private static void DrawVerifiedBadge(Graphics g, Rectangle box, bool certified)
            {
                var s = box.Width / 1000f;
                var color = certified ? GoldColor : CheckpointColor; // or si certifié, gris sinon (emplacement)
                var hex = new PointF[Hex.Length];
                for (var i = 0; i < Hex.Length; i++)
                    hex[i] = new PointF(box.Left + Hex[i].X * s, box.Top + Hex[i].Y * s);
                using (var pen = new Pen(color, Math.Max(1.5f, 40f * s)) { LineJoin = LineJoin.Round })
                    g.DrawPolygon(pen, hex);
                using var b = new SolidBrush(color);
                DrawNMark(g, box, 260f, 0.48f, b);
            }

            private static void DrawStateGlyph(Graphics g, Rectangle box, bool paused)
            {
                using var brush = new SolidBrush(AccentColor);
                if (paused)
                {
                    var bw = box.Width / 3;
                    g.FillRectangle(brush, box.Left, box.Top, bw, box.Height);
                    g.FillRectangle(brush, box.Right - bw, box.Top, bw, box.Height);
                }
                else
                {
                    Point[] tri =
                    {
                        new(box.Left, box.Top),
                        new(box.Left, box.Bottom),
                        new(box.Right, box.Top + box.Height / 2),
                    };
                    g.FillPolygon(brush, tri);
                }
            }

            /// <summary>Croix directionnelle : base grise, le(s) bras actif(s) en bleu accent.</summary>
            private static void DrawDpad(Graphics g, Rectangle box, Dir dir)
            {
                var t = box.Width / 3;
                var midX = box.Left + t;
                var midY = box.Top + t;
                using var baseB = new SolidBrush(CheckpointColor);
                using var accB = new SolidBrush(AccentColor);

                // croix de base
                FillRounded(g, baseB, new Rectangle(midX, box.Top, t, box.Height), 4);
                FillRounded(g, baseB, new Rectangle(box.Left, midY, box.Width, t), 4);

                // bras actif(s)
                var half = box.Height / 2;
                switch (dir)
                {
                    case Dir.Up:
                        FillRounded(g, accB, new Rectangle(midX, box.Top, t, half + 2), 4); break;
                    case Dir.Down:
                        FillRounded(g, accB, new Rectangle(midX, box.Top + half - 2, t, half + 2), 4); break;
                    case Dir.LeftRight:
                        FillRounded(g, accB, new Rectangle(box.Left, midY, box.Width / 2 + 2, t), 4);
                        FillRounded(g, accB, new Rectangle(box.Left + box.Width / 2 - 2, midY, box.Width / 2 + 2, t), 4);
                        break;
                }
            }

            /// <summary>Badge de touche « START » (contour accent + texte). Renvoie sa largeur.</summary>
            private static int DrawStartBadge(Graphics g, int x, int mid)
            {
                using var f = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
                const string t = "START";
                var tw = g.MeasureString(t, f);
                var w = (int)Math.Ceiling(tw.Width) + 18;
                const int h = 26;
                var rect = new Rectangle(x, mid - h / 2, w, h);
                using (var pen = new Pen(AccentColor, 1.5f)) DrawRounded(g, pen, rect, 7);
                using var b = new SolidBrush(AccentColor);
                g.DrawString(t, f, b, x + (w - tw.Width) / 2, mid - tw.Height / 2);
                return w;
            }

            private static string Fmt(long frame, double fps)
            {
                var totalSec = frame / (fps <= 0 ? 60 : fps);
                var m = (int)(totalSec / 60);
                var sec = (int)(totalSec % 60);
                return $"{m:00}:{sec:00}";
            }

            private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
            {
                if (radius <= 0 || r.Width < radius * 2 || r.Height < radius * 2) { g.FillRectangle(brush, r); return; }
                using var path = new GraphicsPath();
                var d = radius * 2;
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }

            private static void DrawRounded(Graphics g, Pen pen, Rectangle r, int radius)
            {
                using var path = new GraphicsPath();
                var d = radius * 2;
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.DrawPath(pen, path);
            }
        }
    }

    // ── P/Invoke (topmost sans focus + résolution d'écran) ──
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
