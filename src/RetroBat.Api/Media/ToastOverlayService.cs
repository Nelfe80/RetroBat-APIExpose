using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class ToastOverlayService : BackgroundService, IToastNotificationService
{
    private static readonly Size ToastSize = new(460, 172);
    private static readonly Size ToastWithImageSize = new(540, 192);
    private static readonly Padding ScreenPadding = new(24);
    private static readonly int ToastSpacing = 14;
    private static readonly Channel<ToastNotification> Queue = Channel.CreateUnbounded<ToastNotification>();

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ILogger<ToastOverlayService>? _logger;
    private readonly object _sync = new();
    private readonly List<ToastForm> _activeToasts = new();
    private Thread? _uiThread;
    private ApplicationContext? _applicationContext;
    private Control? _dispatcher;

    public ToastOverlayService(
        IOptionsMonitor<ApiExposeOptions> options,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ILogger<ToastOverlayService>? logger = null)
    {
        _options = options;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(ToastNotification notification, CancellationToken cancellationToken = default)
    {
        if (!_runtimeOptions.AreToastNotificationsEnabled())
        {
            return ValueTask.CompletedTask;
        }

        var normalized = NormalizeNotification(notification);
        return Queue.Writer.WriteAsync(normalized, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        EnsureUiThreadStarted(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var notification = await Queue.Reader.ReadAsync(stoppingToken);
                await ShowToastOnUiThreadAsync(notification, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Echec du service de toast.");
            }
        }

        CloseUiThread();
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
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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
                _activeToasts.Clear();
            }
        })
        {
            IsBackground = true,
            Name = "APIExpose.ToastOverlay"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        lock (_sync)
        {
            _uiThread = thread;
        }

        ready.Wait(cancellationToken);
    }

    private async Task ShowToastOnUiThreadAsync(ToastNotification notification, CancellationToken cancellationToken)
    {
        Control? dispatcher;
        lock (_sync)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher == null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ShowToastInternal(notification);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }));

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await completion.Task;
    }

    private void ShowToastInternal(ToastNotification notification)
    {
        var toast = new ToastForm(notification, _options.CurrentValue.Toasts.Opacity, _logger);
        toast.FormClosed += (_, _) => OnToastClosed(toast);

        lock (_sync)
        {
            _activeToasts.Add(toast);
            RepositionToasts(notification.Position);
        }

        toast.Show();
        toast.ForceVisibleOnTop();
        toast.Play(notification.Animation);
        _logger?.LogInformation(
            "Toast displayed: title={Title}, position={Position}, animation={Animation}, targetLocation={X},{Y}",
            notification.Title,
            notification.Position,
            notification.Animation,
            toast.TargetLocation.X,
            toast.TargetLocation.Y);
    }

    private void OnToastClosed(ToastForm toast)
    {
        lock (_sync)
        {
            _activeToasts.Remove(toast);
            RepositionToasts(toast.PositionKind);
        }
    }

    private void RepositionToasts(ToastPosition position)
    {
        var screen = ResolveEmulationStationScreen() ?? Screen.PrimaryScreen;
        if (screen == null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var sameScreenToasts = _activeToasts
            .Where(toast => toast.PositionKind == position)
            .OrderBy(toast => toast.CreatedAtUtc)
            .ToList();

        for (var index = 0; index < sameScreenToasts.Count; index++)
        {
            var toast = sameScreenToasts[index];
            var size = toast.Size;
            var location = ComputeToastLocation(area, size, position, index);
            toast.SetTargetLocation(location);
        }
    }

    private static Point ComputeToastLocation(Rectangle area, Size size, ToastPosition position, int index)
    {
        var verticalOffset = index * (size.Height + ToastSpacing);
        var horizontalOffset = index * (size.Width + ToastSpacing);

        return position switch
        {
            ToastPosition.TopLeft => new Point(area.Left + ScreenPadding.Left, area.Top + ScreenPadding.Top + verticalOffset),
            ToastPosition.TopCenter => new Point(area.Left + Math.Max(0, (area.Width - size.Width) / 2), area.Top + ScreenPadding.Top + verticalOffset),
            ToastPosition.TopRight => new Point(area.Right - size.Width - ScreenPadding.Right, area.Top + ScreenPadding.Top + verticalOffset),
            ToastPosition.MiddleLeft => new Point(area.Left + ScreenPadding.Left, area.Top + Math.Max(0, (area.Height - size.Height) / 2) + verticalOffset),
            ToastPosition.Center => new Point(area.Left + Math.Max(0, (area.Width - size.Width) / 2), area.Top + Math.Max(0, (area.Height - size.Height) / 2) + verticalOffset),
            ToastPosition.MiddleRight => new Point(area.Right - size.Width - ScreenPadding.Right, area.Top + Math.Max(0, (area.Height - size.Height) / 2) + verticalOffset),
            ToastPosition.BottomLeft => new Point(area.Left + ScreenPadding.Left, area.Bottom - size.Height - ScreenPadding.Bottom - verticalOffset),
            ToastPosition.BottomCenter => new Point(area.Left + Math.Max(0, (area.Width - size.Width) / 2), area.Bottom - size.Height - ScreenPadding.Bottom - verticalOffset),
            _ => new Point(area.Right - size.Width - ScreenPadding.Right, area.Bottom - size.Height - ScreenPadding.Bottom - verticalOffset)
        };
    }

    private static ToastNotification NormalizeNotification(ToastNotification notification)
    {
        return new ToastNotification
        {
            Type = string.IsNullOrWhiteSpace(notification.Type)
                ? InferToastType(notification.Title)
                : notification.Type.Trim(),
            Title = notification.Title?.Trim() ?? string.Empty,
            Message = notification.Message?.Trim() ?? string.Empty,
            ImagePath = string.IsNullOrWhiteSpace(notification.ImagePath) ? null : notification.ImagePath.Trim(),
            DurationMs = notification.DurationMs <= 0 ? 4000 : notification.DurationMs,
            Position = notification.Position,
            Animation = notification.Animation
        };
    }

    private static string InferToastType(string? title)
    {
        var normalized = (title ?? string.Empty).Trim();
        if (normalized.Contains("scrap", StringComparison.OrdinalIgnoreCase))
        {
            return "Scraper";
        }

        return "UI";
    }

    private void CloseUiThread()
    {
        ApplicationContext? context;
        lock (_sync)
        {
            context = _applicationContext;
        }

        if (context == null)
        {
            return;
        }

        try
        {
            context.MainForm?.BeginInvoke(new Action(() => context.ExitThread()));
            context.ExitThread();
        }
        catch
        {
            // Ignore shutdown issues.
        }
    }

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
                        if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                        {
                            continue;
                        }

                        if (process.ProcessName.Contains("emulationstation", StringComparison.OrdinalIgnoreCase)
                            || (process.MainWindowTitle ?? string.Empty).Contains("EmulationStation", StringComparison.OrdinalIgnoreCase))
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

    private sealed class ToastForm : Form
    {
        private static readonly Color TextColor = ColorTranslator.FromHtml("#FFFFFF");
        private static readonly Color TitleBackColor = ColorTranslator.FromHtml("#2961b0");
        private static readonly Color WindowBackColor = ColorTranslator.FromHtml("#081323");
        private static readonly Color BorderColor = Color.Black;
        private const float TitleFontSize = 20f;
        private const float BodyFontSize = 14f;
        private static readonly PrivateFontCollection ToastFonts = new();
        private static bool toastFontsLoaded;

        private readonly ToastNotification _notification;
        private readonly ILogger<ToastOverlayService>? _logger;
        private readonly System.Windows.Forms.Timer _closeTimer;
        private readonly System.Windows.Forms.Timer _animationTimer;
        private readonly double _targetOpacity;
        private Image? _loadedImage;
        private Image? _typeIcon;
        private Point _targetLocation;
        private int _animationStep;
        private int _maxAnimationSteps = 10;

        public ToastForm(ToastNotification notification, double opacity, ILogger<ToastOverlayService>? logger)
        {
            _notification = notification;
            _logger = logger;
            CreatedAtUtc = DateTime.UtcNow;
            PositionKind = notification.Position;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = WindowBackColor;
            ForeColor = TextColor;
            _targetOpacity = Math.Clamp(opacity, 0.35d, 1d);
            Opacity = 0d;
            Size = string.IsNullOrWhiteSpace(notification.ImagePath) ? ToastSize : ToastWithImageSize;

            var surface = new ToastSurface
            {
                Dock = DockStyle.Fill,
                BackColor = WindowBackColor
            };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Padding = new Padding(22, 12, 22, 12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var titlePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = TitleBackColor,
                Padding = new Padding(20, 0, 20, 0),
                Margin = new Padding(0, 0, 0, 8)
            };
            titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _typeIcon = LoadImageCopy(ResolveToastTypeIconPath(notification.Type));
            if (_typeIcon != null)
            {
                titlePanel.Controls.Add(new PictureBox
                {
                    Image = _typeIcon,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Dock = DockStyle.Fill,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 6, 10, 6)
                }, 0, 0);
            }

            var titleLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = (notification.Title ?? string.Empty).ToUpperInvariant(),
                Font = CreateToastFont(TitleFontSize, FontStyle.Bold),
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseCompatibleTextRendering = true,
                Margin = new Padding(0)
            };
            titlePanel.Controls.Add(titleLabel, 1, 0);

            var bodyPanel = new TableLayoutPanel
            {
                BackColor = Color.Transparent,
                ColumnCount = string.IsNullOrWhiteSpace(notification.ImagePath) ? 1 : 2,
                RowCount = 1,
                Dock = DockStyle.Fill,
                Margin = new Padding(20, 0, 20, 4)
            };

            if (!string.IsNullOrWhiteSpace(notification.ImagePath) && File.Exists(notification.ImagePath))
            {
                _loadedImage = LoadImageCopy(notification.ImagePath);
                if (_loadedImage != null)
                {
                    bodyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
                    bodyPanel.Controls.Add(new PictureBox
                    {
                        Image = _loadedImage,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Dock = DockStyle.Fill,
                        BackColor = Color.Transparent,
                        Margin = new Padding(0, 0, 16, 0)
                    }, 0, 0);
                }
            }

            bodyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var messageColumn = bodyPanel.ColumnCount == 1 || _loadedImage == null ? 0 : 1;
            bodyPanel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = notification.Message,
                Font = CreateToastFont(BodyFontSize, FontStyle.Regular),
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = false,
                Margin = new Padding(0)
            }, messageColumn, 0);

            root.Controls.Add(titlePanel, 0, 0);
            root.Controls.Add(bodyPanel, 0, 1);
            surface.Controls.Add(root);
            Controls.Add(surface);

            _closeTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1200, notification.DurationMs) };
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer.Stop();
                Close();
            };

            _animationTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animationTimer.Tick += (_, _) => AdvanceAnimation();

            Shown += (_, _) =>
            {
                _closeTimer.Start();
            };
        }

        public DateTime CreatedAtUtc { get; }
        public ToastPosition PositionKind { get; }
        public Point TargetLocation => _targetLocation;

        public void SetTargetLocation(Point location)
        {
            _targetLocation = location;
            if (_notification.Animation == ToastAnimation.None)
            {
                Location = location;
            }
        }

        public void ForceVisibleOnTop()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            Show();
            BringToFront();
            Activate();
            SetWindowPos(
                Handle,
                HWND_TOPMOST,
                Left,
                Top,
                Width,
                Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public void Play(ToastAnimation animation)
        {
            if (animation == ToastAnimation.None)
            {
                Location = _targetLocation;
                Opacity = _targetOpacity;
                return;
            }

            Refresh();
            Location = GetAnimationStartLocation(animation, _targetLocation);
            _animationStep = 0;
            _animationTimer.Start();
        }

        private void AdvanceAnimation()
        {
            _animationStep++;
            var progress = Math.Min(1d, _animationStep / (double)_maxAnimationSteps);

            if (_notification.Animation == ToastAnimation.FadeIn)
            {
                Opacity = _targetOpacity * progress;
                Location = _targetLocation;
            }
            else
            {
                var start = GetAnimationStartLocation(_notification.Animation, _targetLocation);
                var x = (int)Math.Round(start.X + ((_targetLocation.X - start.X) * progress));
                var y = (int)Math.Round(start.Y + ((_targetLocation.Y - start.Y) * progress));
                Location = new Point(x, y);
                Opacity = Math.Max(0.7d, _targetOpacity * progress);
            }

            if (progress >= 1d)
            {
                _animationTimer.Stop();
                Location = _targetLocation;
                Opacity = _targetOpacity;
            }
        }

        private Point GetAnimationStartLocation(ToastAnimation animation, Point target)
        {
            return animation switch
            {
                ToastAnimation.SlideFromLeft => new Point(target.X - 80, target.Y),
                ToastAnimation.SlideFromTop => new Point(target.X, target.Y - 50),
                ToastAnimation.SlideFromBottom => new Point(target.X, target.Y + 50),
                _ => new Point(target.X + 80, target.Y)
            };
        }

        private static Font CreateToastFont(float size, FontStyle style)
        {
            var family = ResolveToastFontFamily();
            return family == null
                ? new Font("Segoe UI", size, style, GraphicsUnit.Pixel)
                : new Font(family, size, style, GraphicsUnit.Pixel);
        }

        private static FontFamily? ResolveToastFontFamily()
        {
            if (!toastFontsLoaded)
            {
                toastFontsLoaded = true;
                foreach (var fontPath in GetCabinFontCandidates())
                {
                    if (!File.Exists(fontPath))
                    {
                        continue;
                    }

                    try
                    {
                        ToastFonts.AddFontFile(fontPath);
                    }
                    catch
                    {
                        // Font loading is cosmetic; keep the toast available with the system font.
                    }
                }
            }

            return ToastFonts.Families.FirstOrDefault();
        }

        private static IEnumerable<string> GetCabinFontCandidates()
        {
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon", "art", "fonts", "Cabin-Regular.ttf");
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon", "art", "fonts", "Cabin-Bold.ttf");
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon-master", "art", "fonts", "Cabin-Regular.ttf");
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon-master", "art", "fonts", "Cabin-Bold.ttf");
        }

        private static string ResolveToastTypeIconPath(string? toastType)
        {
            var iconName = NormalizeToastTypeIconName(toastType);
            foreach (var themeName in new[] { "es-theme-carbon", "es-theme-carbon-master" })
            {
                var candidate = Path.Combine(RetroBatPaths.EmulationStationThemesRoot, themeName, "art", "icons", iconName + ".png");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static string NormalizeToastTypeIconName(string? toastType)
        {
            var normalized = (toastType ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "UI";
            }

            return normalized.ToLowerInvariant() switch
            {
                "scrape" or "scraper" or "scraping" => "Scraper",
                "controller" or "controllers" or "controls" => "Controllers",
                "game" or "games" => "Games",
                "network" => "Network",
                "manual" => "Manual",
                "system" => "System",
                "update" or "updates" => "Updates",
                "sound" => "Sound",
                "contest" => "Contest",
                "quit" => "Quit",
                "restart" => "Restart",
                "shutdown" => "Shutdown",
                "advanced" => "Advanced",
                _ => "UI"
            };
        }

        private Image? LoadImageCopy(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            try
            {
                using var image = Image.FromFile(imagePath);
                return new Bitmap(image);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Impossible de charger l'image du toast {ImagePath}.", imagePath);
                return null;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_COMPOSITED = 0x02000000;
                var createParams = base.CreateParams;
                createParams.ExStyle |= WS_EX_COMPOSITED;
                return createParams;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closeTimer.Dispose();
                _animationTimer.Dispose();
                _loadedImage?.Dispose();
                _typeIcon?.Dispose();
            }

            base.Dispose(disposing);
        }

        private sealed class ToastSurface : Panel
        {
            public ToastSurface()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.None;
                var bounds = ClientRectangle;
                using var background = new SolidBrush(WindowBackColor);
                e.Graphics.FillRectangle(background, bounds);

                base.OnPaint(e);

                using var borderPen = new Pen(BorderColor, 2f);
                e.Graphics.DrawRectangle(borderPen, 1, 1, Width - 3, Height - 3);
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
