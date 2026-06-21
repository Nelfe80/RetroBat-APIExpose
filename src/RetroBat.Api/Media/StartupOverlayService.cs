using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class StartupOverlayService : IHostedService, IDisposable, IStartupOverlayService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ILogger<StartupOverlayService>? _logger;
    private readonly object _lock = new();
    private Thread? _uiThread;
    private OverlayForm? _form;
    private bool _overlayEnabled;
    private bool _awaitingFirstReload;
    private bool _hideRequested;
    private bool _hideDelayScheduled;
    private bool _started;
    private DateTime _shownAtUtc = DateTime.MinValue;
    private DateTime _completedAtUtc = DateTime.MinValue;
    private DateTime _lastSplashRotationAtUtc = DateTime.MinValue;
    private string _lastSplashImagePath = string.Empty;
    private OverlayProgressState _lastProgressState = new("startup_loading", 0, 1, null);

    public StartupOverlayService(
        IOptionsMonitor<ApiExposeOptions> options,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ILogger<StartupOverlayService>? logger = null)
    {
        _options = options;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _overlayEnabled = OperatingSystem.IsWindows() && _runtimeOptions.IsStartupOverlayEnabled();
        _started = true;

        if (!_overlayEnabled)
        {
            _logger?.LogInformation("Startup overlay disabled by config or platform.");
            return Task.CompletedTask;
        }

        var ready = new ManualResetEventSlim(false);
        _uiThread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var options = _options.CurrentValue.StartupOverlay;
            var localizedContent = ResolveOverlayContent(options);
            var splashImagePath = ResolveSplashImagePath(options.SplashImagePath);
            lock (_lock)
            {
                _lastSplashImagePath = splashImagePath;
                _lastSplashRotationAtUtc = DateTime.UtcNow;
            }

            var progressState = _lastProgressState;
            var progressContent = ResolveProgressContent(progressState.MessageKey, progressState.Detail, progressState.Current, progressState.Total);

            var form = new OverlayForm(
                localizedContent.Title,
                localizedContent.Body,
                progressContent.Body,
                progressState.Current,
                progressState.Total,
                ClampOpacity(options.Opacity),
                splashImagePath,
                options.MinimumVisibleMilliseconds,
                _logger);
            form.FormClosed += (_, _) => Application.ExitThread();

            lock (_lock)
            {
                _form = form;
                _shownAtUtc = DateTime.UtcNow;
            }

            ready.Set();
            if (_hideRequested)
            {
                _ = Task.Run(HideOverlay);
            }

            Application.Run(form);

            lock (_lock)
            {
                _form = null;
            }
        })
        {
            IsBackground = true,
            Name = "APIExpose.StartupOverlay",
        };

        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        ready.Wait(cancellationToken);

        _logger?.LogInformation("Startup overlay displayed.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        HideOverlay();
        return Task.CompletedTask;
    }

    public void MarkStartupBootstrapCompleted(bool awaitingFirstReload)
    {
        lock (_lock)
        {
            _awaitingFirstReload = awaitingFirstReload;
            if (!_awaitingFirstReload)
            {
                _completedAtUtc = DateTime.UtcNow;
                _hideRequested = true;
            }
        }

        if (!awaitingFirstReload)
        {
            HideOverlay();
        }
    }

    public void NotifyReloadSucceeded()
    {
        var shouldHide = false;
        lock (_lock)
        {
            if (_awaitingFirstReload)
            {
                _awaitingFirstReload = false;
                _completedAtUtc = DateTime.UtcNow;
                _hideRequested = true;
                shouldHide = true;
            }
        }

        if (shouldHide)
        {
            HideOverlay();
        }
    }

    public bool IsStartupActiveOrRecentlyCompleted(TimeSpan recentWindow)
    {
        lock (_lock)
        {
            if (_started && !_hideRequested)
            {
                return true;
            }

            if (_form is { IsDisposed: false })
            {
                return true;
            }

            return _completedAtUtc != DateTime.MinValue &&
                DateTime.UtcNow - _completedAtUtc <= recentWindow;
        }
    }

    public void UpdateStartupProgress(string messageKey, int current, int total, string? detail = null)
    {
        lock (_lock)
        {
            _lastProgressState = new OverlayProgressState(messageKey, current, total, detail);
        }

        OverlayForm? form;
        lock (_lock)
        {
            form = _form;
        }

        if (form == null || form.IsDisposed || !form.IsHandleCreated)
        {
            return;
        }

        var progress = ResolveProgressContent(messageKey, detail, current, total);
        var nextSplashImagePath = string.Equals(messageKey, "startup_roms_media_migration", StringComparison.OrdinalIgnoreCase)
            ? ResolveNextMigrationSplashImagePath()
            : string.Empty;
        try
        {
            form.BeginInvoke(new Action(() =>
            {
                form.ApplyProgress(progress.Body, current, total);
                if (!string.IsNullOrWhiteSpace(nextSplashImagePath))
                {
                    form.ApplySplashImage(nextSplashImagePath);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // Form already closing/disposed.
        }
    }

    public void Dispose()
    {
        HideOverlay();
    }

    private void HideOverlay()
    {
        OverlayForm? form;
        lock (_lock)
        {
            if (!_overlayEnabled || !_started)
            {
                return;
            }

            form = _form;
            if (form == null)
            {
                _hideRequested = true;
                return;
            }

            var minimumVisible = Math.Max(0, _options.CurrentValue.StartupOverlay.MinimumVisibleMilliseconds);
            var visibleFor = DateTime.UtcNow - _shownAtUtc;
            var remaining = TimeSpan.FromMilliseconds(minimumVisible) - visibleFor;
            if (remaining > TimeSpan.Zero && !_hideDelayScheduled)
            {
                _hideDelayScheduled = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(remaining);
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            _hideDelayScheduled = false;
                        }

                        HideOverlay();
                    }
                });
                return;
            }
        }

        try
        {
            if (form.IsHandleCreated)
            {
                form.BeginInvoke(new Action(() =>
                {
                    if (!form.IsDisposed)
                    {
                        form.CompleteProgress();
                        form.Close();
                    }
                }));
            }
        }
        catch (InvalidOperationException)
        {
            // Form already closing/disposed.
        }
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

    private OverlayContent ResolveOverlayContent(ApiExposeOptions.StartupOverlayOptions options)
    {
        var language = ResolveLanguage();

        try
        {
            var messagesPath = options.MessagesFilePath;
            if (string.IsNullOrWhiteSpace(messagesPath))
            {
                return new OverlayContent(options.Title, options.Message);
            }

            var absolutePath = ResolveMessagesPath(messagesPath);
            if (!File.Exists(absolutePath))
            {
                return new OverlayContent(options.Title, options.Message);
            }

            using var stream = File.OpenRead(absolutePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var defaultLanguage = root.TryGetProperty("defaultLanguage", out var defaultLanguageElement)
                ? defaultLanguageElement.GetString()?.Trim().ToLowerInvariant()
                : "fr";

            if (!root.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Object)
            {
                return new OverlayContent(options.Title, options.Message);
            }

            if (TryGetMessage(messagesElement, language, "startup_title", out var titleText)
                && TryGetMessage(messagesElement, language, "startup_loading", out var bodyText))
            {
                return new OverlayContent(titleText!, bodyText!);
            }

            if (!string.IsNullOrWhiteSpace(defaultLanguage)
                && TryGetMessage(messagesElement, defaultLanguage!, "startup_title", out titleText)
                && TryGetMessage(messagesElement, defaultLanguage!, "startup_loading", out bodyText))
            {
                return new OverlayContent(titleText!, bodyText!);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to load startup overlay localized messages.");
        }

        return new OverlayContent(options.Title, options.Message);
    }

    private OverlayContent ResolveProgressContent(string messageKey, string? detail, int current, int total)
    {
        var options = _options.CurrentValue.StartupOverlay;
        var fallback = new OverlayContent(options.Title, options.Message);
        var language = ResolveLanguage();

        try
        {
            var absolutePath = ResolveMessagesPath(options.MessagesFilePath);
            if (!File.Exists(absolutePath))
            {
                return fallback;
            }

            using var stream = File.OpenRead(absolutePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var defaultLanguage = root.TryGetProperty("defaultLanguage", out var defaultLanguageElement)
                ? defaultLanguageElement.GetString()?.Trim().ToLowerInvariant()
                : "fr";

            if (!root.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }

            if (!TryGetMessage(messagesElement, language, "startup_title", out var titleText)
                && !string.IsNullOrWhiteSpace(defaultLanguage))
            {
                TryGetMessage(messagesElement, defaultLanguage!, "startup_title", out titleText);
            }

            if (!TryGetMessage(messagesElement, language, messageKey, out var bodyText)
                && !string.IsNullOrWhiteSpace(defaultLanguage))
            {
                TryGetMessage(messagesElement, defaultLanguage!, messageKey, out bodyText);
            }

            var resolvedBody = string.IsNullOrWhiteSpace(bodyText) ? options.Message : bodyText!;
            resolvedBody = resolvedBody
                .Replace("{detail}", detail ?? string.Empty, StringComparison.Ordinal)
                .Replace("{current}", current.ToString(), StringComparison.Ordinal)
                .Replace("{total}", total.ToString(), StringComparison.Ordinal);

            return new OverlayContent(
                string.IsNullOrWhiteSpace(titleText) ? options.Title : titleText!,
                resolvedBody);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unable to resolve startup overlay progress content.");
            return fallback;
        }
    }

    private static string ResolveLanguage()
    {
        try
        {
            var settingsService = new EmulationStationSettingsService();
            var scrapingSettings = settingsService.GetScrapingSettings();
            if (!string.IsNullOrWhiteSpace(scrapingSettings.Language))
            {
                return scrapingSettings.Language.Split('_', '-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "fr";
            }
        }
        catch
        {
            // Ignore and fall back below.
        }

        return "fr";
    }

    private static string ResolveMessagesPath(string messagesPath)
    {
        if (Path.IsPathRooted(messagesPath))
        {
            return messagesPath;
        }

        var pluginRelative = Path.Combine(RetroBatPaths.PluginRoot, messagesPath);
        if (File.Exists(pluginRelative))
        {
            return pluginRelative;
        }

        return Path.GetFullPath(pluginRelative);
    }

    private static string ResolveSplashImagePath(string splashImagePath)
    {
        if (string.IsNullOrWhiteSpace(splashImagePath))
        {
            return string.Empty;
        }

        var resolvedPath = Path.IsPathRooted(splashImagePath)
            ? splashImagePath
            : Path.Combine(RetroBatPaths.PluginRoot, splashImagePath);

        var randomNumberedSplashes = ResolveNumberedSplashCandidates(resolvedPath);
        if (randomNumberedSplashes.Count > 0)
        {
            return randomNumberedSplashes[Random.Shared.Next(randomNumberedSplashes.Count)];
        }

        return File.Exists(resolvedPath) ? resolvedPath : string.Empty;
    }

    private string ResolveNextMigrationSplashImagePath()
    {
        var options = _options.CurrentValue.StartupOverlay;
        if (string.IsNullOrWhiteSpace(options.SplashImagePath))
        {
            return string.Empty;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if ((now - _lastSplashRotationAtUtc).TotalSeconds < 8d)
            {
                return string.Empty;
            }
        }

        var resolvedPath = Path.IsPathRooted(options.SplashImagePath)
            ? options.SplashImagePath
            : Path.Combine(RetroBatPaths.PluginRoot, options.SplashImagePath);
        var candidates = ResolveNumberedSplashCandidates(resolvedPath);
        if (candidates.Count <= 1)
        {
            return string.Empty;
        }

        lock (_lock)
        {
            var nextCandidates = candidates
                .Where(path => !string.Equals(path, _lastSplashImagePath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var next = nextCandidates.Length == 0
                ? candidates[Random.Shared.Next(candidates.Count)]
                : nextCandidates[Random.Shared.Next(nextCandidates.Length)];
            _lastSplashImagePath = next;
            _lastSplashRotationAtUtc = now;
            return next;
        }
    }

    private static List<string> ResolveNumberedSplashCandidates(string configuredPath)
    {
        var directory = Path.GetDirectoryName(configuredPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new List<string>();
        }

        var extension = Path.GetExtension(configuredPath);
        var configuredStem = Path.GetFileNameWithoutExtension(configuredPath);
        var prefix = TrimTrailingDigits(configuredStem);
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(extension))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(directory, prefix + "*" + extension, SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                return stem.Length > prefix.Length &&
                    stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    stem[prefix.Length..].All(char.IsDigit);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TrimTrailingDigits(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsDigit(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    private static bool TryGetMessage(JsonElement messagesElement, string language, string key, out string? message)
    {
        message = null;
        if (!messagesElement.TryGetProperty(language, out var langElement) || langElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!langElement.TryGetProperty(key, out var messageElement))
        {
            return false;
        }

        message = messageElement.GetString()?.Trim();
        return !string.IsNullOrWhiteSpace(message);
    }

    private readonly record struct OverlayContent(string Title, string Body);
    private readonly record struct OverlayProgressState(string MessageKey, int Current, int Total, string? Detail);

    private sealed class OverlayForm : Form
    {
        private static readonly Color TextColor = ColorTranslator.FromHtml("#FFFFFF");
        private static readonly Color TitleBackColor = ColorTranslator.FromHtml("#2961b0");
        private static readonly Color WindowBackColor = ColorTranslator.FromHtml("#081323");
        private static readonly Color BorderColor = Color.Black;
        private const float TitleFontSize = 20f;
        private const float BodyFontSize = 14f;
        private static readonly PrivateFontCollection OverlayFonts = new();
        private static bool overlayFontsLoaded;

        private readonly ILogger<StartupOverlayService>? _logger;
        private readonly Label? _statusLabel;
        private readonly EsProgressBar? _progressBar;
        private readonly PictureBox? _splashPictureBox;
        private Image? _splashImage;
        private readonly System.Windows.Forms.Timer? _splashProgressTimer;
        private readonly DateTime _splashStartedAtUtc;
        private readonly int _minimumVisibleMilliseconds;
        private bool _hasExplicitProgress;

        public OverlayForm(
            string titleText,
            string bodyText,
            string statusText,
            int progressCurrent,
            int progressTotal,
            double opacity,
            string splashImagePath,
            int minimumVisibleMilliseconds,
            ILogger<StartupOverlayService>? logger)
        {
            _logger = logger;
            _minimumVisibleMilliseconds = Math.Max(1, minimumVisibleMilliseconds);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = BorderColor;
            ForeColor = TextColor;
            Opacity = opacity;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(2);

            _splashImage = LoadSplashImage(splashImagePath);
            if (_splashImage != null)
            {
                _splashStartedAtUtc = DateTime.UtcNow;
                var screen = ResolveEmulationStationScreen() ?? Screen.PrimaryScreen;
                var area = screen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
                var splashWidth = Math.Max(320, (int)Math.Round(area.Width * 0.6d));
                var progressHeight = Math.Max(14, (int)Math.Round(area.Height * 0.018d));
                var statusHeight = Math.Max(62, (int)Math.Round(area.Height * 0.068d));
                var statusTopGap = Math.Max(10, (int)Math.Round(area.Height * 0.012d));
                var progressTopGap = Math.Max(8, (int)Math.Round(area.Height * 0.01d));
                var maxOverlayHeight = Math.Max(220, (int)Math.Round(area.Height * 0.75d));
                var maxSplashHeight = Math.Max(180, maxOverlayHeight - statusTopGap - statusHeight - progressTopGap - progressHeight);
                var scale = Math.Min(
                    splashWidth / (double)_splashImage.Width,
                    maxSplashHeight / (double)_splashImage.Height);
                var displayWidth = Math.Max(1, (int)Math.Round(_splashImage.Width * scale));
                var displayHeight = Math.Max(1, (int)Math.Round(_splashImage.Height * scale));

                var splashContainer = new TableLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = WindowBackColor,
                    ColumnCount = 1,
                    RowCount = 3,
                    Padding = new Padding(0),
                    Margin = new Padding(0)
                };
                splashContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, displayWidth));
                splashContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, displayHeight));
                splashContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, statusTopGap + statusHeight));
                splashContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, progressTopGap + progressHeight));

                var splash = new PictureBox
                {
                    Image = _splashImage,
                    Width = displayWidth,
                    Height = displayHeight,
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0)
                };
                _splashPictureBox = splash;

                _statusLabel = new Label
                {
                    Text = statusText,
                    Width = displayWidth,
                    Height = statusHeight,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = CreateOverlayFont(BodyFontSize, FontStyle.Regular),
                    ForeColor = TextColor,
                    BackColor = Color.Transparent,
                    AutoEllipsis = true,
                    UseCompatibleTextRendering = true,
                    Margin = new Padding(0, statusTopGap, 0, 0)
                };

                _progressBar = new EsProgressBar
                {
                    Width = displayWidth,
                    Height = progressHeight,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, progressTopGap, 0, 0)
                };

                splashContainer.Controls.Add(splash, 0, 0);
                splashContainer.Controls.Add(_statusLabel, 0, 1);
                splashContainer.Controls.Add(_progressBar, 0, 2);
                Controls.Add(splashContainer);

                _splashProgressTimer = new System.Windows.Forms.Timer { Interval = 33 };
                _splashProgressTimer.Tick += (_, _) => ApplyTimedSplashProgress();
                _splashProgressTimer.Start();
                ApplyTimedSplashProgress();

                Load += (_, _) => PositionOnEmulationStationScreen();
                return;
            }

            var container = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = WindowBackColor,
                Padding = new Padding(20, 10, 20, 10),
                ColumnCount = 1,
                RowCount = 4
            };

            var titlePanel = new Panel
            {
                Width = 700,
                Height = 66,
                BackColor = TitleBackColor,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(20, 0, 20, 0)
            };

            var title = new Label
            {
                Dock = DockStyle.Fill,
                Text = titleText.ToUpperInvariant(),
                Font = CreateOverlayFont(TitleFontSize, FontStyle.Bold),
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseCompatibleTextRendering = true,
                Margin = new Padding(0)
            };
            titlePanel.Controls.Add(title);

            var body = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                Text = bodyText,
                Font = CreateOverlayFont(BodyFontSize, FontStyle.Regular),
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                Margin = new Padding(20, 0, 20, 0)
            };

            _progressBar = new EsProgressBar
            {
                Width = 700,
                Height = 18,
                Margin = new Padding(0, 18, 0, 0),
            };

            _statusLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                Text = statusText,
                Font = CreateOverlayFont(BodyFontSize, FontStyle.Regular),
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                Margin = new Padding(20, 10, 20, 0)
            };

            container.Controls.Add(titlePanel);
            container.Controls.Add(body);
            container.Controls.Add(_progressBar);
            container.Controls.Add(_statusLabel);
            Controls.Add(container);

            ApplyProgress(statusText, progressCurrent, progressTotal);
            Load += (_, _) => PositionOnEmulationStationScreen();
        }

        protected override bool ShowWithoutActivation => true;

        public void ApplyProgress(string status, int current, int total)
        {
            if (IsDisposed || _progressBar == null)
            {
                return;
            }

            var safeTotal = Math.Max(1, total);
            var clampedCurrent = Math.Max(0, Math.Min(current, safeTotal));
            var percent = clampedCurrent * 100d / safeTotal;
            _hasExplicitProgress = true;

            if (_statusLabel == null)
            {
                _progressBar.Percent = Math.Max(_progressBar.Percent, Math.Min(99d, percent));
                return;
            }

            _statusLabel.Text = status;
            _progressBar.Percent = percent;
        }

        public void ApplySplashImage(string splashImagePath)
        {
            if (IsDisposed || _splashPictureBox == null || string.IsNullOrWhiteSpace(splashImagePath))
            {
                return;
            }

            var image = LoadSplashImage(splashImagePath);
            if (image == null)
            {
                return;
            }

            var previous = _splashImage;
            _splashImage = image;
            _splashPictureBox.Image = image;
            previous?.Dispose();
        }

        public void CompleteProgress()
        {
            if (!IsDisposed && _progressBar != null)
            {
                _progressBar.Percent = 100d;
            }
        }

        private void ApplyTimedSplashProgress()
        {
            if (IsDisposed || _progressBar == null)
            {
                return;
            }

            if (_hasExplicitProgress)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - _splashStartedAtUtc;
            var percent = Math.Min(99d, elapsed.TotalMilliseconds * 100d / _minimumVisibleMilliseconds);
            _progressBar.Percent = Math.Max(_progressBar.Percent, percent);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _splashProgressTimer?.Stop();
                _splashProgressTimer?.Dispose();
                _splashImage?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void PositionOnEmulationStationScreen()
        {
            var screen = ResolveEmulationStationScreen() ?? Screen.PrimaryScreen;
            if (screen == null)
            {
                CenterToScreen();
                return;
            }

            var area = screen.WorkingArea;
            var width = Width;
            var height = Height;
            var x = area.Left + Math.Max(0, (area.Width - width) / 2);
            var y = area.Top + Math.Max(0, (area.Height - height) / 2);
            Location = new Point(x, y);
            _logger?.LogInformation("Startup overlay positioned on screen {DeviceName} at {X},{Y}.", screen.DeviceName, x, y);
        }

        private static Screen? ResolveEmulationStationScreen()
        {
            var windowHandle = TryResolveEmulationStationWindow();
            if (windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out var rect))
            {
                return null;
            }

            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return Screen.FromRectangle(bounds);
        }

        private static IntPtr TryResolveEmulationStationWindow()
        {
            var exactHandle = FindWindowW(null, "EmulationStation");
            if (exactHandle != IntPtr.Zero)
            {
                return exactHandle;
            }

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

                        var processName = process.ProcessName ?? string.Empty;
                        var title = process.MainWindowTitle ?? string.Empty;
                        if (processName.Contains("emulationstation", StringComparison.OrdinalIgnoreCase)
                            || title.Contains("EmulationStation", StringComparison.OrdinalIgnoreCase))
                        {
                            return process.MainWindowHandle;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to primary screen.
            }

            return IntPtr.Zero;
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

        private static Image? LoadSplashImage(string splashImagePath)
        {
            if (string.IsNullOrWhiteSpace(splashImagePath) || !File.Exists(splashImagePath))
            {
                return null;
            }

            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(splashImagePath));
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
