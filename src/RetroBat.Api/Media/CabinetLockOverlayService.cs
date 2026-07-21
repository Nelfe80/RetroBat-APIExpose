using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Écran de verrouillage de la borne, poussé par le hub quand la salle la
/// réserve (préparation d'événement, maintenance…). Plein écran : un beau
/// fanart en fond — celui du jeu en cours, sinon un jeu du système en cours,
/// sinon fond sombre — avec la raison en très grand (« Borne réservée »,
/// « Borne en maintenance »). Le QR de check-in est masqué par le hub pendant
/// le verrouillage : personne ne scanne pour rien.
/// Même opt-in salle que le badge (CabinetBadgeOverlay:Enabled).
/// </summary>
public sealed class CabinetLockOverlayService : BackgroundService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiContext _context;
    private readonly ILogger<CabinetLockOverlayService>? _logger;
    private readonly object _sync = new();
    private Thread? _uiThread;
    private ApplicationContext? _applicationContext;
    private Control? _dispatcher;
    private LockForm? _form;

    public CabinetLockOverlayService(
        IOptionsMonitor<ApiExposeOptions> options,
        ApiContext context,
        ILogger<CabinetLockOverlayService>? logger = null)
    {
        _options = options;
        _context = context;
        _logger = logger;
    }

    public sealed record LockState(bool Visible, string? Title, string? Subtitle);

    private LockState _state = new(false, null, null);

    public LockState GetState() => _state;

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

    public Task ApplyAsync(bool visible, string? title, string? subtitle, CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.CabinetBadgeOverlay.Enabled)
        {
            _state = new LockState(false, title, subtitle);
            return Task.CompletedTask;
        }

        _state = new LockState(visible, title, subtitle);

        // Fanart choisi au moment du verrouillage et figé ensuite : un écran
        // stable, pas un diaporama au gré de la navigation.
        var (fanart, logo) = visible ? ResolveMedia() : (null, null);

        EnsureUiThreadStarted(cancellationToken);
        Control? dispatcher;
        lock (_sync)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher is null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
        {
            return Task.CompletedTask;
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

                _form ??= new LockForm(ResolveTargetScreen);
                _form.Configure(title ?? string.Empty, subtitle ?? string.Empty, fanart, logo);
                _form.PositionFullscreen(ResolveTargetScreen());
                _form.Show();
                _form.TopMost = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cabinet lock overlay update failed.");
            }
            finally
            {
                completion.TrySetResult();
            }
        }));
        return completion.Task;
    }

    /// <summary>Fanart du jeu en cours (running sinon sélectionné), sinon un
    /// jeu au hasard du système en cours, résolu depuis les gamelists locales.</summary>
    private (string? Fanart, string? Logo) ResolveMedia()
    {
        try
        {
            var game = _context.Ui.Running ?? _context.Ui.Selected;
            if (game is { GamePath.Length: > 0 })
            {
                var resolved = ResolveGameMedia(game.SystemId, Path.GetFileName(game.GamePath.Replace('\\', '/')));
                if (resolved.Fanart is not null)
                {
                    return resolved;
                }
            }

            var systemId = game?.SystemId ?? _context.Ui.SelectedSystem?.Name;
            return systemId is { Length: > 0 } ? ResolveSystemMedia(systemId) : (null, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cabinet lock media resolution failed.");
            return (null, null);
        }
    }

    private static (string? Fanart, string? Logo) ResolveGameMedia(string systemId, string fileName)
    {
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
        return game is null ? (null, null) : (ResolveTag(game, systemDir, "fanart") ?? ResolveTag(game, systemDir, "image"),
            ResolveTag(game, systemDir, "marquee"));
    }

    /// <summary>Un jeu du système qui a un fanart — le premier venu d'un
    /// parcours mélangé, pour varier d'un verrouillage à l'autre.</summary>
    private static (string? Fanart, string? Logo) ResolveSystemMedia(string systemId)
    {
        var systemDir = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelist = Path.Combine(systemDir, "gamelist.xml");
        if (!File.Exists(gamelist))
        {
            return (null, null);
        }

        var doc = XDocument.Load(gamelist);
        var games = doc.Root?.Elements("game").ToArray() ?? [];
        foreach (var game in games.OrderBy(_ => Random.Shared.Next()))
        {
            var fanart = ResolveTag(game, systemDir, "fanart") ?? ResolveTag(game, systemDir, "image");
            if (fanart is not null)
            {
                return (fanart, ResolveTag(game, systemDir, "marquee"));
            }
        }

        return (null, null);
    }

    private static string? ResolveTag(XElement game, string systemDir, string tag)
    {
        var value = ((string?)game.Element(tag) ?? "").Trim().Replace('\\', '/');
        if (value.Length == 0)
        {
            return null;
        }

        // Ne retirer QUE le préfixe "./" — les chemins gamelist traversent en
        // "./../../plugins/…" et un TrimStart aveugle casserait la remontée.
        if (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        var full = Path.IsPathFullyQualified(value)
            ? value
            : Path.GetFullPath(Path.Combine(systemDir, value));
        return File.Exists(full) ? full : null;
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
            Name = "APIExpose.CabinetLockOverlay"
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

    private sealed class LockForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        private static readonly IntPtr TopMostHandle = new(-1);
        private static readonly IntPtr NoTopMostHandle = new(-2);
        private const int GwlExstyle = -20;
        private const int WsExTopmost = 0x0008;
        private const uint NoSizeNoActivate = 0x0001 | 0x0010 | 0x0040;

        private readonly System.Windows.Forms.Timer _reassert;
        private Image? _background;
        private Image? _logo;
        private string _title = string.Empty;
        private string _subtitle = string.Empty;

        public LockForm(Func<Screen> resolveScreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(8, 8, 14);
            DoubleBuffered = true;

            // Cet écran reste affiché longtemps (réservation, maintenance) :
            // même blindage que le badge — le timer ne meurt JAMAIS, et le flag
            // TopMost REEL est vérifié (la propriété WinForms répond sur son
            // cache) avec la séquence NOTOPMOST→TOPMOST qui le rétablit.
            _reassert = new System.Windows.Forms.Timer { Interval = 2000 };
            _reassert.Tick += (_, _) =>
            {
                try
                {
                    if (!Visible)
                    {
                        return;
                    }

                    PositionFullscreen(resolveScreen());
                    var exStyle = GetWindowLong(Handle, GwlExstyle);
                    if ((exStyle & WsExTopmost) == 0)
                    {
                        SetWindowPos(Handle, NoTopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
                    }

                    SetWindowPos(Handle, TopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
                }
                catch (Exception)
                {
                }
            };
            _reassert.Start();
        }

        protected override bool ShowWithoutActivation => true;

        public void Configure(string title, string subtitle, string? fanartPath, string? logoPath)
        {
            _title = title;
            _subtitle = subtitle;
            SwapImage(ref _background, LoadImage(fanartPath));
            SwapImage(ref _logo, LoadImage(logoPath));
            Invalidate();
        }

        private static Image? LoadImage(string? path)
        {
            try
            {
                return path is null ? null : Image.FromFile(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SwapImage(ref Image? slot, Image? next)
        {
            slot?.Dispose();
            slot = next;
        }

        public void PositionFullscreen(Screen screen)
        {
            if (Bounds != screen.Bounds)
            {
                Bounds = screen.Bounds;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            if (_background is not null)
            {
                // Fanart plein cadre (cover), assombri en dégradé vers le bas
                // pour asseoir le texte.
                var scale = Math.Max((float)Width / _background.Width, (float)Height / _background.Height);
                var w = _background.Width * scale;
                var h = _background.Height * scale;
                g.DrawImage(_background, (Width - w) / 2, (Height - h) / 2, w, h);
            }

            using (var veil = new LinearGradientBrush(
                ClientRectangle, Color.FromArgb(120, 5, 5, 10), Color.FromArgb(225, 5, 5, 10), 90f))
            {
                g.FillRectangle(veil, ClientRectangle);
            }

            // Logo (wheel) du jeu au tiers haut, s'il y en a un.
            if (_logo is not null)
            {
                var maxW = Width * 0.38f;
                var maxH = Height * 0.2f;
                var s = Math.Min(maxW / _logo.Width, maxH / _logo.Height);
                var lw = _logo.Width * s;
                var lh = _logo.Height * s;
                g.DrawImage(_logo, (Width - lw) / 2, Height * 0.16f + (maxH - lh) / 2, lw, lh);
            }

            // La raison, en très grand — ombre portée douce puis blanc pur.
            var titleFont = FitFont(g, _title, new Font("Segoe UI", 64, FontStyle.Bold), Width * 0.9f);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var titleRect = new RectangleF(0, Height * 0.42f, Width, Height * 0.22f);
            var shadowRect = titleRect;
            shadowRect.Offset(0, 4);
            using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            {
                g.DrawString(_title, titleFont, shadow, shadowRect, format);
            }

            g.DrawString(_title, titleFont, Brushes.White, titleRect, format);
            titleFont.Dispose();

            if (_subtitle.Length > 0)
            {
                using var subFont = new Font("Segoe UI", 22, FontStyle.Regular);
                using var subBrush = new SolidBrush(Color.FromArgb(210, 210, 225));
                g.DrawString(_subtitle, subFont, subBrush, new RectangleF(0, Height * 0.64f, Width, Height * 0.1f), format);
            }

            format.Dispose();
        }

        /// <summary>Réduit la taille du titre jusqu'à tenir dans la largeur
        /// donnée (les raisons longues restent sur une ligne).</summary>
        private static Font FitFont(Graphics g, string text, Font font, float maxWidth)
        {
            var current = font;
            while (current.Size > 24 && g.MeasureString(text, current).Width > maxWidth)
            {
                var smaller = new Font(current.FontFamily, current.Size - 4, current.Style);
                current.Dispose();
                current = smaller;
            }

            return current;
        }
    }
}
