using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Annonce de challenge sur l'écran de la borne : fenêtre centrée (80 % de la
/// surface) avec le fanart du jeu en fond, son logo (marquee), l'objectif du
/// challenge, « Tenez-vous prêt ! » et un compte à rebours — plus le QR de
/// check-in pour que les joueurs prennent la borne et valident leur
/// participation depuis leur téléphone. Poussée par le hub avant le coup
/// d'envoi ; le jeu est lancé à zéro et l'annonce se retire toute seule.
/// </summary>
public sealed class ChallengeAnnounceOverlayService : BackgroundService
{
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<ChallengeAnnounceOverlayService>? _logger;
    private readonly object _sync = new();
    private Thread? _uiThread;
    private ApplicationContext? _applicationContext;
    private Control? _dispatcher;
    private AnnounceForm? _form;

    public ChallengeAnnounceOverlayService(
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<ChallengeAnnounceOverlayService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public sealed record AnnounceState(
        bool Visible, string? GamePath, string? GameName, string? Objective,
        DateTime? StartsAtUtc, string? QrImageUrl, string? Conditions = null);

    private AnnounceState _state = new(false, null, null, null, null, null);

    public AnnounceState GetState() => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        CloseUiThread();
    }

    public async Task ApplyAsync(
        bool visible, string? gamePath, string? gameName, string? objective,
        DateTime? startsAtUtc, string? qrImageUrl, string? conditions = null,
        CancellationToken cancellationToken = default)
    {
        _state = new AnnounceState(visible, gamePath, gameName, objective, startsAtUtc, qrImageUrl, conditions);

        byte[]? qrBytes = null;
        if (visible && !string.IsNullOrWhiteSpace(qrImageUrl))
        {
            try
            {
                qrBytes = await ImageHttp.GetByteArrayAsync(qrImageUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger?.LogWarning(ex, "Challenge announce QR fetch failed: {Url}", qrImageUrl);
            }
        }

        // Médias résolus LOCALEMENT depuis le gamelist de la borne (fanart ou
        // image en fond, marquee en logo) — aucun aller-retour réseau.
        var (fanart, logo) = visible ? ResolveMedia(gamePath) : (null, null);

        EnsureUiThreadStarted(cancellationToken);
        Control? dispatcher;
        lock (_sync)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher is null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!visible)
                {
                    _form?.Hide();
                    completion.TrySetResult();
                    return;
                }

                _form ??= new AnnounceForm(ResolveTargetScreen, () => _form?.Hide());
                _form.Configure(
                    gameName ?? string.Empty,
                    objective ?? string.Empty,
                    conditions ?? string.Empty,
                    startsAtUtc,
                    fanart, logo, qrBytes);
                _form.PositionCentered(ResolveTargetScreen());
                _form.Show();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Challenge announce overlay update failed.");
            }
            finally
            {
                completion.TrySetResult();
            }
        }));
        await completion.Task;
    }

    /// <summary>Fanart (sinon image) + marquee du jeu depuis
    /// roms/&lt;system&gt;/gamelist.xml, chemins résolus relatifs au dossier système.</summary>
    private (string? Fanart, string? Logo) ResolveMedia(string? gamePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                return (null, null);
            }

            var normalized = gamePath.Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            var systemId = normalized.StartsWith("roms/", StringComparison.OrdinalIgnoreCase)
                ? normalized.Split('/')[1]
                : Path.GetFileName(Path.GetDirectoryName(normalized)) ?? string.Empty;
            var systemDir = Path.Combine(RetroBatPaths.RomsRoot, systemId);
            var gamelist = Path.Combine(systemDir, "gamelist.xml");
            if (!File.Exists(gamelist))
            {
                return (null, null);
            }

            var doc = XDocument.Load(gamelist);
            var game = doc.Root?.Elements("game").FirstOrDefault(element =>
                string.Equals(
                    Path.GetFileName(((string?)element.Element("path") ?? "").Replace('\\', '/')),
                    fileName, StringComparison.OrdinalIgnoreCase));
            if (game is null)
            {
                return (null, null);
            }

            string? Resolve(string tag)
            {
                var value = ((string?)game.Element(tag) ?? "").Trim().Replace('\\', '/');
                if (value.Length == 0)
                {
                    return null;
                }

                // Ne retirer QUE le préfixe "./" : les chemins gamelist de
                // RetroBat traversent en "./../../plugins/…" — un TrimStart
                // aveugle des '.' et '/' détruirait la remontée.
                if (value.StartsWith("./", StringComparison.Ordinal))
                {
                    value = value[2..];
                }

                var full = Path.IsPathFullyQualified(value)
                    ? value
                    : Path.GetFullPath(Path.Combine(systemDir, value));
                return File.Exists(full) ? full : null;
            }

            return (Resolve("fanart") ?? Resolve("image") ?? Resolve("thumbnail"), Resolve("marquee"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Challenge announce media resolution failed for {GamePath}", gamePath);
            return (null, null);
        }
    }

    private void EnsureUiThreadStarted(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_uiThread != null)
            {
                return;
            }
        }

        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            var context = new ApplicationContext();
            var dispatcher = new Control();
            dispatcher.CreateControl();
            lock (_sync)
            {
                _applicationContext = context;
                _dispatcher = dispatcher;
            }

            ready.Set();
            Application.Run(context);
            lock (_sync)
            {
                _dispatcher?.Dispose();
                _dispatcher = null;
                _applicationContext = null;
                _form = null;
            }
        })
        {
            IsBackground = true,
            Name = "APIExpose.ChallengeAnnounceOverlay"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        lock (_sync)
        {
            _uiThread = thread;
        }

        ready.Wait(cancellationToken);
    }

    private void CloseUiThread()
    {
        ApplicationContext? context;
        lock (_sync)
        {
            context = _applicationContext;
        }

        try
        {
            context?.MainForm?.BeginInvoke(new Action(() => context.ExitThread()));
            context?.ExitThread();
        }
        catch
        {
        }
    }

    private Screen ResolveTargetScreen()
    {
        var configured = _options.CurrentValue.CabinetBadgeOverlay.Screen;
        if (int.TryParse(configured, out var index) && index >= 0 && index < Screen.AllScreens.Length)
        {
            return Screen.AllScreens[index];
        }

        return ResolveEmulationStationScreen() ?? Screen.PrimaryScreen!;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static Screen? ResolveEmulationStationScreen()
    {
        var handle = FindWindowW(null, "EmulationStation");
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return null;
        }

        return Screen.FromRectangle(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
    }

    private sealed class AnnounceForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr TopMostHandle = new(-1);
        private const uint NoSizeNoActivate = 0x0001 | 0x0010 | 0x0040;

        private readonly System.Windows.Forms.Timer _reassert;
        private readonly System.Windows.Forms.Timer _countdown;
        private readonly Form _backdrop;
        private readonly Label _title;
        private readonly Label _objective;
        private readonly Label _conditions;
        private readonly Label _ready;
        private readonly Label _timer;
        private readonly PictureBox _logo;
        private readonly PictureBox _qr;
        private readonly Label _qrHint;
        private Image? _background;
        private DateTime? _startsAtUtc;
        private int _pulseStep;

        // 70 → 140 pt en ~10 pas de 40 ms : le chiffre « saute » à l'écran.
        private static readonly Font[] _pulseFonts = Enumerable.Range(0, 11)
            .Select(i => new Font("Segoe UI", 70f + i * 7f, FontStyle.Bold))
            .ToArray();

        private static Color Blend(Color from, Color to, double t) => Color.FromArgb(
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));

        public AnnounceForm(Func<Screen> resolveScreen, Action onExpired)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(8, 8, 14);
            DoubleBuffered = true;

            // Voile noir 70 % sur TOUT l'écran, derrière la fenêtre 80 % :
            // l'interface RetroBat s'efface, le challenge prend la scène.
            _backdrop = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.Black,
                Opacity = 0.7
            };

            _reassert = new System.Windows.Forms.Timer { Interval = 3000 };
            _reassert.Tick += (_, _) =>
            {
                if (Visible)
                {
                    PositionCentered(resolveScreen());
                    // Le voile d'abord, l'annonce ENSUITE : le dernier
                    // réaffirmé reste au-dessus.
                    SetWindowPos(_backdrop.Handle, TopMostHandle, _backdrop.Location.X, _backdrop.Location.Y, 0, 0, NoSizeNoActivate);
                    SetWindowPos(Handle, TopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
                }
            };
            _reassert.Start();

            // Décompte animé : chiffre BLANC qui grossit avec un fondu
            // d'apparition puis d'effacement à chaque seconde.
            _countdown = new System.Windows.Forms.Timer { Interval = 40 };
            _countdown.Tick += (_, _) =>
            {
                if (!Visible || _startsAtUtc is null)
                {
                    return;
                }

                var remain = _startsAtUtc.Value - DateTime.UtcNow;
                string text;
                var pulse = false;
                if (remain.TotalSeconds <= 0)
                {
                    text = "GO !";
                    _ready.Text = "Appuyez sur START !";
                    // Filet de sécurité : l'annonce disparaît seule 20 s après
                    // le déblocage même si le hub ne la retire pas.
                    if (remain.TotalSeconds < -20)
                    {
                        onExpired();
                        return;
                    }
                }
                else if (remain.TotalMinutes >= 1)
                {
                    text = $"{(int)remain.TotalMinutes}:{remain.Seconds:00}";
                }
                else
                {
                    text = Math.Ceiling(remain.TotalSeconds).ToString("0");
                    pulse = true;
                }

                if (text != _timer.Text)
                {
                    _timer.Text = text;
                    _pulseStep = 0;
                }

                _pulseStep++;
                if (pulse || text == "GO !")
                {
                    // Grossit (70 → 140 pt) et fond : entrée franche, sortie douce.
                    _timer.Font = _pulseFonts[Math.Min(_pulseFonts.Length - 1, _pulseStep)];
                    _timer.ForeColor = _pulseStep <= 4
                        ? Blend(Color.FromArgb(70, 70, 85), Color.White, _pulseStep / 4.0)
                        : _pulseStep >= 16
                            ? Blend(Color.White, Color.FromArgb(110, 110, 125), Math.Min(1.0, (_pulseStep - 16) / 8.0))
                            : Color.White;
                }
                else
                {
                    _timer.Font = _pulseFonts[2];
                    _timer.ForeColor = Color.White;
                }
            };
            _countdown.Start();

            _title = MakeLabel(new Font("Segoe UI", 26, FontStyle.Bold), Color.White);
            // L'OBJECTIF est la star de l'annonce : gros et doré.
            _objective = MakeLabel(new Font("Segoe UI", 26, FontStyle.Bold), Color.FromArgb(255, 210, 87));
            _conditions = MakeLabel(new Font("Segoe UI", 13.5f, FontStyle.Bold), Color.FromArgb(200, 200, 215));
            _ready = MakeLabel(new Font("Segoe UI", 22, FontStyle.Bold), Color.White);
            _ready.Text = "Tenez-vous prêt !";
            _timer = MakeLabel(_pulseFonts[4], Color.White);
            _logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            _qr = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
            _qrHint = MakeLabel(new Font("Segoe UI", 12, FontStyle.Bold), Color.White);
            _qrHint.Text = "📱 Scannez pour participer — vos scores à votre nom";
            Controls.AddRange([_logo, _title, _objective, _conditions, _ready, _timer, _qr, _qrHint]);
        }

        private static Label MakeLabel(Font font, Color color) => new()
        {
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter
        };

        protected override bool ShowWithoutActivation => true;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // Le voile suit l'annonce (montré dessous, caché avec elle).
            if (Visible)
            {
                _backdrop.Show();
                SetWindowPos(_backdrop.Handle, TopMostHandle, _backdrop.Location.X, _backdrop.Location.Y, 0, 0, NoSizeNoActivate);
                SetWindowPos(Handle, TopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
            }
            else
            {
                _backdrop.Hide();
            }
        }

        public void Configure(
            string gameName, string objective, string conditions, DateTime? startsAtUtc,
            string? fanartPath, string? logoPath, byte[]? qrBytes)
        {
            _startsAtUtc = startsAtUtc;
            _title.Text = gameName;
            _objective.Text = objective;
            // Conditions de participation TOUJOURS annoncées, sous l'objectif.
            _conditions.Text = conditions.Length > 0 ? "🔒 " + conditions : "Ouvert à tous";
            _timer.Text = string.Empty;

            SwapImage(ref _background, fanartPath is null ? null : Image.FromFile(fanartPath));
            var previousLogo = _logo.Image;
            _logo.Image = logoPath is null ? null : Image.FromFile(logoPath);
            previousLogo?.Dispose();
            _title.Visible = _logo.Image is null;

            if (qrBytes is not null)
            {
                using var stream = new MemoryStream(qrBytes);
                var previousQr = _qr.Image;
                _qr.Image = Image.FromStream(stream);
                previousQr?.Dispose();
            }

            _qr.Visible = _qrHint.Visible = _qr.Image is not null;
            Invalidate();
        }

        private static void SwapImage(ref Image? slot, Image? next)
        {
            slot?.Dispose();
            slot = next;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_background is not null)
            {
                // Fanart plein cadre (cover) assombri pour la lisibilité.
                var scale = Math.Max((float)Width / _background.Width, (float)Height / _background.Height);
                var w = _background.Width * scale;
                var h = _background.Height * scale;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                e.Graphics.DrawImage(_background, (Width - w) / 2, (Height - h) / 2, w, h);
                using var veil = new SolidBrush(Color.FromArgb(170, 8, 8, 14));
                e.Graphics.FillRectangle(veil, ClientRectangle);
            }
        }

        public void PositionCentered(Screen screen)
        {
            // 80 % de la surface de l'écran, centré ; le voile couvre tout.
            var area = screen.Bounds;
            Size = new Size((int)(area.Width * 0.8), (int)(area.Height * 0.8));
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
            _backdrop.Bounds = area;

            var w = Width;
            var h = Height;
            // Haut : logo/titre, puis l'OBJECTIF en gros et les conditions
            // juste en dessous — pleine largeur.
            _logo.Bounds = new Rectangle(w / 4, (int)(h * 0.03), w / 2, (int)(h * 0.18));
            _title.Bounds = new Rectangle(0, (int)(h * 0.05), w, (int)(h * 0.13));
            _objective.Bounds = new Rectangle((int)(w * 0.04), (int)(h * 0.23), (int)(w * 0.92), (int)(h * 0.14));
            _conditions.Bounds = new Rectangle((int)(w * 0.05), (int)(h * 0.375), (int)(w * 0.9), (int)(h * 0.06));
            // Bas en DEUX colonnes : QR GÉANT à gauche (on le scanne à
            // plusieurs mètres), « Tenez-vous prêt ! » + chrono à droite —
            // plus jamais l'un qui cache l'autre.
            var qrSize = Math.Min((int)(h * 0.42), 440);
            var qrLeft = (int)(w * 0.25) - qrSize / 2;
            var qrTop = (int)(h * 0.46);
            _qr.Bounds = new Rectangle(Math.Max(16, qrLeft), qrTop, qrSize, qrSize);
            _qrHint.Bounds = new Rectangle(0, Math.Min(h - (int)(h * 0.05) - 4, qrTop + qrSize + 6), w / 2, (int)(h * 0.05));
            _ready.Bounds = new Rectangle(w / 2, (int)(h * 0.45), w / 2, (int)(h * 0.09));
            _timer.Bounds = new Rectangle(w / 2, (int)(h * 0.54), w / 2, (int)(h * 0.32));
            Invalidate();
        }
    }
}
