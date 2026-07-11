using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Client Live Contest natif : une fois le playToken remis par la page de
/// participation (POST /api/v1/livecontest/enroll), CE service orchestre tout
/// en local — la page web ne pilote rien. Il interroge la plateforme en
/// HTTPS SORTANT uniquement : resolution du jeu dans la ludotheque LOCALE
/// (le chemin du streamer ne circule jamais), lancement via EmulationStation,
/// pause au signal GAME_PLAYING (vraie partie), ready, comptage natif de la
/// metrique sur le bus d'evenements, depart simultane a startAt, push de la
/// progression, submit automatique, overlay in-game et fermeture de RetroArch.
/// </summary>
public sealed class LiveContestClientService : BackgroundService
{
    private static readonly bool Fr =
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";

    private static string T(string fr, string en) => Fr ? fr : en;

    private static readonly Uri EmulationStationBaseUri = new("http://127.0.0.1:1234");
    private const string RetroArchHost = "127.0.0.1";
    private const int RetroArchPort = 55355;

    private readonly IEventBus _eventBus;
    private readonly LiveContestOverlayService _overlay;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LiveContestClientService> _logger;
    private readonly string _statePath =
        Path.Combine(AppContext.BaseDirectory, "livecontest-enroll.json");
    private readonly object _gate = new();

    private Enrollment? _enrollment;

    // etat de la manche en cours
    private bool _prepared, _ready, _started, _finished;
    private bool _waitingGamePlaying;
    private string? _metricKind, _metricSignal;
    private double _metricTarget;
    private bool _targetReached;
    private double _value;
    private double _lastSent = -1;
    private long _seq;
    private long _startAtMs;
    private string _phase = "idle";
    private string? _system, _slug, _resolvedRom, _lastError;

    public LiveContestClientService(
        IEventBus eventBus,
        LiveContestOverlayService overlay,
        IHttpClientFactory httpFactory,
        ILogger<LiveContestClientService> logger)
    {
        _eventBus = eventBus;
        _overlay = overlay;
        _httpFactory = httpFactory;
        _logger = logger;
        _enrollment = LoadEnrollment();
    }

    public sealed record Enrollment(string PlayToken, string PlatformBase, DateTimeOffset EnrolledAt);

    public object Status()
    {
        lock (_gate)
        {
            return new
            {
                enrolled = _enrollment is not null,
                platform = _enrollment?.PlatformBase,
                phase = _phase,
                value = _value,
                ready = _ready,
                started = _started,
                system = _system,
                gameSlug = _slug,
                resolvedRom = _resolvedRom,
                lastError = _lastError
            };
        }
    }

    /// <summary>Inscription : le seul point d'entree venant du navigateur.</summary>
    public void Enroll(string playToken, string platformBase)
    {
        lock (_gate)
        {
            _enrollment = new Enrollment(playToken.Trim(),
                platformBase.TrimEnd('/'), DateTimeOffset.UtcNow);
            ResetRound();
            SaveEnrollment();
        }

        _logger.LogInformation("livecontest : inscription recue ({Platform})", platformBase);
        _overlay.Show(null, T("Inscription reçue !", "Enrolled!"), T("Préparation de ton jeu…", "Getting your game ready…"), 6000);
    }

    public void Withdraw()
    {
        lock (_gate)
        {
            _enrollment = null;
            ResetRound();
            SaveEnrollment();
        }

        _overlay.Hide();
    }

    private void ResetRound()
    {
        _prepared = _ready = _started = _finished = false;
        _waitingGamePlaying = false;
        _targetReached = false;
        _metricTarget = 0;
        _value = 0;
        _lastSent = -1;
        _seq = 0;
        _startAtMs = 0;
        _phase = "idle";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _eventBus.Subscribe<EventEnvelope>(OnBusEvent);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "livecontest : tick en echec");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var enrollment = _enrollment;
        if (enrollment is null || _finished)
        {
            return;
        }

        using var http = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            enrollment.PlatformBase + "/play/contest");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrollment.PlayToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogInformation("livecontest : playToken expire, desinscription");
            Withdraw();
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        _metricKind = Get(root, "objective", "metric", "kind") ?? "score";
        _metricSignal = Get(root, "objective", "metric", "signal");
        if (root.TryGetProperty("objective", out var objEl) &&
            objEl.TryGetProperty("metric", out var metEl) &&
            metEl.TryGetProperty("target", out var tgtEl) &&
            tgtEl.ValueKind == JsonValueKind.Number)
        {
            _metricTarget = tgtEl.GetDouble();
        }
        if (root.TryGetProperty("startAt", out var sa) && sa.ValueKind == JsonValueKind.Number)
        {
            // horloge serveur -> locale (offset via serverNow)
            var serverNow = root.TryGetProperty("serverNow", out var sn) &&
                            sn.ValueKind == JsonValueKind.Number
                ? sn.GetInt64()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var offset = serverNow - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _startAtMs = sa.GetInt64() - offset;
        }

        switch (status)
        {
            // des que le viewer a confirme, son jeu se lance : il appuie sur
            // START, la pause tombe sur GAME_PLAYING — chacun se met pret a
            // son rythme pendant que les inscriptions restent ouvertes
            case "open" or "test" or "locked" when !_prepared:
                _prepared = true;
                _ = PrepareAsync(root, cancellationToken);
                break;

            case "open" or "test" or "locked":
                break;

            case "running" when !_started && _startAtMs > 0:
                if (!_prepared)
                {
                    _prepared = true;
                    _ = PrepareAsync(root, cancellationToken); // inscrit en retard
                    break;
                }

                _started = true;
                _ = CountdownAndGoAsync(cancellationToken);
                break;

            case "closed":
                await FinishAsync(enrollment, cancellationToken);
                break;
        }

        if (_started && !_finished && Math.Abs(_value - _lastSent) > 0.0001)
        {
            await PushProgressAsync(enrollment, cancellationToken);
        }
    }

    /// <summary>Lock streamer : lancer le jeu local, inviter a appuyer sur START.</summary>
    private async Task PrepareAsync(JsonElement contest, CancellationToken cancellationToken)
    {
        _phase = "preparing";
        var system = _system = Get(contest, "system");
        var slug = _slug = Get(contest, "gameSlug");
        if (system is null || slug is null)
        {
            _phase = "no-game-info";
            _lastError = "contest sans systeme/jeu (cree avant la v3) : recreez le contest";
            _overlay.Show(null, T("Contest incomplet", "Incomplete contest"),
                T("Le streamer doit recréer le contest (infos de jeu manquantes).", "The streamer must recreate the contest (missing game info)."), 0);
            return;
        }

        DisablePauseNonactive();
        var romPath = _resolvedRom = ResolveLocalRom(system, slug);
        if (romPath is null)
        {
            _phase = "rom-not-found";
            _lastError = "aucun fichier ne matche « " + slug + " » dans roms\\" + system;
            _overlay.Show(null, T("Jeu introuvable dans ta ludothèque", "Game not found in your library"),
                slug + " (" + system + ") — " + T("lance-le toi-même puis appuie sur START.", "launch it yourself then press START."), 0);
        }
        else
        {
            _overlay.Show(null, T("Lancement du jeu…", "Launching the game…"), slug, 0);
            try
            {
                using var es = new HttpClient { BaseAddress = EmulationStationBaseUri };
                using var content = new StringContent(romPath, Encoding.UTF8, "text/plain");
                var res = await es.PostAsync("/launch", content, cancellationToken);
                if (!res.IsSuccessStatusCode)
                {
                    _lastError = "launch ES HTTP " + (int)res.StatusCode;
                }

                _logger.LogInformation("livecontest : launch {Rom} -> {Status}", romPath, res.StatusCode);
            }
            catch (Exception e)
            {
                _lastError = "launch ES injoignable : " + e.Message;
                _logger.LogWarning(e, "livecontest : launch ES en echec");
            }
        }

        _waitingGamePlaying = true;
        _phase = "press-start";
        _overlay.Show(null, T("Appuie sur START !", "Press START!"),
            T("Ta partie sera mise en pause, prête pour le départ…", "Your run will be paused, ready for the countdown…"), 0);
    }

    /// <summary>Signal GAME_PLAYING : la vraie partie demarre -> pause + ready.</summary>
    private void OnBusEvent(EventEnvelope envelope)
    {
        if (_enrollment is null || _finished ||
            !envelope.Type.StartsWith("retroarch.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.SerializeToElement(envelope.Payload);
        }
        catch (Exception)
        {
            return;
        }

        var actionType = Get(payload, "actionType")?.ToUpperInvariant();

        if (_waitingGamePlaying && actionType == "GAME_PLAYING")
        {
            _waitingGamePlaying = false;
            _ = PauseAndReadyAsync();
            return;
        }

        // comptage de la metrique UNIQUEMENT apres le GO
        if (!_started || actionType is null)
        {
            return;
        }

        if (_metricKind == "signal_count")
        {
            if (!_targetReached &&
                (envelope.Type.StartsWith("retroarch.action", StringComparison.OrdinalIgnoreCase) ||
                 envelope.Type.StartsWith("retroarch.score", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(actionType, _metricSignal, StringComparison.OrdinalIgnoreCase))
            {
                var reached = false;
                lock (_gate)
                {
                    _value += 1;
                    if (_metricTarget > 0 && _value >= _metricTarget)
                    {
                        _value = _metricTarget; // en course, seul le TEMPS compte
                        _targetReached = true;
                        reached = true;
                    }
                }

                if (reached)
                {
                    _ = TargetReachedAsync();
                }
            }
        }
        else if (envelope.Type.StartsWith("retroarch.score", StringComparison.OrdinalIgnoreCase) &&
                 payload.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Number)
        {
            lock (_gate)
            {
                _value = v.GetDouble();
            }
        }
    }

    private async Task PauseAndReadyAsync()
    {
        var state = await SendRetroArchAsync("GET_STATUS", expectResponse: true);
        if (state.Contains("PLAYING", StringComparison.Ordinal))
        {
            await SendRetroArchAsync("PAUSE_TOGGLE", expectResponse: false);
        }

        var enrollment = _enrollment;
        if (enrollment is not null)
        {
            try
            {
                using var http = _httpFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    enrollment.PlatformBase + "/play/ready");
                request.Headers.Authorization = new("Bearer", enrollment.PlayToken);
                await http.SendAsync(request, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "livecontest : /play/ready en echec");
            }
        }

        _ready = true;
        _phase = "ready";
        _overlay.Show(null, T("Prêt !", "Ready!"),
            T("Le départ sera donné pour tout le monde en même temps…", "The start will fire for everyone at once…"), 0);
    }

    /// <summary>Depart simultane : decompte 3-2-1 puis depause a startAt pile.</summary>
    private async Task CountdownAndGoAsync(CancellationToken cancellationToken)
    {
        // detection reelle : RetroArch doit tourner AVEC un jeu charge,
        // sinon pas de decompte (rien a depauser, ce serait mensonger)
        var probe = await SendRetroArchAsync("GET_STATUS", expectResponse: true);
        if (string.IsNullOrEmpty(probe) || probe.Contains("CONTENTLESS", StringComparison.Ordinal))
        {
            _phase = "game-not-running";
            _lastError = string.IsNullOrEmpty(probe)
                ? "RetroArch injoignable (jeu non lance ?)"
                : "RetroArch sans jeu charge";
            _overlay.Show(null, T("La manche est partie…", "The round has started…"),
                T("Ton jeu n’était pas lancé : lance-le et joue, tes points comptent quand même !", "Your game was not running: launch it and play, your points still count!"), 8000);
            _phase = "playing";
            return;
        }

        _phase = "countdown";
        for (var n = 3; n >= 1; n--)
        {
            var wait = _startAtMs - n * 1000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken);
            }

            _overlay.Show(null, n + "…", T("Départ imminent…", "Starting soon…"), 0);
        }

        var final = _startAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (final > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(final), cancellationToken);
        }

        lock (_gate)
        {
            _value = 0; // la metrique demarre au GO
            _lastSent = -1;
        }

        var state = await SendRetroArchAsync("GET_STATUS", expectResponse: true);
        if (state.Contains("PAUSED", StringComparison.Ordinal))
        {
            await SendRetroArchAsync("PAUSE_TOGGLE", expectResponse: false);
        }

        FocusRetroArch();
        _phase = "playing";
        _overlay.Show(null, "GO !!!", "", 5000);
    }

    /// <summary>Objectif atteint (course) : l'instant d'arrivee est pousse
    /// IMMEDIATEMENT (il fait le classement), le jeu se met en pause.</summary>
    private async Task TargetReachedAsync()
    {
        var enrollment = _enrollment;
        if (enrollment is not null)
        {
            await PushProgressAsync(enrollment, CancellationToken.None);
        }

        var state = await SendRetroArchAsync("GET_STATUS", expectResponse: true);
        if (state.Contains("PLAYING", StringComparison.Ordinal))
        {
            await SendRetroArchAsync("PAUSE_TOGGLE", expectResponse: false);
        }

        _phase = "target-reached";
        _overlay.Show(null, T("🏁 Objectif atteint !", "🏁 Target reached!"),
            T("Ton temps est enregistré — regarde le classement sur le stream !",
              "Your time is in — watch the stream for the standings!"), 0);
    }

    private async Task PushProgressAsync(Enrollment enrollment, CancellationToken cancellationToken)
    {
        double value;
        long seq;
        lock (_gate)
        {
            value = _value;
            seq = ++_seq;
        }

        using var http = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            enrollment.PlatformBase + "/play/events");
        request.Headers.Authorization = new("Bearer", enrollment.PlayToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            events = new[] { new { kind = "progress", value, clientSeq = seq } }
        }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _lastSent = value;
        }
    }

    /// <summary>Cloture : submit transparent, bravo, et RetroArch se ferme.</summary>
    private async Task FinishAsync(Enrollment enrollment, CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _phase = "finished";
        try
        {
            using var http = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                enrollment.PlatformBase + "/play/submit");
            request.Headers.Authorization = new("Bearer", enrollment.PlayToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { finalValue = _value }),
                Encoding.UTF8, "application/json");
            await http.SendAsync(request, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "livecontest : submit final en echec");
        }

        if (_started)
        {
            _overlay.Show(null, T("BRAVO !!!", "WELL PLAYED!!!"), T("Score final : ", "Final score: ") + _value, 0);
            await Task.Delay(4000, cancellationToken);
            _overlay.Show(null, T("À toute sur Twitch !!!", "See you on Twitch!!!"), T("Le jeu va se fermer…", "The game is about to close…"), 4500);
            await Task.Delay(4500, cancellationToken);
            await SendRetroArchAsync("QUIT", expectResponse: false);
        }

        _overlay.Hide();
        Withdraw();
    }

    /// <summary>
    /// RetroBat/RetroArch pausent le jeu quand la fenetre perd le focus
    /// (pause_nonactive) : incompatible avec le depart pilote. On corrige le
    /// TEMPLATE RetroBat (retroarch.cfg est reecrit depuis lui a chaque
    /// lancement) ET la config live.
    /// </summary>
    private void DisablePauseNonactive()
    {
        foreach (var file in new[]
        {
            Path.Combine(RetroBatPaths.RetroBatRoot, "system", "templates", "retroarch", "retroarch.cfg"),
            Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.cfg")
        })
        {
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                var content = File.ReadAllText(file);
                if (content.Contains("pause_nonactive = \"true\"", StringComparison.Ordinal))
                {
                    File.WriteAllText(file, content.Replace(
                        "pause_nonactive = \"true\"", "pause_nonactive = \"false\""));
                    _logger.LogInformation("livecontest : pause_nonactive desactive dans {File}", file);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "livecontest : pause_nonactive non modifiable ({File})", file);
            }
        }
    }

    /// <summary>Ramene RetroArch au premier plan (fenetre parfois en arriere-plan apres le launch ES).</summary>
    private static void FocusRetroArch()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("retroarch"))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(process.MainWindowHandle);
                }
            }
        }
        catch (Exception) { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Resolution LOCALE du jeu : le contest donne systeme + slug, on cherche
    /// dans roms\{system} du RetroBat du viewer (normalisation des noms —
    /// « sonic-the-hedgehog » matche « Sonic The Hedgehog (USA, Europe).zip »).
    /// </summary>
    internal static string? ResolveLocalRom(string? system, string? slug, string? romsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var dir = Path.Combine(romsRoot ?? Path.Combine(RetroBatPaths.RetroBatRoot, "roms"), system);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var wanted = Normalize(slug);
        string? best = null;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var normalized = Normalize(name);
            if (normalized == wanted)
            {
                return file; // correspondance exacte
            }

            if (best is null && (normalized.StartsWith(wanted, StringComparison.Ordinal) ||
                                 wanted.StartsWith(normalized, StringComparison.Ordinal)))
            {
                best = file;
            }
        }

        return best;
    }

    /// <summary>minuscules, sans groupes (...) [...], sans separateurs.</summary>
    internal static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        var depth = 0;
        foreach (var c in name)
        {
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static async Task<string> SendRetroArchAsync(string command, bool expectResponse)
    {
        try
        {
            using var udp = new UdpClient();
            var bytes = Encoding.UTF8.GetBytes(command);
            await udp.SendAsync(bytes, bytes.Length, RetroArchHost, RetroArchPort);
            if (!expectResponse)
            {
                return string.Empty;
            }

            var receive = udp.ReceiveAsync();
            var done = await Task.WhenAny(receive, Task.Delay(1200));
            return done == receive ? Encoding.UTF8.GetString(receive.Result.Buffer) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? Get(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private Enrollment? LoadEnrollment()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<Enrollment>(File.ReadAllText(_statePath))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveEnrollment()
    {
        try
        {
            if (_enrollment is null)
            {
                File.Delete(_statePath);
            }
            else
            {
                File.WriteAllText(_statePath, JsonSerializer.Serialize(_enrollment));
            }
        }
        catch (IOException) { }
    }
}
