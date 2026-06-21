using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class TaskProgressOverlayService : BackgroundService, ITaskProgressService
{
    private static readonly Size OverlaySize = new(680, 142);
    private static readonly Padding ScreenPadding = new(24);
    private static readonly Channel<TaskProgressMessage> Queue = Channel.CreateUnbounded<TaskProgressMessage>();

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ILogger<TaskProgressOverlayService>? _logger;
    private readonly object _sync = new();
    private Thread? _uiThread;
    private ApplicationContext? _applicationContext;
    private Control? _dispatcher;
    private ProgressForm? _form;
    private string? _currentTaskId;
    private DateTime _currentTaskShownAtUtc = DateTime.MinValue;
    private System.Windows.Forms.Timer? _completionDelayTimer;

    public TaskProgressOverlayService(
        IOptionsMonitor<ApiExposeOptions> options,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ILogger<TaskProgressOverlayService>? logger = null)
    {
        _options = options;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    public void Report(string taskId, string title, int current, int total, string? detail = null)
    {
        if (!ShouldShowTask(taskId))
        {
            return;
        }

        var safeTotal = Math.Max(1, total);
        var safeCurrent = Math.Max(0, Math.Min(current, safeTotal));
        Queue.Writer.TryWrite(TaskProgressMessage.Report(taskId, title, safeCurrent, safeTotal, detail));
    }

    public void Complete(string taskId)
    {
        if (!_runtimeOptions.IsTaskProgressEnabled())
        {
            return;
        }

        Queue.Writer.TryWrite(TaskProgressMessage.Complete(taskId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !_runtimeOptions.IsTaskProgressEnabled())
        {
            return;
        }

        EnsureUiThreadStarted(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await Queue.Reader.ReadAsync(stoppingToken);
                await DispatchAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Echec du service de progression de tache.");
            }
        }

        CloseUiThread();
    }

    private bool ShouldShowTask(string taskId)
    {
        var options = _options.CurrentValue.TaskProgress;
        if (!_runtimeOptions.IsTaskProgressEnabled())
        {
            return false;
        }

        if (options.ShowTasks.TryGetValue(taskId, out var show))
        {
            return show;
        }

        return false;
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
                _completionDelayTimer?.Dispose();
                _completionDelayTimer = null;
                _form?.Dispose();
                _form = null;
                _dispatcher?.Dispose();
                _dispatcher = null;
                _applicationContext = null;
            }
        })
        {
            IsBackground = true,
            Name = "APIExpose.TaskProgressOverlay"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        lock (_sync)
        {
            _uiThread = thread;
        }

        ready.Wait(cancellationToken);
    }

    private async Task DispatchAsync(TaskProgressMessage message, CancellationToken cancellationToken)
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
                if (message.IsCompletion)
                {
                    CompleteOnUiThread(message.TaskId);
                }
                else
                {
                    ShowOrUpdateOnUiThread(message.TaskId, message.Title!, message.Current, message.Total, message.Detail);
                }

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

    private void ShowOrUpdateOnUiThread(string taskId, string title, int current, int total, string? detail)
    {
        CancelPendingCompletionOnUiThread();
        var form = _form;
        if (form == null || form.IsDisposed)
        {
            form = new ProgressForm(ClampOpacity(_options.CurrentValue.TaskProgress.Opacity), _logger);
            form.FormClosed += (_, _) =>
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_form, form))
                    {
                        _form = null;
                        _currentTaskId = null;
                    }
                }
            };

            _form = form;
            form.Show();
            form.ForceVisibleOnTop();
        }

        if (!string.Equals(_currentTaskId, taskId, StringComparison.OrdinalIgnoreCase))
        {
            _currentTaskShownAtUtc = DateTime.UtcNow;
        }

        _currentTaskId = taskId;
        form.Apply(title, detail, current, total);
        form.PositionOnEmulationStationScreen();
        form.ForceVisibleOnTop();

        _logger?.LogDebug(
            "Task progress displayed: taskId={TaskId}, title={Title}, current={Current}, total={Total}",
            taskId,
            title,
            current,
            total);
    }

    private void CompleteOnUiThread(string taskId)
    {
        if (!string.Equals(_currentTaskId, taskId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var form = _form;
        if (form == null || form.IsDisposed)
        {
            _currentTaskId = null;
            _currentTaskShownAtUtc = DateTime.MinValue;
            return;
        }

        var minimumVisibleMs = Math.Max(0, _options.CurrentValue.TaskProgress.MinimumVisibleMilliseconds);
        var elapsedMs = (int)Math.Floor((DateTime.UtcNow - _currentTaskShownAtUtc).TotalMilliseconds);
        var remainingMs = minimumVisibleMs - elapsedMs;
        if (remainingMs <= 0)
        {
            CloseCurrentProgressOnUiThread(form);
            return;
        }

        CancelPendingCompletionOnUiThread();
        _completionDelayTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, remainingMs) };
        _completionDelayTimer.Tick += (_, _) =>
        {
            if (!string.Equals(_currentTaskId, taskId, StringComparison.OrdinalIgnoreCase))
            {
                CancelPendingCompletionOnUiThread();
                return;
            }

            CloseCurrentProgressOnUiThread(form);
        };
        _completionDelayTimer.Start();
    }

    private void CloseCurrentProgressOnUiThread(ProgressForm form)
    {
        CancelPendingCompletionOnUiThread();
        _currentTaskId = null;
        _currentTaskShownAtUtc = DateTime.MinValue;
        if (!form.IsDisposed)
        {
            form.Close();
        }
    }

    private void CancelPendingCompletionOnUiThread()
    {
        if (_completionDelayTimer == null)
        {
            return;
        }

        _completionDelayTimer.Stop();
        _completionDelayTimer.Dispose();
        _completionDelayTimer = null;
    }

    private void CloseUiThread()
    {
        ApplicationContext? context;
        Control? dispatcher;
        lock (_sync)
        {
            context = _applicationContext;
            dispatcher = _dispatcher;
        }

        if (dispatcher != null && dispatcher.IsHandleCreated)
        {
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _completionDelayTimer?.Dispose();
                    _completionDelayTimer = null;
                    _form?.Close();
                    context?.ExitThread();
                }));
                return;
            }
            catch
            {
                // Fallback below.
            }
        }

        context?.ExitThread();
    }

    private static double ClampOpacity(double value)
    {
        if (value < 0.25d)
        {
            return 0.25d;
        }

        if (value > 1d)
        {
            return 1d;
        }

        return value;
    }

    private sealed class ProgressForm : Form
    {
        private static readonly Color TextColor = ColorTranslator.FromHtml("#FFFFFF");
        private static readonly Color TitleBackColor = ColorTranslator.FromHtml("#2961b0");
        private static readonly Color WindowBackColor = ColorTranslator.FromHtml("#081323");
        private static readonly Color BorderColor = Color.Black;
        private const float TitleFontSize = 20f;
        private const float BodyFontSize = 14f;
        private static readonly PrivateFontCollection OverlayFonts = new();
        private static bool overlayFontsLoaded;

        private readonly ILogger<TaskProgressOverlayService>? _logger;
        private readonly Label _titleLabel;
        private readonly Label _detailLabel;
        private readonly EsProgressBar _progressBar;

        public ProgressForm(double opacity, ILogger<TaskProgressOverlayService>? logger)
        {
            _logger = logger;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = BorderColor;
            ForeColor = TextColor;
            Opacity = opacity;
            Size = OverlaySize;
            Padding = new Padding(2);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = WindowBackColor,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(20, 10, 20, 10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var titlePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = TitleBackColor,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(20, 0, 20, 0)
            };

            _titleLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Tache en cours",
                Font = CreateOverlayFont(TitleFontSize, FontStyle.Bold),
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseCompatibleTextRendering = true,
                Margin = new Padding(0)
            };
            titlePanel.Controls.Add(_titleLabel);

            _progressBar = new EsProgressBar
            {
                Dock = DockStyle.Top,
                Height = 18,
                Margin = new Padding(0, 0, 0, 0)
            };

            _detailLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = string.Empty,
                Font = CreateOverlayFont(BodyFontSize, FontStyle.Regular),
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(20, 8, 20, 0)
            };

            root.Controls.Add(titlePanel);
            root.Controls.Add(_progressBar);
            root.Controls.Add(_detailLabel);
            Controls.Add(root);
        }

        protected override bool ShowWithoutActivation => true;

        public void Apply(string title, string? detail, int current, int total)
        {
            if (IsDisposed)
            {
                return;
            }

            _titleLabel.Text = title;
            _detailLabel.Text = string.IsNullOrWhiteSpace(detail)
                ? $"{current}/{total}"
                : $"{detail}  {current}/{total}";

            var safeTotal = Math.Max(1, total);
            _progressBar.Percent = Math.Max(0, Math.Min(current, safeTotal)) * 100d / safeTotal;
        }

        public void PositionOnEmulationStationScreen()
        {
            var screen = ResolveEmulationStationScreen() ?? Screen.PrimaryScreen;
            if (screen == null)
            {
                CenterToScreen();
                return;
            }

            var area = screen.WorkingArea;
            var x = area.Left + Math.Max(0, (area.Width - Width) / 2);
            var y = area.Bottom - Height - ScreenPadding.Bottom;
            Location = new Point(x, y);
            _logger?.LogDebug("Task progress overlay positioned on screen {DeviceName} at {X},{Y}.", screen.DeviceName, x, y);
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

        private static Font CreateOverlayFont(float size, FontStyle style)
        {
            var family = ResolveOverlayFontFamily();
            return family == null
                ? new Font("Segoe UI", size, style, GraphicsUnit.Pixel)
                : new Font(family, size, style, GraphicsUnit.Pixel);
        }

        private static FontFamily? ResolveOverlayFontFamily()
        {
            if (!overlayFontsLoaded)
            {
                overlayFontsLoaded = true;
                foreach (var fontPath in GetCabinFontCandidates())
                {
                    if (!File.Exists(fontPath))
                    {
                        continue;
                    }

                    try
                    {
                        OverlayFonts.AddFontFile(fontPath);
                    }
                    catch
                    {
                        // Cosmetic fallback only.
                    }
                }
            }

            return OverlayFonts.Families.FirstOrDefault();
        }

        private static IEnumerable<string> GetCabinFontCandidates()
        {
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon", "art", "fonts", "Cabin-Regular.ttf");
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon", "art", "fonts", "Cabin-Bold.ttf");
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon-master", "art", "fonts", "Cabin-Regular.ttf");
            yield return Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon-master", "art", "fonts", "Cabin-Bold.ttf");
        }

        private sealed class EsProgressBar : Control
        {
            private double percent;

            public EsProgressBar()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            public double Percent
            {
                get => percent;
                set
                {
                    percent = Math.Max(0d, Math.Min(100d, value));
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                using var background = new SolidBrush(WindowBackColor);
                e.Graphics.FillRectangle(background, ClientRectangle);

                var fillWidth = (int)Math.Round((Width - 4) * percent / 100d);
                if (fillWidth > 0)
                {
                    using var fill = new SolidBrush(TitleBackColor);
                    e.Graphics.FillRectangle(fill, 2, 2, fillWidth, Math.Max(0, Height - 4));
                }

                using var border = new Pen(BorderColor, 2f);
                e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
            }
        }
    }

    private sealed record TaskProgressMessage(
        string TaskId,
        bool IsCompletion,
        string? Title,
        int Current,
        int Total,
        string? Detail)
    {
        public static TaskProgressMessage Report(string taskId, string title, int current, int total, string? detail)
            => new(taskId, false, title, current, total, detail);

        public static TaskProgressMessage Complete(string taskId)
            => new(taskId, true, null, 0, 0, null);
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
