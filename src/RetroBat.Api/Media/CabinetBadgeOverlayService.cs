using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Media;

/// <summary>
/// Small persistent badge over the RetroBat screen (bottom-right): the
/// cabinet's check-in QR code and its number, so a player identifies from
/// their phone without any printed sticker. Driven by the fleet hub
/// (visible on a free cabinet, hidden while a player is checked in).
/// Disabled by default — venue opt-in (CabinetBadgeOverlay:Enabled).
/// </summary>
public sealed class CabinetBadgeOverlayService : BackgroundService
{
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<CabinetBadgeOverlayService>? _logger;
    private readonly object _sync = new();
    private Thread? _uiThread;
    private ApplicationContext? _applicationContext;
    private Control? _dispatcher;
    private BadgeForm? _form;
    private string? _currentImageUrl;

    public CabinetBadgeOverlayService(
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<CabinetBadgeOverlayService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public sealed record BadgeState(
        bool Visible, string? ImageUrl, string? Label, string? Mode = "qr",
        string? Subtitle = null, string? Honors = null, DateTime? ChallengeEndsAtUtc = null);

    private BadgeState _state = new(false, null, null);

    public BadgeState GetState() => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The UI thread starts lazily on the first Apply; nothing to poll.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        CloseUiThread();
    }

    /// <summary>Shows, updates or hides the badge. Mode « qr » : image
    /// telechargee (checkin-qr du hub). Mode « player » : plaque joueur —
    /// avatar pixel DESSINE localement (seed+colors, meme algorithme que la
    /// plateforme), pseudo en label, rang en sous-titre.</summary>
    public async Task ApplyAsync(
        bool visible, string? imageUrl, string? label,
        string? mode = null, string? seed = null, int? colors = null, string? subtitle = null,
        string? honors = null, DateTime? challengeEndsAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var playerMode = string.Equals(mode, "player", StringComparison.OrdinalIgnoreCase);
        if (!_options.CurrentValue.CabinetBadgeOverlay.Enabled)
        {
            _state = new BadgeState(false, imageUrl, label, playerMode ? "player" : "qr", subtitle, honors, challengeEndsAtUtc);
            return;
        }

        byte[]? imageBytes = null;
        if (visible && !playerMode && !string.IsNullOrWhiteSpace(imageUrl) && imageUrl != _currentImageUrl)
        {
            try
            {
                imageBytes = await ImageHttp.GetByteArrayAsync(imageUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger?.LogWarning(ex, "Cabinet badge image fetch failed: {ImageUrl}", imageUrl);
            }
        }

        _state = new BadgeState(visible, imageUrl, label, playerMode ? "player" : "qr", subtitle, honors, challengeEndsAtUtc);
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

                _form ??= new BadgeForm(
                    _options.CurrentValue.CabinetBadgeOverlay.Opacity,
                    _options.CurrentValue.CabinetBadgeOverlay.Title,
                    ResolveTargetScreen);
                if (playerMode)
                {
                    _form.SetPlayer(
                        seed ?? label ?? "player", Math.Clamp(colors ?? 1, 1, 6),
                        label ?? string.Empty, honors ?? string.Empty, subtitle ?? string.Empty,
                        challengeEndsAtUtc);
                    _currentImageUrl = null;
                }
                else
                {
                    if (imageBytes is not null)
                    {
                        _form.SetImage(imageBytes);
                        _currentImageUrl = imageUrl;
                    }

                    _form.SetQrMode(label ?? string.Empty);
                }

                _form.PositionBottomRight(ResolveTargetScreen());
                _form.Show();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cabinet badge overlay update failed.");
            }
            finally
            {
                completion.TrySetResult();
            }
        }));
        await completion.Task;
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
            Name = "APIExpose.CabinetBadgeOverlay"
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

    /// <summary>Écran cible : index configuré (marquee/topper…) ou, en
    /// « auto », l'écran où tourne EmulationStation.</summary>
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
        if (handle == IntPtr.Zero)
        {
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero &&
                            process.ProcessName.Contains("emulationstation", StringComparison.OrdinalIgnoreCase))
                        {
                            handle = process.MainWindowHandle;
                            break;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return null;
        }

        return Screen.FromRectangle(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
    }

    private sealed class BadgeForm : Form
    {
        private readonly PictureBox _picture;
        private readonly Label _label;
        private readonly Label _titleLabel;
        private readonly Label _subtitleLabel;
        private readonly Label _challengeTimer;
        private readonly System.Windows.Forms.Timer _reassert;
        private readonly System.Windows.Forms.Timer _challengeTick;
        private readonly string _qrTitle;
        private DateTime? _challengeEndsAtUtc;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr TopMostHandle = new(-1);
        private const uint NoSizeNoActivate = 0x0001 /*NOSIZE*/ | 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/;

        public BadgeForm(double opacity, string title, Func<Screen> resolveScreen)
        {
            // EmulationStation repasse au-dessus a chaque (re)lancement : on
            // reaffirme position + TopMost periodiquement, sans jamais voler
            // le focus.
            _reassert = new System.Windows.Forms.Timer { Interval = 3000 };
            _reassert.Tick += (_, _) =>
            {
                if (!Visible)
                {
                    return;
                }

                PositionBottomRight(resolveScreen());
                SetWindowPos(Handle, TopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
            };
            _reassert.Start();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = Math.Clamp(opacity, 0.3, 1.0);
            // Fond NOIR (discret sur l'ecran de la borne) — le blanc est
            // reserve au QR lui-meme, indispensable au scan.
            _qrTitle = title;
            var dark = Color.FromArgb(12, 12, 18);
            BackColor = dark;
            Size = new Size(300, 404);
            var titleLabel = new Label
            {
                Bounds = new Rectangle(0, 8, 300, 34),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 17, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = dark,
                Text = title
            };
            var subtitleLabel = new Label
            {
                Bounds = new Rectangle(0, 42, 300, 22),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = Color.FromArgb(150, 150, 170),
                BackColor = dark,
                Text = title.Equals("Scan to play", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : "Scan to play"
            };
            _picture = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(10, 70, 280, 280),
                BackColor = Color.White
            };
            _label = new Label
            {
                Bounds = new Rectangle(0, 354, 300, 44),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 19, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = dark
            };
            _titleLabel = titleLabel;
            _subtitleLabel = subtitleLabel;
            // Cartouche chrono d'un challenge chronométré : temps restant,
            // affiché AU-DESSUS de la plaque joueur.
            _challengeTimer = new Label
            {
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 210, 87),
                BackColor = dark,
                Visible = false
            };
            _challengeTick = new System.Windows.Forms.Timer { Interval = 500 };
            _challengeTick.Tick += (_, _) =>
            {
                if (!Visible || _challengeEndsAtUtc is null || !_challengeTimer.Visible)
                {
                    return;
                }

                var remain = _challengeEndsAtUtc.Value - DateTime.UtcNow;
                _challengeTimer.Text = remain <= TimeSpan.Zero
                    ? "⏱ Terminé !"
                    : $"⏱ {(int)remain.TotalMinutes}:{remain.Seconds:00}";
            };
            _challengeTick.Start();
            Controls.Add(titleLabel);
            Controls.Add(subtitleLabel);
            Controls.Add(_challengeTimer);
            Controls.Add(_picture);
            Controls.Add(_label);
        }

        protected override bool ShowWithoutActivation => true;

        public void SetImage(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var image = Image.FromStream(stream);
            var previous = _picture.Image;
            _picture.Image = image;
            previous?.Dispose();
        }

        public void SetLabel(string label) => _label.Text = "\U0001F579 " + label;

        /// <summary>Mode borne libre : grande carte QR + accroche « scan ».</summary>
        public void SetQrMode(string cabinetLabel)
        {
            Size = new Size(300, 404);
            _titleLabel.Visible = true;
            _subtitleLabel.Visible = true;
            _challengeTimer.Visible = false;
            _challengeEndsAtUtc = null;
            _titleLabel.Text = _qrTitle;
            _subtitleLabel.Text = _qrTitle.Equals("Scan to play", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : "Scan to play";
            _picture.Bounds = new Rectangle(10, 70, 280, 280);
            _picture.BackColor = Color.White;
            _label.Bounds = new Rectangle(0, 354, 300, 44);
            _label.TextAlign = ContentAlignment.MiddleCenter;
            _label.Font = new Font("Segoe UI", 19, FontStyle.Bold);
            SetLabel(cabinetLabel);
        }

        /// <summary>Plaque joueur DISCRETE : une seule ligne — avatar pixel a
        /// gauche, pseudo + trophees, contest et rang s'il y en a un. Avec un
        /// challenge chronométré : cartouche ⏱ temps restant AU-DESSUS.</summary>
        public void SetPlayer(string seed, int colors, string pseudo, string honors, string subtitle, DateTime? challengeEndsAtUtc = null)
        {
            var text = pseudo;
            if (honors.Length > 0)
            {
                text += "  " + honors;
            }

            if (subtitle.Length > 0)
            {
                text += "  ·  " + subtitle;
            }

            _challengeEndsAtUtc = challengeEndsAtUtc;
            var timerBand = challengeEndsAtUtc is not null ? 26 : 0;
            var font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            var textWidth = TextRenderer.MeasureText(text, font).Width;
            var width = Math.Max(150, 52 + textWidth + 12);
            Size = new Size(width, 44 + timerBand);
            _titleLabel.Visible = false;
            _subtitleLabel.Visible = false;
            _challengeTimer.Visible = timerBand > 0;
            _challengeTimer.Bounds = new Rectangle(0, 0, width, timerBand);
            _picture.Bounds = new Rectangle(4, timerBand + 4, 36, 36);
            _picture.BackColor = Color.FromArgb(12, 12, 18);
            _label.Bounds = new Rectangle(48, timerBand, width - 52, 44);
            _label.TextAlign = ContentAlignment.MiddleLeft;
            _label.Font = font;
            _label.Text = text;
            var previous = _picture.Image;
            _picture.Image = RenderIdenticon(seed, colors, 36);
            previous?.Dispose();
        }

        /// <summary>Identicon 8x8 symetrique — portage GDI de l'algorithme SVG
        /// de la plateforme (SHA-256, palette HSL par niveau de couleurs).</summary>
        private static Bitmap RenderIdenticon(string seed, int colors, int size)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
            var baseHue = (hash[0] << 8 | hash[1]) % 360;
            var palette = new Color[colors];
            for (var index = 0; index < colors; index++)
            {
                palette[index] = FromHsl(
                    (baseHue + index * (300 / colors) + hash[2] % 25) % 360,
                    (62 + index * 4) / 100.0, (52 + index * 3) / 100.0);
            }

            var small = new Bitmap(8, 8);
            using (var graphics = Graphics.FromImage(small))
            {
                graphics.Clear(Color.FromArgb(20, 20, 30));
            }

            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var bitIndex = y * 4 + x;
                    if ((hash[3 + bitIndex / 8] >> (bitIndex % 8) & 1) == 0)
                    {
                        continue;
                    }

                    var color = palette[hash[(11 + bitIndex) % hash.Length] % palette.Length];
                    small.SetPixel(x, y, color);
                    small.SetPixel(7 - x, y, color);
                }
            }

            var scaled = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(small, 0, 0, size, size);
            }

            small.Dispose();
            return scaled;
        }

        private static Color FromHsl(double hue, double saturation, double lightness)
        {
            var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            var x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
            var m = lightness - c / 2;
            var (r, g, b) = hue switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };
            return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        public void PositionBottomRight(Screen screen)
        {
            // La plaque joueur colle au bord bas (4 px) pour cacher le moins
            // possible de jeu ; le grand QR de borne libre garde sa marge.
            var margin = _titleLabel.Visible ? 16 : 4;
            var area = screen.Bounds;
            Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);
        }
    }
}
