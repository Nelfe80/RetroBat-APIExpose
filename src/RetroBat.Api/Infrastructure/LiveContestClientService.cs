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
    private double _value;
    private double _lastSent = -1;
    private long _seq;
    private long _startAtMs;
    private string _phase = "idle";

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
                started = _started
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
        _overlay.Show(null, "Inscription reçue !", "En attente de la préparation par le streamer…", 6000);
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
            case "open" or "test":
                _phase = "waiting";
                break;

            case "locked" when !_prepared:
                _prepared = true;
                _ = PrepareAsync(root, cancellationToken);
                break;

            case "running" when !_started && _startAtMs > 0:
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
        var system = Get(contest, "system");
        var slug = Get(contest, "gameSlug");
        var romPath = ResolveLocalRom(system, slug);
        if (romPath is null)
        {
            _phase = "rom-not-found";
            _overlay.Show(null, "Jeu introuvable dans ta ludothèque",
                (slug ?? "?") + " (" + (system ?? "?") + ") — lance-le toi-même puis appuie sur START.", 0);
        }
        else
        {
            _overlay.Show(null, "Lancement du jeu…", slug ?? "", 0);
            try
            {
                using var es = new HttpClient { BaseAddress = EmulationStationBaseUri };
                using var content = new StringContent(romPath, Encoding.UTF8, "text/plain");
                var res = await es.PostAsync("/launch", content, cancellationToken);
                _logger.LogInformation("livecontest : launch {Rom} -> {Status}", romPath, res.StatusCode);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "livecontest : launch ES en echec");
            }
        }

        _waitingGamePlaying = true;
        _phase = "press-start";
        _overlay.Show(null, "Appuie sur START !",
            "Ta partie sera mise en pause, prête pour le départ…", 0);
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
            if ((envelope.Type.StartsWith("retroarch.action", StringComparison.OrdinalIgnoreCase) ||
                 envelope.Type.StartsWith("retroarch.score", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(actionType, _metricSignal, StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    _value += 1;
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
        _overlay.Show(null, "Prêt !",
            "Le départ sera donné pour tout le monde en même temps…", 0);
    }

    /// <summary>Depart simultane : decompte 3-2-1 puis depause a startAt pile.</summary>
    private async Task CountdownAndGoAsync(CancellationToken cancellationToken)
    {
        _phase = "countdown";
        for (var n = 3; n >= 1; n--)
        {
            var wait = _startAtMs - n * 1000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken);
            }

            _overlay.Show(null, n + "…", "Départ imminent…", 0);
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

        _phase = "playing";
        _overlay.Show(null, "GO !!!", "", 5000);
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
            _overlay.Show(null, "BRAVO !!!", "Score final : " + _value, 0);
            await Task.Delay(4000, cancellationToken);
            _overlay.Show(null, "À toute sur Twitch !!!", "Le jeu va se fermer…", 4500);
            await Task.Delay(4500, cancellationToken);
            await SendRetroArchAsync("QUIT", expectResponse: false);
        }

        _overlay.Hide();
        Withdraw();
    }

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
