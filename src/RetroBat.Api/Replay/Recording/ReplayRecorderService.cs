using System.Text;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Runtime;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Recording;

/// <summary>État persisté d'un enregistrement en cours (pour le recovery après crash).</summary>
public sealed record ActiveRecordingState(
    string Schema, string ReplayId, string SessionId, string System, string Game,
    string? Crc32, DateTime StartedAt, string RetroarchVersion);

/// <summary>
/// Recorder Replay (R1). Découplé du scoring : il POLL GET_STATUS de RetroArch (UDP 55355)
/// pour détecter le démarrage/arrêt d'une partie, envoie RECORD_REPLAY au début et
/// HALT_REPLAY + finalisation à la fin (stabilisation fichier -> SHA-256 -> ObjectStore ->
/// manifeste immuable -> index -> event replay.finalized). Une partie MAME standalone
/// (pas de RetroArch) ne répond pas en UDP -> aucun enregistrement (normal, hors périmètre R1).
/// </summary>
public sealed class ReplayRecorderService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    // R3.2 — dernier recours SEULEMENT (log muet et mesure inexploitable). 60 est faux pour tout
    // jeu PAL (50) et pour la plupart des cartes d'arcade ; le manifeste dit alors "default" pour
    // qu'on sache que la valeur n'a pas été vérifiée.
    private const double FallbackFps = 60.0;

    // Bornes de plausibilité d'une cadence mesurée : en dessous/au-dessus, l'échantillon vient
    // d'une pause, d'une avance rapide ou d'un décrochage — pas de la cadence du jeu.
    private const double MinPlausibleFps = 40.0;
    private const double MaxPlausibleFps = 75.0;
    private const int MinRateSamples = 8; // ~12 s de partie : en dessous, la médiane ne vaut rien

    private readonly RetroArchReplayClient _ra;
    private readonly ReplayStore _store;
    private readonly ReplayCoreTimingProbe _timing;
    private readonly IEventBus _bus;
    private readonly RetroBat.Api.Replay.Playback.ReplayPlaybackService _playback;
    private readonly ILogger<ReplayRecorderService> _logger;
    private readonly IConfiguration _config;

    /// <summary>
    /// Le dernier START vu sur le panel. L'enregistrement ne demarre que si un START est recent :
    /// sans cette regle, RetroArch chargeait le contenu et on enregistrait AUSSITOT, c'est-a-dire
    /// l'ecran titre et la demo. Et apres un game over, RetroArch coupe son film et on en
    /// relancait un autre sur la demo suivante : des replays d'attract mode par dizaines.
    ///
    /// La FENETRE existe a cause d'un ordre des choses : le START qui relance la partie apres un
    /// game over est presse AVANT qu'on ait vu RetroArch couper le film (on sonde toutes les
    /// 1,5 s). Un START des vingt dernieres secondes compte donc, meme anterieur a la coupure.
    ///
    /// Pourquoi START et pas la demo : tous les jeux n'en ont pas, et le START est resolu par la
    /// cartographie de chaque borne, donc il vaut pour toutes les machines et tous les
    /// emulateurs. `Replay:Record:RequireStart=false` rend l'ancien comportement a une machine
    /// sans panel.
    /// </summary>
    private DateTime _dernierStartUtc = DateTime.MinValue;
    private static readonly TimeSpan FenetreStart = TimeSpan.FromSeconds(20);
    private string _attenteAnnoncee = "";
    private IDisposable? _abonnement;

    private sealed class Recording
    {
        public required string ReplayId;
        public required string SessionId;
        public required string System;
        public required string Game;
        public string? Crc32;
        public DateTime StartedAtUtc;
        public long LastFrame;
        public required string RetroArchVersion;

        // R3.2 — cadence du core. Annoncée par le core au chargement (exacte) si le log la donne…
        public double? CoreFps;
        // …sinon on la DÉDUIT de la cadence observée : une mesure par tick (Δframes / Δtemps).
        // On garde tous les échantillons pour en prendre la MÉDIANE : une pause tire vers le bas,
        // une avance rapide vers le haut, la médiane ignore les deux tant qu'ils restent minoritaires.
        public readonly List<double> RateSamples = new();
        public DateTime LastSampleUtc;
    }

    private Recording? _current;

    public ReplayRecorderService(RetroArchReplayClient ra, ReplayStore store, ReplayCoreTimingProbe timing,
        IEventBus bus, RetroBat.Api.Replay.Playback.ReplayPlaybackService playback, ILogger<ReplayRecorderService> logger,
        IConfiguration config)
    {
        _ra = ra; _store = store; _timing = timing; _bus = bus; _playback = playback; _logger = logger;
        _config = config;
    }

    private bool StartRequis => _config.GetValue("Replay:Record:RequireStart", true);

    private void OnBusEvent(EventEnvelope e)
    {
        if (!string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal)) return;
        try
        {
            var el = System.Text.Json.JsonSerializer.SerializeToElement(e.Payload);
            if (el.TryGetProperty("System", out var sys) && string.Equals(sys.GetString(), "START", StringComparison.Ordinal))
                _dernierStartUtc = DateTime.UtcNow;
        }
        catch
        {
            // Un evenement illisible n'est pas un START.
        }
    }

    /// <summary>Un START assez recent pour qu'on enregistre. Le dit une fois par attente.</summary>
    private bool StartRecent(RaStatus status)
    {
        if (!StartRequis) return true;
        if (DateTime.UtcNow - _dernierStartUtc <= FenetreStart) return true;
        var cle = status.System + "/" + status.Game;
        if (!string.Equals(_attenteAnnoncee, cle, StringComparison.Ordinal))
        {
            _attenteAnnoncee = cle;
            _logger.LogInformation("Replay : {Game} charge, en attente d'un START pour enregistrer.", status.Game);
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _abonnement = _bus.Subscribe<EventEnvelope>(OnBusEvent); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay recorder : abonnement au bus impossible, START jamais vu."); }
        await TryRecoverAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Replay recorder démarré (poll RetroArch {Ms} ms).", PollInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay recorder : tick en erreur"); }
            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // Arrêt propre : finaliser une session en cours si possible.
        if (_current is not null)
            try { await FinalizeAsync(_current, recovered: false, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Replay : finalisation à l'arrêt échouée"); }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
        var active = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);

        if (_current is null)
        {
            // Démarrage : un jeu RetroArch est chargé, aucun replay actif, on n'est pas en lecture,
            // et le joueur vient d'appuyer sur START (sinon on enregistrerait la demo).
            if (status is { ContentLoaded: true } && active is { Active: false } && !_playback.IsBusy
                && StartRecent(status))
            {
                _attenteAnnoncee = "";
                await StartAsync(status, ct).ConfigureAwait(false);
            }
            return;
        }

        // En cours d'enregistrement : suivre la dernière frame connue.
        if (active is { Recording: true })
        {
            SampleRate(_current, active.Frame);
            _current.LastFrame = active.Frame;
        }

        // Fin : RetroArch fermé, jeu changé, ou l'enregistrement s'est arrêté.
        var ended = status is null
            || !status.ContentLoaded
            || !string.Equals(status.Game, _current.Game, StringComparison.Ordinal)
            || active is not { Recording: true };

        if (ended)
            await FinalizeAsync(_current, recovered: false, ct).ConfigureAwait(false);
    }

    private async Task StartAsync(RaStatus status, CancellationToken ct)
    {
        var version = await _ra.GetVersionAsync(ct).ConfigureAwait(false) ?? "unknown";
        await _ra.RecordAsync(ct).ConfigureAwait(false);

        // Confirmer que l'enregistrement a bien démarré (active_replay flags=8).
        await Task.Delay(300, ct).ConfigureAwait(false);
        var check = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);
        if (check is not { Recording: true })
        {
            _logger.LogDebug("Replay : RECORD_REPLAY non confirmé (active={Active}), on réessaiera au prochain tick.", check);
            return;
        }

        var rec = new Recording
        {
            ReplayId = Ulid.NewReplayId(),
            SessionId = Ulid.NewSessionId(),
            System = status.System,
            Game = status.Game,
            Crc32 = status.Crc32,
            StartedAtUtc = DateTime.UtcNow,
            LastFrame = check.Frame,
            RetroArchVersion = version.Trim(),
        };
        rec.CoreFps = ProbeCoreFps(rec.Crc32, rec.Game);
        rec.LastSampleUtc = DateTime.UtcNow;
        _current = rec;

        _store.WriteJsonAtomic(_store.ActiveRecordingPath, new ActiveRecordingState(
            "nelfe.replay.active-recording.v1", rec.ReplayId, rec.SessionId, rec.System, rec.Game,
            rec.Crc32, rec.StartedAtUtc, rec.RetroArchVersion));

        _logger.LogInformation("Replay : enregistrement démarré {ReplayId} ({System}/{Game}).",
            rec.ReplayId, rec.System, rec.Game);
        // Objet anonyme (propriétés) et non `rec` (champs) : le payload doit rester
        // sérialisable pour les abonnés qui le lisent en JSON (ex. le reporter, qui
        // retient l'id du replay actif pour le lien replay↔score).
        await PublishAsync("replay.recording.started",
            new { rec.ReplayId, rec.SessionId, rec.System, rec.Game }, ct).ConfigureAwait(false);
    }

    private async Task FinalizeAsync(Recording rec, bool recovered, CancellationToken ct)
    {
        _current = null; // on sort de l'état "en cours" immédiatement (idempotence)
        try
        {
            await _ra.HaltAsync(ct).ConfigureAwait(false);

            var file = FindReplayFile(rec.StartedAtUtc);
            if (file is null)
            {
                _logger.LogWarning("Replay : aucun fichier .replay trouvé pour {ReplayId} — abandon.", rec.ReplayId);
                _store.DeleteQuiet(_store.ActiveRecordingPath);
                return;
            }

            if (!await StabilizeAsync(file, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Replay : fichier {File} non stabilisé — conservé pour diagnostic.", file);
                _store.DeleteQuiet(_store.ActiveRecordingPath);
                return;
            }

            var obj = await _store.ImportObjectAsync(file, ct).ConfigureAwait(false);
            var hint = BuildLaunchHint(file);
            var manifest = BuildManifest(rec, obj, hint);
            _store.SaveManifest(manifest);
            _store.SaveMeta(ReplayLocalMetadata.Fresh(rec.ReplayId, hint));
            _store.RebuildIndex();
            _store.DeleteQuiet(_store.ActiveRecordingPath);

            _logger.LogInformation("Replay finalisé {ReplayId} : sha256={Sha} taille={Size} frames={Frames}{Rec}.",
                rec.ReplayId, obj.Sha256, obj.Size, rec.LastFrame, recovered ? " (recovery)" : "");
            await PublishAsync("replay.finalized", new { rec.ReplayId, rec.SessionId, obj.Sha256, obj.Size }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : finalisation de {ReplayId} en échec", rec.ReplayId);
        }
    }

    private ReplayManifest BuildManifest(Recording rec, ReplayObjectRef obj, ReplayLaunchHint? hint)
    {
        var game = new ReplayGame(
            GameId: $"{Slug(rec.System)}/{Slug(rec.Game)}",
            SystemId: rec.System,
            RomGroup: null,
            Ruleset: null,
            Crc32: rec.Crc32,
            // Dossier système frontend (« megadrive ») : identifiant PORTABLE (pas un chemin local),
            // pour qu'un peer sans hint sache où chercher la ROM par crc32.
            SystemFolder: hint?.SystemFolder);

        var runtime = new ReplayRuntime(
            RuntimeId: $"nelfe-{Slug(rec.System)}-r1",
            RetroarchVersion: rec.RetroArchVersion,
            // R4 : empreintes runtime pour la PORTABILITÉ (NelfeNet). C'est de la DONNÉE — ça ne
            // bloque rien ici. Le futur vérificateur de compat (R5/R6) les emploie SOUPLEMENT :
            // seul le CONTENU ROM (crc32/hash) est un gate dur, le runtime reste best-effort +
            // vérifié par les checkpoints à la lecture (un vieux RA/core n'est jamais bloqué a priori).
            RomSha256: HashFileQuiet(hint?.RomPath),  // identifiant exact du fichier ROM (le crc32 du contenu = repère PORTABLE)
            CoreSha256: HashFileQuiet(hint?.CoreDll), // = version du core → format de savestate .bsv
            BiosSha256: null,                        // TODO : par-système, seulement quand un BIOS est requis
            CoreOptionsDigest: null,                 // TODO : sous-ensemble DÉTERMINISTE des core options (pas les options cosmétiques)
            ReplayFormat: "bsv");

        var (fps, fpsSource) = ResolveFps(rec);
        var frames = new ReplayFrames(
            Start: 0,
            RunStart: null,           // corrélation scoring = étape ultérieure (run.finalized)
            RunEnd: null,
            ReplayEnd: rec.LastFrame,
            NominalFps: fps,
            FpsSource: fpsSource);

        return new ReplayManifest(
            Schema: ReplayManifest.SchemaId,
            ReplayId: rec.ReplayId,
            SessionId: rec.SessionId,
            Game: game,
            CreatedAt: DateTime.UtcNow,
            Origin: "home",
            Runtime: runtime,
            Object: obj,
            Frames: frames,
            ScoreLink: null,
            Recovery: new ReplayRecovery(false));
    }

    /// <summary>
    /// Cadence annoncée par le core pour CE contenu, ou null. Le log ne dit pas « le jeu en cours
    /// tourne à tant » mais « le dernier contenu chargé tournait à tant » : on n'accepte donc la
    /// valeur que si le CRC journalisé est celui de la partie. Sans CRC des deux côtés, on accepte
    /// au bénéfice du doute (le log est réécrit à chaque lancement et borné en ancienneté).
    /// </summary>
    private double? ProbeCoreFps(string? crc32, string game)
    {
        var timing = _timing.ReadLatest();
        if (timing is null) return null;

        var expected = NormalizeCrc(crc32);
        var found = NormalizeCrc(timing.Crc32);
        if (expected is not null && found is not null && !string.Equals(expected, found, StringComparison.Ordinal))
        {
            _logger.LogInformation("Replay : cadence du log ignorée pour {Game} (CRC log {Found} ≠ partie {Expected}).", game, found, expected);
            return null;
        }

        _logger.LogInformation("Replay : cadence du core = {Fps} img/s ({W}x{H}) pour {Game}.",
            timing.Fps, timing.Width, timing.Height, game);
        return timing.Fps;
    }

    /// <summary>CRC comparable : sans préfixe 0x, minuscule, sur 8 chiffres. Null si vide/invalide.</summary>
    private static string? NormalizeCrc(string? crc)
    {
        var s = crc?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("0x", StringComparison.Ordinal)) s = s[2..];
        s = s.TrimEnd('.');
        return s.Length is > 0 and <= 8 && s.All(Uri.IsHexDigit) ? s.PadLeft(8, '0') : null;
    }

    /// <summary>Un échantillon de cadence par tick : combien de frames émulées par seconde réelle.</summary>
    private static void SampleRate(Recording rec, long frame)
    {
        var now = DateTime.UtcNow;
        var dt = (now - rec.LastSampleUtc).TotalSeconds;
        var df = frame - rec.LastFrame;
        rec.LastSampleUtc = now;
        if (dt <= 0.5 || dt > 10 || df <= 0) return; // tick anormal, jeu en pause, ou compteur remis à zéro
        var rate = df / dt;
        if (rate is > MinPlausibleFps and < MaxPlausibleFps) rec.RateSamples.Add(rate);
    }

    /// <summary>
    /// R3.2 — la cadence retenue pour le manifeste, par ordre de confiance : ce que le core a
    /// ANNONCÉ (exact), sinon ce qu'on a MESURÉ (médiane des échantillons, ±1 % environ), sinon 60
    /// en dernier recours. On n'arrondit JAMAIS vers une cadence « standard » : ce serait juste en
    /// console et faux en arcade, où chaque carte a la sienne.
    /// </summary>
    private (double Fps, string Source) ResolveFps(Recording rec)
    {
        if (rec.CoreFps is > 0) return (rec.CoreFps.Value, "core");

        if (rec.RateSamples.Count >= MinRateSamples)
        {
            var sorted = rec.RateSamples.OrderBy(x => x).ToList();
            var median = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            var fps = Math.Round(median, 2);
            _logger.LogInformation("Replay : cadence MESURÉE {Fps} img/s pour {Game} ({N} échantillons) — le log n'a pas donné l'av_info.",
                fps, rec.Game, sorted.Count);
            return (fps, "measured");
        }

        _logger.LogWarning("Replay : cadence inconnue pour {Game} (log muet, {N} échantillons) — repli sur {Fps}, seek approximatif si le jeu n'est pas en 60 Hz.",
            rec.Game, rec.RateSamples.Count, FallbackFps);
        return (FallbackFps, "default");
    }

    // Hash SHA-256 d'un fichier (best-effort, null si illisible) : empreinte runtime pour R4.
    private string? HashFileQuiet(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : hash empreinte {Path} échoué", path); return null; }
    }

    /// <summary>Le fichier .replay le plus récent écrit depuis le début de l'enregistrement.</summary>
    private static string? FindReplayFile(DateTime startedAtUtc)
    {
        var margin = startedAtUtc.AddSeconds(-2);
        string? best = null; DateTime bestTime = DateTime.MinValue;
        if (!Directory.Exists(RetroBatPaths.SavesRoot)) return null;
        foreach (var f in Directory.EnumerateFiles(RetroBatPaths.SavesRoot, "*.replay*", SearchOption.AllDirectories))
        {
            var t = File.GetLastWriteTimeUtc(f);
            if (t >= margin && t > bestTime) { best = f; bestTime = t; }
        }
        return best;
    }

    /// <summary>
    /// Dérive les indices de lancement (core dll + ROM) depuis le chemin du .replay
    /// (saves/&lt;sys&gt;/libretro.&lt;core&gt;/&lt;jeu&gt;.replayN). Locaux -> stockés en meta, jamais au manifeste.
    /// </summary>
    private static ReplayLaunchHint? BuildLaunchHint(string replayFilePath)
    {
        try
        {
            var rel = Path.GetRelativePath(RetroBatPaths.SavesRoot, replayFilePath);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length < 3) return null;
            var systemFolder = parts[0];
            var coreDir = parts[1]; // "libretro.genesis_plus_gx"
            var core = coreDir.StartsWith("libretro.", StringComparison.OrdinalIgnoreCase)
                ? coreDir["libretro.".Length..] : coreDir;
            var name = parts[^1];
            var idx = name.LastIndexOf(".replay", StringComparison.OrdinalIgnoreCase);
            var gameBase = idx > 0 ? name[..idx] : name;

            var coreDll = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores", core + "_libretro.dll");
            var romDir = Path.Combine(RetroBatPaths.RomsRoot, systemFolder);
            var romPath = "";
            if (Directory.Exists(romDir))
            {
                foreach (var f in Directory.EnumerateFiles(romDir, gameBase + ".*"))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".txt" or ".xml" or ".dat" or ".jpg" or ".png") continue;
                    romPath = f; break;
                }
            }
            return new ReplayLaunchHint(systemFolder, core, coreDll, romPath);
        }
        catch { return null; }
    }

    /// <summary>Attend que RetroArch ait fini d'écrire (taille+mtime stables 3 relevés, timeout 10 s).</summary>
    private static async Task<bool> StabilizeAsync(string file, CancellationToken ct)
    {
        long lastLen = -1; DateTime lastWrite = DateTime.MinValue; int stable = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var fi = new FileInfo(file);
            if (fi.Exists && fi.Length > 0)
            {
                if (fi.Length == lastLen && fi.LastWriteTimeUtc == lastWrite) { if (++stable >= 3) return true; }
                else { stable = 0; lastLen = fi.Length; lastWrite = fi.LastWriteTimeUtc; }
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Recovery au démarrage : un active-recording.json résiduel = session interrompue.</summary>
    private async Task TryRecoverAsync(CancellationToken ct)
    {
        var state = _store.ReadJson<ActiveRecordingState>(_store.ActiveRecordingPath);
        if (state is null) return;
        _logger.LogInformation("Replay : session interrompue détectée ({ReplayId}), tentative de recovery.", state.ReplayId);
        var file = FindReplayFile(state.StartedAt);
        if (file is null || !await StabilizeAsync(file, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Replay : recovery impossible pour {ReplayId} (fichier absent/instable).", state.ReplayId);
            _store.DeleteQuiet(_store.ActiveRecordingPath);
            return;
        }
        var obj = await _store.ImportObjectAsync(file, ct).ConfigureAwait(false);
        var rec = new Recording
        {
            ReplayId = state.ReplayId, SessionId = state.SessionId, System = state.System, Game = state.Game,
            Crc32 = state.Crc32, StartedAtUtc = state.StartedAt, RetroArchVersion = state.RetroarchVersion, LastFrame = 0,
        };
        // La session interrompue n'a laissé aucun échantillon de cadence : le log reste la seule
        // chance d'avoir la vraie valeur, et le contrôle de CRC empêche de prendre celle d'un autre jeu.
        rec.CoreFps = ProbeCoreFps(rec.Crc32, rec.Game);
        var hint = BuildLaunchHint(file);
        var manifest = BuildManifest(rec, obj, hint) with { Recovery = new ReplayRecovery(true) };
        _store.SaveManifest(manifest);
        _store.SaveMeta(ReplayLocalMetadata.Fresh(rec.ReplayId, hint));
        _store.RebuildIndex();
        _store.DeleteQuiet(_store.ActiveRecordingPath);
        _logger.LogInformation("Replay recovery OK {ReplayId} (sha256={Sha}).", rec.ReplayId, obj.Sha256);
    }

    private async Task PublishAsync(string type, object payload, CancellationToken ct)
    {
        try { await _bus.PublishAsync(new EventEnvelope { Type = type, Payload = payload }).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : publication event {Type} échouée", type); }
        _ = ct;
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
