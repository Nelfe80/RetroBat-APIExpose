using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Runtime;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// Player Replay (R2). Résout un replay -> vérifie le runtime -> lance RetroArch en lecture
/// (-P &lt;objet&gt;) avec le VRAI core (cores_real/, pas le wrapper de scoring, pour un playback
/// déterministe SANS armer de session de score) -> suit la frame via active_replay -> fin propre.
/// Singleton, une seule lecture active à la fois.
/// </summary>
public sealed class ReplayPlaybackService
{
    private readonly RetroArchReplayClient _ra;
    // R7 : le lecteur ne connaît QUE des rôles. Il ignore si le replay est né ici ou est arrivé
    // d'une autre borne — c'est la condition pour que NelfeNet se branche sans le rouvrir.
    private readonly IReplayManifestStore _manifests;
    private readonly IReplayObjectStore _objects;
    private readonly IReplayMetadataStore _meta;
    private readonly IReplaySourceResolver _source;
    private readonly IReplayRuntimeResolver _resolver;
    private readonly IEventBus _bus;
    private readonly RetroBat.Api.Infrastructure.NelfePlayAgentService _agent;   // pseudo appairé = joueur de la carte
    private readonly RetroBat.Api.Infrastructure.NelfePlayDeviceStore _devices;  // credential pour le backfill carte
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ReplayPlaybackService> _logger;

    private readonly object _gate = new();
    private ReplayPlaybackState _state = ReplayPlaybackState.Idle;
    private string? _replayId;
    private long _frame;
    private long? _runStart, _runEnd, _replayEnd;
    private double _nominalFps = 60;
    private string? _fpsSource;
    private bool _paused;
    private ReplayErrorCode _error = ReplayErrorCode.None;
    private ReplayCard? _card;
    private Process? _process;
    private CancellationTokenSource? _monitorCts;

    public ReplayPlaybackService(RetroArchReplayClient ra, IReplayManifestStore manifests, IReplayObjectStore objects,
        IReplayMetadataStore meta, IReplaySourceResolver source, IReplayRuntimeResolver resolver,
        IEventBus bus, RetroBat.Api.Infrastructure.NelfePlayAgentService agent,
        RetroBat.Api.Infrastructure.NelfePlayDeviceStore devices, IHttpClientFactory httpFactory,
        ILogger<ReplayPlaybackService> logger)
    {
        _ra = ra; _manifests = manifests; _objects = objects; _meta = meta; _source = source; _resolver = resolver;
        _bus = bus; _agent = agent; _devices = devices; _httpFactory = httpFactory; _logger = logger;
    }

    /// <summary>Vrai pendant qu'une lecture est en cours (le recorder s'abstient d'enregistrer).</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _state is ReplayPlaybackState.Resolving or ReplayPlaybackState.Replicating
            or ReplayPlaybackState.Verifying or ReplayPlaybackState.Preparing or ReplayPlaybackState.Launching
            or ReplayPlaybackState.Playing or ReplayPlaybackState.Paused or ReplayPlaybackState.Stopping; }
    }

    public sealed record PlayResult(bool Accepted, string State, ReplayErrorCode Error);
    public sealed record StateSnapshot(string Mode, string State, string? ReplayId, long Frame,
        long? RunStartFrame, long? RunEndFrame, long? ReplayEndFrame, bool Paused, string? Error,
        double NominalFps, string? FpsSource, ReplayCard? Card);

    /// <summary>Fiche « performance NelfePlay » de l'overlay (record sportif/esport). En R1
    /// seuls Game/System/Date sont réels ; Player/Score/Rank/Certified sont des emplacements
    /// (à brancher au record + au lien scoring, lot ultérieur).</summary>
    public sealed record ReplayCard(string Game, string System, string DateText, string Player,
        long? Score, int? Rank, bool Certified);

    public StateSnapshot GetState()
    {
        lock (_gate)
        {
            var mode = _state is ReplayPlaybackState.Idle ? "none" : "replay";
            return new StateSnapshot(mode, _state.ToString().ToLowerInvariant(), _replayId, _frame,
                _runStart, _runEnd, _replayEnd, _paused,
                _error == ReplayErrorCode.None ? null : _error.ToString(),
                _nominalFps <= 0 ? 60 : _nominalFps, _fpsSource, _card);
        }
    }

    public async Task<PlayResult> PlayAsync(string replayId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (IsBusy) return new PlayResult(false, _state.ToString().ToLowerInvariant(), ReplayErrorCode.ReplayAlreadyRunning);
            _state = ReplayPlaybackState.Resolving; _replayId = replayId; _error = ReplayErrorCode.None;
            _frame = 0; _paused = false; _card = null;
        }

        var manifest = _manifests.GetManifest(replayId);
        if (manifest is null) return Fail(ReplayErrorCode.ReplayNotFound);
        var meta = _meta.GetMeta(replayId);
        ReplayCard? builtCard;
        lock (_gate) { _card = BuildCard(manifest, meta, _agent.Status.Pseudo); builtCard = _card; }
        // Backfill : replay estampillé AVANT la corrélation score → pas de score en méta.
        // On le récupère du serveur en tâche de fond ; la carte se rafraîchit via /state.
        if (builtCard is { Score: null }) { _ = BackfillCardAsync(replayId, ct); }
        var hint = meta?.Launch;
        var objectPath = _objects.ObjectPath(manifest.Object.Sha256);

        // ── LE RÉSEAU NE DOIT JAMAIS ÊTRE DANS LE CHEMIN DE RÉPONSE ──────────────
        //
        // Si l'objet n'est pas là, on va le chercher EN TÂCHE DE FOND et on rend la main tout de
        // suite, dans l'état « replicating ». La demande de lecture ne peut donc plus être
        // retardée par un pair lent, ni même par un pair qui ne répond pas du tout : il n'est plus
        // sur le chemin. C'est structurel, ça ne dépend d'aucun réglage de délai.
        //
        // Le client suit la progression par /replay/state, exactement comme il le fait déjà pour
        // « launching ». Rien ne change pour lui.
        if (!File.Exists(objectPath))
        {
            lock (_gate) _state = ReplayPlaybackState.Replicating;
            _logger.LogInformation("Replay : objet absent pour {ReplayId}, récupération en tâche de fond.", replayId);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await _source.EnsureObjectAvailableAsync(manifest, CancellationToken.None).ConfigureAwait(false))
                        await ContinueAfterFetchAsync(replayId, manifest, meta, CancellationToken.None).ConfigureAwait(false);
                    else
                        Fail(ReplayErrorCode.ReplayObjectUnavailable);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Replay : récupération de fond échouée pour {ReplayId}.", replayId);
                    Fail(ReplayErrorCode.ReplayObjectUnavailable);
                }
            });
            return new PlayResult(true, ReplayPlaybackState.Replicating.ToString().ToLowerInvariant(), ReplayErrorCode.None);
        }
        return await ContinueAfterFetchAsync(replayId, manifest, meta, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// La suite de la séquence, une fois l'objet PRÉSENT : intégrité, résolution runtime, lancement.
    /// Appelée soit directement (objet déjà là), soit par la tâche de fond qui vient de le
    /// récupérer. Un seul chemin de vérité, quel que soit le trajet emprunté par l'objet.
    /// </summary>
    private async Task<PlayResult> ContinueAfterFetchAsync(string replayId, ReplayManifest manifest,
        ReplayLocalMetadata? meta, CancellationToken ct)
    {
        var hint = meta?.Launch;
        var objectPath = _objects.ObjectPath(manifest.Object.Sha256);

        // R6 : intégrité (taille + SHA-256 == manifeste) AVANT lecture — un hash au LANCEMENT (une
        // fois), négligeable devant le démarrage RetroArch ; détecte corruption/altération.
        // Indispensable dès qu'un objet vient d'un peer (NelfeNet) ; localement (store adressé-par-
        // contenu) ça passe toujours, sauf bit rot.
        if (!await _objects.VerifyObjectAsync(manifest.Object, ct).ConfigureAwait(false))
            return Fail(ReplayErrorCode.ReplayObjectCorrupt);

        // ── vérification runtime (R2 MVP : présence ; empreintes strictes quand le manifeste les portera) ──
        lock (_gate) { _state = ReplayPlaybackState.Verifying; _replayEnd = manifest.Frames.ReplayEnd;
            _runStart = manifest.Frames.RunStart; _runEnd = manifest.Frames.RunEnd; _nominalFps = manifest.Frames.NominalFps;
            // Manifeste d'avant R3.2 : pas de provenance => la cadence n'a pas été vérifiée.
            _fpsSource = manifest.Frames.FpsSource ?? "default"; }
        // R5 : le hint local n'est qu'un ACCÉLÉRATEUR — le résolveur retrouve core+ROM depuis le
        // MANIFESTE (core par empreinte core_sha256, ROM par crc32 de contenu), pour qu'un replay
        // SANS hint (reçu d'un peer) reste jouable. Politique souple : jamais bloqué sur la version.
        var resolved = _resolver.Resolve(manifest, hint);
        if (resolved is null) return Fail(ReplayErrorCode.RuntimeIncompatible);
        var coreDll = resolved.CoreDll;

        // ── pas de jeu déjà en cours (on lance notre propre RetroArch) ──
        var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
        if (status is { ContentLoaded: true }) return Fail(ReplayErrorCode.GameAlreadyRunning);

        // ── ReplayLaunchProfile : neutralise les hotkeys gamepad de RetroArch le temps de la
        //    lecture (sinon SELECT = input_enable_hotkey, et SELECT+bouton ouvre le menu RA au
        //    lieu d'aller à notre routeur). config_save_on_exit=false => AUCUNE persistance :
        //    la config normale de l'utilisateur reste intacte. Appliqué via --appendconfig. ──
        var sessionCfg = Path.Combine(_objects.TempRoot, "replay-session.cfg");
        try
        {
            File.WriteAllText(sessionCfg, string.Join('\n', new[]
            {
                "config_save_on_exit = \"false\"",
                // Capture PIXEL PERFECT : « false » photographie le tampon du coeur, a la
                // definition d'origine du jeu (384x224 sur CPS-1), au lieu de la sortie GPU
                // mise a l'echelle avec shaders. C'est ce qui fait de l'image du record une
                // image du JEU plutot qu'une image de cet ecran-la.
                "video_gpu_screenshot = \"false\"",
                "input_menu_toggle_btn = \"nul\"",
                "input_exit_emulator_btn = \"nul\"",
                "input_menu_toggle_gamepad_combo = \"0\"",
                "input_quit_gamepad_combo = \"0\"",
                // Un replay n'est PAS une partie : on coupe RetroAchievements (sinon le login
                // réseau + l'identification + le démarrage de session RETARDENT l'entrée en
                // lecture au-delà du timeout — l'API tuait alors RetroArch — et on débloquerait
                // en plus des succès pour un simple visionnage) et le rewind (inutile ici).
                "cheevos_enable = \"false\"",
                "rewind_enable = \"false\"",
                // R3.2, mesuré : active_replay.frame avance d'UNE unité par frame vidéo du core
                // (~59,9/s mesuré sur Genesis, qui annonce 59,92) — l'ancienne note « ~2x » était
                // fausse. La lecture se fait donc à vitesse normale, sans override de cadence.
            }) + "\n");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : écriture du profil de session échouée (hotkeys non neutralisées)"); }

        // ── lancement ──
        lock (_gate) _state = ReplayPlaybackState.Launching;
        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.exe");
        var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(coreDll);
        psi.ArgumentList.Add(resolved.RomPath);
        psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(objectPath);
        psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(RetroBatPaths.RetroArchConfigPath);
        psi.ArgumentList.Add("--appendconfig"); psi.ArgumentList.Add(sessionCfg);
        psi.ArgumentList.Add("--eof-exit");

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex) { _logger.LogWarning(ex, "Replay : lancement RetroArch échoué"); return Fail(ReplayErrorCode.RetroArchUnavailable); }
        lock (_gate) _process = proc;
        // Une lecture demandee depuis le web part derriere le navigateur : c'est lui qui a le
        // premier plan au moment ou RetroArch demarre. On le ramene devant des que sa fenetre
        // existe - elle n'existe pas encore ici, le coeur charge sa ROM.
        _ = RetroBat.Api.Infrastructure.EmulatorForeground.FocusEmulatorWhenUpAsync();
        _logger.LogInformation("Replay : lecture lancée {ReplayId} (core={Core}, rom={Rom}).", replayId, Path.GetFileName(coreDll), Path.GetFileName(resolved.RomPath));
        await Publish("replay.launching", new { replayId }).ConfigureAwait(false);

        // ── COURTE confirmation (~3 s) puis on rend la main. Sur borne faible, le handshake réseau
        //    active_replay (55355) est lent/flaky — on n'ATTEND donc PAS la lecture ici et on ne TUE
        //    JAMAIS un RetroArch vivant (c'était le bug : ReplayLaunchTimeout tuait une lecture qui
        //    démarrait). Dès que RetroArch tient, on renvoie « accepté » (état launching) et le
        //    MONITOR confirme la lecture (Launching → Playing) puis suit la frame ; il ne coupera que
        //    si le process meurt ou si la lecture n'est jamais confirmée (garde de démarrage). ──
        // Bref instant pour laisser RetroArch échouer au démarrage (args invalides, core KO…). On ne
        // SONDE PAS active_replay ici : le handshake réseau (55355) est lent/flaky sur borne faible et
        // bloquerait la réponse HTTP. Le MONITOR fait la détection et promeut Launching → Playing.
        try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (proc.HasExited) return Fail(ReplayErrorCode.RetroArchUnavailable);
        lock (_gate) { _state = ReplayPlaybackState.Launching; }

        StartMonitor();
        await Publish("replay.started", new { replayId }).ConfigureAwait(false);
        return new PlayResult(true, GetState().State, ReplayErrorCode.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Process? proc;
        lock (_gate)
        {
            if (_state is ReplayPlaybackState.Idle) return;
            _state = ReplayPlaybackState.Stopping; proc = _process;
        }
        _monitorCts?.Cancel();
        await _ra.HaltAsync(ct).ConfigureAwait(false);
        try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
        lock (_gate) { _process = null; _replayId = null; _state = ReplayPlaybackState.Idle; _frame = 0; _card = null; }
        await Publish("replay.finished", new { reason = "user" }).ConfigureAwait(false);
    }

    // ── commandes de contrôle (appelées par le routeur panel R3 ou l'API) ──
    public async Task PauseToggleAsync(CancellationToken ct)
    {
        if (!IsBusy) return;
        await _ra.PauseToggleAsync(ct).ConfigureAwait(false); // _paused sera relu par le monitor
    }

    public async Task SeekRelativeAsync(double seconds, CancellationToken ct)
    {
        if (!IsBusy) return;
        long cur, hi; long? loRun; double fps;
        lock (_gate) { cur = _frame; hi = _replayEnd ?? 0; loRun = _runStart; fps = _nominalFps <= 0 ? 60 : _nominalFps; }
        var target = cur + (long)Math.Round(seconds * fps);
        var lo = loRun ?? 0;
        if (target < lo) target = lo;
        if (hi > 0 && target > hi) target = hi;
        var resp = await _ra.SeekAsync(target, ct).ConfigureAwait(false);
        if (resp is not null && resp.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            lock (_gate) _frame = target; // approx ; le monitor recalera sur active_replay
        else
            _logger.LogDebug("Replay : SEEK {Target} refusé ({Resp})", target, resp);
    }

    public async Task NextCheckpointAsync(CancellationToken ct)
    {
        if (!IsBusy) return;
        await _ra.NextCheckpointAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Checkpoint PRÉCÉDENT (R3.5) — la commande runtime existait déjà côté client UDP,
    /// elle manquait juste au métier (donc au panel et à l'API).</summary>
    public async Task PreviousCheckpointAsync(CancellationToken ct)
    {
        if (!IsBusy) return;
        await _ra.PrevCheckpointAsync(ct).ConfigureAwait(false);
    }

    // ── seeks NOMMÉS (R3.11) : les interfaces publiques (panel, SDK, Replay Room) demandent une
    //    INTENTION, pas une durée ; c'est le serveur qui décide court=5 s / long=60 s. seek_relative
    //    reste dispo pour les outils techniques.
    public const double SeekShortSeconds = 5;
    public const double SeekLongSeconds = 60;
    public Task SeekShortBackwardAsync(CancellationToken ct) => SeekRelativeAsync(-SeekShortSeconds, ct);
    public Task SeekShortForwardAsync(CancellationToken ct) => SeekRelativeAsync(+SeekShortSeconds, ct);
    public Task SeekLongBackwardAsync(CancellationToken ct) => SeekRelativeAsync(-SeekLongSeconds, ct);
    public Task SeekLongForwardAsync(CancellationToken ct) => SeekRelativeAsync(+SeekLongSeconds, ct);

    public async Task RestartRunAsync(CancellationToken ct)
    {
        if (!IsBusy) return;
        long target; lock (_gate) target = _runStart ?? 0;
        await _ra.SeekAsync(target, ct).ConfigureAwait(false);
        lock (_gate) _frame = target;
    }

    private void StartMonitor()
    {
        _monitorCts?.Cancel();
        _monitorCts = new CancellationTokenSource();
        var ct = _monitorCts.Token;
        _ = Task.Run(() => MonitorLoopAsync(ct), ct);
    }

    private const long EndPauseMargin = 130; // on fige un peu AVANT la vraie fin (jamais l'EOF → pas de fermeture/boucle)

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var idle = 0; var endHold = 0; long lastHoldFrame = -1; var endPauseSent = false;
        var started = false; var startWait = 0;   // lecture pas encore confirmée (garde de démarrage, borne lente)
        while (!ct.IsCancellationRequested)
        {
            Process? proc; lock (_gate) proc = _process;
            // Fin PRIMAIRE = process fermé (l'utilisateur a quitté, ou joué au-delà du point de pause → --eof-exit).
            if (proc is null || proc.HasExited) { Finish("process terminé"); return; }

            var active = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);
            var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
            var paused = status is { State: "PAUSED" };
            var nowActive = active is { Active: true };
            long frame, end;
            lock (_gate)
            {
                if (nowActive)
                {
                    _frame = active!.Frame;
                    if (_state == ReplayPlaybackState.Launching) _state = ReplayPlaybackState.Playing; // confirmation tardive
                }
                _paused = paused;
                frame = _frame; end = _replayEnd ?? 0;
            }
            if (nowActive && !started) { started = true; _logger.LogInformation("Replay {ReplayId} : lecture confirmée.", _replayId); }

            // Réarme le figeage si on a rembobiné bien avant la fin (→ re-fige si on rejoue jusqu'au bout).
            if (endPauseSent && end > 0 && frame < end - EndPauseMargin - 90) endPauseSent = false;

            // AUTO-PAUSE UNE SEULE FOIS un peu avant la fin (latch) → fige, permet de revenir en arrière (◀).
            if (!endPauseSent && end > 0 && frame >= end - EndPauseMargin && !paused)
            {
                await _ra.PauseToggleAsync(ct).ConfigureAwait(false);
                endPauseSent = true; paused = true;
                lock (_gate) _paused = true;
            }

            // Avant le PREMIER « actif », la lecture DÉMARRE (handshake réseau lent sur borne faible) :
            // on n'interprète PAS « inactif » comme une fin — on attend, tant que RetroArch vit
            // (proc.HasExited couvre sa mort). Garde : si jamais confirmée (~60 s), on abandonne proprement.
            if (!started)
            {
                if (++startWait >= 120) { _logger.LogWarning("Replay {ReplayId} : lecture jamais confirmée (~60 s) — abandon.", _replayId); Finish("démarrage non confirmé"); return; }
            }
            // Secours (APRÈS démarrage) : inactif hors pause et pas au point de fin = fin/blocage → terminé.
            else if (active is not { Active: true } && !paused && !endPauseSent) { if (++idle >= 8) { Finish("fin (inactif)"); return; } }
            else idle = 0;

            // Sécurité : figé à la fin, frame inchangée trop longtemps (~100 s) → fermeture auto.
            if (endPauseSent && paused && frame == lastHoldFrame) { if (++endHold >= 200) { Finish("fin (auto)"); return; } }
            else endHold = 0;
            lastHoldFrame = frame;

            try { await Task.Delay(750, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private void Finish(string reason)
    {
        Process? proc;
        lock (_gate)
        {
            if (_state is ReplayPlaybackState.Idle) return;
            proc = _process; _process = null; _state = ReplayPlaybackState.Finished; _paused = false;
        }
        try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
        _logger.LogInformation("Replay : lecture terminée ({Reason}).", reason);
        _ = Publish("replay.finished", new { reason });
        lock (_gate) { if (_state is ReplayPlaybackState.Finished) { _state = ReplayPlaybackState.Idle; _replayId = null; _frame = 0; _card = null; } }
    }

    private PlayResult Fail(ReplayErrorCode code)
    {
        lock (_gate) { _state = ReplayPlaybackState.Error; _error = code; }
        _logger.LogWarning("Replay : lecture refusée/échouée : {Code}", code);
        return new PlayResult(false, "error", code);
    }

    private async Task Publish(string type, object payload)
    {
        try { await _bus.PublishAsync(new EventEnvelope { Type = type, Payload = payload }).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : publication event {Type} échouée", type); }
    }

    // ── fiche performance (overlay) ──
    private static readonly CultureInfo Fr = new("fr-FR");
    private static readonly HashSet<string> RegionTokens = new(StringComparer.OrdinalIgnoreCase)
    { "usa", "europe", "japan", "world", "eu", "us", "jp", "en", "fr", "de", "es", "it",
      "rev", "proto", "beta", "demo", "sample", "unl", "pd" };

    // Récupère score+rang+joueur du serveur pour un replay dont la méta ne les a pas
    // (estampillé AVANT la corrélation score↔replay), met à jour la carte affichée (via
    // /state) et estampille la méta locale (permanent : plus de fetch la fois suivante).
    // Best-effort : silencieux si non appairé / hors-ligne / replay inconnu du serveur.
    private async Task BackfillCardAsync(string replayId, CancellationToken ct)
    {
        try
        {
            var credential = _devices.GetCredential();
            if (string.IsNullOrEmpty(credential)) return;

            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(RetroBat.Api.Infrastructure.NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Add("X-NELFEPLAY-DEVICE", credential);

            using var resp = await client.GetAsync(
                $"/api/v1/agent/scores/replay-card?replay_id={Uri.EscapeDataString(replayId)}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return;

            long? score = root.TryGetProperty("score", out var sv) && sv.TryGetInt64(out var s) ? s : (long?)null;
            int? rank = root.TryGetProperty("rank", out var rv) && rv.TryGetInt32(out var r) ? r : (int?)null;
            var player = root.TryGetProperty("player", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
            if (score is null && rank is null) return;

            lock (_gate)
            {
                if (_card is not null && string.Equals(_replayId, replayId, StringComparison.Ordinal))
                    _card = _card with { Score = score ?? _card.Score, Rank = rank ?? _card.Rank };
            }
            var meta = _meta.GetMeta(replayId);
            if (meta is not null)
            {
                _meta.SaveMeta(meta with
                {
                    ScoreValue = score,
                    Rank = rank,
                    Player = string.IsNullOrWhiteSpace(player) ? meta.Player : player,
                });
            }
            _logger.LogInformation("Replay carte backfill {Id} : score={Score} rang={Rank}", replayId, score, rank);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay carte backfill impossible.");
        }
    }

    private static ReplayCard BuildCard(ReplayManifest m, ReplayLocalMetadata? meta, string? pseudo)
    {
        var game = PrettifyGame(m.Game);
        var system = PrettifyWords(m.Game.SystemId);
        var date = m.CreatedAt.ToLocalTime().ToString("dd MMM yyyy", Fr);
        // Joueur = pseudo de la borne appairée (l'auteur du record sur CETTE machine) ;
        // « JOUEUR » seulement si non appairé. Le rang reste à brancher sur le scoring ;
        // le score existe dans ScoreLink mais reste null tant que la corrélation n'y écrit
        // pas. Certifié = replay publié (état de publication).
        // Player/score/rang viennent en priorité de la méta estampillée au scellement
        // (le vrai record) ; sinon on retombe sur le pseudo appairé (player) et le
        // snapshot du manifeste (score). Le rang n'existe que via la méta.
        var player = !string.IsNullOrWhiteSpace(meta?.Player) ? meta!.Player!
            : (string.IsNullOrWhiteSpace(pseudo) ? "JOUEUR" : pseudo!);
        var score = meta?.ScoreValue ?? m.ScoreLink?.ScoreValueSnapshot;
        int? rank = meta?.Rank;
        var certified = string.Equals(meta?.PublicationState, "published", StringComparison.OrdinalIgnoreCase);
        return new ReplayCard(game, system, date, player, score, rank, certified);
    }

    private static string PrettifyGame(ReplayGame g)
    {
        if (!string.IsNullOrWhiteSpace(g.RomGroup)) return PrettifyWords(g.RomGroup!);
        var seg = g.GameId.Contains('/') ? g.GameId[(g.GameId.LastIndexOf('/') + 1)..] : g.GameId;
        var words = seg.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => !RegionTokens.Contains(w));
        return PrettifyWords(string.Join(' ', words));
    }

    private static string PrettifyWords(string raw)
    {
        var s = raw.Replace('-', ' ').Replace('_', ' ').Trim();
        if (s.Length == 0) return raw;
        return Fr.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }
}
