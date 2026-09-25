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
    /// <summary>La surimpression qui ne prend JAMAIS le focus (WS_EX_NOACTIVATE) : c'est
    /// par la que passe tout ce qui s'affiche PENDANT une partie.</summary>
    private readonly LiveContestOverlayService? _overlay;
    /// <summary>Ce que la borne sait des coeurs : lesquels exposent de quoi mesurer.</summary>
    private readonly CoreMemoryCapability? _coeurs;
    /// <summary>Le prevol a-t-il promis une partie certifiable ? Confronte a la fin de partie.</summary>
    private bool _prevolCertifiable;
    /// <summary>Quand la promesse a ete faite : une partie de quinze secondes n'en est pas une.</summary>
    private DateTime? _prevolAt;
    /// <summary>Une session est-elle arrivee pour cette partie ?</summary>
    private bool _sessionRecue;
    private readonly ILogger<NelfePlayScoringReporter>? _logger;

    private IDisposable? _subscription;
    private readonly object _sync = new();

    private string? _enrolledKeyId;
    private string? _listenerSha256, _coreSha256, _memSha256, _contentSha256, _contentMd5, _contentSha1, _contentSet, _wrapperVersion;
    // Ce que le coeur declare de lui-meme : « FinalBurn Neo », « 0.289 (eb342748) ». Indice,
    // jamais preuve - c'est l'empreinte qui tranche. Il dit QUELLE source verifier.
    private string? _coreName, _coreVersion;
    private JsonElement? _ticket;
    private long _lastFrame;
    private long? _finalTotal;
    private bool _inDemo;   // attract mode : le jeu se joue seul → on ignore le score
    // Phase D (segmentation en RUNS, 100% APIExpose) : la trajectoire des scores suffit —
    // on la découpe aux CHUTES de score (un score qui retombe = partie relancée) et on ne
    // soumet QUE le meilleur run (segment monotone). Un super score n'est plus perdu si on
    // rejoue, et le wrapper n'est PAS touché ni sollicité par event (0 surcoût en jeu).
    private readonly List<(long frame, long total)> _trajectory = new();

    // LA FENÊTRE DE JEU (2026-09-25). Une lecture de score ne compte que si elle tombe entre un
    // START et le GAME OVER qui suit, dès lors qu'un START a été vu dans la session. Tout le reste
    // est de l'attract : la démo avant la partie, et la démo qui REPREND après le game over dans la
    // même session, laquelle aurait pu battre le joueur et partir à son nom. Les lignes de démo des
    // .MEM ne suffisent pas : celle de Metal Slug 3 ne s'allume jamais pendant la démo, et un
    // réglage mal étiqueté DEMO_MODE s'allumait, lui, à des moments arbitraires (mesuré en direct).
    // Sans aucun START vu (clavier, machine sans panneau), rien ne change : comportement d'avant.
    private readonly List<bool> _horsJeu = new();   // parallèle à _trajectory
    private bool _startVu;
    private bool _enJeu;
    /// <summary>
    /// Les pertes et gains de vie de la partie, avec leur valeur : c'est eux qui disent OU le run
    /// s'est termine. Voir FinsDeRun -- le decoupage ne connaissait que les chutes de score, et un
    /// continue d'arcade conserve le score.
    /// </summary>
    private readonly List<EvenementDeVie> _vies = new();

    // ── Lien replay ↔ score (funnel « ▷ REPLAY » de /rankings) ───────────────
    // Le reporter connaît le session_id (il le génère) et le verdict ; le recorder
    // publie l'id du replay actif (replay.recording.started) puis son sha au finalize
    // (replay.finalized). On rapproche les deux — quel que soit l'ordre d'arrivée —
    // et on POST /api/v1/agent/scores/replay-link. Purement additif et best-effort :
    // un échec n'affecte ni le scoring ni l'enregistrement.
    private readonly RetroBat.Api.Replay.Storage.ReplayStore? _replayStore;
    /// <summary>Les NVRAM du jeu (reglages des jeux sans DIP switches), jointes au passeport.</summary>
    private readonly NvramSnapshotService? _nvram;
    private readonly BiosFingerprintService? _bios;   // pour estampiller score/rang sur la méta du replay
    private readonly RetroBat.Api.Replay.Sharing.ReplaySeedQueue? _semis;
    private readonly RetroBat.Api.Replay.Sharing.ReplaySeedService? _semeur;
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
        LiveContestOverlayService? overlay = null,
        RetroBat.Api.Replay.Storage.ReplayStore? replayStore = null,
        ILogger<NelfePlayScoringReporter>? logger = null,
        RetroBat.Api.Replay.Sharing.ReplaySeedQueue? semis = null,
        RetroBat.Api.Replay.Sharing.ReplaySeedService? semeur = null,
        NvramSnapshotService? nvram = null,
        BiosFingerprintService? bios = null,
        CertifiedSettingsService? certified = null,
        CoreMemoryCapability? coeurs = null,
        RetroBat.Api.Replay.Playback.ReplayPlaybackService? playback = null)
    {
        _nvram = nvram;
        _bios = bios;
        _certified = certified;
        _overlay = overlay;
        _coeurs = coeurs;
        _playback = playback;
        _replayStore = replayStore;
        _semis = semis;
        _semeur = semeur;
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
        // La lecture d'un replay n'est pas une partie : rien de ce qui arrive pendant qu'elle
        // tourne (attestation, scores, session) ne doit ni prevenir le joueur ni soumettre quoi
        // que ce soit. Le lecteur emploie le vrai core, sans wrapper ; ceci est le filet.
        if (_playback?.IsBusy == true) return;
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
                    _prevolCertifiable = false;
                    _sessionRecue = false;
                    break;
                case "ui.game.ended":
                    // RIEN N'EST REMONTE, ET LE JOUEUR DOIT L'APPRENDRE.
                    //
                    // Le prevol promet « Partie certifiable », puis la partie se termine et il ne
                    // se passe rien : ni message, ni score. C'est ce qu'un joueur a vu quatre fois
                    // de suite le 24 septembre 2026 avant de comprendre tout seul.
                    //
                    // Ce controle-ci ne depend d'AUCUN chemin de mesure. Le wrapper libretro et le
                    // pont Lua de MAME echouent differemment -- le premier se tait faute de RAM
                    // exposee, le second lit des adresses sans jamais former de score -- et une
                    // detection propre a l'un ne voit pas l'autre. Ici on ne constate qu'une
                    // chose, vraie dans les deux cas : on a promis, et rien n'est venu.
                    if (_prevolCertifiable && !_sessionRecue
                        && _prevolAt is { } promesse && DateTime.UtcNow - promesse >= TimeSpan.FromSeconds(90))
                    {
                        _overlay?.ShowTop(
                            "SCORING",
                            "Aucun score n'a été mesuré",
                            "la partie annoncée certifiable n'a rien remonté",
                            9000);
                        Trace("fin de partie : prevol certifiable mais AUCUNE session recue");
                        _logger?.LogWarning(
                            "Scoring : partie annoncee certifiable terminee sans aucune session. "
                            + "Le coeur employe n'expose probablement rien a lire.");
                    }

                    _prevolCertifiable = false;
                    _prevolAt = null;
                    _sessionRecue = false;
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
                case "retroarch.memory.changed":
                case "ingame.memory.changed":
                {
                    var charge = ToJson(envelope.Payload);
                    CaptureVie(charge);
                    // Les ETATS du pont Lua de MAME arrivent ici, et seulement ici : le wrapper les
                    // projette en plus sur retroarch.state, le pont Lua non. Sans cette ligne, un
                    // DEMO_MODE sous MAME n'atteignait jamais la detection de la demo, et le score
                    // de l'attract pouvait partir a la place de celui du joueur (Metal Slug 3,
                    // 2026-09-25 : 17 700 de demo retenus pour 1 700 joues).
                    CaptureEtatSignal(charge);
                    break;
                }
                case "panel.input.pressed":
                    CaptureStart(ToJson(envelope.Payload));
                    break;
                case "scoring.lab.start":
                    SortirDeDemo();
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
            _listenerSha256 = _coreSha256 = _memSha256 = _contentSha256 = _contentMd5 = _contentSha1 = _contentSet = _wrapperVersion = null;
            _coreName = _coreVersion = null;
            _ticket = null;
            _lastFrame = 0;
            _finalTotal = null;
            _inDemo = false;
            _trajectory.Clear();
            _horsJeu.Clear();
            _startVu = false;
            _enJeu = false;
            _vies.Clear();
        }
    }

    private static JsonObject Triple(string? h) => new() { ["start_sha256"] = h, ["loaded_sha256"] = h, ["end_sha256"] = h };

    /// <summary>L'emulateur, avec ce qu'il declare etre. L'empreinte identifie le binaire ;
    /// le nom et la version disent a la plateforme QUELLE source aller verifier chez l'editeur,
    /// ce qu'une empreinte seule, opaque par construction, ne permet pas.</summary>
    private static JsonObject CoreArtifact(string? sha, string? nom, string? version)
    {
        var o = Triple(sha);
        if (!string.IsNullOrWhiteSpace(nom)) o["name"] = nom;
        if (!string.IsNullOrWhiteSpace(version)) o["version"] = version;
        return o;
    }

    private static JsonObject ContentArtifact(string? sha, string? md5, string? sha1)
    {
        var o = Triple(sha);
        if (md5 is not null) o["md5"] = md5;     // Voie A : md5 No-Intro de la ROM (consoles)
        if (sha1 is not null) o["sha1"] = sha1;  // MAME : sha1 du set (gamelist), MAME vérifiant le romset
        return o;
    }

    private readonly CertifiedSettingsService? _certified;
    private readonly RetroBat.Api.Replay.Playback.ReplayPlaybackService? _playback;

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
            _contentSet = GetString(root, "ContentSet");
            _wrapperVersion = GetString(root, "WrapperVersion");
            _coreName = GetString(root, "CoreName");
            _coreVersion = GetString(root, "CoreVersion");
        }
        _ = PreflightAsync(root, CancellationToken.None);
    }

    /// <summary>
    /// Le verdict AVANT la partie. Tout ce que le profil impose aux artefacts est connu dès le
    /// chargement ; la plateforme le juge avec le code du verdict final et la borne l'affiche
    /// pendant que le joueur peut encore agir, au lieu de le laisser découvrir à la fin qu'il
    /// jouait pour rien. Ce qui ne se voit qu'en jouant (cheats, rembobinage, entrées) reste
    /// vérifié à la fin. Un jeu non ouvert n'affiche rien : pas de bruit hors scoring.
    /// </summary>
    private async Task PreflightAsync(JsonElement attestation, CancellationToken ct)
    {
        try
        {
            var systemId = GetString(attestation, "SystemId") ?? "";
            var romGroup = GetString(attestation, "Rom") ?? "";
            if (systemId.Length == 0 || romGroup.Length == 0 || _esNotify is null) return;
            var credential = ResolveCredential();
            if (string.IsNullOrEmpty(credential)) return;
            var profile = await FetchProfileAsync(credential, systemId, romGroup, ct).ConfigureAwait(false);
            if (profile is null) return;

            var coreOptions = FilterGameplayCoreOptions(GetString(attestation, "CoreOptions"));
            var digest = !string.IsNullOrEmpty(coreOptions) ? Crypto.Sha256Hex(coreOptions) : Crypto.Sha256Hex("core-options@default");
            var contentSha1 = GetString(attestation, "ContentSha1")
                ?? GamelistIdentity.DeclaredSha1(systemId, romGroup, SetArcade(systemId, GetString(attestation, "ContentSet")));
            var mesures = new JsonObject
            {
                ["system_id"] = systemId,
                ["rom_group"] = romGroup,
                ["artifacts"] = new JsonObject
                {
                    ["core"] = CoreArtifact(GetString(attestation, "CoreSha256"),
                        GetString(attestation, "CoreName"), GetString(attestation, "CoreVersion")),
                    ["content"] = ContentArtifact(GetString(attestation, "ContentSha256"), GetString(attestation, "ContentMd5"), contentSha1),
                    ["mem"] = Triple(GetString(attestation, "MemSha256")),
                    ["core_options_digest"] = digest,
                    ["bios"] = _bios?.PourLePasseport(profile.Value) ?? new JsonObject { ["mode"] = "none" },
                    ["forced_options"] = GetString(attestation, "ForcedOptions") ?? "",
                },
                ["listener"] = new JsonObject { ["loaded_sha256"] = GetString(attestation, "ListenerSha256") },
            };

            using var client = CreateClient(credential);
            using var content = new StringContent(mesures.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/preflight", content, ct).ConfigureAwait(false);
            var corps = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Trace($"preflight HTTP {(int)response.StatusCode} - {corps}");
            if (!response.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(corps);
            var root = doc.RootElement;
            if (!root.TryGetProperty("open", out var open) || open.ValueKind != JsonValueKind.True) return;
            var certifiable = root.TryGetProperty("certifiable", out var c) && c.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var force = (GetString(attestation, "ForcedOptions") ?? "").Length > 0;
            // Le frontend, que la plateforme ne voit pas : rembobinage, run-ahead, sauvegarde
            // automatique font refuser le score a la fin. Le forcage les neutralise par jeu ;
            // eteint, ou pris de court, il reste a prevenir avant que le joueur ne joue pour rien.
            var dangers = _certified?.DangersFrontendActifs() ?? Array.Empty<string>();
            // RIEN NE SERA MESURE, ET LE JOUEUR DOIT L'APPRENDRE MAINTENANT.
            //
            // Le wrapper enveloppe les coeurs de RetroArch ; sans lui, aucune lecture de score
            // ne remonte. Un joueur a joue toute une soiree sans qu'un seul score n'arrive
            // (2026-09-22) : ses replays etaient la, donc RetroArch tournait, mais rien n'etait
            // mesure. Il a fallu trois allers-retours pour le decouvrir.
            //
            // C'est un etat LOCAL, connu sans reseau : il se dit ici, avec le prevol, et non
            // par le canal de la plateforme. Une alerte qui depend du lien pour annoncer que la
            // mesure est morte arriverait trop tard, et parfois jamais.
            var wrapper = CabinetState.Wrapper;
            if (wrapper is "none:0" or "missing")
            {
                _overlay?.ShowTop(
                    "SCORING",
                    "Aucun score ne sera mesuré",
                    wrapper == "missing"
                        ? "le module de mesure est absent de cette borne"
                        : "le module de mesure n'enveloppe aucun émulateur",
                    8000);
                Trace($"prévol : wrapper {wrapper}, rien ne sera mesuré");

                return;   // le reste du prevol parlerait de certification : il n'y a rien a certifier
            }

            // CE CŒUR-LA N'EXPOSE PAS SA MEMOIRE, ET ON LE SAIT DEJA.
            //
            // Le wrapper peut etre en place et enveloppe correctement : s'il n'a rien a lire, il
            // se tait image apres image. Un joueur a lance Altered Beast quatre fois sous
            // mame2003_plus le 24 septembre 2026, avec quatre « Partie certifiable » et zero
            // session, avant de comprendre tout seul en deplacant sa ROM vers roms/fbneo.
            //
            // La borne retient ce qu'elle a vu, coeur par coeur, et la fiche que RetroArch pose a
            // cote de chaque .dll relie le nom du coeur a son fichier. Elle peut donc prevenir
            // AVANT la partie, et non plus seulement a la premiere image.
            var coeur = GetString(attestation, "CoreName") ?? string.Empty;
            if (_coeurs?.ConnuParNomAffiche(coeur) is { Measures: false })
            {
                _overlay?.ShowTop(
                    "SCORING",
                    "Aucun score ne sera mesuré",
                    "le cœur « " + coeur + " » n'expose pas sa mémoire",
                    8000);
                Trace($"prévol : {coeur} est connu pour ne rien exposer, rien ne sera mesuré");

                return;
            }
            // LE BANDEAU A DEUX LIGNES, ET LE PREVOL N'EN UTILISAIT QU'UNE.
            //
            // Mesure du 2026-09-23 : la premiere ligne du bandeau haut fait 652 pixels utiles
            // en Segoe UI 15 gras, soit une cinquantaine de caracteres. Trois messages la
            // depassaient largement - « Reglages en attente de conformite : ton score sera
            // garde et classe des qu'ils seront reconnus » demandait 890 pixels - et le joueur
            // n'en lisait donc que le debut, en perdant justement la partie rassurante.
            //
            // Le titre tient ce qui doit etre lu d'un coup d'oeil, le detail passe sur la
            // seconde ligne, en 9 points : elle absorbe meme quatre anomalies cumulees.
            string titre;
            string? detail;
            // Un emulateur inconnu ne fait plus perdre la partie : le score sera garde et
            // entrera au classement quand le build sera reconnu. On le dit AVANT, sinon le
            // joueur croit jouer pour rien et s'arrete.
            if (!certifiable && reason == "profile.core_mismatch")
            {
                titre = "Émulateur pas encore reconnu";
                detail = "ton score sera gardé et classé dès qu'il le sera";
            }
            // Meme regle pour les reglages (decision user 2026-09-22) : des reglages que la
            // plateforme ne connait pas encore ne font pas perdre la partie, ils attendent
            // leur quorum. « Non conformes (usine requis) » disait au joueur qu'il avait
            // triche, alors qu'il jouait le plus souvent avec les reglages de tout le monde.
            else if (!certifiable && reason == "profile.core_options_mismatch")
            {
                titre = "Réglages en attente";
                detail = "ton score sera gardé et classé dès qu'ils seront reconnus";
            }
            else if (!certifiable)
            {
                titre = "Partie non certifiable";
                detail = ReasonToText(reason);
            }
            else if (dangers.Count > 0)
            {
                titre = "Partie non certifiable";
                detail = string.Join(", ", dangers) + ", à désactiver dans les options RetroBat de ce jeu";
            }
            else
            {
                titre = "Partie certifiable";
                detail = force ? "réglages certifiés appliqués" : "pour le classement";
            }
            // On retient la PROMESSE, et QUAND elle a ete faite : c'est elle qu'on confrontera a
            // la fin de la partie, et sa date dit si le joueur a eu le temps de jouer.
            _prevolCertifiable = certifiable && dangers.Count == 0;
            _prevolAt = DateTime.UtcNow;
            Trace("prévol : " + (detail is { Length: > 0 } ? titre + " : " + detail : titre));
            // LE PREVOL S'AFFICHE PENDANT QUE LE JEU TOURNE : il ne passe donc PAS par la
            // notification d'EmulationStation, qui ramene ES au premier plan et sort le joueur
            // de sa partie (vecu le 2026-09-21 : le testeur s'est retrouve sur l'ecran de
            // selection en plein jeu). La surimpression, elle, est topmost et posee avec
            // WS_EX_NOACTIVATE : elle s'affiche par-dessus sans jamais prendre le focus.
            //
            // Si elle n'est pas la, on ne dit RIEN plutot que d'ejecter : un avertissement qui
            // interrompt la partie est pire que pas d'avertissement, et le joueur aura de toute
            // facon le verdict a la fin, quand ES a repris la main.
            if (_overlay is not null)
            {
                _overlay.ShowTop("SCORING", titre, detail, 6000);
            }
            else
            {
                Trace("prevol non affiche : pas de surimpression disponible");
            }
        }
        catch (Exception ex)
        {
            Trace("preflight impossible : " + ex.Message);
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
        AppliquerEtat(action);
    }

    /// <summary>
    /// L'état porté par un signal mémoire (pont Lua de MAME, ou wrapper). Le pont Lua range TOUS
    /// ses signaux dans le canal ACTION, états compris : on ne peut pas filtrer par canal. On ne
    /// réagit donc qu'aux deux jetons EXACTS du cycle de vie, jamais à un nom qui les contiendrait.
    /// </summary>
    private void CaptureEtatSignal(JsonElement root)
    {
        if (!root.TryGetProperty("signal", out var signal) && !root.TryGetProperty("Signal", out signal)) return;
        var nom = (GetString(signal, "Name") ?? "").Trim().ToUpperInvariant();
        if (nom is "DEMO_MODE" or "GAME_PLAYING" or "GAME_OVER") AppliquerEtat(nom);
    }

    private void AppliquerEtat(string action)
    {
        if (action.Length == 0) return;
        lock (_sync)
        {
            (_inDemo, _enJeu) = EtatsApres(_inDemo, _enJeu, action);
        }
    }

    /// <summary>
    /// L'effet d'un état du cycle de vie sur (démo, en jeu).
    ///
    /// Pendant une partie ouverte par START, un DEMO_MODE est un FAUX signal : sur Metal Slug 3, un
    /// réglage mal étiqueté (« Soft Dip - toggle demo sound ») s'allumait en plein jeu, et la
    /// partie du joueur passait pour de la démo (1 900 joués, 0 retenu, 2026-09-25). Un GAME_OVER
    /// ferme la partie : ce qui suit est de l'attract jusqu'au prochain START.
    /// </summary>
    internal static (bool Demo, bool EnJeu) EtatsApres(bool demo, bool enJeu, string action)
    {
        if (!(enJeu && action.Contains("DEMO", StringComparison.Ordinal))) demo = EtatDemoApres(demo, action);
        if (action.Contains("GAME_OVER", StringComparison.Ordinal)) enJeu = false;
        return (demo, enJeu);
    }

    /// <summary>Les lectures prises EN JEU, c'est-à-dire entre un START et le GAME OVER qui suit.</summary>
    internal static List<(long frame, long total)> FiltrerEnJeu(
        IReadOnlyList<(long frame, long total)> trajectoire, IReadOnlyList<bool> horsJeu)
    {
        var gardes = new List<(long frame, long total)>(trajectoire.Count);
        for (var i = 0; i < trajectoire.Count; i++)
        {
            if (i < horsJeu.Count && horsJeu[i]) continue;
            gardes.Add(trajectoire[i]);
        }
        return gardes;
    }

    /// <summary>
    /// Le set lance compte pour l'ARCADE seulement : c'est lui qui distingue deux jeux au meme nom
    /// (ddragon, le Double Dragon de Technos ouvert au scoring, et doubledr, celui de la Neo-Geo).
    /// Sans lui, l'identite etait lue par le nom du groupe, et le Double Dragon Neo-Geo s'annoncait
    /// « certifiable » avec le sha1 de celui de Technos (2026-09-25). Une console garde sa recherche
    /// par nom : son contenu est mesure sur le fichier.
    /// </summary>
    internal static string? SetArcade(string? systemId, string? set)
        => string.Equals(systemId, "arcade", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(set)
            ? set.Trim()
            : null;

    /// <summary>
    /// Personne n'a joué tant que le score n'est jamais monté. Metal Slug 3 sous MAME, 2026-09-25 :
    /// la machine lit 63 au démarrage puis se réinitialise d'elle-même une quinzaine de secondes
    /// après le lancement. Le pont Lua refermait là une session de 63 points, à CHAQUE lancement,
    /// qui serait partie au classement une fois le jeu ouvert au scoring.
    /// </summary>
    internal static bool ScoreAMonte(IReadOnlyList<(long frame, long total)> trajectoire)
    {
        for (var i = 1; i < trajectoire.Count; i++)
        {
            if (trajectoire[i].total > trajectoire[i - 1].total) return true;
        }
        return false;
    }

    /// <summary>
    /// La démo commence sur un état DEMO ; elle ne finit que sur un état « en jeu ». Beaucoup de
    /// .MEM n'en déclarent aucun (Metal Slug 3, Altered Beast, 19xx...) : sans autre signal, une
    /// démo vue une fois laissait toute la suite marquée démo, et la vraie partie ne comptait pas.
    /// D'où <see cref="SortirDeDemo"/> sur START.
    /// </summary>
    internal static bool EtatDemoApres(bool enDemo, string action)
    {
        if (action.Contains("DEMO", StringComparison.Ordinal)) return true;
        if (action.Contains("PLAYING", StringComparison.Ordinal) || action.Contains("GAME_PLAY", StringComparison.Ordinal)) return false;
        return enDemo;
    }

    /// <summary>
    /// UN START FAIT SORTIR DE LA DÉMO. Le joueur vient de lancer une partie : c'est le signal que
    /// l'enregistreur de replay emploie déjà, résolu par la cartographie de chaque borne, donc
    /// valable sur toutes les machines, et qui ne dépend d'aucune ligne de .MEM. Le START forcé par
    /// le labo à travers le pont MAME compte aussi (scoring.lab.start).
    ///
    /// Limite connue : sur une borne à pièces, un START pressé pendant la démo SANS crédit la fait
    /// sortir de la démo alors que l'attract continue. La démo suivante la ré-arme, et le score
    /// retenu reste le meilleur run. À affiner avec le crédit quand on en aura le signal.
    /// </summary>
    private void SortirDeDemo()
    {
        // Un START ouvre la partie : fin de la démo, et début de la fenêtre de jeu.
        lock (_sync) { _inDemo = false; _startVu = true; _enJeu = true; }
    }

    private void CaptureStart(JsonElement root)
    {
        var systeme = GetString(root, "System") ?? GetString(root, "system") ?? "";
        if (string.Equals(systeme, "START", StringComparison.OrdinalIgnoreCase)) SortirDeDemo();
    }

    // Phase D : découpe la trajectoire aux CHUTES de score (le score qui retombe = un
    // nouveau run) et renvoie le sous-segment MONOTONE du MEILLEUR run (pic le plus haut).
    // Robuste : ne dépend PAS des frames (le score seul suffit). Un seul run croissant →
    // toute la trajectoire. C'est ce qui fait qu'un super score n'est jamais perdu.
    // Une chute qui REPREND là où le score en était avant les dernières lectures n'est pas une
    // nouvelle partie : c'est une lecture parasite (RAM en cours d'écriture, texte de l'attract
    // passé par l'adresse du score). Ms. Pac-Man sous MAME, 2026-09-22 : 430 → 906030 → 440 ; le
    // pic isolé, BCD valide, devenait le « meilleur run » et partait au classement. On retire
    // au plus deux lectures de queue quand la valeur qui suit continue d'avant elles.
    private const int MaxGlitchTail = 2;

    internal static List<(long frame, long total)> SelectBestRun(List<(long frame, long total)> traj)
        => SelectBestRun(traj, []);

    /// <param name="finsDeRun">
    /// Les frames ou les vies du joueur mesure ont atteint zero. On y coupe, EN PLUS des chutes de
    /// score, et c'est ce qui manquait au 1CC : un continue d'arcade conserve le score, donc la
    /// courbe ne retombe jamais et la partie entiere passait pour un seul run. Voir FinsDeRun.
    /// </param>
    internal static List<(long frame, long total)> SelectBestRun(
        List<(long frame, long total)> traj, IReadOnlyList<long> finsDeRun)
    {
        if (traj.Count == 0) return traj;
        List<(long frame, long total)>? best = null;
        long bestPeak = long.MinValue;
        var cur = new List<(long frame, long total)>();
        long prev = long.MinValue;
        // La derniere vie perdue ferme le run : ce qui suit appartient a un autre run, continue
        // ou nouvelle partie. On ne cherche PAS a reconnaitre le continue -- crédit au demarrage,
        // entree d'un second joueur, free play, 1-up : aucune de ces distinctions n'est fiable.
        //
        // OU TOMBE LA COUPE. La mort a SA trame (le wrapper la transmet) ; les lectures de score ont
        // la leur, celle du dernier changement de score. Elles ne coincident presque jamais : exiger
        // l'egalite (premiere version, 2026-09-24) ne coupait donc quasiment rien en vrai, et les
        // tests passaient parce qu'on leur donnait des trames qui coincidaient. La mort tombe
        // ENTRE deux lectures : on coupe avant la premiere lecture posterieure a la mort.
        //
        // Sur le pont Lua de MAME aucune trame ne circule, tout vaut 0 : aucune mort ne tombe
        // « entre » deux lectures de meme trame, donc rien n'est coupe et le decoupage ordinaire
        // s'applique, comme avant.
        var fins = finsDeRun is { Count: > 0 } ? finsDeRun.OrderBy(f => f).ToArray() : null;
        long? framePrecedente = null;
        // UN SEGMENT QUI SUIT UN CONTINUE NE CONCOURT PAS, et c'est tout l'enjeu.
        //
        // Apres un continue, le score est REPORTE : le joueur repart de ses 5000 points et monte a
        // 12000. Le second segment affiche donc 12000 sans les avoir gagnes -- comparer les
        // sommets absolus rendrait au continue exactement ce qu'on veut lui retirer.
        //
        // Une vraie nouvelle partie, elle, remet le score a zero : sa chute est vue par le
        // decoupage ordinaire et les deux tentatives se comparent alors honnetement.
        var reporte = false;
        foreach (var pt in traj)
        {
            // Une derniere vie est tombee apres la lecture precedente (ou pile dessus) et avant
            // celle-ci : la lecture precedente est le score AVEC lequel le joueur a perdu, et
            // celle-ci ouvre un autre run.
            var coupe = fins is not null && framePrecedente is long fp && cur.Count > 0
                && fins.Any(f => f >= fp && f < pt.frame);
            framePrecedente = pt.frame;

            if (coupe)
            {
                long peakFin = cur.Count > 0 ? cur[^1].total : long.MinValue;
                if (!reporte && peakFin > bestPeak)
                {
                    bestPeak = peakFin;
                    best = new List<(long frame, long total)>(cur);
                }

                // Le score a-t-il ete remis a zero ? Sinon, ce qui suit herite du run precedent.
                reporte = peakFin != long.MinValue && pt.total >= peakFin;
                cur.Clear();
                prev = long.MinValue;
            }

            if (pt.total < prev)   // chute : parasite de queue, ou fin du run précédent
            {
                // k lectures de queue sont parasites si la valeur qui suit reprend au niveau
                // d'avant elles (egalite admise pour une seule lecture : le score n'a pas
                // bouge pendant le parasite) et s'il reste au moins deux lectures au run :
                // une chute au tout premier point est une nouvelle partie, pas un parasite.
                var parasite = 0;
                for (var k = 1; k <= MaxGlitchTail && cur.Count - k >= 2; k++)
                {
                    var avant = cur[cur.Count - 1 - k].total;
                    if (k == 1 ? pt.total >= avant : pt.total > avant) { parasite = k; break; }
                }
                if (parasite > 0)
                {
                    cur.RemoveRange(cur.Count - parasite, parasite);
                    cur.Add(pt);
                    prev = pt.total;
                    continue;
                }
                long peak = cur.Count > 0 ? cur[^1].total : long.MinValue;
                if (!reporte && peak > bestPeak) { bestPeak = peak; best = new List<(long frame, long total)>(cur); }
                // Une chute de score est une remise a zero : la tentative qui suit est a elle.
                reporte = false;
                cur.Clear();
            }
            cur.Add(pt);
            prev = pt.total;
        }
        if (cur.Count > 0 && !reporte && (best is null || cur[^1].total > bestPeak)) best = cur;
        // Rien retenu alors qu'on a joue : tous les segments heritaient d'un continue. On rend le
        // premier, qui est le seul dont le score soit vraiment le sien.
        if (best is null && cur.Count > 0) best = cur;

        // UN SEGMENT D'UNE SEULE LECTURE N'EST PAS UNE PARTIE. Un score sportif progresse : il
        // arrive par une suite de lectures, pas d'un coup, seul entre deux chutes. La RAM de
        // MAME au demarrage, elle, donne une lecture isolee avant que le jeu n'ecrive son
        // score - et quand elle est BCD-valide (0x906030 -> 906 030 sur Ms. Pac-Man le
        // 2026-09-22), rien d'autre ne la distingue d'un score. On prend donc le meilleur
        // segment qui compte AU MOINS DEUX lectures ; un segment isole ne gagne que s'il n'y a
        // rien d'autre (partie tres courte, ou une seule lecture dans toute la trajectoire).
        if (best is { Count: 1 })
        {
            var mieux = Segments(traj).Where(seg => seg.Count >= 2).OrderByDescending(seg => seg[^1].total).FirstOrDefault();
            if (mieux is { Count: >= 2 }) best = mieux;
        }
        return best ?? traj;
    }

    /// <summary>Les segments monotones de la trajectoire, dans l'ordre.</summary>
    private static List<List<(long frame, long total)>> Segments(List<(long frame, long total)> traj)
    {
        var sortie = new List<List<(long frame, long total)>>();
        var cur = new List<(long frame, long total)>();
        long prev = long.MinValue;
        foreach (var pt in traj)
        {
            if (pt.total < prev && cur.Count > 0) { sortie.Add(cur); cur = new List<(long frame, long total)>(); }
            cur.Add(pt);
            prev = pt.total;
        }
        if (cur.Count > 0) sortie.Add(cur);
        return sortie;
    }

    /// <summary>
    /// Une perte ou un gain de vie, avec SA VALEUR. Le joueur vient du signal pour le wrapper, de
    /// la racine pour le pont Lua de MAME : les deux ponts ne le rangent pas au meme endroit.
    /// </summary>
    private void CaptureVie(JsonElement root)
    {
        if (!root.TryGetProperty("signal", out var signal) && !root.TryGetProperty("Signal", out signal))
        {
            return;
        }

        var nom = (GetString(signal, "Name") ?? "").Trim();
        var perte = nom.Equals("LOSE_LIFE", StringComparison.OrdinalIgnoreCase);
        if (!perte && !nom.Equals("GAIN_LIFE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var adresse = (GetString(signal, "Address") ?? "").Trim();
        if (adresse.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_inDemo) return;   // une demo n'est pas une partie
            _vies.Add(new EvenementDeVie(
                Normaliser(adresse),
                perte,
                Entier(signal, "Value"),
                Entier(signal, "Frame") ?? _lastFrame,
                Entier(signal, "Player") ?? Entier(root, "player") ?? 1));
            if (_vies.Count > MaxTrajectory) _vies.RemoveAt(0);
        }
    }

    /// <summary>Le pont MAME ecrit 0x604, le wrapper 0X0604 : la meme ligne doit se reconnaitre.</summary>
    private static string Normaliser(string adresse)
    {
        var t = adresse.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        t = t.TrimStart('0');
        return "0x" + (t.Length == 0 ? "0" : t.ToUpperInvariant());
    }

    private static int? Entier(JsonElement el, string nom)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
        {
            if (!string.Equals(p.Name, nom, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.Number when p.Value.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(p.Value.GetString(), out var n) => n,
                _ => null,
            };
        }

        return null;
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
                _horsJeu.Add(!_enJeu);
                if (_trajectory.Count > MaxTrajectory) { _trajectory.RemoveAt(0); _horsJeu.RemoveAt(0); }
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
        // Une session est arrivee. Elle ne vaut PAS quittance a elle seule : celles d'Altered
        // Beast sous MAME arrivaient vides, sans le moindre score. C'est le chemin de soumission
        // qui decidera, un peu plus bas, s'il y avait quelque chose a mesurer.
        _sessionRecue = true;
        if (sessionJson is null) return;
        try
        {
            // Ce que le listener a vu des entrées et ce qu'il a forcé : lisible ici même quand la
            // partie ne donne lieu à aucun passeport (pas de score), pour le diagnostic.
            var vu = JsonNode.Parse(sessionJson)!.AsObject();
            Trace($"entrées: impossible={(long?)vu["impossible_inputs"] ?? -1} appuis={(long?)vu["press_count"] ?? -1} macro={(long?)vu["macro_repeats"] ?? -1} forcé=[{(string?)vu["forced_options"] ?? ""}]");
            // Une partie sans le moindre appui n'est pas une partie : c'est la démo d'attract, ou
            // un jeu lancé et laissé là. Sonic a marqué 200 points tout seul et les a fait publier
            // sous le compte de la borne (2026-09-17). Le listener 0.336 compte les appuis ; un
            // listener plus ancien ne dit rien (champ absent) et garde l'ancien comportement.
            if (vu["press_count"] is not null && ((long?)vu["press_count"] ?? 0) == 0)
            {
                Trace("STOP: aucun appui du joueur pendant la session (démo ou jeu laissé là)");
                return;
            }
        }
        catch { }

        // Appairé OU anonyme : le scoring accepte les deux (anonyme = score « anonyme »).
        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential))
        {
            Trace("STOP: pas de credential (ni appairé ni anonyme)");
            return;
        }

        string? listenerSha, coreSha, memSha, contentSha, contentMd5, contentSha1, contentSet, wrapperVersion, coreName, coreVersion;
        long? finalTotal;
        List<(long frame, long total)> trajectory;
        List<EvenementDeVie> vies;
        lock (_sync)
        {
            listenerSha = _listenerSha256; coreSha = _coreSha256; memSha = _memSha256;
            contentSha = _contentSha256; contentMd5 = _contentMd5; contentSha1 = _contentSha1; contentSet = _contentSet; wrapperVersion = _wrapperVersion; finalTotal = _finalTotal;
            coreName = _coreName; coreVersion = _coreVersion;
            trajectory = new List<(long, long)>(_trajectory);
            vies = new List<EvenementDeVie>(_vies);

            // La fenêtre de jeu : un START a été vu, donc on sait ce qui est joué. Ce qui ne l'est
            // pas (démo avant la partie, attract après le game over) sort de la trajectoire.
            if (_startVu)
            {
                var gardes = FiltrerEnJeu(_trajectory, _horsJeu);
                if (gardes.Count != trajectory.Count)
                {
                    Trace($"fenetre de jeu : {trajectory.Count - gardes.Count} lecture(s) hors jeu ecartee(s) (demo, attract)");
                }
                trajectory = gardes;
                // Rien de joué : pas de filet sur le dernier total, qui serait celui de l'attract.
                if (trajectory.Count == 0) finalTotal = null;
            }
        }

        // Un score qui n'est jamais monté : personne n'a joué (lecture de démarrage, reset de la
        // machine, coup d'oeil au titre). On s'arrête sans rien soumettre ni rien annoncer.
        if (finalTotal is not null && !ScoreAMonte(trajectory))
        {
            Trace($"STOP: le score n'est jamais monte ({trajectory.Count} lecture(s), dernier total {finalTotal}) : personne n'a joue");
            return;
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
            contentSha1 = GamelistIdentity.DeclaredSha1(systemId, romGroup, SetArcade(systemId, contentSet));
            Trace($"identite declaree : sha1={(contentSha1 is null ? "introuvable" : contentSha1)} (sys={systemId} rom={romGroup})");
        }

        // Phase D : le score soumis = le MEILLEUR run. On segmente la trajectoire aux CHUTES
        // de score (un score qui retombe = le joueur a relancé une partie) et on garde le
        // segment monotone au pic le plus haut. Un super score n'est donc jamais perdu par un
        // mauvais run qui suit. Calculé tôt + tracé pour valider même hors chemin certifié.
        var finsDeRun = FinsDeRun.Calculer(vies);
        if (finsDeRun.Count > 0)
        {
            Trace($"fins de run (vies a zero) : {string.Join(", ", finsDeRun)}");
        }

        var bestRun = SelectBestRun(trajectory, finsDeRun);
        long runPeak = bestRun.Count > 0 ? bestRun[^1].total : (finalTotal ?? 0);
        Trace($"segmentation : meilleur run {bestRun.Count}/{trajectory.Count} pts, pic={runPeak} (total global {finalTotal})");

        // Rien à certifier sans score ni attestation : on s'arrête AVANT de consommer
        // quoi que ce soit (démo, navigation, jeu non joué).
        Trace($"état: listener={listenerSha is not null} core={coreSha is not null} content={contentSha is not null} finalTotal={finalTotal} trajPts={trajectory.Count} inDemo={_inDemo}");
        if (listenerSha is null || finalTotal is null)
        {
            Trace("STOP: pas de score/attestation");

            // LE JOUEUR A ENTENDU « CERTIFIABLE », ET RIEN N'EST VENU.
            //
            // Le bon critere n'est pas « aucune session » -- ma premiere version testait cela et ne
            // se declenchait jamais. Mesure du 24 septembre 2026, deux lancements d'Altered Beast :
            // une session ARRIVE bien (sessionLen=255) mais elle est VIDE, finalTotal absent,
            // trajPts=0. Ce qui manque n'est pas la session, c'est le SCORE.
            //
            // Ici on le sait de facon certaine et quel que soit le chemin de mesure : le wrapper
            // libretro et le pont Lua de MAME echouent differemment, mais tous deux aboutissent a
            // cette ligne.
            //
            // MAIS SEULEMENT SI LE JOUEUR A JOUE. Deux essais du 24 septembre 2026 ont dure 18 et
            // 16 secondes : le jeu lance puis quitte aussitot. Il n'y avait aucun score a mesurer,
            // et annoncer « aucun score n'a ete mesure » a qui vient de quitter apres un coup
            // d'oeil, c'est crier au loup -- au bout de trois fois, plus personne ne lit les
            // bandeaux, y compris celui qui compte.
            //
            // Le seuil porte sur la duree depuis la promesse du prevol. Une minute et demie : on
            // ne fait pas de score en dessous, et un coeur muet, lui, se laisse decouvrir aussi
            // bien a la deuxieme minute qu'a la premiere.
            var joue = _prevolAt is { } debut && DateTime.UtcNow - debut >= TimeSpan.FromSeconds(90);
            if (_prevolCertifiable && joue)
            {
                _overlay?.ShowTop(
                    "SCORING",
                    "Aucun score n'a été mesuré",
                    "la partie annoncée certifiable n'a rien remonté",
                    9000);
                _logger?.LogWarning(
                    "Scoring : partie annoncee certifiable terminee sans aucun score mesure "
                    + "(listener={Listener}, total={Total}). Le coeur employe n'expose probablement rien a lire.",
                    listenerSha is not null, finalTotal);
                _prevolCertifiable = false;
            }

            return;
        }

        var profile = await FetchProfileAsync(credential!, systemId, romGroup, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            // Mode laboratoire (NelfeScoreLab) : on soumet quand meme. La plateforme refuse le score
            // mais garde la tentative signee, sans laquelle aucun profil ne peut s'ouvrir.
            if (RetroBat.Api.Scoring.ScoreLabLabMode.IsActive(DateTime.UtcNow, out var labo))
            {
                Trace($"profil {romGroup} non ouvert, soumission de laboratoire ({labo})");
                profile = RetroBat.Api.Scoring.ScoreLabLabMode.PlaceholderProfile();
            }
            else
            {
                Trace($"STOP: profil {romGroup} non ouvert (fetch null)");
                return;
            }
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

        // Les NVRAM, lues APRES la fermeture de l'emulateur (voir NvramSnapshotService) : on les
        // obtient avant de signer, puisqu'elles font partie du passeport.
        JsonArray? nvram = null;
        if (_nvram is not null)
        {
            try { nvram = await _nvram.PourLePasseportAsync(NvramSnapshotService.EpinglesDuProfil(profile), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { Trace($"NVRAM indisponible : {ex.Message}"); }
        }

        // Les BIOS que le profil exige (none quand le jeu n'en utilise pas).
        JsonObject? bios = null;
        if (_bios is not null)
        {
            try { bios = _bios.PourLePasseport(profile.Value); }
            catch (Exception ex) { Trace($"BIOS indisponible : {ex.Message}"); }
        }

        using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
        JsonObject passport;
        try
        {
            passport = BuildPassport(
                systemId, romGroup, sessionJson, ticket.Value, profile.Value,
                deviceId!, deviceKey, listenerSha, coreSha, memSha, contentSha, contentMd5, contentSha1, wrapperVersion,
                coreName, coreVersion,
                runPeak, bestRun, trajectory, nvram, bios);
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

    /// <summary>Le plus grand ecart entre deux lectures consecutives du run retenu : un saut
    /// isole se voit la, sans rien savoir du jeu.</summary>
    private static long PlusGrandPas(List<(long frame, long total)> run)
    {
        long max = 0;
        for (var i = 1; i < run.Count; i++)
        {
            var pas = run[i].total - run[i - 1].total;
            if (pas > max) max = pas;
        }
        return max;
    }

    private JsonObject BuildPassport(
        string systemId, string romGroup, string sessionJson, JsonElement ticket, JsonElement profile,
        string deviceId, CngDeviceKey deviceKey, string listenerSha, string? coreSha, string? memSha,
        string? contentSha, string? contentMd5, string? contentSha1, string? wrapperVersion,
        string? coreName, string? coreVersion, long finalTotal, List<(long frame, long total)> trajectory,
        List<(long frame, long total)>? toutesLesLectures = null, JsonArray? nvram = null, JsonObject? bios = null)
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
        // Entrees (wrapper 0.336) : les images a directions opposees, et la statistique des durees
        // d'appui. Absents avec un listener plus ancien : le verifieur compte 0.
        long impossibleInputs = (long?)session["impossible_inputs"] ?? 0;
        long pressCount = (long?)session["press_count"] ?? 0;
        long pressSum = (long?)session["press_frames_sum"] ?? 0;
        long pressSq = (long?)session["press_frames_sq"] ?? 0;
        string forcedOptions = (string?)session["forced_options"] ?? "";
        long macroRepeats = (long?)session["macro_repeats"] ?? 0;
        long macroWindows = (long?)session["macro_windows"] ?? 0;
        // Phase E : réglages (DIP/vies/difficulté) capturés par le listener sous forme de chaîne
        // canonique triée. Absent (backend pas encore câblé) → placeholder stable. Le vérifieur ne
        // contrôle le digest QUE si le profil épingle allowed_core_options_digest (opt-in additif).
        string? coreOptionsRaw = (string?)session["core_options"];
        bool mameDipSwitches = string.Equals((string?)session["core_options_source"], "mame_dip", StringComparison.Ordinal);
        string? coreOptions = FilterGameplayCoreOptions(coreOptionsRaw, mameDipSwitches);
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

        // TOUTES LES LECTURES DE LA PARTIE, a cote des checkpoints (qui ne portent que le run
        // retenu et doivent rester croissants). Elles sont signees comme le reste, et c'est
        // avec elles que la plateforme peut RETROUVER le bon score quand la borne s'est
        // trompee de segment - au lieu d'ecarter la partie, ce qui punirait le joueur. Vecu le
        // 2026-09-22 : la RAM de MAME avant le demarrage donne 906 030, lecture isolee retenue
        // comme run ; la vraie partie, elle, est dans ces lectures.
        var lectures = new JsonArray();
        var brut = toutesLesLectures ?? trajectory;
        var pasLecture = brut.Count > 128 ? (brut.Count / 128) + 1 : 1;
        for (var i = 0; i < brut.Count; i += pasLecture)
        {
            lectures.Add(brut[i].total);
        }
        if (brut.Count > 0 && (brut.Count - 1) % pasLecture != 0)
        {
            lectures.Add(brut[^1].total);   // la derniere lecture compte toujours
        }

        // Empreintes gated par le profil (listener/core/mem) = attestation. Les modules
        // détaillés (frontend/apiexpose/launcher/hook) + process + content ROM restent à
        // calibrer par introspection une fois le profil ouvert.
        var modules = new JsonArray
        {
            new JsonObject { ["role"] = "listener", ["sha256"] = listenerSha },
            new JsonObject { ["role"] = "real_core", ["sha256"] = coreSha ?? listenerSha },
        };
        var modulesDigest = Crypto.Sha256Hex(Jcs.CanonicalBytes(modules));

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

        var document = new JsonObject
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
                // Partie de LABORATOIRE (drapeau de NelfeScoreLab) : signee et verifiee comme les
                // autres, jamais classee. Un essai a publie 906 030 sur Ms. Pac-Man (2026-09-22).
                ["lab"] = RetroBat.Api.Scoring.ScoreLabLabMode.IsActive(DateTime.UtcNow, out _) ? true : null,
            },
            ["listener"] = new JsonObject
            {
                ["build"] = wrapperVersion ?? "0", ["start_sha256"] = listenerSha,
                ["loaded_sha256"] = listenerSha, ["end_sha256"] = listenerSha,
                ["certification"] = "listener-homologation",
            },
            ["software"] = new JsonObject
            {
                ["modules"] = modules,
                ["modules_digest"] = modulesDigest,
                // Avec quoi cette partie a ete mesuree : sans cela, une anomalie de mesure ne
                // peut pas etre rattachee a une version, et on ne sait pas qui doit mettre a jour.
                ["apiexpose"] = CabinetState.Version,
                ["wrapper_state"] = CabinetState.Wrapper,
            },
            ["artifacts"] = new JsonObject
            {
                ["core"] = CoreArtifact(coreSha, coreName, coreVersion), ["content"] = ContentArtifact(contentSha, contentMd5, contentSha1), ["mem"] = Triple(memSha),
                ["core_options_digest"] = coreOptionsDigest,
                ["bios"] = bios ?? new JsonObject { ["mode"] = "none" },
                ["forced_options"] = forcedOptions,
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
                ["impossible_inputs"] = impossibleInputs,
                ["press_count"] = pressCount, ["press_frames_sum"] = pressSum, ["press_frames_sq"] = pressSq,
                ["macro_repeats"] = macroRepeats, ["macro_windows"] = macroWindows,
            },
            ["metric"] = new JsonObject
            {
                ["type"] = "score", ["unit"] = "points", ["value"] = finalTotal.ToString(),
                ["ranking_direction"] = direction, ["result_source"] = resultSource,
                // LA FORME DE LA TRAJECTOIRE, SIGNEE AVEC LE RESTE. Sans elle, la plateforme ne
                // voit qu'un nombre et ne peut juger d'une anomalie qu'en le comparant aux autres
                // scores, ce qui punirait un bon joueur. Avec elle, elle reconnait la signature
                // d'une lecture parasite : un score retenu sur UNE SEULE lecture alors que la
                // partie en a produit des dizaines (Ms. Pac-Man, 906 030 sur 117 lectures,
                // 2026-09-22). Corrige a la source depuis la 1.8.22 ; ces nombres sont la pour
                // que le serveur n'ait plus a faire confiance a la version de la borne.
                ["samples"] = toutesLesLectures?.Count ?? trajectory.Count,
                ["run_samples"] = trajectory.Count,
                ["max_step"] = PlusGrandPas(trajectory),
            },
            ["progression"] = new JsonObject
            {
                ["checkpoints"] = checkpoints,
                ["checkpoints_digest"] = checkpointsDigest,
                ["samples"] = lectures,
            },
            ["local_check"] = "pass",
        };
        // Les NVRAM du jeu (EEPROM, RAM de sauvegarde) : jointes seulement quand le jeu en a, pour
        // qu'un passeport sans NVRAM reste identique. Le profil dit ou sont les reglages.
        if (nvram is { Count: > 0 }) document["artifacts"]!["nvram"] = nvram;
        return document;
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
                "published" => $"Score certifié : {score:N0} publié" + (rank is int r ? $" (#{r})" : ""),
                // Signalé : gardé sur le compte du joueur, jamais classé ni ancré. Il sait pourquoi.
                "held" => $"Score {score:N0} signalé, non classé : {ReasonToText(reason)}",
                // La quarantaine n'est PAS un refus : le score est garde avec son passeport
                // signe et entrera au classement des que l'emulateur sera reconnu. Le dire
                // ainsi change tout pour le joueur, qui a joue et qui garde quelque chose.
                "quarantined" => reason == "settings.unknown"
                    ? $"Score {score:N0} enregistré, en attente de conformité des réglages"
                    : $"Score {score:N0} enregistré, en attente : ton émulateur n'est pas encore reconnu",
                "expired" => $"Score {score:N0} non classé : {ReasonToText(reason)}",
                "refused" => $"Score {score:N0} refusé : {ReasonToText(reason)}",
                _ => $"Score non transmis : {ReasonToText(reason)}",
            };
            await _esNotify.NotifyAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : notification ES du verdict impossible.");
        }
    }

    // Phase E : l'empreinte des reglages d'une partie certifiee.
    //
    // REGLE (decision du 2026-09-17) : un reglage d'AFFICHAGE ou de MANETTE ne compte JAMAIS.
    // Resolution, format d'image, filtres, rotation, flip, zone morte, sensibilite analogique,
    // resolution gauche+droite (SOCD), tir automatique, souris, pistolet : RetroBat les ecrit
    // selon l'ecran et les manettes de chaque borne, et une borne d'usine serait refusee pour
    // eux. Seul ce qui change la PARTIE entre dans l'empreinte : vitesse, materiel emule, ROM
    // patchee, cheats, reprise de partie, DIP switches de jeu (vies, difficulte, bonus).
    //
    // Deux sources :
    //   - les options d'un coeur libretro, lues par le wrapper : on ne garde QUE la liste du
    //     coeur ci-dessous ; un coeur sans liste ne verse rien (avant le 2026-09-17 il versait
    //     tout, affichage compris, et MAME sous RetroArch n'avait pas de liste) ;
    //   - les DIP switches de MAME autonome, lus par le plugin Lua : tous gardes, sauf ceux dont
    //     le nom dit monnayage, service, ecran ou commandes (NonGameplayDipWords).
    // Une entree finie par « * » garde une FAMILLE de cles (FBNeo met le nom du jeu dans la cle).
    private static readonly Dictionary<string, HashSet<string>> CoreOptionsAllowlist = new()
    {
        ["genesis_plus_gx_"] = new(StringComparer.Ordinal)
        {
            "genesis_plus_gx_region_detect",   // PAL/NTSC → vitesse & difficulté
            "genesis_plus_gx_vdp_mode",        // timing vidéo → vitesse
            "genesis_plus_gx_system_hw",       // matériel émulé
            "genesis_plus_gx_overclock",       // vitesse CPU → ralentissements
            "genesis_plus_gx_lock_on",         // cartouche lock-on (S&K…)
        },
        ["fbneo-"] = new(StringComparer.Ordinal)
        {
            "fbneo-allow-patched-romsets",     // ROM patchee = autre jeu
            "fbneo-cpu-speed-adjust",          // vitesse CPU → ralentissements
            "fbneo-force-60hz",                // vitesse des jeux 50 Hz
            "fbneo-neogeo-mode",               // variante de BIOS (UniBIOS a un menu de triche)
            "fbneo-memcard-mode",              // carte memoire Neo-Geo = reprise de partie
            "fbneo-dipswitch-*",               // DIP switches du jeu : vies, difficulte
            "fbneo-cheat-*",                   // cheats integres au coeur
        },
        ["mame_"] = new(StringComparer.Ordinal)
        {
            "mame_cheats_enable",              // cheats
            "mame_cpu_overclock",              // vitesse CPU → ralentissements
            "mame_cpu_sound_overclock",        // idem, processeur son
            "mame_read_config",                // cfg de MAME : DIP switches, vies, difficulte
            "mame_auto_save",                  // reprise automatique d'une sauvegarde d'etat
        },
    };

    // Un DIP switch dont le nom contient l'un de ces mots ne change pas la partie : monnayage et
    // service (une borne en free play n'est pas recalee), ecran et commandes (la regle ci-dessus).
    // Meme liste cote plugin Lua, completee ici pour l'ecran et les commandes.
    private static readonly string[] NonGameplayDipWords =
    {
        "coin", "free play", "free_play", "service", "test mode", "test_mode", "demo sound", "demo_sound",
        "unused", "flip", "cabinet", "screen", "monitor", "control", "joystick", "trackball",
    };

    private static bool IsNonGameplayDip(string name)
    {
        var low = name.ToLowerInvariant();
        return NonGameplayDipWords.Any(word => low.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Réduit la chaîne canonique « clé=valeur;… » aux seuls réglages qui changent la partie.
    /// <paramref name="mameDipSwitches"/> : la chaîne vient du plugin Lua de MAME autonome (des
    /// noms de DIP switches), pas des options d'un cœur libretro.
    /// </summary>
    internal static string? FilterGameplayCoreOptions(string? raw, bool mameDipSwitches = false)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var kept = new List<string>();
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair.Substring(0, eq) : pair;
            if (mameDipSwitches)
            {
                if (!IsNonGameplayDip(key)) kept.Add(pair);
                continue;
            }

            HashSet<string>? allow = null;
            foreach (var kv in CoreOptionsAllowlist)
                if (key.StartsWith(kv.Key, StringComparison.Ordinal)) { allow = kv.Value; break; }
            if (allow is null) continue;   // coeur sans liste : aucun de ses reglages ne compte
            if (!allow.Contains(key) && !allow.Any(a => a.EndsWith('*') && key.StartsWith(a[..^1], StringComparison.Ordinal)))
                continue;
            // fbneo-dipswitch-<jeu>-<nom> : le nom du DIP decide, comme sous MAME autonome.
            if (key.StartsWith("fbneo-dipswitch-", StringComparison.Ordinal) && IsNonGameplayDip(key["fbneo-dipswitch-".Length..]))
                continue;
            kept.Add(pair);
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
        "runtime.impossible_inputs" => "directions opposées simultanées (manette ou stick non conforme)",
        "plausibility.macro_detected" => "séquence rejouée à l'identique (macro) : score signalé, non classé",
        "plausibility.statistical_hold" => "score retenu pour vérification",
        "runtime.module_unauthorized" => "logiciel non homologué",
        "profile.core_mismatch" => "émulateur non reconnu",
        "emulator.unknown" => "émulateur pas encore reconnu",
        "emulator.never_recognised" => "émulateur jamais reconnu, attente close",
        "emulator.profile_moved" => "le règlement du jeu a changé pendant l'attente",
        "emulator.rejected" => "émulateur écarté",
        "settings.unknown" => "réglages en attente de conformité",
        "metric.spike" => "lecture isolée : le score ne suit pas la partie, à vérifier",
        "metric.single_sample" => "une seule lecture du score sur toute la partie : mets APIExpose à jour",
        "profile.content_mismatch" => "ROM non reconnue",
        "profile.mem_mismatch" => "définition mémoire non reconnue",
        "profile.core_options_mismatch" => "réglages en attente de conformité",
        "profile.listener_unauthorized" => "listener non homologué (wrapper ou plugin MAME)",
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
        if (string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase)) SemerReplayCertifie(replayId, sha);
    }

    /// <summary>
    /// Un score certifie PUBLIC seme son replay vers l'amorce, sans geste.
    ///
    /// Jusqu'ici la liaison replay/score rendait le lien visible sur le site, mais la poussee vers
    /// l'amorce ne partait que du geste explicite de publication. Le lien ne tenait donc que par la
    /// borne d'origine : celle de 19xx a purge son magasin, et l'objet n'existait plus nulle part.
    /// Un lien sur un score certifie promet une lecture, donc l'objet doit survivre a la borne.
    ///
    /// Le geste explicite reste pour les replays SANS score certifie. Meme file, meme reprise : rien
    /// n'est pousse pendant une partie, et une extinction ne perd que la progression.
    /// </summary>
    private void SemerReplayCertifie(string replayId, string sha256)
    {
        if (_semis is null) return;
        try
        {
            _semis.Enqueue(replayId, sha256);
            var meta = _replayStore?.GetMeta(replayId);
            if (meta is not null)
                _replayStore!.SaveMeta(meta with { Visibility = "public", PublicationState = "mirrored" });
            Trace($"SEMIS inscrit pour {replayId} (score certifie public)");
            if (_semeur is not null)
                _ = Task.Run(async () =>
                {
                    try { await _semeur.NudgeAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { _logger?.LogDebug(ex, "Semis : tentative immediate en echec, la file reprendra."); }
                });
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Semis : inscription impossible pour {ReplayId}.", replayId); }
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
