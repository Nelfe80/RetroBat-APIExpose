using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Media;
using RetroBat.Api.Scoring;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Scoring certifié - côté agent (étape 3c). Ce service enrôle la clé de signature de
/// l'appareil (CNG/NCrypt), demande un ticket au lancement, capte l'attestation du
/// listener, le score cumulé (agrégateur) et la session (checkpoints/timing/intégrité),
/// puis à la fin de partie ASSEMBLE le passeport, le signe (CNG) et le soumet.
///
/// Le score des checkpoints vient de l'AGRÉGATEUR (LiveScoreAggregator, score.live.changed) -
/// jamais des lectures brutes du wrapper - corrélé aux checkpoints par le n° de frame.
/// Les empreintes gated (listener/core/mem) viennent de l'attestation. Les autres
/// empreintes (modules/process/content) restent à calibrer une fois le profil ouvert.
/// </summary>
public sealed class NelfePlayScoringReporter : BackgroundService
{
    public const string ScoringKeyName = "Nelfe.Scoring.Device";
    private const int MaxTrajectory = 512;

    private readonly IEventBus _eventBus;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    private readonly ClaimOverlayService? _claimOverlay;
    private readonly NelfePlayScoringSessionService? _scoringSession;
    private readonly IEmulationStationNotificationService? _esNotify;
    private readonly ILogger<NelfePlayScoringReporter>? _logger;

    private IDisposable? _subscription;
    private readonly object _sync = new();

    private string? _enrolledKeyId;
    private string? _listenerSha256, _coreSha256, _memSha256, _contentSha256, _contentMd5, _contentSha1, _wrapperVersion;
    private JsonElement? _ticket;
    private long _lastFrame;
    private long? _finalTotal;
    private bool _inDemo;   // attract mode : le jeu se joue seul → on ignore le score
    // Phase D (segmentation en RUNS, 100% APIExpose) : la trajectoire des scores suffit —
    // on la découpe aux CHUTES de score (un score qui retombe = partie relancée) et on ne
    // soumet QUE le meilleur run (segment monotone). Un super score n'est plus perdu si on
    // rejoue, et le wrapper n'est PAS touché ni sollicité par event (0 surcoût en jeu).
    private readonly List<(long frame, long total)> _trajectory = new();

    // ── Lien replay ↔ score (funnel « ▷ REPLAY » de /rankings) ───────────────
    // Le reporter connaît le session_id (il le génère) et le verdict ; le recorder
    // publie l'id du replay actif (replay.recording.started) puis son sha au finalize
    // (replay.finalized). On rapproche les deux — quel que soit l'ordre d'arrivée —
    // et on POST /api/v1/agent/scores/replay-link. Purement additif et best-effort :
    // un échec n'affecte ni le scoring ni l'enregistrement.
    private readonly RetroBat.Api.Replay.Storage.ReplayStore? _replayStore;   // pour estampiller score/rang sur la méta du replay
    private string? _activeReplayId;
    private readonly Dictionary<string, (string sessionId, string visibility, long? score, int? rank, DateTime at)> _pendingScoreLink = new();
    private readonly Dictionary<string, (string sha256, DateTime at)> _finalizedReplay = new();
    private static readonly TimeSpan ReplayLinkTtl = TimeSpan.FromMinutes(20);

    public static bool Enabled { get; set; } = true;

    public NelfePlayScoringReporter(
        IEventBus eventBus,
        IHttpClientFactory httpFactory,
        NelfePlayDeviceStore devices,
        ClaimOverlayService? claimOverlay = null,
        NelfePlayScoringSessionService? scoringSession = null,
        IEmulationStationNotificationService? esNotify = null,
        RetroBat.Api.Replay.Storage.ReplayStore? replayStore = null,
        ILogger<NelfePlayScoringReporter>? logger = null)
    {
        _replayStore = replayStore;
        _eventBus = eventBus;
        _httpFactory = httpFactory;
        _devices = devices;
        _claimOverlay = claimOverlay;
        _scoringSession = scoringSession;
        _esNotify = esNotify;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        try
        {
            await EnsureEnrolledAsync(stoppingToken).ConfigureAwait(false);

            // La mesure du score est ÉVÉNEMENTIELLE (pipe → HandleEvent). En fond, un
            // battement calme vérifie l'état recovery « share datas » : si le serveur
            // reconstruit sa base, cette machine re-verse ses records auto-conservés.
            // Marche pour les machines ANONYMES (contrairement à /agent/work lié à un
            // compte) car ResolveCredential retombe sur le credential anonyme.
            var delay = TimeSpan.FromSeconds(20); // premier contrôle peu après le boot
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                await RecoveryCheckAsync(stoppingToken).ConfigureAwait(false);
                await ClaimCheckAsync(stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(180);
            }
        }
        catch (OperationCanceledException) { }
        finally { _subscription?.Dispose(); }
    }

    /// <summary>
    /// Battement recovery : interroge l'état « share datas » (endpoint public, sans SQL)
    /// et, s'il est armé, re-verse les records auto-conservés. On n'agit JAMAIS
    /// spontanément - uniquement quand l'admin a explicitement armé une reconstruction.
    /// </summary>
    private async Task RecoveryCheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) return;
        try
        {
            using var client = CreateClient(credential);
            using var response = await client.GetAsync("/api/v1/scores/recovery-status", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject;
            var contribute = (bool?)root?["contribute"] ?? false;
            if (!contribute) return;

            // NOUVEL ÉPISODE : une époque inédite (nouvel armement admin) → on RÉ-ARME les
            // records déjà versés (*.sent → *.json) pour qu'une NOUVELLE récupération les
            // re-verse aussi. Le .sent ne vaut donc que POUR l'épisode courant. L'époque est
            // persistée pour survivre à un redémarrage au milieu d'un même épisode.
            var epoch = (string?)root?["epoch"] ?? "";
            if (!string.IsNullOrEmpty(epoch) && epoch != ReadLastEpoch())
            {
                RearmSentFiles(CertifiedDir());
                WriteLastEpoch(epoch);
            }

            await ContributeCertifiedAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace($"recovery-check échec : {ex.Message}");
        }
    }

    /// <summary>
    /// À l'identification de la machine (appairée), rattache ses scores ANONYMES au
    /// compte. La machine prouve qu'elle possède les deux credentials (appairé pour
    /// l'auth, anonyme dans le corps). Une seule fois (marqueur claimed.flag).
    /// </summary>
    private async Task ClaimCheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || !_devices.IsPaired) return;
        var paired = _devices.GetCredential();
        if (string.IsNullOrEmpty(paired)) return;

        var flag = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "claimed.flag");
        if (System.IO.File.Exists(flag)) return;

        var anon = ReadAnonymousCredential();
        if (string.IsNullOrEmpty(anon))
        {
            try { System.IO.File.WriteAllText(flag, "no-anon"); } catch { }
            return; // rien d'anonyme à réclamer
        }

        try
        {
            using var client = CreateClient(paired);
            var body = new JsonObject { ["anonymous_credential"] = anon };
            using var content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/claim", content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var b = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Trace($"claim scores anonymes : {b}");
                try { System.IO.File.WriteAllText(flag, DateTime.UtcNow.ToString("o")); } catch { }
            }
        }
        catch (Exception ex)
        {
            Trace($"claim échec : {ex.Message}");
        }
    }

    private static string? ReadAnonymousCredential()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (System.IO.File.Exists(path))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    return c.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private static string CertifiedDir() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "certified");

    private static string EpochFile() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "recovery-epoch.txt");

    private static string ReadLastEpoch()
    {
        try { return System.IO.File.Exists(EpochFile()) ? System.IO.File.ReadAllText(EpochFile()).Trim() : ""; }
        catch { return ""; }
    }

    private static void WriteLastEpoch(string epoch)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(EpochFile())!);
            System.IO.File.WriteAllText(EpochFile(), epoch, new UTF8Encoding(false));
        }
        catch { /* best-effort : au pire on ré-arme une fois de trop, sans dommage (idempotent) */ }
    }

    /// <summary>Ré-arme les records d'un épisode précédent : *.sent → *.json.</summary>
    private void RearmSentFiles(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return;
        var n = 0;
        foreach (var sent in System.IO.Directory.EnumerateFiles(dir, "*.sent"))
        {
            try { System.IO.File.Move(sent, sent[..^5], overwrite: true); n++; } catch { /* ignore */ }
        }
        if (n > 0) Trace($"recovery : {n} record(s) ré-armé(s) (nouvel épisode).");
    }

    /// <summary>
    /// Re-verse les passeports auto-conservés dans certified/ vers le serveur en
    /// reconstruction. Chaque record REPASSE le pipeline vérifié (signature + règles) et
    /// est idempotent (déjà présent = duplicate). On marque le fichier .sent après envoi
    /// pour ne pas le renvoyer ; un échec transport le laisse pour le prochain battement.
    /// </summary>
    private async Task ContributeCertifiedAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var dir = CertifiedDir();
        if (!System.IO.Directory.Exists(dir)) return;

        var sent = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body;
            try { body = await System.IO.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch { continue; }

            using var content = new StringContent(body, new UTF8Encoding(false), "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/contribute", content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                try { System.IO.File.Move(path, path + ".sent", overwrite: true); } catch { /* on retentera */ }
                sent++;
            }
        }

        if (sent > 0)
        {
            Trace($"recovery : {sent} record(s) auto-conservé(s) re-versé(s).");
            _logger?.LogInformation("Scoring : {Count} record(s) re-versé(s) (recovery « share datas »).", sent);
        }
    }

    private void HandleEvent(EventEnvelope envelope)
    {
        if (!Enabled) return;
        try
        {
            switch (envelope.Type?.ToLowerInvariant())
            {
                case "ui.game.started":
                    // Pas de ticket ici : on ne le demande qu'à la fin, si on soumet
                    // vraiment (score + jeu ouvert). Une démo sans score = zéro ticket.
                    // Une nouvelle partie retire aussitôt une éventuelle surimpression
                    // de réclamation restée à l'écran.
                    _claimOverlay?.HideNow();
                    ResetSession();
                    break;
                case "scoring.listener.attestation":
                    CaptureAttestation(ToJson(envelope.Payload));
                    break;
                case "retroarch.score":
                    CaptureFrame(ToJson(envelope.Payload));
                    break;
                case "retroarch.state":
                    CaptureState(ToJson(envelope.Payload));
                    break;
                case "score.live.changed":
                    CaptureTotal(ToJson(envelope.Payload));
                    break;
                case "scoring.listener.session":
                    _ = OnSessionAsync(ToJson(envelope.Payload), CancellationToken.None);
                    break;
                case "replay.recording.started":
                    CaptureActiveReplay(ToJson(envelope.Payload));
                    break;
                case "replay.finalized":
                    OnReplayFinalized(ToJson(envelope.Payload));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : événement {Type} ignoré", envelope.Type);
        }
    }

    private void ResetSession()
    {
        lock (_sync)
        {
            _listenerSha256 = _coreSha256 = _memSha256 = _contentSha256 = _contentMd5 = _contentSha1 = _wrapperVersion = null;
            _ticket = null;
            _lastFrame = 0;
            _finalTotal = null;
            _inDemo = false;
            _trajectory.Clear();
        }
    }

    private void CaptureAttestation(JsonElement root)
    {
        lock (_sync)
        {
            _listenerSha256 = GetString(root, "ListenerSha256");
            _coreSha256 = GetString(root, "CoreSha256");
            _memSha256 = GetString(root, "MemSha256");
            _contentSha256 = GetString(root, "ContentSha256");
            _contentMd5 = GetString(root, "ContentMd5");
            _contentSha1 = GetString(root, "ContentSha1");
            _wrapperVersion = GetString(root, "WrapperVersion");
        }
    }

    private void CaptureFrame(JsonElement root)
    {
        if (root.TryGetProperty("Frame", out var f) && f.TryGetInt64(out var frame))
        {
            lock (_sync) { if (!_inDemo) _lastFrame = frame; }
        }
    }

    // Attract mode : le jeu se joue seul. On ne certifie que du jeu HUMAIN, donc on
    // ignore le score pendant la démo. Convention .MEM : action GAME_PLAYING vs DEMO_*.
    private void CaptureState(JsonElement root)
    {
        var action = (GetString(root, "actionType") ?? GetString(root, "ActionType") ?? "").ToUpperInvariant();
        if (action.Length == 0) return;
        lock (_sync)
        {
            if (action.Contains("DEMO")) _inDemo = true;
            else if (action.Contains("PLAYING") || action.Contains("GAME_PLAY")) _inDemo = false;
        }
    }

    // Phase D : découpe la trajectoire aux CHUTES de score (le score qui retombe = un
    // nouveau run) et renvoie le sous-segment MONOTONE du MEILLEUR run (pic le plus haut).
    // Robuste : ne dépend PAS des frames (le score seul suffit). Un seul run croissant →
    // toute la trajectoire. C'est ce qui fait qu'un super score n'est jamais perdu.
    private static List<(long frame, long total)> SelectBestRun(List<(long frame, long total)> traj)
    {
        if (traj.Count == 0) return traj;
        List<(long frame, long total)>? best = null;
        long bestPeak = long.MinValue;
        var cur = new List<(long frame, long total)>();
        long prev = long.MinValue;
        foreach (var pt in traj)
        {
            if (pt.total < prev)   // chute → fin du run précédent (segment monotone)
            {
                long peak = cur.Count > 0 ? cur[^1].total : long.MinValue;
                if (peak > bestPeak) { bestPeak = peak; best = new List<(long frame, long total)>(cur); }
                cur.Clear();
            }
            cur.Add(pt);
            prev = pt.total;
        }
        if (cur.Count > 0 && (best is null || cur[^1].total > bestPeak)) best = cur;
        return best ?? traj;
    }

    private void CaptureTotal(JsonElement root)
    {
        if (!root.TryGetProperty("Score", out var s) || !s.TryGetInt64(out var total)) return;
        lock (_sync)
        {
            if (_inDemo) return;   // score de démo → jamais certifié
            _finalTotal = total;
            // Le total agrégé à la frame courante : la trajectoire vérifiable du score.
            if (_trajectory.Count == 0 || _trajectory[^1].total != total)
            {
                _trajectory.Add((_lastFrame, total));
                if (_trajectory.Count > MaxTrajectory) _trajectory.RemoveAt(0);
            }
        }
    }

    // ── Fin de partie : assembler + signer + soumettre ───────────────────────

    private async Task OnSessionAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var systemId = GetString(payload, "SystemId") ?? "";
        var romGroup = GetString(payload, "Rom") ?? "";
        var sessionJson = GetString(payload, "Session");
        Trace($"session reçue sys={systemId} rom={romGroup} sessionLen={sessionJson?.Length ?? -1}");
        if (sessionJson is null) return;

        // Appairé OU anonyme : le scoring accepte les deux (anonyme = score « anonyme »).
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential))
        {
            Trace("STOP: pas de credential (ni appairé ni anonyme)");
            return;
        }

        string? listenerSha, coreSha, memSha, contentSha, contentMd5, contentSha1, wrapperVersion;
        long? finalTotal;
        List<(long frame, long total)> trajectory;
        lock (_sync)
        {
            listenerSha = _listenerSha256; coreSha = _coreSha256; memSha = _memSha256;
            contentSha = _contentSha256; contentMd5 = _contentMd5; contentSha1 = _contentSha1; wrapperVersion = _wrapperVersion; finalTotal = _finalTotal;
            trajectory = new List<(long, long)>(_trajectory);
        }

        // ARCADE : l'identite du contenu n'est pas mesurable depuis le fichier.
        //
        // Le referentiel decrit un set arcade par un sha1 issu des DAT, pas par une
        // empreinte du .zip - on l'a verifie : le zip de 19xx donne 24754f53..., le
        // referentiel dit 813f465f..., et aucun md5 d'arcade n'y figure (0 % de
        // couverture). Une empreinte MESUREE du fichier ne peut donc jamais resoudre.
        //
        // Le pont MAME contourne cela depuis toujours en LISANT le sha1 declare dans la
        // gamelist ; l'integrite reelle du romset est garantie par l'emulateur, qui le
        // verifie contre son DAT au chargement. Le chemin RetroArch fait desormais
        // pareil : sans cela, le meme jeu etait certifiable sous MAME et refuse sous
        // FBNeo avec un « ROM non reconnue » trompeur, alors que la ROM etait la bonne.
        //
        // Le passeport porte donc les DEUX : l'identite declaree (sha1) et ce que le
        // wrapper a reellement mesure (md5, sha256).
        if (string.IsNullOrEmpty(contentSha1) && !string.IsNullOrEmpty(romGroup))
        {
            contentSha1 = GamelistIdentity.DeclaredSha1(systemId, romGroup);
            Trace($"identite declaree : sha1={(contentSha1 is null ? "introuvable" : contentSha1)} (sys={systemId} rom={romGroup})");
        }

        // Phase D : le score soumis = le MEILLEUR run. On segmente la trajectoire aux CHUTES
        // de score (un score qui retombe = le joueur a relancé une partie) et on garde le
        // segment monotone au pic le plus haut. Un super score n'est donc jamais perdu par un
        // mauvais run qui suit. Calculé tôt + tracé pour valider même hors chemin certifié.
        var bestRun = SelectBestRun(trajectory);
        long runPeak = bestRun.Count > 0 ? bestRun[^1].total : (finalTotal ?? 0);
        Trace($"segmentation : meilleur run {bestRun.Count}/{trajectory.Count} pts, pic={runPeak} (total global {finalTotal})");

        // Rien à certifier sans score ni attestation : on s'arrête AVANT de consommer
        // quoi que ce soit (démo, navigation, jeu non joué).
        Trace($"état: listener={listenerSha is not null} core={coreSha is not null} content={contentSha is not null} finalTotal={finalTotal} trajPts={trajectory.Count} inDemo={_inDemo}");
        if (listenerSha is null || finalTotal is null)
        {
            Trace("STOP: pas de score/attestation");
            return;
        }

        var profile = await FetchProfileAsync(credential!, systemId, romGroup, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            Trace($"STOP: profil {romGroup} non ouvert (fetch null)");
            return;
        }

        // Ticket PARESSEUX : un seul, obtenu ici, uniquement parce qu'on va soumettre.
        await RequestTicketAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? ticket;
        lock (_sync) { ticket = _ticket; }
        if (ticket is null)
        {
            Trace("STOP: ticket indisponible");
            return;
        }
        // L'identité de l'appareil vient du ticket (résolue par le serveur : appairé ou
        // anonyme) - l'agent n'a pas besoin de la connaître lui-même.
        var deviceId = ticket.Value.TryGetProperty("device_id", out var did) ? did.GetString() : null;
        if (string.IsNullOrEmpty(deviceId))
        {
            Trace("STOP: ticket sans device_id");
            return;
        }
        Trace($"assemblage du passeport (device={deviceId})…");

        if (runPeak <= 0) runPeak = finalTotal.Value;   // filet : aucun segment exploitable

        using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
        JsonObject passport;
        try
        {
            passport = BuildPassport(
                systemId, romGroup, sessionJson, ticket.Value, profile.Value,
                deviceId!, deviceKey, listenerSha, coreSha, memSha, contentSha, contentMd5, contentSha1, wrapperVersion,
                runPeak, bestRun);
            var body = passport.DeepClone()!.AsObject();
            body.Remove("signature");
            passport["signature"] = deviceKey.SignB64Url(Jcs.CanonicalBytes(body));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scoring : assemblage du passeport impossible.");
            return;
        }

        await SubmitAsync(credential!, passport, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject BuildPassport(
        string systemId, string romGroup, string sessionJson, JsonElement ticket, JsonElement profile,
        string deviceId, CngDeviceKey deviceKey, string listenerSha, string? coreSha, string? memSha,
        string? contentSha, string? contentMd5, string? contentSha1, string? wrapperVersion, long finalTotal, List<(long frame, long total)> trajectory)
    {
        var session = JsonNode.Parse(sessionJson)!.AsObject();
        long frameCount = (long?)session["frame_count"] ?? 0;
        long monotonicMs = (long?)session["monotonic_ms"] ?? 0;
        long resets = (long?)session["resets"] ?? 0;
        long saveStateLoads = (long?)session["save_state_loads"] ?? 0;
        // Anti-triche (Gap 2) : le wrapper homologué compte ces vecteurs et les émet dans
        // la session ; absents (vieux wrapper / autre backend) → 0 = neutre. Le certifié
        // n'existe QUE via un listener whitelisté (CoreVerifier profile.listener_unauthorized),
        // donc « 0 » ne vaut jamais « présumé propre » là où on n'observe pas.
        long cheats = (long?)session["cheats"] ?? 0;
        long rewind = (long?)session["rewind"] ?? 0;
        long runahead = (long?)session["runahead"] ?? 0;
        long fastForward = (long?)session["fast_forward"] ?? 0;
        long netplay = (long?)session["netplay"] ?? 0;
        long continues = (long?)session["continues"] ?? 0;
        // Phase E : réglages (DIP/vies/difficulté) capturés par le listener sous forme de chaîne
        // canonique triée. Absent (backend pas encore câblé) → placeholder stable. Le vérifieur ne
        // contrôle le digest QUE si le profil épingle allowed_core_options_digest (opt-in additif).
        string? coreOptionsRaw = (string?)session["core_options"];
        string? coreOptions = FilterGameplayCoreOptions(coreOptionsRaw);
        string coreOptionsDigest = !string.IsNullOrEmpty(coreOptions)
            ? Crypto.Sha256Hex(coreOptions)
            : Crypto.Sha256Hex("core-options@default");
        if (!string.IsNullOrEmpty(coreOptions))
            _logger?.LogInformation("Scoring Phase E : réglages gameplay = [{Options}] → core_options_digest={Digest} (à épingler ; brut = [{Raw}])", coreOptions, coreOptionsDigest, coreOptionsRaw);

        string ruleset = profile.GetProperty("ruleset").GetString() ?? "";
        long profileVersion = profile.GetProperty("profile_version").GetInt64();
        string profileDocSha = profile.GetProperty("profile_document_sha256").GetString() ?? "";
        string engine = profile.TryGetProperty("engine", out var e) ? (e.GetString() ?? "libretro") : "libretro";
        string? manifestCommit = profile.TryGetProperty("manifest_commit", out var mc) ? mc.GetString() : null;
        var metricProfile = profile.TryGetProperty("metric", out var mp) ? mp : default;
        string direction = metricProfile.ValueKind == JsonValueKind.Object && metricProfile.TryGetProperty("ranking_direction", out var rd)
            ? (rd.GetString() ?? "higher_better") : "higher_better";
        string resultSource = metricProfile.ValueKind == JsonValueKind.Object && metricProfile.TryGetProperty("result_source", out var rs)
            ? (rs.GetString() ?? "final") : "final";

        // Checkpoints = la progression du MEILLEUR run (`trajectory` est déjà réduite au
        // segment gagnant, monotone). Le score porte dans `metric` (string) ; le dernier
        // checkpoint porte `game_end` (exigé par le vérifieur) et son metric == metric.value
        // (result_source=final). monotonic_ms interpolé sur la durée de session : l'ordre et
        // les valeurs de score sont fiables, les frames du listener ne le sont pas ici.
        // Bornée (SPEC : ≤128 checkpoints) par sous-échantillonnage régulier.
        var checkpoints = new JsonArray();
        long endMs = monotonicMs > 0 ? monotonicMs : frameCount;
        int n = trajectory.Count;
        int step = n > 96 ? (n / 96) + 1 : 1;
        for (var i = 0; i < n; i += step)
        {
            var (f, t) = trajectory[i];
            var last = i + step >= n;
            long ms = n > 1 ? (long)((double)i / (n - 1) * endMs) : endMs;
            var node = new JsonObject { ["monotonic_ms"] = ms, ["frame"] = f, ["metric"] = (last ? finalTotal : t).ToString() };
            if (last) node["event"] = "game_end";
            checkpoints.Add(node);
        }
        // Filet : le vérifieur exige au moins un checkpoint game_end.
        if (checkpoints.Count == 0)
        {
            checkpoints.Add(new JsonObject { ["monotonic_ms"] = endMs, ["frame"] = frameCount, ["metric"] = finalTotal.ToString(), ["event"] = "game_end" });
        }
        var checkpointsDigest = Crypto.Sha256Hex(Jcs.CanonicalBytes(checkpoints));

        // Empreintes gated par le profil (listener/core/mem) = attestation. Les modules
        // détaillés (frontend/apiexpose/launcher/hook) + process + content ROM restent à
        // calibrer par introspection une fois le profil ouvert.
        var modules = new JsonArray
        {
            new JsonObject { ["role"] = "listener", ["sha256"] = listenerSha },
            new JsonObject { ["role"] = "real_core", ["sha256"] = coreSha ?? listenerSha },
        };
        var modulesDigest = Crypto.Sha256Hex(Jcs.CanonicalBytes(modules));

        JsonObject Triple(string? h) => new() { ["start_sha256"] = h, ["loaded_sha256"] = h, ["end_sha256"] = h };
        JsonObject ContentArtifact(string? sha, string? md5, string? sha1)
        {
            var o = Triple(sha);
            if (md5 is not null) o["md5"] = md5;     // Voie A : md5 No-Intro de la ROM (consoles)
            if (sha1 is not null) o["sha1"] = sha1;  // MAME : sha1 du set (gamelist), MAME vérifiant le romset
            return o;
        }

        // MONDE de la partie : la session le porte.
        //  - STATION : joueur checké-in en salle par le hub → provenance salle (nom/ville).
        //  - STREAM  : participation à un contest de streamer (salle OU maison) → chaîne +
        //    contest_id. Armée par APIExpose (enrôlement contest) ou par le hub (borne).
        //  - HOME    : aucune session (machine perso / borne libre).
        // Le record est attribué au code joueur (RGPC) de la session. Champs ADDITIFS et
        // SIGNÉS (JCS re-trie ; un vérifieur qui les ignore reste valide).
        var sessionPlayer = _scoringSession?.Get();
        var world = sessionPlayer?.World ?? "home";
        JsonObject? contextVenue = sessionPlayer is not null
            && (sessionPlayer.VenueName is not null || sessionPlayer.VenueCity is not null)
            ? new JsonObject { ["name"] = sessionPlayer.VenueName, ["city"] = sessionPlayer.VenueCity }
            : null;

        return new JsonObject
        {
            ["protocol"] = 1,
            ["session_id"] = Guid.NewGuid().ToString(),
            ["ticket"] = JsonNode.Parse(ticket.GetRawText()),
            ["game"] = new JsonObject
            {
                ["system_id"] = systemId, ["rom_group"] = romGroup, ["engine"] = engine,
                ["ruleset"] = ruleset, ["profile_version"] = profileVersion,
                ["manifest_commit"] = manifestCommit, ["profile_document_sha256"] = profileDocSha,
            },
            ["device"] = new JsonObject { ["device_id"] = deviceId, ["key_id"] = deviceKey.KeyId, ["key_type"] = "ecdsa_p256" },
            ["identity"] = new JsonObject { ["player_ref"] = null, ["session_player_id"] = sessionPlayer?.PlayerCode },
            ["context"] = new JsonObject
            {
                ["world"] = world,
                ["venue"] = contextVenue,
                ["channel"] = sessionPlayer?.Channel,
                ["contest_id"] = sessionPlayer?.ContestId,
            },
            ["listener"] = new JsonObject
            {
                ["build"] = wrapperVersion ?? "0", ["start_sha256"] = listenerSha,
                ["loaded_sha256"] = listenerSha, ["end_sha256"] = listenerSha,
                ["certification"] = "listener-homologation",
            },
            ["software"] = new JsonObject { ["modules"] = modules, ["modules_digest"] = modulesDigest },
            ["artifacts"] = new JsonObject
            {
                ["core"] = Triple(coreSha), ["content"] = ContentArtifact(contentSha, contentMd5, contentSha1), ["mem"] = Triple(memSha),
                ["core_options_digest"] = coreOptionsDigest,
                ["bios"] = new JsonObject { ["mode"] = "none" },
            },
            ["process"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId, ["executable_sha256"] = null, ["parent_pid"] = 0,
                ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["open_files"] = new JsonArray(),
            },
            ["timing"] = new JsonObject
            {
                ["started_at"] = DateTime.UtcNow.AddMilliseconds(-monotonicMs).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["ended_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["monotonic_ms"] = monotonicMs, ["frame_count"] = frameCount,
            },
            ["sensitive"] = new JsonObject
            {
                ["cheats"] = cheats > 0, ["save_state_loaded"] = saveStateLoads > 0, ["resets"] = resets,
                ["rewind"] = rewind > 0, ["runahead"] = runahead > 0, ["fast_forward"] = fastForward > 0,
                ["netplay"] = netplay > 0, ["continues"] = continues,
            },
            ["metric"] = new JsonObject
            {
                ["type"] = "score", ["unit"] = "points", ["value"] = finalTotal.ToString(),
                ["ranking_direction"] = direction, ["result_source"] = resultSource,
            },
            ["progression"] = new JsonObject { ["checkpoints"] = checkpoints, ["checkpoints_digest"] = checkpointsDigest },
            ["local_check"] = "pass",
        };
    }

    private async Task<JsonElement?> FetchProfileAsync(string credential, string systemId, string romGroup, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(credential);
            var url = $"/api/v1/agent/scores/profile?system_id={Uri.EscapeDataString(systemId)}&rom_group={Uri.EscapeDataString(romGroup)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.True
                && doc.RootElement.TryGetProperty("profile", out var profile))
            {
                return profile.Clone();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : résolution du profil impossible.");
            return null;
        }
    }

    private async Task SubmitAsync(string credential, JsonObject passport, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(credential);
            using var content = new StringContent(passport.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/submissions", content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Trace($"VERDICT HTTP {(int)response.StatusCode} - {body}");
            _logger?.LogInformation("Scoring : verdict serveur {Status} - {Body}", (int)response.StatusCode, body);
            PersistCertified(passport, body);
            MaybeShowClaimOverlay(passport, body);
            await NotifyVerdictAsync(passport, body, cancellationToken).ConfigureAwait(false);
            CaptureReplayLinkOnPublished(passport, body);
            PublishVerdict(passport, body);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scoring : soumission impossible.");
        }
    }

    /// <summary>
    /// Annonce le verdict sur le bus, pour qui a quelque chose à faire APRÈS une soumission.
    ///
    /// Le premier client est la capture d'écran du record : elle est prise pendant la partie,
    /// mais elle ne doit monter que si le score a été publié. Sans cet évènement il faudrait
    /// surveiller le dossier `certified/`, c'est-à-dire deviner par le disque ce que le
    /// rapporteur sait déjà.
    ///
    /// Additif et silencieux : personne n'est obligé de s'y abonner, et une exception ici ne doit
    /// surtout pas remonter dans le chemin certifié.
    /// </summary>
    private void PublishVerdict(JsonObject passport, string responseBody)
    {
        try
        {
            var obj = JsonNode.Parse(responseBody) as JsonObject;
            var scoreText = (string?)((passport["metric"] as JsonObject)?["value"]) ?? "0";
            _ = long.TryParse(scoreText, out var score);
            _ = _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "scoring.verdict",
                Payload = new
                {
                    SessionId = (string?)passport["session_id"] ?? "",
                    RomGroup = (string?)((passport["game"] as JsonObject)?["rom_group"]) ?? "",
                    SystemId = (string?)((passport["game"] as JsonObject)?["system_id"]) ?? "",
                    Ruleset = (string?)((passport["game"] as JsonObject)?["ruleset"]) ?? "",
                    Status = (string?)(obj?["status"] ?? obj?["verdict"]) ?? "",
                    Score = score,
                    Rank = (int?)(obj?["rank"]),
                },
            });
        }
        catch (Exception ex)
        {
            Trace($"verdict non publié sur le bus : {ex.Message}");
        }
    }

    /// <summary>
    /// Notif ES (popup léger, comme le scrap) : le joueur voit le verdict + la RAISON en
    /// revenant sur EmulationStation. Publié attribué / en attente / refusé sont notifiés ;
    /// le publié ANONYME est laissé à l'overlay « Réclame ton record ! » (pas de doublon).
    /// </summary>
    private async Task NotifyVerdictAsync(JsonObject passport, string responseBody, CancellationToken cancellationToken)
    {
        if (_esNotify is null)
        {
            return;
        }

        try
        {
            var obj = JsonNode.Parse(responseBody) as JsonObject;
            var status = (string?)(obj?["status"]) ?? "";
            var reason = (string?)(obj?["reason"]) ?? "";
            var hasClaim = !string.IsNullOrWhiteSpace((string?)(obj?["claim_code"]));
            if (status == "published" && hasClaim)
            {
                return;   // l'overlay claim s'en charge
            }

            var scoreText = (string?)((passport["metric"] as JsonObject)?["value"]) ?? "0";
            _ = long.TryParse(scoreText, out var score);
            var rank = (int?)(obj?["rank"]);

            var message = status switch
            {
                "published" => $"🏆 Score certifié : {score:N0} publié" + (rank is int r ? $" (#{r})" : ""),
                "held" => $"⏳ Score {score:N0} en attente de vérification",
                "refused" => $"❌ Score {score:N0} refusé — {ReasonToText(reason)}",
                _ => $"⚠️ Score non transmis — {ReasonToText(reason)}",
            };
            await _esNotify.NotifyAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : notification ES du verdict impossible.");
        }
    }

    // Phase E : chaque core libretro a SES propres options (clés préfixées par le nom du core).
    // Pour un core connu, on ne digère que l'allowlist des réglages qui AFFECTENT LE JEU ; le
    // cosmétique (audio, filtres vidéo, ratio, volumes…) est ignoré. Une clé d'un backend non
    // listé (DIP MAME « Difficulty »/« 1-1 »…) passe INCHANGÉE → les digests déjà épinglés (19xx)
    // ne bougent pas. Ajouter un core = une entrée ici (partagée par tous ses jeux).
    private static readonly Dictionary<string, HashSet<string>> CoreOptionsAllowlist = new()
    {
        ["genesis_plus_gx_"] = new(StringComparer.Ordinal)
        {
            "genesis_plus_gx_region_detect",   // PAL/NTSC → vitesse & difficulté
            "genesis_plus_gx_vdp_mode",        // timing vidéo → vitesse
            "genesis_plus_gx_system_hw",       // matériel émulé
            "genesis_plus_gx_overclock",       // vitesse CPU → ralentissements
            "genesis_plus_gx_no_sprite_limit", // rendu → peut changer le jeu
            "genesis_plus_gx_lock_on",         // cartouche lock-on (S&K…)
        },
    };

    // Réduit la chaîne canonique « clé=valeur;… » aux seuls réglages gameplay (voir ci-dessus).
    private static string? FilterGameplayCoreOptions(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var kept = new List<string>();
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair.Substring(0, eq) : pair;
            HashSet<string>? allow = null;
            foreach (var kv in CoreOptionsAllowlist)
                if (key.StartsWith(kv.Key, StringComparison.Ordinal)) { allow = kv.Value; break; }
            if (allow is not null)
            {
                if (allow.Contains(key)) kept.Add(pair);   // gameplay → gardé ; sinon cosmétique → écarté
            }
            else
            {
                kept.Add(pair);   // backend non filtré (DIP MAME…) → inchangé
            }
        }
        kept.Sort(StringComparer.Ordinal);
        return string.Join(";", kept);
    }

    // Codes d'échec du vérifieur → texte joueur (FR). Voir CoreVerifier / regles-de-score.
    private static string ReasonToText(string reason) => reason switch
    {
        "" => "accepté",
        "runtime.fast_forward_detected" => "avance rapide détectée",
        "runtime.rewind_detected" => "rembobinage détecté",
        "runtime.runahead_detected" => "run-ahead détecté",
        "runtime.save_state_detected" => "sauvegarde d'état détectée",
        "runtime.cheat_detected" => "triche (cheat) détectée",
        "runtime.continue_forbidden" => "continue interdit pour ce record",
        "runtime.module_unauthorized" => "logiciel non homologué",
        "profile.core_mismatch" => "émulateur non reconnu",
        "profile.content_mismatch" => "ROM non reconnue",
        "profile.mem_mismatch" => "définition mémoire non reconnue",
        "profile.core_options_mismatch" => "réglages non conformes (usine requis)",
        "profile.listener_unauthorized" => "wrapper non homologué",
        "profile.not_open" => "classement fermé",
        "profile.mismatch" => "jeu ou règlement non concordant",
        "session.no_game_end" => "partie non terminée",
        "session.ticket_expired" => "session expirée",
        "session.ticket_invalid" or "session.ticket_missing" => "session invalide",
        "timing.incoherent" => "horodatage incohérent",
        "format.out_of_bounds" => "score hors limites",
        _ when reason.StartsWith("progression.", StringComparison.Ordinal) => "progression incohérente",
        _ when reason.StartsWith("format.", StringComparison.Ordinal) => "format invalide",
        _ => reason,
    };

    /// <summary>
    /// Score anonyme PUBLIÉ → le verdict porte un <c>claim_code</c> : on affiche la
    /// surimpression « Réclame ton record ! » sur l'écran de la machine. Une machine
    /// appairée/liée soumet un score attribué (non anonyme) : pas de code, donc pas
    /// d'overlay - le gating par identité est implicite côté serveur.
    /// </summary>
    private void MaybeShowClaimOverlay(JsonObject passport, string responseBody)
    {
        if (_claimOverlay is null)
        {
            return;
        }

        try
        {
            var obj = JsonNode.Parse(responseBody) as JsonObject;
            var code = (string?)(obj?["claim_code"]);
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            var game = passport["game"] as JsonObject;
            var systemId = (string?)(game?["system_id"]) ?? "";
            var ruleset = (string?)(game?["ruleset"]) ?? "";
            var scoreText = (string?)((passport["metric"] as JsonObject)?["value"]) ?? "0";
            _ = long.TryParse(scoreText, out var score);

            int? rank = null;
            if (obj?["rank"] is JsonValue rv && rv.TryGetValue<int>(out var r))
            {
                rank = r;
            }

            _ = _claimOverlay.ShowAsync(systemId, ruleset, score, code!, rank);
        }
        catch (Exception ex)
        {
            Trace($"overlay claim non affiché : {ex.Message}");
        }
    }

    /// <summary>
    /// SELF-CUSTODY : conserve localement le passeport SIGNÉ + le verdict serveur dans
    /// <c>state/nelfeplay/certified/{session_id}.json</c>. C'est la sauvegarde distribuée
    /// de la flotte : en cas de recovery serveur (mode « contribute »), cette machine
    /// re-verse ses propres exploits, chacun re-vérifiable (signature d'appareil + OTS).
    /// Le joueur DÉTIENT ses records - rien ne dépend d'un seul serveur.
    /// </summary>
    private void PersistCertified(JsonObject passport, string responseBody)
    {
        try
        {
            var sessionId = (string?)passport["session_id"];
            if (string.IsNullOrEmpty(sessionId)) return;

            string? verdict = null;
            try
            {
                var obj = JsonNode.Parse(responseBody) as JsonObject;
                verdict = (string?)(obj?["status"] ?? obj?["verdict"]); // le serveur renvoie `status`
            }
            catch { /* corps non-JSON : on garde le passeport quand même */ }

            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "certified");
            System.IO.Directory.CreateDirectory(dir);
            var record = new JsonObject
            {
                ["session_id"] = sessionId,
                ["verdict"] = verdict,
                ["submitted_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["server_response"] = responseBody,
                ["passport"] = passport.DeepClone(),
            };
            var path = System.IO.Path.Combine(dir, sessionId + ".json");
            // SANS BOM : l'archive doit être universellement parsable (json_decode PHP,
            // outils tiers, miroirs). Encoding.UTF8 écrirait un BOM qui les fait échouer.
            System.IO.File.WriteAllText(path, record.ToJsonString(), new UTF8Encoding(false));
            Trace($"certified/ écrit : {sessionId}.json (verdict={verdict ?? "?"})");
        }
        catch (Exception ex)
        {
            Trace($"certified/ échec : {ex.Message}");
        }
    }

    // ── Enrôlement + ticket ──────────────────────────────────────────────────

    private async Task EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) { Trace($"enroll: pas de credential (paired={_devices.IsPaired})"); return; }
        try
        {
            using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
            if (_enrolledKeyId == deviceKey.KeyId) return;

            using var client = CreateClient(credential);
            using var content = new StringContent(deviceKey.PublicKeyPem, Encoding.ASCII, "application/x-pem-file");
            using var response = await client.PostAsync("/api/v1/agent/scores/enroll-key", content, cancellationToken).ConfigureAwait(false);
            Trace($"enroll HTTP {(int)response.StatusCode} key_id={deviceKey.KeyId}");
            if (response.IsSuccessStatusCode)
            {
                _enrolledKeyId = deviceKey.KeyId;
                _logger?.LogInformation("Scoring : clé d'appareil enrôlée (key_id {KeyId}).", deviceKey.KeyId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : enrôlement impossible.");
        }
    }

    private async Task RequestTicketAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) return;
        await EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = CreateClient(credential);
            using var response = await client.PostAsync("/api/v1/agent/scores/ticket", content: null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ticket", out var ticket))
            {
                lock (_sync) { _ticket = ticket.Clone(); }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : demande de ticket impossible.");
        }
    }

    // Le secret à présenter : celui de l'appareil APPAIRÉ, sinon celui de l'install
    // ANONYME (déjà enregistrée par NelfePlayPlayReporter dans anonymous.json).
    private string? ResolveCredential()
    {
        var paired = _devices.GetCredential();
        if (!string.IsNullOrEmpty(paired)) return paired;
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "anonymous.json");
            if (System.IO.File.Exists(path))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
            }
        }
        catch { }
        return null;
    }

    private HttpClient CreateClient(string credential)
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayScoringReporter));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
        return client;
    }

    // Trace de diagnostic best-effort (le log ILogger n'a pas de sink fichier ici).
    private static void Trace(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nelfe_scoring.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    // ── Lien replay ↔ score ──────────────────────────────────────────────────

    // Le recorder annonce le replay en cours : on retient son id (gardé même après
    // finalize, le temps qu'un score de fin de partie arrive).
    private void CaptureActiveReplay(JsonElement payload)
    {
        var id = GetString(payload, "ReplayId");
        if (string.IsNullOrEmpty(id)) return;
        lock (_sync) { _activeReplayId = id; }
    }

    // Replay finalisé (objet scellé) : on retient son sha puis on tente le
    // rapprochement (un score « published » a pu arriver avant OU après).
    private void OnReplayFinalized(JsonElement payload)
    {
        var id = GetString(payload, "ReplayId");
        var sha = GetString(payload, "Sha256");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(sha)) return;
        lock (_sync) { PruneReplayLinks(); _finalizedReplay[id!] = (sha!, DateTime.UtcNow); }
        TryRegisterReplayLink(id!);
    }

    // Score PUBLIÉ : le record est public → son replay le devient aussi (il s'affiche
    // sur le classement). On rattache le score au replay ACTIF (celui de cette partie).
    private void CaptureReplayLinkOnPublished(JsonObject passport, string responseBody)
    {
        try
        {
            var verdict = JsonNode.Parse(responseBody) as JsonObject;
            var status = (string?)(verdict?["status"]) ?? "";
            if (status != "published") return;
            var sessionId = (string?)passport["session_id"];
            if (string.IsNullOrEmpty(sessionId)) return;

            // Score (metric.value peut être nombre ou chaîne) + rang (verdict serveur),
            // pour la carte du player affichée à la lecture.
            long? score = null;
            if ((passport["metric"] as JsonObject)?["value"] is JsonValue mv)
            {
                if (mv.TryGetValue<long>(out var ml)) score = ml;
                else if (mv.TryGetValue<string>(out var ms) && long.TryParse(ms, out var mp)) score = mp;
            }
            int? rank = (verdict?["rank"] is JsonValue rv && rv.TryGetValue<int>(out var rk)) ? rk : (int?)null;

            string? replayId;
            lock (_sync)
            {
                replayId = _activeReplayId;
                if (string.IsNullOrEmpty(replayId)) return;
                PruneReplayLinks();
                _pendingScoreLink[replayId!] = (sessionId!, "public", score, rank, DateTime.UtcNow);
            }
            TryRegisterReplayLink(replayId!);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Replay-link : capture du score publié impossible.");
        }
    }

    // Rapprochement : quand le score publié ET le replay finalisé sont là pour le même
    // id, on enregistre UNE fois puis on purge les deux entrées.
    private void TryRegisterReplayLink(string replayId)
    {
        string sessionId, visibility, sha;
        long? score; int? rank;
        lock (_sync)
        {
            if (!_pendingScoreLink.TryGetValue(replayId, out var p)) return;
            if (!_finalizedReplay.TryGetValue(replayId, out var f)) return;
            sessionId = p.sessionId; visibility = p.visibility; score = p.score; rank = p.rank; sha = f.sha256;
            _pendingScoreLink.Remove(replayId);
            _finalizedReplay.Remove(replayId);
        }
        StampReplayCard(replayId, score, rank);
        _ = RegisterReplayLinkAsync(sessionId, replayId, sha, visibility, CancellationToken.None);
    }

    // Estampille la carte du replay (score + rang) sur la méta locale, pour l'overlay
    // de lecture. Best-effort : n'affecte ni le scoring ni l'enregistrement.
    private void StampReplayCard(string replayId, long? score, int? rank)
    {
        try
        {
            var meta = _replayStore?.GetMeta(replayId);
            if (meta is null) return;
            _replayStore!.SaveMeta(meta with { ScoreValue = score, Rank = rank });
            Trace($"REPLAY-CARD estampillée {replayId} score={score} rank={rank}");
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Replay-link : estampillage carte impossible."); }
    }

    // Appelé sous _sync : oublie les rapprochements jamais complétés (partie sans score
    // publié, ou replay jamais finalisé).
    private void PruneReplayLinks()
    {
        var now = DateTime.UtcNow;
        var stale = new List<string>();
        foreach (var e in _pendingScoreLink) if (now - e.Value.at > ReplayLinkTtl) stale.Add(e.Key);
        foreach (var k in stale) _pendingScoreLink.Remove(k);
        stale.Clear();
        foreach (var e in _finalizedReplay) if (now - e.Value.at > ReplayLinkTtl) stale.Add(e.Key);
        foreach (var k in stale) _finalizedReplay.Remove(k);
    }

    private async Task RegisterReplayLinkAsync(
        string sessionId, string replayId, string sha256, string visibility, CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential)) return;
        try
        {
            using var client = CreateClient(credential);
            var body = new JsonObject
            {
                ["session_id"] = sessionId,
                ["replay_id"] = replayId,
                ["object_sha256"] = sha256,
                ["visibility"] = visibility,
            };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/replay-link", content, cancellationToken).ConfigureAwait(false);
            var respBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Trace($"REPLAY-LINK HTTP {(int)response.StatusCode} - {respBody}");
            _logger?.LogInformation("Replay-link : {Status} - {Body}", (int)response.StatusCode, respBody);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Replay-link : enregistrement impossible (best-effort).");
        }
    }

    private static JsonElement ToJson(object? payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;


}
