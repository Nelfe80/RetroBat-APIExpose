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
        DateTime? StartsAtUtc, string? QrImageUrl);

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
        DateTime? startsAtUtc, string? qrImageUrl, CancellationToken cancellationToken = default)
    {
        _state = new AnnounceState(visible, gamePath, gameName, objective, startsAtUtc, qrImageUrl);

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
        private readonly Label _ready;
        private readonly Label _timer;
        private readonly PictureBox _logo;
        private readonly PictureBox _qr;
        private readonly Label _qrHint;
        private Image? _background;
        private DateTime? _startsAtUtc;

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

            _countdown = new System.Windows.Forms.Timer { Interval = 250 };
            _countdown.Tick += (_, _) =>
            {
                if (!Visible || _startsAtUtc is null)
                {
                    return;
                }

                var remain = _startsAtUtc.Value - DateTime.UtcNow;
                if (remain.TotalSeconds <= 0)
                {
                    _timer.Text = "GO !";
                    // Le jeu est débloqué : le joueur entre en partie et
                    // appuie sur START — le gérant lance le challenge quand
                    // tout le monde est prêt.
                    _ready.Text = "Appuyez sur START !";
                    // Filet de sécurité : l'annonce disparaît seule 20 s après
                    // le déblocage même si le hub ne la retire pas.
                    if (remain.TotalSeconds < -20)
                    {
                        onExpired();
                    }
                }
                else
                {
                    _timer.Text = remain.TotalMinutes >= 1
                        ? $"{(int)remain.TotalMinutes}:{remain.Seconds:00}"
                        : remain.Seconds.ToString();
                }
            };
            _countdown.Start();

            _title = MakeLabel(new Font("Segoe UI", 26, FontStyle.Bold), Color.White);
            _objective = MakeLabel(new Font("Segoe UI", 18, FontStyle.Bold), Color.FromArgb(255, 210, 87));
            _ready = MakeLabel(new Font("Segoe UI", 22, FontStyle.Bold), Color.White);
            _ready.Text = "Tenez-vous prêt !";
            _timer = MakeLabel(new Font("Segoe UI", 64, FontStyle.Bold), Color.FromArgb(77, 163, 255));
            _logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            _qr = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
            _qrHint = MakeLabel(new Font("Segoe UI", 12, FontStyle.Bold), Color.White);
            _qrHint.Text = "📱 Scannez pour participer — vos scores à votre nom";
            Controls.AddRange([_logo, _title, _objective, _ready, _timer, _qr, _qrHint]);
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
            string gameName, string objective, DateTime? startsAtUtc,
            string? fanartPath, string? logoPath, byte[]? qrBytes)
        {
            _startsAtUtc = startsAtUtc;
            _title.Text = gameName;
            _objective.Text = objective;
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
            _logo.Bounds = new Rectangle(w / 4, (int)(h * 0.03), w / 2, (int)(h * 0.18));
            _title.Bounds = new Rectangle(0, (int)(h * 0.06), w, (int)(h * 0.13));
            _objective.Bounds = new Rectangle((int)(w * 0.05), (int)(h * 0.24), (int)(w * 0.9), (int)(h * 0.14));
            _ready.Bounds = new Rectangle(0, (int)(h * 0.39), w, (int)(h * 0.09));
            _timer.Bounds = new Rectangle(0, (int)(h * 0.48), w, (int)(h * 0.14));
            // QR volontairement GRAND (on le scanne à plusieurs mètres), avec
            // une vraie respiration au-dessus.
            var qrSize = Math.Min((int)(h * 0.28), 340);
            var qrTop = (int)(h * 0.66);
            _qr.Bounds = new Rectangle((w - qrSize) / 2, qrTop, qrSize, qrSize);
            _qrHint.Bounds = new Rectangle(0, Math.Min(h - (int)(h * 0.045) - 4, qrTop + qrSize + 6), w, (int)(h * 0.045));
            Invalidate();
        }
    }
}
