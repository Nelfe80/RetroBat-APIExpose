using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QRCoder;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// « Réclame ton record ! » - surimpression affichée sur l'écran de la MACHINE
/// (borne, PC perso, mini-PC…) à la fin d'une partie dont le score a été
/// certifié et publié SOUS ANONYMAT. Plein écran : le fanart du jeu en fond, en
/// DEUX COLONNES - à gauche l'identité du record (marque, « Record certifié »,
/// logo/nom du jeu, score en or, rang, sceau) ; à droite un panneau avec le QR
/// + le short-link nelfeplay.com/claim/XXXXXX. Le joueur scanne depuis son
/// téléphone pour rattacher le record à son compte.
///
/// Purement de l'AFFICHAGE : pas de clic ni d'interaction (la fenêtre n'est
/// jamais activée). Elle se retire dès que le score est réclamé (poll du
/// ticket), à la première touche, ou après un délai. Une nouvelle partie la
/// fait disparaître aussitôt.
///
/// Déclenchée par <see cref="NelfePlayScoringReporter"/> quand le verdict porte
/// un <c>claim_code</c> (score anonyme publié). Une machine appairée/liée n'en
/// produit pas → aucune surimpression : le gating par identité est implicite.
/// </summary>
public sealed class ClaimOverlayService : BackgroundService
{
    private const string ClaimBaseUrl = "https://nelfeplay.com";
    private static readonly HttpClient PollHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiContext _context;
    private readonly ILogger<ClaimOverlayService>? _logger;
    private readonly object _sync = new();
    private Thread? _uiThread;
    private ApplicationContext? _applicationContext;
    private Control? _dispatcher;
    private ClaimForm? _form;
    private CancellationTokenSource? _pollCts;

    public ClaimOverlayService(
        IOptionsMonitor<ApiExposeOptions> options,
        ApiContext context,
        ILogger<ClaimOverlayService>? logger = null)
    {
        _options = options;
        _context = context;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        CancelPoll();
        CloseUiThread();
    }

    /// <summary>Affiche la surimpression de réclamation pour un score anonyme publié.</summary>
    public Task ShowAsync(string systemId, string? ruleset, long score, string code, int? rank, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Task.CompletedTask;
        }

        var (fanart, logo, gameName) = ResolveMedia(systemId);
        byte[]? qr = BuildQr($"{ClaimBaseUrl}/claim/{code}");
        var strings = ClaimStrings.For(ResolveLanguage());

        string rankText = string.Empty;
        if (rank is > 0)
        {
            rankText = rank == 1
                ? "🥇  #1 · " + strings.World
                : "#" + rank + " · " + strings.World;
        }

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
                _form ??= new ClaimForm(ResolveTargetScreen);
                _form.Configure(strings, gameName, ruleset ?? string.Empty,
                    score.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                    code, rankText, fanart, logo, qr);
                _form.PositionFullscreen(ResolveTargetScreen());
                _form.Show();
                _form.TopMost = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Claim overlay update failed.");
            }
            finally
            {
                completion.TrySetResult();
            }
        }));

        StartClaimPoll(code);
        return completion.Task;
    }

    /// <summary>Retire la surimpression immédiatement (ex. une nouvelle partie démarre).</summary>
    public void HideNow()
    {
        CancelPoll();
        Control? dispatcher;
        lock (_sync)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher is null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(new Action(() => _form?.HideClaim()));
        }
        catch
        {
        }
    }

    // ── Poll de réclamation ──────────────────────────────────────────────────

    private void StartClaimPoll(string code)
    {
        CancelPoll();
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _pollCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 100 && !cts.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    if (await IsClaimedAsync(code, cts.Token).ConfigureAwait(false))
                    {
                        NotifyClaimed();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Claim overlay poll stopped.");
            }
        }, cts.Token);
    }

    private static async Task<bool> IsClaimedAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PollHttp.GetAsync($"{ClaimBaseUrl}/api/v1/claim/{code}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("used", out var used)
                && used.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private void NotifyClaimed()
    {
        Control? dispatcher;
        lock (_sync)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher is null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(new Action(() => _form?.MarkClaimed()));
        }
        catch
        {
        }
    }

    private void CancelPoll()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _pollCts;
            _pollCts = null;
        }

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch
        {
        }
    }

    // ── QR + langue + médias ─────────────────────────────────────────────────

    private byte[]? BuildQr(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            return png.GetGraphic(12, new byte[] { 17, 17, 22 }, new byte[] { 255, 255, 255 });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Claim overlay QR generation failed.");
            return null;
        }
    }

    private string ResolveLanguage()
    {
        var candidate = Normalize2(_options.CurrentValue.ApiSettings.LanguageProfile);
        if (candidate is null)
        {
            try { candidate = Normalize2(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName); }
            catch { candidate = null; }
        }

        return candidate ?? "en";
    }

    private static string? Normalize2(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var two = value.Trim().ToLowerInvariant();
        two = two.Length >= 2 ? two[..2] : two;
        return two is "fr" or "en" or "es" or "ja" or "zh" or "ko" ? two : null;
    }

    private (string? Fanart, string? Logo, string? GameName) ResolveMedia(string systemId)
    {
        try
        {
            var game = _context.Ui.Running ?? _context.Ui.Selected;
            var gameName = game?.GameName;
            if (game is { GamePath.Length: > 0 })
            {
                var effectiveSystem = string.IsNullOrWhiteSpace(game.SystemId) ? systemId : game.SystemId;
                var (fanart, logo, xmlName) = ResolveGameMedia(effectiveSystem, Path.GetFileName(game.GamePath.Replace('\\', '/')));
                return (fanart, logo, string.IsNullOrWhiteSpace(gameName) ? xmlName : gameName);
            }

            return (null, null, gameName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Claim overlay media resolution failed.");
            return (null, null, null);
        }
    }

    private static (string? Fanart, string? Logo, string? Name) ResolveGameMedia(string systemId, string fileName)
    {
        var systemDir = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelist = Path.Combine(systemDir, "gamelist.xml");
        if (!File.Exists(gamelist))
        {
            return (null, null, null);
        }

        var doc = XDocument.Load(gamelist);
        var game = doc.Root?.Elements("game").FirstOrDefault(element =>
            string.Equals(
                Path.GetFileName(((string?)element.Element("path") ?? "").Replace('\\', '/')),
                fileName, StringComparison.OrdinalIgnoreCase));
        if (game is null)
        {
            return (null, null, null);
        }

        var name = ((string?)game.Element("name") ?? "").Trim();
        return (
            ResolveTag(game, systemDir, "fanart") ?? ResolveTag(game, systemDir, "image"),
            ResolveTag(game, systemDir, "marquee"),
            name.Length > 0 ? name : null);
    }

    private static string? ResolveTag(XElement game, string systemDir, string tag)
    {
        var value = ((string?)game.Element(tag) ?? "").Trim().Replace('\\', '/');
        if (value.Length == 0)
        {
            return null;
        }

        if (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        var full = Path.IsPathFullyQualified(value)
            ? value
            : Path.GetFullPath(Path.Combine(systemDir, value));
        return File.Exists(full) ? full : null;
    }

    // ── Thread UI (STA) ──────────────────────────────────────────────────────

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
        var thread = new Thread(() => OverlayUiGuard.Run("ClaimOverlayService", ready, () =>
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
        }))
        {
            IsBackground = true,
            Name = "APIExpose.ClaimOverlay"
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

    // ── Textes localisés (nom du jeu / « Nelfe Play » jamais traduits) ───────

    private sealed record ClaimStrings(
        string Kick, string Title, string Lead, string LinkIntro, string Points,
        string Seal, string Dismiss, string Claimed, string World)
    {
        public static ClaimStrings For(string lang) => lang switch
        {
            "fr" => new ClaimStrings(
                "Record certifié",
                "Réclame ton record !",
                "Scanne pour rattacher ce score à ton compte - depuis ton téléphone.",
                "ou va sur",
                "points",
                "Mesuré · Signé sur la machine · 🔗 Enregistré sur la blockchain",
                "Se ferme dès que tu ouvres le lien · ou à la première touche · sinon {0} s",
                "✅ Record associé - bravo !",
                "Monde"),
            "es" => new ClaimStrings(
                "Récord certificado",
                "¡Reclama tu récord!",
                "Escanea para vincular este puntaje a tu cuenta - desde tu teléfono.",
                "o entra en",
                "puntos",
                "Medido · Firmado en la máquina · 🔗 Registrado en la blockchain",
                "Se cierra al abrir el enlace · o con la primera tecla · si no {0} s",
                "✅ Récord vinculado - ¡bravo!",
                "Mundo"),
            "ja" => new ClaimStrings(
                "認証記録",
                "記録を受け取ろう！",
                "スコアをアカウントに紐づけるにはスキャン - スマホから。",
                "またはアクセス",
                "点",
                "計測 · 端末で署名 · 🔗 ブロックチェーンに記録",
                "リンクを開くと閉じます · または最初のキー · なければ {0} 秒",
                "✅ 記録を紐づけました！",
                "世界"),
            "zh" => new ClaimStrings(
                "认证纪录",
                "认领你的纪录！",
                "扫码将此分数绑定到你的账号 - 用手机。",
                "或访问",
                "分",
                "已测量 · 已在本机签名 · 🔗 已记录在区块链",
                "打开链接即关闭 · 或按第一个键 · 否则 {0} 秒",
                "✅ 纪录已绑定 - 恭喜！",
                "世界"),
            "ko" => new ClaimStrings(
                "인증 기록",
                "기록을 등록하세요!",
                "이 점수를 계정에 연결하려면 스캔하세요 - 휴대폰으로.",
                "또는 방문",
                "점",
                "측정됨 · 기기에서 서명됨 · 🔗 블록체인에 기록됨",
                "링크를 열면 닫힘 · 또는 첫 키 입력 · 그렇지 않으면 {0}초",
                "✅ 기록이 연결되었습니다!",
                "세계"),
            _ => new ClaimStrings(
                "Certified record",
                "Claim your record!",
                "Scan to attach this score to your account - from your phone.",
                "or go to",
                "points",
                "Measured · Signed on the machine · 🔗 Recorded on the blockchain",
                "Closes when you open the link · or on the first key · otherwise {0} s",
                "✅ Record linked - well played!",
                "World"),
        };
    }

    // ── Fenêtre de surimpression (dessin GDI+ deux colonnes) ─────────────────

    private sealed class ClaimForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static readonly IntPtr TopMostHandle = new(-1);
        private static readonly IntPtr NoTopMostHandle = new(-2);
        private const int GwlExstyle = -20;
        private const int WsExTopmost = 0x0008;
        private const uint NoSizeNoActivate = 0x0001 | 0x0010 | 0x0040;
        private const int AutoCloseSeconds = 45;

        // Palette calquée sur la maquette.
        private static readonly Color Ink = Color.FromArgb(238, 243, 255);
        private static readonly Color Dim = Color.FromArgb(169, 184, 220);
        private static readonly Color Faint = Color.FromArgb(109, 126, 166);
        private static readonly Color Gold = Color.FromArgb(255, 210, 74);
        private static readonly Color Sonic = Color.FromArgb(58, 141, 255);
        private static readonly Color Ok = Color.FromArgb(57, 211, 83);
        private static readonly Color Chain = Color.FromArgb(138, 125, 255);

        private readonly System.Windows.Forms.Timer _tick;

        private ClaimStrings _strings = ClaimStrings.For("en");
        private string _gameName = string.Empty;
        private string _ruleset = string.Empty;
        private string _scoreText = string.Empty;
        private string _code = string.Empty;
        private string _rankText = string.Empty;
        private Image? _background;
        private Image? _logo;
        private Image? _qr;

        private DateTime _shownAtUtc;
        private DateTime? _claimedAtUtc;
        private bool _keyGateOpen;
        private int _reassertCounter;
        private int _lastRemaining = -1;

        public ClaimForm(Func<Screen> resolveScreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(4, 6, 14);
            DoubleBuffered = true;

            _tick = new System.Windows.Forms.Timer { Interval = 200 };
            _tick.Tick += (_, _) => OnTick(resolveScreen);
            _tick.Start();
        }

        protected override bool ShowWithoutActivation => true;

        public void Configure(
            ClaimStrings strings, string? gameName, string ruleset, string scoreText, string code,
            string rankText, string? fanartPath, string? logoPath, byte[]? qrBytes)
        {
            _strings = strings;
            _gameName = string.IsNullOrWhiteSpace(gameName) ? string.Empty : gameName!;
            _ruleset = ruleset;
            _scoreText = scoreText;
            _code = code;
            _rankText = rankText;

            SwapImage(ref _background, LoadImage(fanartPath));
            SwapImage(ref _logo, LoadImage(logoPath));

            Image? qr = null;
            if (qrBytes is not null)
            {
                try { using var s = new MemoryStream(qrBytes); qr = Image.FromStream(s); }
                catch { qr = null; }
            }

            SwapImage(ref _qr, qr);

            _shownAtUtc = DateTime.UtcNow;
            _claimedAtUtc = null;
            _keyGateOpen = false;
            _lastRemaining = -1;
            Invalidate();
        }

        public void MarkClaimed()
        {
            if (_claimedAtUtc is not null)
            {
                return;
            }

            _claimedAtUtc = DateTime.UtcNow;
            Invalidate();
        }

        public void HideClaim()
        {
            if (Visible)
            {
                Hide();
            }
        }

        private void OnTick(Func<Screen> resolveScreen)
        {
            if (!Visible)
            {
                return;
            }

            if (++_reassertCounter >= 10)
            {
                _reassertCounter = 0;
                PositionFullscreen(resolveScreen());
                var exStyle = GetWindowLong(Handle, GwlExstyle);
                if ((exStyle & WsExTopmost) == 0)
                {
                    SetWindowPos(Handle, NoTopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
                }

                SetWindowPos(Handle, TopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
            }

            if (_claimedAtUtc is { } claimedAt)
            {
                if ((DateTime.UtcNow - claimedAt).TotalSeconds >= 3)
                {
                    Hide();
                }

                return;
            }

            // Barrière clavier : on n'arme la fermeture-au-clavier qu'après avoir
            // vu TOUTES les touches relâchées - sinon la combinaison de sortie du
            // jeu (encore enfoncée) refermerait l'overlay à l'instant même.
            var anyDown = AnyKeyDown();
            if (!_keyGateOpen)
            {
                if (!anyDown && (DateTime.UtcNow - _shownAtUtc).TotalMilliseconds > 600)
                {
                    _keyGateOpen = true;
                }
            }
            else if (anyDown)
            {
                Hide();
                return;
            }

            var remaining = AutoCloseSeconds - (int)(DateTime.UtcNow - _shownAtUtc).TotalSeconds;
            if (remaining <= 0)
            {
                Hide();
                return;
            }

            if (remaining != _lastRemaining)
            {
                _lastRemaining = remaining;
                // Ne redessiner que le bandeau bas (le compte à rebours) : le
                // fanart plein écran n'est pas repeint à chaque seconde.
                Invalidate(new Rectangle(0, (int)(Height * 0.9), Width, (int)(Height * 0.1) + 1));
            }
        }

        private static bool AnyKeyDown()
        {
            for (var vk = 0x08; vk <= 0xFE; vk++)
            {
                if (vk is >= 0x01 and <= 0x06)
                {
                    continue;
                }

                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static Image? LoadImage(string? path)
        {
            try
            {
                return path is null ? null : Image.FromFile(path);
            }
            catch
            {
                return null;
            }
        }

        private static void SwapImage(ref Image? slot, Image? next)
        {
            slot?.Dispose();
            slot = next;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                SetWindowPos(Handle, TopMostHandle, Location.X, Location.Y, 0, 0, NoSizeNoActivate);
            }
        }

        public void PositionFullscreen(Screen screen)
        {
            if (Bounds != screen.Bounds)
            {
                Bounds = screen.Bounds;
                Invalidate();
            }
        }

        // ── Rendu ────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var w = Width;
            var h = Height;

            // Fond : fanart plein cadre (cover) ou dégradé sombre de repli.
            if (_background is not null)
            {
                var scale = Math.Max((float)w / _background.Width, (float)h / _background.Height);
                var bw = _background.Width * scale;
                var bh = _background.Height * scale;
                g.DrawImage(_background, (w - bw) / 2, (h - bh) / 2, bw, bh);
            }
            else
            {
                using var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(12, 20, 52), Color.FromArgb(20, 8, 40), 145f);
                g.FillRectangle(bg, ClientRectangle);
            }

            // Voile : plus dense à gauche (lisibilité du texte) et vers le bas.
            using (var veilH = new LinearGradientBrush(ClientRectangle, Color.FromArgb(242, 4, 6, 14), Color.FromArgb(40, 4, 6, 14), 0f))
            {
                g.FillRectangle(veilH, ClientRectangle);
            }

            using (var veilV = new LinearGradientBrush(ClientRectangle, Color.FromArgb(0, 4, 6, 14), Color.FromArgb(200, 4, 6, 14), 90f))
            {
                g.FillRectangle(veilV, ClientRectangle);
            }

            // État « réclamé » : bannière plein centre par-dessus tout.
            if (_claimedAtUtc is not null)
            {
                DrawClaimedBanner(g, w, h);
                return;
            }

            var pad = (int)(w * 0.055);
            var leftX = pad;
            var leftW = (int)(w * 0.52) - pad;

            DrawLeftColumn(g, leftX, leftW, pad, h);
            DrawRightPanel(g, w, h);
            DrawDismiss(g, w, h);
        }

        private void DrawLeftColumn(Graphics g, int x, int colW, int pad, int h)
        {
            var y = pad;

            // Marque : NelfePlay + puce « records ».
            using (var fBrandN = new Font("Segoe UI", 15f, FontStyle.Bold))
            {
                var nelfe = "Nelfe";
                var play = "Play";
                var wn = g.MeasureString(nelfe, fBrandN).Width;
                g.DrawString(nelfe, fBrandN, new SolidBrush(Ink), x, y);
                g.DrawString(play, fBrandN, new SolidBrush(Sonic), x + wn - 4, y);
                var wp = g.MeasureString(play, fBrandN).Width;
                DrawChip(g, "RECORDS", x + (int)(wn + wp) + 6, y + 2, Ok);
            }

            y += (int)(h * 0.075);

            // « Record certifié » - doré, majuscules espacées.
            using (var fKick = new Font("Segoe UI", 13f, FontStyle.Bold))
            {
                DrawTracked(g, _strings.Kick.ToUpperInvariant(), fKick, Gold, x, y, 3f);
            }

            y += (int)(h * 0.055);

            // Logo du jeu (marquee) sinon nom du jeu en grand.
            var titleH = (int)(h * 0.17);
            if (_logo is not null)
            {
                var maxW = colW;
                var maxH = titleH;
                var s = Math.Min((float)maxW / _logo.Width, (float)maxH / _logo.Height);
                var lw = _logo.Width * s;
                var lh = _logo.Height * s;
                g.DrawImage(_logo, x, y + (maxH - lh) / 2, lw, lh);
            }
            else if (_gameName.Length > 0)
            {
                using var fTitle = FitFont(g, _gameName, "Segoe UI", 52f, FontStyle.Bold, colW, 26f);
                var rect = new RectangleF(x, y, colW, titleH);
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
                g.DrawString(_gameName, fTitle, new SolidBrush(Ink), rect, sf);
            }

            y += titleH + (int)(h * 0.01);

            // Sous-ligne : règle (ex. 1CC).
            if (_ruleset.Length > 0)
            {
                using var fSys = new Font("Segoe UI", 14f, FontStyle.Regular);
                g.DrawString(_ruleset, fSys, new SolidBrush(Dim), x, y);
                y += (int)(h * 0.05);
            }

            // Score géant doré + « points ».
            using (var fScore = new Font("Consolas", 74f, FontStyle.Bold))
            {
                var scoreSize = g.MeasureString(_scoreText, fScore);
                g.DrawString(_scoreText, fScore, new SolidBrush(Gold), x, y);
                using var fPts = new Font("Segoe UI", 15f, FontStyle.Bold);
                DrawTracked(g, _strings.Points.ToUpperInvariant(), fPts, Faint, x + (int)scoreSize.Width + 12, y + (int)(scoreSize.Height * 0.55), 2f);
                y += (int)scoreSize.Height + (int)(h * 0.01);
            }

            // Médaille / rang.
            if (_rankText.Length > 0)
            {
                using var fRank = new Font("Segoe UI", 18f, FontStyle.Bold);
                g.DrawString(_rankText, fRank, new SolidBrush(Gold), x, y);
            }

            // Sceau, ancré vers le bas de la colonne gauche.
            using (var fSeal = new Font("Segoe UI", 12.5f, FontStyle.Regular))
            {
                var sealRect = new RectangleF(x, h - pad - (int)(h * 0.06), colW + (int)(colW * 0.35), (int)(h * 0.06));
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
                g.DrawString(_strings.Seal, fSeal, new SolidBrush(Dim), sealRect, sf);
            }
        }

        private void DrawRightPanel(Graphics g, int w, int h)
        {
            var panelW = (int)(w * 0.40);
            var panelH = (int)(h * 0.52);
            var panelX = w - (int)(w * 0.055) - panelW;
            var panelY = (h - panelH) / 2;
            var panel = new Rectangle(panelX, panelY, panelW, panelH);

            using (var path = RoundedRect(panel, 22))
            {
                using var fill = new SolidBrush(Color.FromArgb(180, 12, 20, 44));
                g.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(64, 140, 165, 220), 1.4f);
                g.DrawPath(border, path);
            }

            var ipad = (int)(panelW * 0.075);
            var cx = panelX + ipad;
            var cy = panelY + ipad;
            var innerW = panelW - ipad * 2;

            // En-tête : pastille 📱 + titre.
            var icoSize = (int)(h * 0.06);
            using (var icoPath = RoundedRect(new Rectangle(cx, cy, icoSize, icoSize), 12))
            {
                using var icoFill = new LinearGradientBrush(new Rectangle(cx, cy, icoSize, icoSize), Color.FromArgb(18, 37, 89), Color.FromArgb(11, 22, 54), 90f);
                g.FillPath(icoFill, icoPath);
                using var fIco = new Font("Segoe UI Emoji", icoSize * 0.42f);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("📱", fIco, Brushes.White, new RectangleF(cx, cy, icoSize, icoSize), sf);
            }

            using (var fTitle = FitFont(g, _strings.Title, "Segoe UI", 26f, FontStyle.Bold, innerW - icoSize - 14, 18f))
            {
                var tRect = new RectangleF(cx + icoSize + 14, cy, innerW - icoSize - 14, icoSize);
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
                g.DrawString(_strings.Title, fTitle, new SolidBrush(Ink), tRect, sf);
            }

            cy += icoSize + (int)(h * 0.02);

            // Lead (retour à la ligne automatique).
            using (var fLead = new Font("Segoe UI", 13.5f, FontStyle.Regular))
            {
                var leadRect = new RectangleF(cx, cy, innerW, (int)(h * 0.11));
                g.DrawString(_strings.Lead, fLead, new SolidBrush(Dim), leadRect);
                cy += (int)(h * 0.12);
            }

            // QR (carte blanche) + short-link à droite.
            var qrSize = Math.Min((int)(panelH * 0.42), (int)(innerW * 0.42));
            var qrCard = new Rectangle(cx, cy, qrSize, qrSize);
            using (var qrPath = RoundedRect(qrCard, 14))
            {
                g.FillPath(Brushes.White, qrPath);
            }

            if (_qr is not null)
            {
                var qpad = (int)(qrSize * 0.07);
                g.DrawImage(_qr, qrCard.X + qpad, qrCard.Y + qpad, qrSize - qpad * 2, qrSize - qpad * 2);
            }

            var linkX = cx + qrSize + (int)(innerW * 0.05);
            var linkW = innerW - qrSize - (int)(innerW * 0.05);
            using (var fOr = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            {
                DrawTracked(g, _strings.LinkIntro.ToUpperInvariant(), fOr, Faint, linkX, cy + (int)(qrSize * 0.14), 1.5f);
            }

            using (var fUrl = new Font("Consolas", 12.5f, FontStyle.Regular))
            {
                g.DrawString("nelfeplay.com/claim/", fUrl, new SolidBrush(Faint), linkX, cy + (int)(qrSize * 0.34));
            }

            using (var fCode = new Font("Consolas", 22f, FontStyle.Bold))
            {
                g.DrawString(_code, fCode, new SolidBrush(Gold), linkX, cy + (int)(qrSize * 0.5));
            }
        }

        private void DrawDismiss(Graphics g, int w, int h)
        {
            var remaining = Math.Max(0, AutoCloseSeconds - (int)(DateTime.UtcNow - _shownAtUtc).TotalSeconds);
            var text = string.Format(_strings.Dismiss, remaining);
            using var f = new Font("Segoe UI", 12f, FontStyle.Regular);
            var size = g.MeasureString(text, f);
            var totalW = size.Width + 18;
            var startX = (w - totalW) / 2;
            var y = (int)(h * 0.935);

            // Pastille verte + texte.
            using (var dot = new SolidBrush(Ok))
            {
                g.FillEllipse(dot, startX, y + (int)(size.Height / 2) - 5, 9, 9);
            }

            g.DrawString(text, f, new SolidBrush(Faint), startX + 18, y);
        }

        private void DrawClaimedBanner(Graphics g, int w, int h)
        {
            using var f = FitFont(g, _strings.Claimed, "Segoe UI", 40f, FontStyle.Bold, (int)(w * 0.8), 24f);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var rect = new RectangleF(0, 0, w, h);
            using var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.DrawString(_strings.Claimed, f, shadow, new RectangleF(0, 3, w, h), sf);
            g.DrawString(_strings.Claimed, f, new SolidBrush(Color.FromArgb(120, 240, 150)), rect, sf);
        }

        // ── Helpers GDI+ ──────────────────────────────────────────────────────

        private static void DrawChip(Graphics g, string text, int x, int y, Color color)
        {
            using var f = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            var size = g.MeasureString(text, f);
            var rect = new Rectangle(x, y, (int)size.Width + 14, (int)size.Height + 4);
            using var path = RoundedRect(rect, 6);
            using var border = new Pen(Color.FromArgb(120, color), 1.2f);
            g.DrawPath(border, path);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, f, new SolidBrush(color), rect, sf);
        }

        private static void DrawTracked(Graphics g, string text, Font font, Color color, float x, float y, float tracking)
        {
            using var brush = new SolidBrush(color);
            foreach (var ch in text)
            {
                var s = ch.ToString();
                g.DrawString(s, font, brush, x, y);
                x += g.MeasureString(s, font).Width + tracking - 2f;
            }
        }

        private static Font FitFont(Graphics g, string text, string family, float startSize, FontStyle style, int maxWidth, float minSize)
        {
            var size = startSize;
            while (size > minSize)
            {
                var font = new Font(family, size, style);
                if (g.MeasureString(text, font).Width <= maxWidth)
                {
                    return font;
                }

                font.Dispose();
                size -= 2f;
            }

            return new Font(family, minSize, style);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tick.Dispose();
                _background?.Dispose();
                _logo?.Dispose();
                _qr?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
