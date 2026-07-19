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

    public sealed record BadgeState(bool Visible, string? ImageUrl, string? Label);

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

    /// <summary>Shows, updates or hides the badge. Fetches the QR image when
    /// the URL changes (typically the hub's checkin-qr endpoint).</summary>
    public async Task ApplyAsync(bool visible, string? imageUrl, string? label, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.CabinetBadgeOverlay.Enabled)
        {
            _state = new BadgeState(false, imageUrl, label);
            return;
        }

        byte[]? imageBytes = null;
        if (visible && !string.IsNullOrWhiteSpace(imageUrl) && imageUrl != _currentImageUrl)
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

        _state = new BadgeState(visible, imageUrl, label);
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

                _form ??= new BadgeForm(_options.CurrentValue.CabinetBadgeOverlay.Opacity);
                if (imageBytes is not null)
                {
                    _form.SetImage(imageBytes);
                    _currentImageUrl = imageUrl;
                }

                _form.SetLabel(label ?? string.Empty);
                _form.PositionBottomRight(ResolveEmulationStationScreen() ?? Screen.PrimaryScreen!);
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

        public BadgeForm(double opacity)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = Math.Clamp(opacity, 0.3, 1.0);
            BackColor = Color.White;
            Size = new Size(150, 178);
            _picture = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(5, 5, 140, 140),
                BackColor = Color.White
            };
            _label = new Label
            {
                Bounds = new Rectangle(0, 148, 150, 26),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(20, 20, 30),
                BackColor = Color.White
            };
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

        public void PositionBottomRight(Screen screen)
        {
            var area = screen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }
    }
}
