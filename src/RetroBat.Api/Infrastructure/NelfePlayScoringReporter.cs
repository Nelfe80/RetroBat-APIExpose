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
    // AUCUNE LECTURE NE SE PERD (charte, principe 4). A 512, les plus anciennes lectures d'une longue
    // session etaient effacees : 711 lectures sur 19xx, 1 280 sur Metal Slug 3 chez un joueur
    // (2026-09-29), et une premiere partie meilleure pouvait disparaitre au profit d'une plus faible.
    // Cent mille lectures tiennent en deux megaoctets et couvrent des heures de jeu.
    private const int MaxTrajectory = 100_000;

    private readonly IEventBus _eventBus;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NelfePlayDeviceStore _devices;
    /// <summary>La carte du reseau (CDC infra §15.4) : les cles qui signent les verdicts, les relais.</summary>
    private readonly RetroBat.Api.Reseau.ServiceDeCarte? _cartes;
    /// <summary>Les relais, quand la borne ne joint pas le central (CDC infra §15.5).</summary>
    private readonly RetroBat.Api.Reseau.ClientDeRelais? _relais;
    /// <summary>
    /// Le ticket pris au lancement de la partie (CDC infra §15.6) : si le central tombe pendant la partie, le
    /// passeport peut quand meme etre complet, et un relais le garder. Un par lancement, pris par le premier
    /// brouillon.
    /// </summary>
    private JsonNode? _ticketDuLancement;
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
    private long _lastFrame;
    /// <summary>Les lectures de score recues pendant la session, et celles ecartees en demo : sans
    /// elles, « pas de score » ne disait pas s'il n'en etait venu aucune ou si toutes avaient ete ecartees.</summary>
    private int _scoresRecus;
    private int _scoresEnDemo;
    /// <summary>
    /// LE CHIFFRE DES CREDITS (2026-09-27) : 19xx et Metal Slug 3 ecrivent les continues dans le
    /// dernier chiffre du score. La plateforme le dit dans la reponse d'annonce ; au premier +1, le
    /// joueur apprend que seul son score d'avant le continue sera certifie. La partie n'est jamais
    /// coupee (regle user 2026-09-27).
    /// </summary>
    private bool _chiffreCredits;
    private long? _dernierPropre;
    private long? _derniereLecture;
    private long? _avantContinue;
    /// <summary>
    /// La fin de la partie certifiee a ete dite au replay (scoring.run.ended) : une fois par partie.
    /// L'enregistrement s'arrete la, pour que le replay montre ce que le score certifie, sans les
    /// continues (demande user 2026-09-27).
    /// </summary>
    private bool _finDeRunPubliee;
    /// <summary>Le jeton de la session : un bandeau de credit en attente d'une autre session se tait.</summary>
    private int _jetonDeSession;
    /// <summary>Le premier credit consomme de la session, le depart, a ete vu.</summary>
    private bool _departAuCredit;
    /// <summary>
    /// Un credit consomme apres le depart a clos la partie certifiee : la session fait foi, rien de
    /// ce qui suit ne concourt, meme quand le jeu remet le score a zero (Double Dragon).
    /// </summary>
    private bool _closeParCredit;
    /// <summary>L'arrivee d'un joueur 2 a deja ete dite : la partie est hors classement solo.</summary>
    private bool _partieADeuxAnnoncee;
    /// <summary>
    /// Cette borne a rejoint la partie d'un autre (netplay) : ce qu'elle lit est la partie de
    /// l'hote. Elle ne soumet rien et ne s'attribue aucun bandeau de l'hote ; en joueuse, elle dit
    /// seulement que la partie est a plusieurs.
    /// </summary>
    private RetroBat.Api.Netplay.NetplayGuestService.Role _invite;

    /// <summary>Le debut de la partie en cours, pour savoir si elle a ete ouverte aux joueurs.</summary>
    private DateTime _debutSessionUtc = DateTime.UtcNow;

    /// <summary>Le role pris au « Rejoindre » : trois minutes pour que le jeu demarre.</summary>
    private static readonly TimeSpan FenetreRejointe = TimeSpan.FromMinutes(3);

    private void PrendreRoleInvite()
    {
        var role = RetroBat.Api.Netplay.NetplayGuestService.PrendreRejointe(FenetreRejointe);
        if (role == RetroBat.Api.Netplay.NetplayGuestService.Role.Aucun) return;
        // La place et la seance, prises MAINTENANT : la place se libere quand l'emulateur se ferme,
        // parfois avant que la fin de la partie arrive ici.
        var place = role == RetroBat.Api.Netplay.NetplayGuestService.Role.Joueur
            ? RetroBat.Api.Netplay.NetplayGuestService.PlaceDeJoueur
            : null;
        var seance = place is null ? null : RetroBat.Api.Netplay.NetplayGuestService.SeanceDeLaPlace;
        lock (_sync) { _invite = role; _placeInvite = place; _seanceInvite = seance; }
        Trace(place is { } p
            ? $"partie rejointe en netplay en joueur, place {p} : pas de 1CC solo, le 1CC MULTI du joueur {p}"
            : $"partie rejointe en netplay ({role}) : la partie de l'hote, rien a soumettre ici");
    }
    private int? _placeInvite;
    private string? _seanceInvite;

    // 1CC MULTI (2026-09-30) : le port de manette de cette borne, retrouve a ses appuis, et la
    // trajectoire du score de chaque autre joueur (celle de sa place, pour la borne invitee).
    private readonly RetroBat.Api.Scoring.PortLocal _portLocal = new();
    private readonly Dictionary<int, List<(long frame, long total)>> _trajectoiresAutres = new();
    private long? _finalTotal;
    private bool _inDemo;   // attract mode : le jeu se joue seul → on ignore le score
    /// <summary>Le dernier appui du joueur : panel lu par l'API, ou entrees vues par le wrapper.</summary>
    private DateTime _dernierAppuiUtc = DateTime.MinValue;
    /// <summary>
    /// Un DEMO_MODE recu en pleine partie, pas encore cru : son heure et l'image ou il est arrive. Les
    /// lectures prises depuis restent dans la trajectoire ; s'il se confirme, elles en sortent.
    /// </summary>
    private (DateTime Depuis, long Frame)? _demoEnSuspens;
    /// <summary>Un GAME_OVER depuis le dernier depart : la demo qui suit est vraie, on ne la discute pas.</summary>
    private bool _gameOverVu;
    /// <summary>
    /// Un GAME_OVER depuis le debut de la partie en cours (2026-10-09). Le compteur d'un code de
    /// continue ne compte qu'apres lui (ContinuesParCompteur.BaisseRecevable). Une nouvelle partie
    /// l'efface : un START, ou le score qui retombe.
    /// </summary>
    private bool _gameOverDansLaPartie;
    /// <summary>Les compteurs de code de continue du .MEM charge, lus une fois par .MEM.</summary>
    private (string? Chemin, HashSet<string> Adresses) _compteursDeCode = (null, new HashSet<string>(StringComparer.Ordinal));
    /// <summary>Les signaux de demo ignores dans la session (journal de la session).</summary>
    private int _demosIgnorees;
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

    /// <summary>La derniere valeur du score du joueur 1 recue, demo comprise.</summary>
    private long? _dernierTotalVu;

    /// <summary>
    /// LE SCORE PILOTE LE REPLAY (2026-10-02, plan valide par le user) : un enregistrement en cours
    /// s'arrete quand le score baisse apres avoir monte (nouvelle partie, remise a zero) ; en
    /// filet, le score qui monte rearme, sur une borne dont l'API ne lit aucun appui, trois fois
    /// par partie au plus (une demo sans fin ne remplit pas le magasin).
    /// </summary>
    private bool _enregistrementEnCours;
    private bool _monteeDansLEnregistrement;
    private bool _attenteMontee = true;
    private int _departsParScore;
    private const int MaxDepartsParScore = 3;

    /// <summary>
    /// Montee et baisse se jugent contre le score d'AVANT la salve de signaux en cours, et la baisse
    /// qui arrete le replay se confirme a la salve suivante (2026-10-03, 1942 chez un testeur : un
    /// total de passage, 190 entre 90 et 100, coupait le replay). Voir <see cref="RetroBat.Api.Scoring.SalvesDeScore"/>.
    /// </summary>
    private RetroBat.Api.Scoring.SalvesDeScore _salves = new();
    private readonly RetroBat.Api.Scoring.ArretALaBaisse _baisse = new();

    /// <summary>Les enregistrements de la partie, en frames du rapporteur : le replay du meilleur run.</summary>
    private readonly List<(string Id, long Debut, long? Fin)> _enregistrements = new();
    private List<(string Id, long Debut, long? Fin)> _enregistrementsDeLaPartie = new();

    /// <summary>
    /// Le lecteur de manettes de l'API a lu au moins un appui depuis son demarrage : il voit le
    /// START, le score n'a pas a le remplacer (sur une borne lue, un joueur qui laisse tourner la
    /// demo d'un jeu sans credits la ferait enregistrer).
    /// </summary>
    private volatile bool _panelLu;

    /// <summary>Le systeme du jeu charge (attestation du pont) : mastersystem, arcade...</summary>
    private string _systemeDuJeu = "";



    /// <summary>
    /// LE SCORE AU DEPART DE LA PARTIE (2026-10-02), avec la frame du depart. Le pont ne dit un
    /// score que quand il change : la derniere valeur lue avant le START est celle que le jeu
    /// avait au depart. Sans elle, une partie qui marque d'un coup avant son premier continue
    /// n'avait qu'une lecture et passait pour un score qui n'a jamais monte (Metal Slug 3 au
    /// labo : 0 lu au demarrage, ecarte comme hors jeu, puis 500 et le continue).
    /// </summary>
    private (long Frame, long Total)? _scoreAuDepart;
    /// <summary>
    /// Les pertes et gains de vie de la partie, avec leur valeur : c'est eux qui disent OU le run
    /// s'est termine. Voir FinsDeRun -- le decoupage ne connaissait que les chutes de score, et un
    /// continue d'arcade conserve le score.
    /// </summary>
    private readonly List<EvenementDeVie> _vies = new();

    /// <summary>
    /// Le mode et la difficulte en vigueur (lignes GAME_MODE et GAME_DIFFICULTY du .MEM), et leur
    /// instantane a chaque lecture de score, parallele a _trajectory : c'est ce qui dit dans quel
    /// mode le run retenu a ete joue. Voir ModesDeJeu.
    /// </summary>
    private RetroBat.Api.Scoring.ContexteDeJeu _contexte = RetroBat.Api.Scoring.ContexteDeJeu.Vide;
    private readonly List<RetroBat.Api.Scoring.ContexteDeJeu> _contextes = new();

    /// <summary>
    /// Le compteur de credits (action CREDITS du .MEM) et les START de chaque joueur : c'est avec
    /// eux que se reconnait un continue (ContinuesParCredits), plus avec les vies.
    /// </summary>
    private readonly List<EvenementDeCredit> _credits = new();
    private readonly List<DepartDeJoueur> _departs = new();

    // ── Lien replay ↔ score (funnel « ▷ REPLAY » de /rankings) ───────────────
    // Le reporter connaît le session_id (il le génère) et le verdict ; le recorder
    // publie l'id du replay actif (replay.recording.started) puis son sha au finalize
    // (replay.finalized). On rapproche les deux — quel que soit l'ordre d'arrivée —
    // et on POST /api/v1/agent/scores/replay-link. Purement additif et best-effort :
    // un échec n'affecte ni le scoring ni l'enregistrement.
    private readonly RetroBat.Api.Replay.Storage.ReplayStore? _replayStore;
    /// <summary>Les NVRAM du jeu (reglages des jeux sans DIP switches), jointes au passeport.</summary>
    private readonly NvramSnapshotService? _nvram;
    private readonly RetroBat.Domain.Interfaces.IEsSettingsStore? _esSettings;
    private readonly BiosFingerprintService? _bios;   // pour estampiller score/rang sur la méta du replay
    private readonly RetroBat.Api.Replay.Sharing.ReplaySeedQueue? _semis;
    private readonly RetroBat.Api.Replay.Sharing.ReplaySeedService? _semeur;
    private string? _activeReplayId;
    /// <summary>
    /// Le replay du 1CC solo, fige a l'arrivee d'un joueur : celui du 1CC MULTI commence ensuite et
    /// devient le replay actif, mais le score solo garde le sien.
    /// </summary>
    private string? _replayDuSolo;
    private readonly Dictionary<string, (string sessionId, string visibility, long? score, int? rank, DateTime at)> _pendingScoreLink = new();
    private readonly Dictionary<string, (string sha256, DateTime at)> _finalizedReplay = new();
    /// <summary>
    /// PLUS DE LIMITE DE 20 MINUTES (decision user 2026-10-02). Le score publie (a la sortie du jeu)
    /// et le replay scelle (au continue, ou a la fin) devaient se rejoindre en vingt minutes, en
    /// memoire : un joueur qui continuait apres un continue, ou une API relancee entre les deux,
    /// et le record partait sans replay, jamais publie. Les rapprochements vivent maintenant sur
    /// disque jusqu'a la reponse du serveur ; sept jours ne servent qu'au menage.
    /// </summary>
    private static readonly TimeSpan ReplayLinkTtl = TimeSpan.FromDays(7);
    /// <summary>Les liens en cours d'envoi : un seul envoi a la fois par replay.</summary>
    private readonly HashSet<string> _liensEnCours = new(StringComparer.Ordinal);
    /// <summary>Le replay de la partie dont la session vient d'arriver, fige a cet instant.</summary>
    private string? _replayDeLaPartie;
    private static string CheminDesLiens => Path.Combine(
        RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "state", "nelfeplay", "replay-links.json");

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
        RetroBat.Api.Replay.Playback.ReplayPlaybackService? playback = null,
        RetroBat.Domain.Interfaces.IEsSettingsStore? esSettings = null,
        PartieNelfePlayService? partie = null,
        RetroBat.Api.Reseau.ServiceDeCarte? cartes = null,
        RetroBat.Api.Reseau.ClientDeRelais? relais = null)
    {
        _cartes = cartes;
        _relais = relais;
        _esSettings = esSettings;
        _partie = partie;
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
            // Les rapprochements d'avant le redemarrage reprennent ou ils en etaient.
            lock (_sync) { ChargerLesLiens(); }
            RessayerLesLiens();
            // Les parties gardees sur la borne repartent d'elles-memes (piste B).
            _ = Task.Run(() => BoucleDesBrouillonsAsync(stoppingToken), CancellationToken.None);

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
                RessayerLesLiens();
                delay = TimeSpan.FromSeconds(180);
            }
        }
        catch (OperationCanceledException) { }
        finally { _subscription?.Dispose(); }
    }

    /// <summary>
    /// Battement recovery : interroge l'état « share datas » (endpoint public, sans SQL)
    /// et, s'il est armé, re-verse les records auto-conservés. On n'agit JAMAIS
    /// spontanément - uniquement quand le site a armé une reconstruction (le script de
    /// restauration le fait seul). Voir RecuperationDesParties.
    /// </summary>
    private async Task RecoveryCheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        try
        {
            // Le statut est public : pas besoin d'un secret valide pour le lire, et c'est
            // justement quand la base restauree ne connait plus la borne qu'il compte.
            using var client = CreateClient(ResolveCredential() ?? "");
            using var response = await client.GetAsync("/api/v1/scores/recovery-status", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject;
            var contribute = (bool?)root?["contribute"] ?? false;
            if (!contribute) return;

            // NOUVEL ÉPISODE : une époque inédite (nouvel armement) → on RÉ-ARME les records
            // déjà versés DE LA FENÊTRE (*.sent → *.json) pour que cette récupération les
            // re-verse. Le .sent ne vaut donc que POUR l'épisode courant. L'époque est
            // persistée pour survivre à un redémarrage au milieu d'un même épisode.
            var epoch = (string?)root?["epoch"] ?? "";
            var fenetre = RecuperationDesParties.LireLaFenetre(root);
            if (!string.IsNullOrEmpty(epoch) && epoch != ReadLastEpoch())
            {
                RearmSentFiles(CertifiedDir(), fenetre);
                // La base restauree a pu perdre la cle de l'appareil : on la reinscrit.
                _enrolledKeyId = null;
                await EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);
                WriteLastEpoch(epoch);
                Trace($"recovery : nouvel episode {epoch}, fenetre {fenetre.Depuis:u} -> {fenetre.Jusqua:u}");
            }

            await ContributeCertifiedAsync(epoch, fenetre, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Ré-arme les records de la fenêtre d'un nouvel épisode : *.sent → *.json.</summary>
    private void RearmSentFiles(string dir, RecuperationDesParties.Fenetre fenetre)
    {
        if (!System.IO.Directory.Exists(dir)) return;
        var n = 0;
        foreach (var sent in System.IO.Directory.EnumerateFiles(dir, "*.sent"))
        {
            try
            {
                if (JsonNode.Parse(System.IO.File.ReadAllText(sent)) is not JsonObject record) continue;
                if (!fenetre.Contient(RecuperationDesParties.HeureDuRecord(record))) continue;
                System.IO.File.Move(sent, sent[..^5], overwrite: true);
                n++;
            }
            catch { /* ignore */ }
        }
        if (n > 0) Trace($"recovery : {n} record(s) ré-armé(s) (nouvel épisode).");
    }

    /// <summary>Les records gardés sur la borne, envoyés ou non.</summary>
    private static IEnumerable<JsonObject> LesRecords()
    {
        var dir = CertifiedDir();
        if (!System.IO.Directory.Exists(dir)) yield break;
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*.*")
            .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".sent", StringComparison.OrdinalIgnoreCase)))
        {
            JsonObject? record = null;
            try { record = JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject; } catch { /* illisible */ }
            if (record is not null) yield return record;
        }
    }

    /// <summary>
    /// Les secrets de la borne : celui d'aujourd'hui, puis les secrets anonymes que le site a
    /// oubliés (NelfePlayPlayReporter les met de côté au lieu de les effacer). Une partie jouée
    /// sous un ancien secret ne repart que sous lui.
    /// </summary>
    private List<string> SecretsConnus()
    {
        var secrets = new List<string>();
        var actuel = ResolveCredential();
        if (!string.IsNullOrEmpty(actuel)) secrets.Add(actuel);
        foreach (var ancien in NelfePlayPlayReporter.AnciensSecrets())
            if (!secrets.Contains(ancien)) secrets.Add(ancien);
        return secrets;
    }

    /// <summary>Les preuves d'identité refusées pour de bon, par secret et par épisode : on ne les rejoue pas.</summary>
    private readonly HashSet<string> _reconnaissancesRefusees = new(StringComparer.Ordinal);

    /// <summary>
    /// La borne se fait reconnaître d'une base qui l'a perdue : un de ses passeports (ticket de la
    /// plateforme pour cet appareil) et une preuve fraîche signée par la clé de l'appareil.
    /// </summary>
    private async Task<bool> SeFaireReconnaitreAsync(string secret, string deviceId, string epoch, CancellationToken cancellationToken)
    {
        var refus = secret + "|" + epoch + "|" + deviceId;
        if (_reconnaissancesRefusees.Contains(refus)) return false;
        var passeport = RecuperationDesParties.PasseportDeLAppareil(LesRecords(), deviceId);
        if (passeport is null)
        {
            Trace($"recovery : aucun passeport pour se faire reconnaitre comme {deviceId}");
            _reconnaissancesRefusees.Add(refus);
            return false;
        }
        try
        {
            using var cle = CngDeviceKey.OpenOrCreate(ScoringKeyName);
            var corps = new JsonObject
            {
                ["key_pem"] = cle.PublicKeyPem,
                ["passport"] = passeport.DeepClone(),
                ["proof"] = cle.SignB64Url(RecuperationDesParties.MessageDePreuve(deviceId, RecuperationDesParties.Sha256Hex(secret), epoch)),
            };
            using var client = CreateClient(secret);
            using var content = new StringContent(corps.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/v1/agent/recovery/reidentify", content, cancellationToken).ConfigureAwait(false);
            var reponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Trace($"recovery : reidentification de {deviceId} HTTP {(int)response.StatusCode} - {reponse}");
            if (response.IsSuccessStatusCode)
            {
                _logger?.LogInformation("Scoring : borne reconnue par le site restauré ({DeviceId}).", deviceId);
                return true;
            }
            if ((int)response.StatusCode is 409 or 422) _reconnaissancesRefusees.Add(refus);
            return false;
        }
        catch (Exception ex)
        {
            Trace($"recovery : reidentification impossible : {ex.Message}");
            return false;
        }
    }

    private async Task<RecuperationDesParties.Issue> RenvoyerAsync(string secret, string corps, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(secret);
            using var content = new StringContent(corps, new UTF8Encoding(false), "application/json");
            using var response = await client.PostAsync("/api/v1/agent/scores/contribute", content, cancellationToken).ConfigureAwait(false);
            var reponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var issue = RecuperationDesParties.Classer((int)response.StatusCode, reponse);
            if (issue != RecuperationDesParties.Issue.Fait) Trace($"recovery : renvoi HTTP {(int)response.StatusCode} {issue} - {reponse}");
            return issue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Trace($"recovery : renvoi impossible : {ex.Message}");
            return RecuperationDesParties.Issue.ARetenter;
        }
    }

    /// <summary>
    /// Re-verse les passeports auto-conservés dans certified/ vers le serveur en
    /// reconstruction, ceux de la fenêtre seulement. Chaque record REPASSE le pipeline vérifié
    /// (signature + règles) et est idempotent (déjà présent = duplicate). Il n'est marqué .sent
    /// que sur un VRAI verdict : jusqu'au 2026-10-05, un refus « clé inconnue » de la base
    /// restaurée le marquait aussi, et la partie était perdue pour l'épisode. Une clé inconnue
    /// se réinscrit, une borne inconnue se fait reconnaître, puis la partie repart.
    /// </summary>
    private async Task ContributeCertifiedAsync(string epoch, RecuperationDesParties.Fenetre fenetre, CancellationToken cancellationToken)
    {
        var dir = CertifiedDir();
        if (!System.IO.Directory.Exists(dir)) return;
        var secrets = SecretsConnus();
        if (secrets.Count == 0) return;

        var sent = 0;
        var clesReinscrites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*.json").ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body;
            JsonObject? record;
            try
            {
                body = await System.IO.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                record = JsonNode.Parse(body) as JsonObject;
            }
            catch { continue; }
            if (record is null || !fenetre.Contient(RecuperationDesParties.HeureDuRecord(record))) continue;
            var deviceId = (string?)record["passport"]?["device"]?["device_id"];

            // Le secret d'aujourd'hui d'abord, puis les anciens : la partie repart sous le sien.
            var issue = RecuperationDesParties.Issue.ARetenter;
            string? secretRetenu = null;
            var autreIdentitePartout = true;
            foreach (var secret in secrets)
            {
                issue = await RenvoyerAsync(secret, body, cancellationToken).ConfigureAwait(false);
                if (issue == RecuperationDesParties.Issue.BorneInconnue && !string.IsNullOrEmpty(deviceId)
                    && await SeFaireReconnaitreAsync(secret, deviceId!, epoch, cancellationToken).ConfigureAwait(false))
                {
                    issue = await RenvoyerAsync(secret, body, cancellationToken).ConfigureAwait(false);
                }
                if (issue == RecuperationDesParties.Issue.CleInconnue && clesReinscrites.Add(secret))
                {
                    await InscrireLaCleAsync(secret, cancellationToken).ConfigureAwait(false);
                    issue = await RenvoyerAsync(secret, body, cancellationToken).ConfigureAwait(false);
                }
                if (issue != RecuperationDesParties.Issue.AutreIdentite) autreIdentitePartout = false;
                if (issue is RecuperationDesParties.Issue.Fait or RecuperationDesParties.Issue.ARetenter)
                {
                    secretRetenu = secret;
                    break;
                }
            }

            // Le site peine : on s'arrête là, le prochain battement reprendra.
            if (issue == RecuperationDesParties.Issue.ARetenter) break;
            if (issue != RecuperationDesParties.Issue.Fait && !autreIdentitePartout) continue;
            if (autreIdentitePartout)
                Trace($"recovery : {System.IO.Path.GetFileName(path)} n'appartient a aucune identite de cette borne");

            try { System.IO.File.Move(path, path + ".sent", overwrite: true); } catch { /* on retentera */ }
            sent++;
            if (secretRetenu is not null && record["replay_link"] is JsonObject lien)
            {
                // Le lien replay de la partie, perdu avec la base : on le redéclare.
                await RegisterReplayLinkAsync((string?)record["session_id"] ?? "", (string?)lien["replay_id"] ?? "",
                    (string?)lien["object_sha256"] ?? "", (string?)lien["visibility"] ?? "private", cancellationToken, secretRetenu)
                    .ConfigureAwait(false);
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
                    Interlocked.Increment(ref _lancement);
                    ResetSession();
                    PrendreRoleInvite();
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
                            Texte("scoring_none_measured"),
                            Texte("scoring_none_measured_sub"),
                            9000,
                            alerte: true);
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
                    CaptureTrameMemoire(charge);
                    CaptureVie(charge);
                    CaptureModeEtDifficulte(charge);
                    CaptureCredits(charge);
                    CaptureDefinitionChargee(charge);
                    CaptureContinuesConsole(charge);
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
                case "wrapper.ports":
                    CapturePorts(ToJson(envelope.Payload));
                    break;
                case "wrapper.watch.muted":
                    CaptureSurveillanceCoupee(ToJson(envelope.Payload));
                    break;
                case "scoring.lab.start":
                    SortirDeDemo();
                    break;
                case "netplay.guest.host_left":
                    lock (_sync) { _hotePartiFrame ??= _lastFrame; }
                    Trace($"l'hote a quitte la partie (frame {_lastFrame}) : la place de cette borne s'arrete la");
                    break;
                case "score.live.changed":
                    CaptureTotal(ToJson(envelope.Payload));
                    break;
                case "scoring.listener.session":
                    // Le replay de CETTE partie, fige a la fin de session : le verdict revient apres,
                    // parfois quand le jeu suivant a deja demarre et remis le replay actif a zero.
                    lock (_sync)
                    {
                        _replayDeLaPartie = _replayDuSolo ?? _activeReplayId;
                        _enregistrementsDeLaPartie = new List<(string Id, long Debut, long? Fin)>(_enregistrements);
                    }
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
            _lastFrame = 0;
            _finalTotal = null;
            _inDemo = false;
            _demoEnSuspens = null;
            _gameOverVu = false;
            _gameOverDansLaPartie = false;
            _demosIgnorees = 0;
            _scoresRecus = _scoresEnDemo = 0;
            _chiffreCredits = false;
            _dernierPropre = _derniereLecture = _avantContinue = null;
            _finDeRunPubliee = false;
            _jetonDeSession++;
            _departAuCredit = _closeParCredit = _partieADeuxAnnoncee = false;
            _scoresAutresJoueurs.Clear();
            _trajectoiresAutres.Clear();
            _placeInvite = null;
            _seanceInvite = null;
            _creditsMuets = null;
            _hotePartiFrame = null;
            _continuesConsole.Clear();
            _definitionChargee = null;
            _compteursDeCode = (null, new HashSet<string>(StringComparer.Ordinal));
            _invite = RetroBat.Api.Netplay.NetplayGuestService.Role.Aucun;
            _debutSessionUtc = DateTime.UtcNow;
            _replayDuSolo = null;
            // Le replay actif est celui de CETTE partie : il repart a zero au lancement (avant tout
            // START, donc avant l'enregistrement). Sans quoi, la limite de temps partie, un score
            // sans replay recupererait celui d'une partie precedente.
            _activeReplayId = null;
            _trajectory.Clear();
            _horsJeu.Clear();
            _startVu = false;
            _enJeu = false;
            _dernierTotalVu = null;
            _scoreAuDepart = null;
            _systemeDuJeu = "";
            _enregistrementEnCours = false;
            _monteeDansLEnregistrement = false;
            _attenteMontee = true;
            _departsParScore = 0;
            _salves = new RetroBat.Api.Scoring.SalvesDeScore();
            _baisse.Oublier();
            _enregistrements.Clear();
            _vies.Clear();
            _contexte = RetroBat.Api.Scoring.ContexteDeJeu.Vide;
            _contextes.Clear();
            _credits.Clear();
            _departs.Clear();
        }
    }

    /// <summary>
    /// Le .MEM charge voit-il arriver un joueur 2 ? Une ligne CONTINUES vivante qui porte player=2
    /// ou plus le dit (Neo-Geo : PLAYER_MOD2, 2026-10-02).
    /// </summary>
    internal static bool VoitArriverLesJoueurs(string? mem)
    {
        if (string.IsNullOrEmpty(mem)) return false;
        foreach (var brute in mem.Split('\n'))
        {
            var ligne = brute.Split("--", 2)[0];
            if (!LigneContinues.IsMatch(ligne) || LigneMorte.IsMatch(ligne)) continue;
            var joueur = JoueurDeLaLigne.Match(ligne);
            if (joueur.Success && int.TryParse(joueur.Groups[1].Value, out var n) && n >= 2) return true;
        }

        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex LigneContinues =
        new(@"action\s*=\s*[""']CONTINUES[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex LigneMorte =
        new(@"no_(log|survey)\s*=\s*true", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex JoueurDeLaLigne =
        new(@"\bplayer\s*=\s*(\d+)");

    private string? LireMem(string? chemin)
    {
        if (string.IsNullOrEmpty(chemin)) return null;
        try { return File.Exists(chemin) ? File.ReadAllText(chemin) : null; }
        catch (Exception ex)
        {
            Trace($".MEM illisible ({ex.Message}) : on ne sait pas s'il voit arriver un joueur");
            return null;
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

    /// <summary>Le plus long qu'on attende le proces-verbal d'un coeur jamais vu avant d'annoncer.</summary>
    private static readonly TimeSpan AttenteVerdictCoeur = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Le verdict de la liste blanche pour les fichiers d'un nom de coeur : vrai ou faux quand ce
    /// sont tous des coeurs d'arcade qui disent la meme chose, null sinon (hors arcade, inconnu).
    /// </summary>
    internal static bool? VerdictArcade(IReadOnlyList<string> fichiers)
    {
        if (fichiers.Count == 0 || fichiers.Any(fichier => CoeursObservables.CoeurArcadeMesure(fichier) is null))
        {
            return null;
        }

        var verdicts = fichiers.Select(fichier => CoeursObservables.CoeurArcadeMesure(fichier)!.Value).Distinct().ToList();
        return verdicts.Count == 1 ? verdicts[0] : null;
    }

    private async Task<CoreMemoryCapability.Verdict?> AttendreVerdictDuCoeurAsync(string coeur, CancellationToken ct)
    {
        var limite = DateTime.UtcNow + AttenteVerdictCoeur;
        while (DateTime.UtcNow < limite)
        {
            if (_coeurs?.ConnuParNomAffiche(coeur) is { } verdict)
            {
                return verdict;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return null;
    }

    private readonly CertifiedSettingsService? _certified;
    private readonly RetroBat.Api.Replay.Playback.ReplayPlaybackService? _playback;

    /// <summary>
    /// PAS DE SCORING HORS NELFEPLAY (regle user 2026-09-27). Un jeu lance depuis son systeme garde
    /// son propre comportement : ni annonce au lancement, ni score soumis. Seules comptent les
    /// parties lancees depuis la collection World Scoring ou par une fonction NelfePlay (voir
    /// PartieNelfePlayService), et celles du labo, qui ne sont jamais classees.
    /// </summary>
    private readonly PartieNelfePlayService? _partie;

    private bool PartieNelfePlay()
        => !SousAtelier()
           && (_partie is not { EstNelfePlay: false }
               || RetroBat.Api.Scoring.ScoreLabLabMode.IsActive(DateTime.UtcNow, out _));

    /// <summary>L'atelier de NelfeScoreLab tient sur la partie : rien ne se mesure ni ne part (APX-LAB-001).</summary>
    private bool SousAtelier()
        => _partie?.SousAtelier ?? RetroBat.Api.Scoring.ScoreLabAtelier.IsActive(DateTime.UtcNow, out _);

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
            _systemeDuJeu = GetString(root, "SystemId") ?? "";
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
            if (!PartieNelfePlay())
            {
                Trace(SousAtelier()
                    ? $"prevol : {systemId}/{romGroup} sous l'atelier NelfeScoreLab, ni annonce ni scoring"
                    : $"prevol : {systemId}/{romGroup} lance hors NelfePlay, ni annonce ni scoring");
                return;
            }
            // Si le jeu a demarre sans passer par ES, le role n'a pas encore ete repris.
            PrendreRoleInvite();
            RetroBat.Api.Netplay.NetplayGuestService.Role invite;
            lock (_sync) { invite = _invite; }
            if (invite != RetroBat.Api.Netplay.NetplayGuestService.Role.Aucun)
            {
                if (invite == RetroBat.Api.Netplay.NetplayGuestService.Role.Joueur)
                {
                    lock (_sync) { _partieADeuxAnnoncee = true; }
                    AnnoncerCredit("scoring_multiplayer", "partie rejointe en joueur : a plusieurs, hors classement solo");
                    // Son replay est celui du 1CC MULTI des le depart (dit ici, apres ui.game.started,
                    // qui remet la categorie a zero cote enregistreur).
                    PublierPartieMulti(relancer: false);
                }
                else
                {
                    Trace("prevol : spectateur, rien a annoncer");
                }
                return;
            }
            var credential = ResolveCredential();
            if (string.IsNullOrEmpty(credential)) return;
            _ = PrendreLeTicketDuLancementAsync(credential);
            var (profilsDuJeu, profilsDuSite) = await ProfilsAsync(credential, systemId, romGroup, ct).ConfigureAwait(false);
            // Le profil d'une partie seule : jamais le 1CC MULTI, ni le 1LC, meme s'ils venaient en tete.
            var seules = RetroBat.Api.Scoring.ModesDeJeu.DuSolo(profilsDuJeu);
            JsonElement? profile = seules.Count > 0 ? seules[0] : null;
            if (profile is null)
            {
                // Site muet et jeu jamais vu : on ne sait pas dire « certifiable », mais la partie sera
                // gardee sur la borne. Le joueur doit l'apprendre avant de jouer.
                if (!profilsDuSite) AnnoncerHorsLigne(systemId, romGroup);
                return;
            }

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
                // Un profil peut exiger une version minimale d'APIExpose : le verdict d'avant partie
                // la compare a celle-ci (sinon a celle du dernier releve de la borne).
                ["software"] = new JsonObject { ["apiexpose"] = CabinetState.Version },
            };

            // LE PREVOL HORS LIGNE (piste B, 2026-10-04). Chaque verdict du site est garde pour CETTE
            // configuration (coeur, .MEM, reglages, ROM, version) ; site muet, la borne reprend le
            // dernier, chiffre des credits compris, dont depend la coupure du 1CC. Sans verdict garde,
            // le joueur apprend que NelfePlay est injoignable et que son score partira au retour.
            var cleDuPrevol = Crypto.Sha256Hex(Jcs.CanonicalBytes(mesures));
            string? corps = null;
            var horsLigne = false;
            try
            {
                using var client = CreateClient(credential);
                using var content = new StringContent(mesures.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("/api/v1/agent/scores/preflight", content, ct).ConfigureAwait(false);
                corps = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Trace($"preflight HTTP {(int)response.StatusCode} - {corps}");
                if (RetroBat.Api.Scoring.BrouillonDeScore.Classer((int)response.StatusCode, corps) == RetroBat.Api.Scoring.IssueDEnvoi.ARetenter)
                {
                    horsLigne = true;
                }
                else if (!response.IsSuccessStatusCode)
                {
                    return;
                }
                else
                {
                    GarderLePrevol(cleDuPrevol, corps);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Trace($"preflight : site injoignable ({ex.GetType().Name})");
                horsLigne = true;
            }
            if (horsLigne)
            {
                corps = PrevolGarde(cleDuPrevol);
                if (corps is null)
                {
                    AnnoncerHorsLigne(systemId, romGroup);
                    return;
                }
                Trace("preflight : site injoignable, verdict garde pour cette configuration");
            }
            using var doc = JsonDocument.Parse(corps!);
            var root = doc.RootElement;
            if (!root.TryGetProperty("open", out var open) || open.ValueKind != JsonValueKind.True) return;
            var certifiable = root.TryGetProperty("certifiable", out var c) && c.ValueKind == JsonValueKind.True;
            var chiffreCredits = root.TryGetProperty("credit_digit", out var cd) && cd.ValueKind == JsonValueKind.True;
            lock (_sync) { _chiffreCredits = chiffreCredits; }
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var force = (GetString(attestation, "ForcedOptions") ?? "").Length > 0;
            // Les reglages du JEU que le forcage a remis d'usine (DIP), nommes au joueur : il les avait
            // changes, il doit savoir qu'ils ne comptent pas (decision user 2026-10-07).
            var remisDUsine = ReglagesDuJeuRemisDUsine(GetString(attestation, "ForcedOptions"));
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
                    Texte("scoring_nothing_measured"),
                    Texte(wrapper == "missing" ? "scoring_module_missing" : "scoring_module_idle"),
                    8000,
                    alerte: true);
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
            // LA LISTE BLANCHE PARLE AVANT L'EXPERIENCE (2026-09-27). Au tout premier lancement d'un
            // coeur, la borne ne savait rien de lui : « certifiable » en bleu, puis « aucun score »
            // en orange quatre secondes plus tard (19xx sous MAME 2003-Plus, 2026-09-26). Pour
            // l'arcade, la liste blanche tranche d'emblee, dans les deux sens : un vieux MAME ne
            // mesure pas, et MAME recent mesure meme si le wrapper, qui ne le lit pas, l'a note
            // aveugle (c'est le plugin Lua qui mesure).
            var verdictArcade = VerdictArcade(_coeurs?.FichiersParNomAffiche(coeur) ?? Array.Empty<string>());
            var aveugle = verdictArcade == false
                || (verdictArcade is null && _coeurs?.ConnuParNomAffiche(coeur) is { Measures: false });
            // Hors arcade, un coeur jamais vu : son proces-verbal arrive quelques secondes apres
            // l'attestation (4,2 s mesurees). On l'attend plutot que d'annoncer a l'aveugle.
            if (!aveugle && verdictArcade is null && _coeurs is not null
                && _coeurs.FichiersParNomAffiche(coeur).Count > 0 && _coeurs.ConnuParNomAffiche(coeur) is null)
            {
                aveugle = await AttendreVerdictDuCoeurAsync(coeur, ct).ConfigureAwait(false) is { Measures: false };
            }

            if (aveugle)
            {
                _overlay?.ShowTop(
                    "SCORING",
                    Texte("scoring_nothing_measured"),
                    string.Format(Texte("scoring_core_blind"), coeur),
                    8000,
                    alerte: true);
                Trace(verdictArcade == false
                    ? $"prévol : {coeur} n'est pas dans la liste blanche des cœurs qui mesurent, rien ne sera mesuré"
                    : $"prévol : {coeur} est connu pour ne rien exposer, rien ne sera mesuré");

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
                detail = string.Join(", ", dangers.Select(d => CabinetAnnounceText.Get("scoring_danger_" + d, "fr"))) + ", à désactiver dans les options RetroBat de ce jeu";
            }
            else
            {
                titre = "Partie certifiable";
                detail = remisDUsine.Count > 0 ? "réglages du jeu remis d'usine : " + string.Join(", ", remisDUsine)
                    : force ? "réglages certifiés appliqués" : "pour le classement";
            }
            if (horsLigne && certifiable && dangers.Count == 0) detail = "hors ligne, score envoyé au retour de NelfePlay";
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
            // UN LANCEMENT, UN PREVOL A L'ECRAN. Sous le coeur MAME de RetroArch, le wrapper PUIS le pont
            // Lua attestent la meme partie, a 16 s d'ecart : deux prevols, deux « Partie certifiable »
            // (2026-09-30). Le second ne se redit pas, MAIS SEULEMENT DANS LE MEME LANCEMENT, et sans
            // aucune duree : la cle portait le jeu et le verdict avec une fenetre d'une minute, et un
            // joueur qui relancait 1942 aussitot (1CC rate tot, on quitte, on relance) n'avait plus de
            // bandeau des sa deuxieme partie (2026-10-05).
            var clePrevol = CleDuPrevol(Interlocked.Read(ref _lancement), systemId, romGroup, titre, detail);
            bool dejaDit;
            lock (_sync)
            {
                dejaDit = PrevolDejaDit(clePrevol, _dernierPrevolAffiche);
                _dernierPrevolAffiche = clePrevol;
            }
            if (dejaDit)
            {
                Trace("prevol identique au precedent, deja affiche : rien de plus a l'ecran");
            }
            else if (_overlay is not null)
            {
                // Le JOURNAL garde le francais (l'outil de diagnostic le lit) ; l'ECRAN parle la langue
                // du joueur, et passe en orange quand la partie ne sera pas classee.
                var (titreAffiche, detailAffiche) = AnnonceLocalisee(Langue(), certifiable, reason, dangers, force, remisDUsine);
                if (horsLigne && certifiable && dangers.Count == 0) detailAffiche = Texte("scoring_offline_certifiable_sub");
                _overlay.ShowTop("SCORING", titreAffiche, detailAffiche, 6000, alerte: !(certifiable && dangers.Count == 0));
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

    /// <summary>
    /// Site muet et rien de garde pour cette configuration : la partie se joue, son brouillon partira
    /// au retour du site. On le promet au joueur, et c'est cette promesse qu'on tiendra a la fin.
    /// </summary>
    private void AnnoncerHorsLigne(string systemId, string romGroup)
    {
        _prevolCertifiable = true;
        _prevolAt = DateTime.UtcNow;
        Trace($"prévol : NelfePlay injoignable, {systemId}/{romGroup} se joue normalement, score gardé sur la borne");
        _overlay?.ShowTop("SCORING", Texte("scoring_offline"), Texte("scoring_offline_sub"), 6000, alerte: false);
    }

    private static string CheminDuPrevol(string cle)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "prevols", cle + ".json");

    private void GarderLePrevol(string cle, string corps)
    {
        try
        {
            var chemin = CheminDuPrevol(cle);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin + ".tmp", corps, new UTF8Encoding(false));
            File.Move(chemin + ".tmp", chemin, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : prevol non garde.");
        }
    }

    private static string? PrevolGarde(string cle)
    {
        try
        {
            var chemin = CheminDuPrevol(cle);
            return File.Exists(chemin) ? File.ReadAllText(chemin) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// LA TRAME DES LECTURES DE SCORE SUIT AUSSI LES EVENEMENTS MEMOIRE (2026-09-25).
    ///
    /// Elle ne venait que de retroarch.score, que le wrapper n'emet que pour les lignes de score de
    /// l'ancien format : avec les .MEM actuels, le score arrive par retroarch.memory.changed. Toutes les
    /// lectures restaient a la trame 0, et la coupure 1CC a la derniere vie perdue ne tombait jamais
    /// entre deux lectures : une partie de 19xx continuee a ete certifiee AVEC les points gagnes apres
    /// le continue. Le wrapper porte la trame dans chaque signal memoire ; le pont Lua de MAME, non,
    /// et rien ne change pour lui.
    /// </summary>
    private void CaptureTrameMemoire(JsonElement root)
    {
        if (TrameDuSignal(root) is not { } trame) return;
        lock (_sync) { if (!_inDemo) _lastFrame = trame; }
    }

    /// <summary>La trame d'un evenement memoire, ou null quand le pont n'en transmet pas.</summary>
    internal static long? TrameDuSignal(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("signal", out var signal) && !root.TryGetProperty("Signal", out signal)) return null;
        if (signal.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in signal.EnumerateObject())
        {
            if (!string.Equals(p.Name, "Frame", StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.Number when p.Value.TryGetInt64(out var n) && n > 0 => n,
                JsonValueKind.String when long.TryParse(p.Value.GetString(), out var n) && n > 0 => n,
                _ => null,
            };
        }
        return null;
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
        if (nom is "DEMO_MODE" or "GAME_PLAYING" or "GAME_OVER" or JeuCommence) AppliquerEtat(nom);
    }

    /// <summary>
    /// UNE PARTIE HUMAINE COMMENCE, dit le .MEM (2026-09-29). La fenetre de jeu ne se rouvrait
    /// qu'a un appui sur START ; Tetris se lance aussi par le bouton A, et ses parties d'apres le
    /// premier game over partaient « hors jeu ». Un .MEM qui sait reconnaitre le debut d'une
    /// partie jouee (un ecran que la demo ne traverse jamais) le declare par cette action, qui
    /// vaut un appui sur START.
    /// </summary>
    private const string JeuCommence = "GAME_START";

    /// <summary>
    /// LE DEPART DE LA PARTIE, LU DANS LA MEMOIRE DU JEU (2026-10-02) : un credit consomme, ou
    /// l'action GAME_START du .MEM. L'enregistreur de replay ne partait que sur le START lu par le
    /// lecteur de manettes de l'API ; une borne jouee au clavier, ou dont la manette n'est pas lue,
    /// n'enregistrait plus rien alors que son score partait (player : un replay sur vingt-cinq
    /// parties). Le credit consomme tombe au START meme ; l'enregistreur le prend comme tel.
    /// </summary>
    private void AnnoncerLeDepart(string source)
    {
        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "scoring.partie.depart",
            Payload = new { Source = source },
        });
    }

    private void AppliquerEtat(string action)
    {
        if (action.Length == 0) return;
        if (action == JeuCommence)
        {
            AnnoncerLeDepart("GAME_START");
            SortirDeDemo();
            return;
        }
        double? appuiIlYA = null;
        lock (_sync)
        {
            var maintenant = DateTime.UtcNow;
            if (action.Contains("DEMO", StringComparison.Ordinal)
                && DemoDouteuse(_enJeu, _inDemo, _gameOverVu, _dernierAppuiUtc, maintenant))
            {
                // Le joueur appuyait il y a un instant : on attend de voir s'il continue.
                if (_demoEnSuspens is null)
                {
                    _demoEnSuspens = (maintenant, _lastFrame);
                    appuiIlYA = (maintenant - _dernierAppuiUtc).TotalSeconds;
                }
            }
            else
            {
                (_inDemo, _enJeu) = EtatsApres(_inDemo, _enJeu, action);
                if (action.Contains("GAME_OVER", StringComparison.Ordinal)) { _gameOverVu = true; _gameOverDansLaPartie = true; _demoEnSuspens = null; }
            }
        }
        if (appuiIlYA is { } s)
            Trace($"signal {action} en pleine partie (dernier appui il y a {s:0.0} s) : en attente des appuis qui suivent");
    }

    /// <summary>
    /// UN SIGNAL DE DEMO EN PLEINE PARTIE EST IGNORE QUAND LE JOUEUR APPUIE AVANT ET APRES (regle user
    /// 2026-10-06). Une demo se joue seule : personne n'appuie. Le .MEM d'Altered Beast declarait
    /// DEMO_MODE sur l'octet du niveau (« Gameplay Stage 2 ») : chez un joueur dont l'API ne voyait ni
    /// le START ni le credit, tout ce qui suivait le niveau 1 partait en demo (player, 15 parties
    /// perdues et 17 coupees vers 108 000 en deux jours). Le signal n'est donc cru que si le joueur ne
    /// touche plus a rien dans les AppuiApresDemo qui suivent ; un appui dans ce delai le refute. Apres
    /// un depart vu (START, credit, GAME_START), un DEMO_MODE etait deja ignore : ce garde-fou couvre
    /// la borne qui ne voit pas ce depart.
    /// </summary>
    internal static readonly TimeSpan AppuiAvantDemo = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan AppuiApresDemo = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Le signal de demo est-il a mettre en doute ? Pas en jeu ouvert (il y est deja ignore), pas en
    /// demo deja, pas apres un GAME_OVER (la demo qui suit est vraie), et un appui recent.
    /// </summary>
    internal static bool DemoDouteuse(bool enJeu, bool dejaEnDemo, bool gameOverVu, DateTime dernierAppui, DateTime maintenant)
        => !enJeu && !dejaEnDemo && !gameOverVu && maintenant - dernierAppui <= AppuiAvantDemo;

    /// <summary>Un appui a cette heure refute-t-il la demo en suspens depuis cette heure-la ?</summary>
    internal static bool DemoRefutee(DateTime depuis, DateTime appui)
        => appui >= depuis && appui - depuis <= AppuiApresDemo;

    /// <summary>La demo en suspens se confirme-t-elle : aucun appui depuis, et le delai ecoule ?</summary>
    internal static bool DemoConfirmee(DateTime depuis, DateTime dernierAppui, DateTime maintenant)
        => dernierAppui < depuis && maintenant - depuis > AppuiApresDemo;

    /// <summary>Un appui du joueur, de quelque source que ce soit.</summary>
    private void NoterAppui()
    {
        var refutee = false;
        lock (_sync)
        {
            var maintenant = DateTime.UtcNow;
            _dernierAppuiUtc = maintenant;
            if (_demoEnSuspens is { } s && DemoRefutee(s.Depuis, maintenant))
            {
                _demoEnSuspens = null;
                _demosIgnorees++;
                refutee = true;
            }
        }
        if (refutee) Trace("signal de demo ignore : le joueur appuie avant et apres, la partie continue");
    }

    /// <summary>
    /// Appele sous _sync. La demo en suspens qui se confirme : on entre en demo, et les lectures prises
    /// depuis le signal sortent de la trajectoire (une demo n'est jamais certifiee).
    /// </summary>
    private int ConfirmerLaDemoSiSilence(DateTime maintenant)
    {
        if (_demoEnSuspens is not { } s || !DemoConfirmee(s.Depuis, _dernierAppuiUtc, maintenant)) return -1;
        _demoEnSuspens = null;
        _inDemo = true;
        var retirees = 0;
        while (_trajectory.Count > 0 && _trajectory[^1].frame >= s.Frame)
        {
            _trajectory.RemoveAt(_trajectory.Count - 1);
            _horsJeu.RemoveAt(_horsJeu.Count - 1);
            _contextes.RemoveAt(_contextes.Count - 1);
            retirees++;
        }
        _scoresEnDemo += retirees;
        _finalTotal = _trajectory.Count > 0 ? _trajectory[^1].total : null;
        return retirees;
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
    /// <summary>
    /// La partie commence au score qu'avait le jeu au depart, s'il est connu et qu'aucune lecture
    /// en jeu ne le redit deja.
    /// </summary>
    internal static List<(long frame, long total)> AvecLeScoreAuDepart(
        List<(long frame, long total)> trajectoire, (long Frame, long Total)? depart)
    {
        if (depart is not { } d || trajectoire.Count == 0) return trajectoire;
        if (trajectoire[0].frame < d.Frame || trajectoire[0].total == d.Total) return trajectoire;
        var avec = new List<(long frame, long total)>(trajectoire.Count + 1) { (d.Frame, d.Total) };
        avec.AddRange(trajectoire);
        return avec;
    }

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
    /// retenu reste le meilleur run.
    ///
    /// UN CREDIT CONSOMME FAIT AUSSI SORTIR DE LA DEMO (2026-10-02). Sans START lu au panel (borne
    /// jouee au clavier, manette non lue, manette reseau du labo) ni GAME_START dans le .MEM, toute
    /// la partie restait marquee demo et aucun score ne partait : Double Dragon au labo, 24 scores
    /// recus, 24 comptes en demo. Le credit ne se consomme qu'au depart d'une vraie partie.
    /// </summary>
    private void SortirDeDemo()
    {
        // Un START ouvre la partie : fin de la démo, et début de la fenêtre de jeu.
        lock (_sync)
        {
            if (!_enJeu && _scoreAuDepart is null && _dernierTotalVu is { } auDepart) _scoreAuDepart = (_lastFrame, auDepart);
            _inDemo = false; _startVu = true; _enJeu = true;
            _gameOverVu = false;
            _gameOverDansLaPartie = false;
            _demoEnSuspens = null;
        }
    }

    /// <summary>Une seconde d'appuis par port, du wrapper (0.340) : de quoi retrouver le port local.</summary>
    private void CapturePorts(JsonElement root)
    {
        if (!root.TryGetProperty("Presses", out var p) || p.ValueKind != JsonValueKind.Array) return;
        var appuis = p.EnumerateArray().Select(v => v.TryGetInt32(out var n) ? n : 0).ToArray();
        if (appuis.Length > 0) _portLocal.SecondeDuWrapper(appuis);
        // Le wrapper voit les entrees que RetroArch donne au jeu : clavier compris, manette non lue
        // par l'API comprise.
        if (appuis.Any(n => n > 0)) NoterAppui();
    }

    private void CaptureStart(JsonElement root)
    {
        _panelLu = true;
        if ((Entier(root, "Player") ?? Entier(root, "player") ?? 1) == 1) _portLocal.AppuiDuPanel();
        NoterAppui();
        var systeme = GetString(root, "System") ?? GetString(root, "system") ?? "";
        if (!string.Equals(systeme, "START", StringComparison.OrdinalIgnoreCase)) return;
        var joueur = Entier(root, "Player") ?? Entier(root, "player") ?? 1;
        // Le START lu au panel ouvre la partie : le dire au journal, c'est ce qui manquait pour voir
        // qu'une borne ne le lisait plus (player, depuis le 2026-09-30, sans que rien ne le montre).
        Trace($"START lu au panel (joueur {joueur})");
        lock (_sync)
        {
            _departs.Add(new DepartDeJoueur(joueur, _lastFrame));
            if (_departs.Count > MaxTrajectory) _departs.RemoveAt(0);
        }
        SortirDeDemo();
    }

    /// <summary>
    /// Le compteur de credits. Le premier credit consomme de la session est le depart ; tout credit
    /// consomme ensuite est un continue, sauf si un joueur 2 arrive au meme moment
    /// (ContinuesParCredits). Le joueur l'apprend a chaque fois, et le replay s'arrete au premier.
    /// La decision de la soumission se refait en fin de session, sur tout.
    /// </summary>
    private void CaptureCredits(JsonElement root)
    {
        if (!root.TryGetProperty("signal", out var signal) && !root.TryGetProperty("Signal", out signal)) return;
        var nom = (GetString(signal, "Name") ?? "").Trim();
        if (!nom.Equals("CREDITS", StringComparison.OrdinalIgnoreCase)) return;
        if (Entier(signal, "Value") is not { } valeur) return;
        var frame = Entier(signal, "Frame") ?? _lastFrame;
        int session;
        long scoreAvant;
        lock (_sync)
        {
            var avant = _credits.Count > 0 ? _credits[^1].Value : (int?)null;
            _credits.Add(new EvenementDeCredit(valeur, frame));
            if (_credits.Count > MaxTrajectory) _credits.RemoveAt(0);
            if (avant is null || valeur >= avant) return;   // premiere lecture, ou pieces ajoutees
            scoreAvant = _finalTotal ?? 0;
            session = _jetonDeSession;
        }
        AnnoncerLeDepart("credit consomme");
        SortirDeDemo();

        Trace($"credit consomme (score {scoreAvant}, frame {frame}) : depart, continue ou arrivee d'un joueur, tranche dans 3,5 s");
        _ = ConfirmerCreditAsync(session, scoreAvant, frame);
    }

    /// <summary>
    /// A CHAQUE START QUI CONSOMME UN CREDIT, LE JOUEUR SAIT SI LA PARTIE QUI VIENT COMPTE (decision
    /// user du 2026-09-30). Le premier continue dit le score certifie ; chaque credit consomme
    /// ensuite redit que la partie n'est pas certifiable, et qu'il faut quitter puis relancer le jeu
    /// pour une partie certifiee. L'ARRIVEE D'UN JOUEUR N'EST PAS UN CONTINUE : c'est le seul debit
    /// que le 1CC MULTI permettra en plus du depart. Son bandeau dit seulement que la partie sort du
    /// classement solo, une fois (joueur 2, au depart compris) ; les joueurs suivants, rien. On
    /// attend 3,5 s pour voir arriver ce joueur (ContinuesParCredits.FenetreArrivee).
    /// </summary>
    private async Task ConfirmerCreditAsync(int session, long scoreAvant, long frame)
    {
        await Task.Delay(ConfirmationContinue).ConfigureAwait(false);
        BandeauDeCredit bandeau;
        ContinuesParCredits.Debit nature;
        var premiereFin = false;
        var multi = false;
        DateTime debut;
        lock (_sync) { debut = _debutSessionUtc; }
        var ouverte = RetroBat.Api.Netplay.NetplayHostService.OuverteAuxJoueursDepuis(debut - FenetreHebergement);
        lock (_sync)
        {
            if (session != _jetonDeSession) return;   // une autre session a commence
            if (_invite != RetroBat.Api.Netplay.NetplayGuestService.Role.Aucun) return;   // la partie de l'hote
            // Les confirmations passent dans l'ordre des credits (meme delai pour toutes) : chacune
            // voit ce que les precedentes ont tranche.
            nature = ContinuesParCredits.Nature(frame, _departAuCredit, _departs, ouverte);
            if (nature is ContinuesParCredits.Debit.Depart or ContinuesParCredits.Debit.ArriveeDUnJoueur)
            {
                _departAuCredit = true;
            }

            // Un continue ou l'arrivee d'un joueur ferme le 1CC solo : la premiere fois seulement.
            var arrivee = nature is ContinuesParCredits.Debit.ArriveeDUnJoueur or ContinuesParCredits.Debit.ArriveeDistante;
            if (nature == ContinuesParCredits.Debit.Continue || arrivee)
            {
                premiereFin = !_closeParCredit;
                _closeParCredit = true;
            }

            bandeau = QuelBandeau(nature == ContinuesParCredits.Debit.Depart, arrivee, premiereFin, _partieADeuxAnnoncee, scoreAvant > 0);
            if (arrivee) _partieADeuxAnnoncee = true;
            if (bandeau == BandeauDeCredit.JoueurRejoint) _replayDuSolo = _activeReplayId;
            multi = arrivee && premiereFin;
        }

        Trace($"credit consomme a la frame {frame} : {nature}{(ouverte ? " (partie ouverte aux joueurs)" : "")}");
        AppliquerBandeau(bandeau, scoreAvant, multi);
    }

    /// <summary>
    /// Ce que le joueur voit d'un credit consomme ou d'une arrivee, et ce qu'en font les replays :
    /// celui du 1CC solo s'arrete, celui du 1CC MULTI commence.
    /// </summary>
    private void AppliquerBandeau(BandeauDeCredit bandeau, long scoreAvant, bool multi)
    {
        switch (bandeau)
        {
            case BandeauDeCredit.ScoreCertifie:
                AnnoncerContinue(scoreAvant, parCredit: true);
                break;
            case BandeauDeCredit.NonCertifiable:
                AnnoncerCredit("scoring_uncertified", "partie non certifiable");
                break;
            case BandeauDeCredit.PartieAPlusieurs:
                AnnoncerCredit("scoring_multiplayer", "un joueur arrive, partie hors classement solo");
                break;
            case BandeauDeCredit.JoueurRejoint:
                AnnoncerRejoint(scoreAvant);
                break;
        }

        // Le replay montre la partie certifiee : il s'arrete ou finit le 1CC solo.
        if (bandeau is BandeauDeCredit.ScoreCertifie or BandeauDeCredit.JoueurRejoint)
        {
            PublierFinDeRun(bandeau == BandeauDeCredit.JoueurRejoint ? "arrivee d'un joueur" : "credit consomme");
        }

        // Et celui du 1CC MULTI commence : relance apres le replay solo, ou le replay en cours
        // devient le sien quand rien n'a ete fait seul (depart a deux).
        if (multi) PublierPartieMulti(relancer: bandeau == BandeauDeCredit.JoueurRejoint);
    }

    internal enum BandeauDeCredit
    {
        /// <summary>Le depart d'une partie solo : elle compte, rien a dire.</summary>
        Aucun,
        /// <summary>Le premier continue : le score certifie, et la suite qui ne compte plus.</summary>
        ScoreCertifie,
        /// <summary>Tout credit consomme ensuite : la partie qui vient n'est pas certifiable.</summary>
        NonCertifiable,
        /// <summary>L'arrivee d'un joueur 2 : hors classement solo, sans etre un continue.</summary>
        PartieAPlusieurs,
        /// <summary>L'arrivee d'un joueur apres un score fait seul : ce score reste un 1CC.</summary>
        JoueurRejoint,
    }

    /// <summary>Le bandeau d'un credit consomme, selon ce qu'il ouvre.</summary>
    internal static BandeauDeCredit QuelBandeau(bool depart, bool arrivee, bool premiereFin, bool dejaADeux, bool scoreSolo)
    {
        if (arrivee)
        {
            if (dejaADeux) return BandeauDeCredit.Aucun;
            return premiereFin && scoreSolo ? BandeauDeCredit.JoueurRejoint : BandeauDeCredit.PartieAPlusieurs;
        }

        if (depart) return BandeauDeCredit.Aucun;
        return premiereFin && !dejaADeux ? BandeauDeCredit.ScoreCertifie : BandeauDeCredit.NonCertifiable;
    }

    /// <summary>Un joueur arrive : le score fait seul jusque-la reste un 1CC, et le joueur le voit.</summary>
    private void AnnoncerRejoint(long avant)
    {
        if (!PremierBandeauDeCoupe()) return;
        var langue = Langue();
        _overlay?.ShowTop(
            "SCORING",
            string.Format(CabinetAnnounceText.Get("scoring_joined_title", langue), ScoreAffiche(avant, langue)),
            CabinetAnnounceText.Get("scoring_joined_sub", langue),
            8000,
            alerte: true);
        Trace($"bandeau : un joueur arrive, score solo certifie {avant}, la suite a plusieurs");
    }

    /// <summary>Un bandeau d'information sans score : la partie continue, rien n'est refuse.</summary>
    private void AnnoncerCredit(string cle, string trace)
    {
        // Hors NelfePlay, et sous l'atelier de NelfeScoreLab, aucun bandeau : rien n'est mesure.
        if (!PartieNelfePlay()) return;
        if (!PremierBandeauDeCoupe()) return;
        var langue = Langue();
        _overlay?.ShowTop(
            "SCORING",
            CabinetAnnounceText.Get(cle + "_title", langue),
            CabinetAnnounceText.Get(cle + "_sub", langue),
            8000,
            alerte: true);
        Trace($"bandeau : {trace}");
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

    /// <summary>
    /// LES CREUX DE PASSAGE (2026-10-09). Un score BCD tient sur plusieurs octets : quand une retenue
    /// les change tous et que l'image se termine entre deux ecritures, le wrapper lit un total de
    /// passage plus BAS, puis le vrai a l'image suivante. MaxGlitchTail retirait les pics de passage ;
    /// un creux, lui, ouvrait un nouveau run. Alex Kidd in Miracle World : meilleur run de 2 lectures
    /// sur 17, la premiere vie perdue (600 points) restait hors du run, et le 1LC prenait le score de
    /// la deuxieme (4 400). Une ou deux lectures plus basses, suivies en moins de CreuxMaxImages
    /// images d'une lecture qui retrouve au moins le niveau d'avant, sont un creux de passage : on les
    /// retire. Une vraie nouvelle partie repart de zero et met bien plus longtemps a y revenir.
    /// </summary>
    internal static List<(long frame, long total)> SansCreux(List<(long frame, long total)> traj)
    {
        if (traj.Count < 3) return traj;
        var sortie = new List<(long frame, long total)>(traj.Count);
        var i = 0;
        while (i < traj.Count)
        {
            if (sortie.Count > 0 && traj[i].total < sortie[^1].total)
            {
                var niveau = sortie[^1].total;
                var reprise = -1;
                for (var j = i + 1; j <= i + MaxGlitchTail && j < traj.Count; j++)
                {
                    if (traj[j].total < niveau) continue;
                    if (traj[j].frame - traj[i].frame <= CreuxMaxImages) reprise = j;
                    break;
                }
                if (reprise > 0)
                {
                    i = reprise;
                    continue;
                }
            }
            sortie.Add(traj[i]);
            i++;
        }

        return sortie;
    }

    /// <summary>Un creux de passage dure une image ; une demi-seconde laisse de la marge.</summary>
    private const long CreuxMaxImages = 30;

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
        traj = SansCreux(traj);
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
        // Le pont Lua de MAME porte la frame de chaque lecture depuis mame-lua-0.3.2 : la coupe y
        // tombe comme sous RetroArch. Des lectures toutes a la frame 0 (pont plus ancien) ne
        // laissent rien tomber « entre » deux lectures : rien n'est coupe.
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
        // Le segment en cours a ete ouvert par une chute du score : une nouvelle partie.
        var ouvertParChute = false;
        foreach (var pt in traj)
        {
            // Un continue est tombe apres la lecture precedente et au plus tard sur celle-ci : la
            // lecture precedente est le dernier score du premier credit, et celle-ci ouvre un autre
            // run. Une lecture PILE sur la trame du continue appartient au nouveau credit : Capcom
            // y ajoute son +1 de continue (19xx, 2026-09-25 : 36 500 puis 36 801 a la trame meme
            // du continue, et 36 801 avait ete certifie).
            var coupe = fins is not null && framePrecedente is long fp && cur.Count > 0
                && fins.Any(f => f > fp && f <= pt.frame);
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
                //
                // LE DEPART D'UNE NOUVELLE PARTIE N'EST PAS UN CONTINUE. Une nouvelle partie remet
                // le score a zero PUIS donne ses vies : la chute a deja ouvert le segment, et les
                // vies qui remontent ensuite sont celles du depart. Comparer au 0 de la remise a
                // zero faisait passer chaque nouvelle partie pour une partie continuee : Double
                // Dragon (une seule vie), trois parties d'affilee, seule la premiere concourait et
                // c'est elle qui a ete soumise, pas la meilleure (signale le 2026-09-27).
                var departDePartie = ouvertParChute && cur.All(p => p.total == cur[0].total);
                reporte = !departDePartie && peakFin != long.MinValue && peakFin > 0 && pt.total >= peakFin;
                ouvertParChute = false;
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
                cur.Add(pt);
                prev = pt.total;
                ouvertParChute = true;
                continue;
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
            // LES VIES NE COUPENT PLUS UN 1CC (charte, decision du 2026-09-30) : une vie bonus
            // passait pour un continue (Double Dragon coupe a 29 960 pour 41 520 au premier credit).
            // Elles restent relevees, pour le 1LC et pour le diagnostic.
            return;
        }
    }

    /// <summary>
    /// Le mode et la difficulte choisis. Pris en demo comme en jeu : c'est la valeur en vigueur au
    /// debut du run retenu qui compte (ModesDeJeu.ContexteDuRun), et le menu ou le joueur choisit
    /// se trouve souvent entre deux demos.
    /// </summary>
    private void CaptureModeEtDifficulte(JsonElement root)
    {
        if (!root.TryGetProperty("signal", out var signal) && !root.TryGetProperty("Signal", out signal)) return;
        var nom = (GetString(signal, "Name") ?? "").Trim().ToUpperInvariant();
        var mode = nom == RetroBat.Api.Scoring.ModesDeJeu.SignalMode;
        if (!mode && nom != RetroBat.Api.Scoring.ModesDeJeu.SignalDifficulte) return;
        if (Entier(signal, "Value") is not { } valeur) return;
        string trace;
        lock (_sync)
        {
            if (mode)
            {
                // L'octet lu, et sa valeur par adresse : un jeu a drapeaux se relit en fin de partie
                // avec ses profils (ModesDeJeu.ModesParDrapeaux).
                var adresse = (GetString(signal, "Address") ?? "").Trim();
                if (adresse.Length > 0) _contexte = _contexte.AvecDrapeau(adresse, valeur);
                _contexte = _contexte.AvecMode(valeur);
                trace = adresse.Length > 0
                    ? $"mode de jeu : {RetroBat.Api.Scoring.ModesDeJeu.Adresse(adresse)} = {valeur} (0x{valeur:X2})"
                    : $"mode de jeu : {valeur} (0x{valeur:X2})";
            }
            else
            {
                var adresse = (GetString(signal, "Address") ?? "").Trim();
                if (adresse.Length == 0) return;
                _contexte = _contexte.AvecDifficulte(adresse, valeur);
                trace = $"difficulte : {RetroBat.Api.Scoring.ModesDeJeu.Adresse(adresse)} = {valeur}";
            }
        }
        Trace(trace);
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

    /// <summary>
    /// LE SCORE D'UN AUTRE JOUEUR NE SE MELE PAS A CELUI DU JOUEUR 1. L'agregateur publie un score
    /// par joueur (ligne `player=2` du .MEM) ; la trajectoire certifiee est celle du joueur 1, et y
    /// verser les valeurs du joueur 2 dessinait des baisses et des remontees qui ne sont a personne.
    /// Ils sont gardes a part pour le 1CC MULTI (charte de la partie certifiee, 2026-09-30).
    /// </summary>
    internal static int JoueurDuScore(JsonElement root)
        => root.TryGetProperty("Player", out var p) && p.TryGetInt32(out var joueur) && joueur is >= 1 and <= 4 ? joueur : 1;

    private readonly Dictionary<int, long> _scoresAutresJoueurs = new();

    /// <summary>La frame ou le wrapper a coupe la ligne des credits : les continues ne se voient plus.</summary>
    private long? _creditsMuets;

    /// <summary>La frame ou l'hote a quitte la partie rejointe : la place de l'invite s'arrete la.</summary>
    private long? _hotePartiFrame;

    /// <summary>Les lignes CONTINUES du .MEM (compteur console, etat d'un joueur Neo-Geo), au fil de la session.</summary>
    private readonly List<LectureDeContinues> _continuesConsole = new();

    /// <summary>Le .MEM en vigueur pour la partie, tel que le pont le nomme (.contest, .user ou officiel).</summary>
    private string? _definitionChargee;

    private void CaptureDefinitionChargee(JsonElement root)
    {
        var chemin = GetString(root, "DefinitionFile");
        if (string.IsNullOrEmpty(chemin) || string.Equals(chemin, _definitionChargee, StringComparison.Ordinal)) return;
        lock (_sync) { _definitionChargee = chemin; }
    }

    /// <summary>Les compteurs de code de continue du .MEM charge, relus quand le .MEM change.</summary>
    private HashSet<string> CompteursDeCodeDuMem()
    {
        string? chemin;
        (string? Chemin, HashSet<string> Adresses) lu;
        lock (_sync) { chemin = _definitionChargee; lu = _compteursDeCode; }
        if (string.Equals(lu.Chemin, chemin, StringComparison.Ordinal)) return lu.Adresses;
        var adresses = ContinuesParCompteur.CompteursDeCode(LireMem(chemin));
        lock (_sync) { _compteursDeCode = (chemin, adresses); }
        return adresses;
    }

    /// <summary>
    /// UN CONTINUE CONSOLE : le compteur de continues du jeu baisse (ContinuesParCompteur). Le premier
    /// dit le score certifie et arrete le replay ; les suivants redisent que la partie n'est plus
    /// certifiable. Pas d'attente ici : le compteur dit lui-meme a quel joueur il est.
    ///
    /// UNE LIGNE PAR JOUEUR (Neo-Geo, 2026-10-02) : la ligne qui porte player=2 ou plus est celle
    /// d'un autre joueur. Sa premiere lecture est son arrivee, qui ferme le 1CC solo comme un credit
    /// consomme avec son START ; ses baisses sont SES continues : ils ne coupent rien ici, ils
    /// arretent son 1CC MULTI, compte en fin de partie.
    /// </summary>
    private void CaptureContinuesConsole(JsonElement root)
    {
        if (!root.TryGetProperty("signal", out var signal) && !root.TryGetProperty("Signal", out signal)) return;
        var nom = (GetString(signal, "Name") ?? "").Trim();
        if (!nom.Equals("CONTINUES", StringComparison.OrdinalIgnoreCase)) return;
        if (Entier(signal, "Value") is not { } valeur) return;
        var frame = Entier(signal, "Frame") ?? _lastFrame;
        var joueur = Entier(signal, "Player") ?? Entier(root, "player") ?? 0;
        if (joueur is < 0 or > 4) joueur = 0;
        var premiere = false;
        var arrivee = false;
        var multi = false;
        var bandeau = BandeauDeCredit.Aucun;
        var adresse = (GetString(signal, "Address") ?? "").Trim();
        var compteursDeCode = CompteursDeCodeDuMem();
        long scoreAvant;
        lock (_sync)
        {
            int? avant = null;
            for (var i = _continuesConsole.Count - 1; i >= 0; i--)
            {
                if (_continuesConsole[i].Player != joueur) continue;
                avant = _continuesConsole[i].Value;
                break;
            }
            // UN ACHAT N'EST PAS UN CONTINUE (2026-10-09) : la baisse d'un compteur de code de continue
            // ne compte qu'apres un Game Over de cette partie. La lecture n'est pas gardee : le calcul
            // de fin de partie relit cette liste, il ne doit pas la voir non plus.
            if (joueur < 2 && avant is { } precedente && valeur < precedente
                && !ContinuesParCompteur.BaisseRecevable(adresse, compteursDeCode, _gameOverDansLaPartie))
            {
                Trace($"code de continue {adresse} : {precedente} puis {valeur} (frame {frame}) sans Game Over dans la partie, "
                    + "ce n'est pas un continue (l'octet sert aussi ailleurs, la boutique d'Alex Kidd) : le 1CC continue");
                return;
            }
            _continuesConsole.Add(new LectureDeContinues(joueur, valeur, frame));
            if (_continuesConsole.Count > MaxTrajectory) _continuesConsole.RemoveAt(0);
            if (_invite != RetroBat.Api.Netplay.NetplayGuestService.Role.Aucun) return;
            if (joueur >= 2)
            {
                if (avant is not null)
                {
                    if (valeur >= avant) return;
                    scoreAvant = 0;   // le continue d'un autre joueur : rien a couper ici
                }
                else
                {
                    arrivee = true;
                    scoreAvant = ContinuesParCompteur.MeilleurAvant(_trajectory, _horsJeu, frame);
                    premiere = !_closeParCredit;
                    _closeParCredit = true;
                    bandeau = QuelBandeau(depart: false, arrivee: true, premiere, _partieADeuxAnnoncee, scoreAvant > 0);
                    _partieADeuxAnnoncee = true;
                    if (bandeau == BandeauDeCredit.JoueurRejoint) _replayDuSolo = _activeReplayId;
                    multi = premiere;
                }
            }
            else
            {
                if (avant is null || valeur >= avant) return;   // premiere lecture, ou continue gagne
                premiere = !_closeParCredit;
                _closeParCredit = true;
                scoreAvant = ContinuesParCompteur.MeilleurAvant(_trajectory, _horsJeu, frame);
            }
        }

        if (joueur >= 2)
        {
            if (!arrivee)
            {
                Trace($"continue du joueur {joueur} (etat {valeur}, frame {frame}) : son 1CC MULTI s'arrete la, rien ne change pour le joueur 1");
                return;
            }
            Trace($"arrivee du joueur {joueur} (sa ligne CONTINUES parle, frame {frame}) : score fait seul {scoreAvant}, la suite a plusieurs");
            AppliquerBandeau(bandeau, scoreAvant, multi);
            return;
        }

        Trace($"continue console (compteur {valeur}, frame {frame}) : score certifie {scoreAvant}");
        if (premiere)
        {
            AnnoncerContinue(scoreAvant);
            PublierFinDeRun("continue (compteur du jeu)");
        }
        else
        {
            AnnoncerCredit("scoring_uncertified", "partie non certifiable (continue console)");
        }
    }

    /// <summary>
    /// LA LIGNE DES CREDITS COUPEE PAR LE WRAPPER : plus aucun continue ne se voit. Le wrapper fait
    /// taire une ligne hors score qui change huit fois de suite a moins de 20 images d'ecart, et
    /// neuf pieces enchainees suffisent (charte de la partie certifiee, 2026-09-30). Le 1CC s'arrete
    /// la : ce qui a ete joue avant reste certifie, la suite ne l'est plus, et le joueur le sait tout
    /// de suite. Le plus souvent, c'est avant meme le depart : il relance, rien n'est perdu.
    /// </summary>
    private void CaptureSurveillanceCoupee(JsonElement root)
    {
        var evenement = GetString(root, "Evenement") ?? "";
        if (!evenement.Equals("credits", StringComparison.OrdinalIgnoreCase)) return;
        long frame;
        lock (_sync)
        {
            if (_creditsMuets is not null || _invite == RetroBat.Api.Netplay.NetplayGuestService.Role.Spectateur) return;
            _creditsMuets = frame = _lastFrame;
        }

        Trace($"la ligne des credits a ete coupee par le wrapper (changements trop rapides, frame {frame}) : les continues ne se voient plus, fin du 1CC");
        AnnoncerCredit("scoring_credits_muted", "credits illisibles, partie non certifiable");
        PublierFinDeRun("credits illisibles");
    }

    private void CaptureTotal(JsonElement root)
    {
        if (!root.TryGetProperty("Score", out var s) || !s.TryGetInt64(out var total)) return;
        var joueurDuScore = JoueurDuScore(root);
        if (joueurDuScore != 1)
        {
            lock (_sync)
            {
                _scoresAutresJoueurs[joueurDuScore] = total;
                if (!_trajectoiresAutres.TryGetValue(joueurDuScore, out var lectures))
                {
                    _trajectoiresAutres[joueurDuScore] = lectures = new List<(long, long)>();
                }
                if (lectures.Count == 0 || lectures[^1].total != total)
                {
                    lectures.Add((_lastFrame, total));
                    if (lectures.Count > MaxTrajectory) lectures.RemoveAt(0);
                }
            }
            return;
        }
        long? continueAnnonce = null;
        var nouvellePartie = false;
        var departParLeScore = false;
        var arretParBaisse = false;
        lock (_sync)
        {
            _scoresRecus++;
            _dernierTotalVu = total;
            // Rare, et une ligne de journal : sous le verrou, c'est sans consequence.
            var demoConfirmee = ConfirmerLaDemoSiSilence(DateTime.UtcNow);
            if (demoConfirmee >= 0) Trace($"signal de demo confirme : plus d'appui depuis, {demoConfirmee} lecture(s) ecartee(s)");
            if (_inDemo) { _scoresEnDemo++; return; }   // score de démo → jamais certifié
            var precedent = _finalTotal;
            _finalTotal = total;
            // Le score d'avant la salve en cours : les totaux de passage d'une meme image (un chiffre
            // ecrit avant l'autre) ne font ni montee ni baisse.
            var (reference, nouvelleSalve) = _salves.Lire(precedent, DateTime.UtcNow);

            // LE SCORE PILOTE LE REPLAY (voir _enregistrementEnCours). Hors demo : on est passe au-dela
            // du retour anticipe des scores de demo.
            if (reference is { } avantScore)
            {
                if (total > avantScore)
                {
                    if (_enregistrementEnCours)
                    {
                        _monteeDansLEnregistrement = true;
                    }
                    else if (_attenteMontee && !_panelLu && _departsParScore < MaxDepartsParScore)
                    {
                        _attenteMontee = false;
                        _departsParScore++;
                        departParLeScore = true;
                    }
                }
                else if (total < avantScore)
                {
                    _attenteMontee = true;
                }
            }

            if (_baisse.Lire(total, reference, nouvelleSalve, _enregistrementEnCours, _monteeDansLEnregistrement))
            {
                arretParBaisse = true;
                _monteeDansLEnregistrement = false;
            }

            // LE SCORE RETOMBE : une nouvelle partie commence, pour un jeu a chiffre des credits
            // (le continue y garde le score). Si la partie d'avant a ete close par ce chiffre, tout
            // se rearme pour celle-ci (bandeau, fin de partie, replay). Une partie close par un
            // CREDIT ne se rearme pas : la session fait foi, et Double Dragon remet le score a zero
            // au continue meme (labo du 2026-09-30).
            if (reference is { } avantChute && total < avantChute)
            {
                // Une autre partie : son code de continue attendra son propre Game Over.
                _gameOverDansLaPartie = false;
                if (_finDeRunPubliee && !_closeParCredit)
                {
                    _finDeRunPubliee = false;
                    _avantContinue = null;
                    _dernierPropre = null;
                    nouvellePartie = true;
                }
            }
            // Le total agrégé à la frame courante : la trajectoire vérifiable du score.
            if (_trajectory.Count == 0 || _trajectory[^1].total != total)
            {
                _trajectory.Add((_lastFrame, total));
                _horsJeu.Add(!_enJeu);
                _contextes.Add(_contexte);
                if (_trajectory.Count > MaxTrajectory) { _trajectory.RemoveAt(0); _horsJeu.RemoveAt(0); _contextes.RemoveAt(0); }
            }

            // Le premier +1 d'un jeu a chiffre des credits : c'est le continue.
            if (_chiffreCredits && _avantContinue is null && (!_startVu || _enJeu))
            {
                if (EstUnContinue(_dernierPropre, _derniereLecture, total))
                {
                    _avantContinue = _dernierPropre;
                    continueAnnonce = _dernierPropre;
                }
                else if (total % 10 == 0 && total > 0 && (_dernierPropre is null || total > _dernierPropre))
                {
                    _dernierPropre = total;
                }

                _derniereLecture = total;
            }
        }
        if (departParLeScore) AnnoncerLeDepart("score qui monte");
        if (arretParBaisse)
        {
            Trace($"le score a baisse apres avoir monte, et reste bas ({total}) : fin de l'enregistrement en cours");
            _ = _eventBus.PublishAsync(new EventEnvelope { Type = "scoring.replay.stop", Payload = new { Raison = "baisse du score" } });
        }

        if (nouvellePartie)
        {
            Trace($"nouvelle partie (le score retombe a {total}) : replay rearme");
            _ = _eventBus.PublishAsync(new EventEnvelope { Type = "scoring.run.reset", Payload = new { Score = total } });
        }

        if (continueAnnonce is { } avant && _invite == RetroBat.Api.Netplay.NetplayGuestService.Role.Aucun)
        {
            lock (_sync) { _closeParCredit = true; }
            AnnoncerContinue(avant);
            PublierFinDeRun("chiffre des credits");
        }
    }

    /// <summary>
    /// Dit au replay que la partie certifiee est finie : l'enregistrement s'arrete au continue.
    /// Une seule fois par partie, et seulement pour une partie NelfePlay.
    /// </summary>
    private void PublierFinDeRun(string source)
    {
        long frame;
        lock (_sync)
        {
            if (_finDeRunPubliee) return;
            _finDeRunPubliee = true;
            frame = _lastFrame;
        }

        if (!PartieNelfePlay()) return;
        Trace($"fin de la partie certifiee ({source}, frame {frame}) : arret du replay");
        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "scoring.run.ended",
            Payload = new { Raison = "continue", Source = source, Frame = frame },
        });
    }

    /// <summary>
    /// Dit au replay que la suite de la partie est le 1CC MULTI (demande user 2026-09-30) : un
    /// nouvel enregistrement apres celui du 1CC solo, ou celui en cours s'il n'y a pas eu de solo.
    /// </summary>
    private void PublierPartieMulti(bool relancer)
    {
        if (!PartieNelfePlay()) return;
        Trace(relancer
            ? "partie a plusieurs : le replay du 1CC MULTI commence apres celui du 1CC solo"
            : "partie a plusieurs des le depart : le replay en cours est celui du 1CC MULTI");
        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "scoring.run.multi",
            Payload = new { Relancer = relancer, Frame = _lastFrame },
        });
    }

    /// <summary>
    /// Les remontees de vies apres zero lues EN DIRECT, seulement quand la decision ne peut plus
    /// changer. FinsDeRun choisit en fin de partie le compteur le plus complet parmi ceux qui ont
    /// parle ; tant qu'un seul compteur a parle, il n'y a rien a choisir. Avec plusieurs compteurs
    /// (un drapeau de boss range dans le bloc des vies, sur Altered Beast), rien : on ne coupe pas
    /// en direct, le replay va au bout comme avant.
    /// </summary>
    internal static int FinsSurLesVies(IReadOnlyList<EvenementDeVie> vies, int joueur = 1)
    {
        var adresses = vies
            .Where(v => v.Player <= 0 || v.Player == joueur)
            .Select(v => v.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return adresses == 1 ? FinsDeRun.Calculer(vies, joueur).Count : 0;
    }

    /// <summary>
    /// Le temps de voir arriver un joueur 2 (ContinuesParCredits.FenetreArrivee, 3 s) avant de dire
    /// ce que vaut un credit consomme. Plus court que les 6 s d'avant : un joueur qui quitte juste apres son continue
    /// ne voyait pas le bandeau (labo Double Dragon du 2026-09-30, sortie 5,5 s apres).
    /// </summary>
    internal static readonly TimeSpan ConfirmationContinue = TimeSpan.FromSeconds(3.5);

    /// <summary>Un hebergement lance jusqu'a cinq minutes avant la partie la concerne encore.</summary>
    private static readonly TimeSpan FenetreHebergement = TimeSpan.FromMinutes(5);


    /// <summary>
    /// Le continue d'un jeu a chiffre des credits : une lecture qui MONTE sans finir par 0, apres
    /// une premiere lecture propre (les lectures parasites du debut, 63 sur Metal Slug 3, ne
    /// comptent pas). Meme regle que la plateforme (ScoringRepository::avantLePremierContinue).
    /// </summary>
    internal static bool EstUnContinue(long? dernierPropre, long? derniereLecture, long total)
        => total % 10 != 0 && dernierPropre is not null && derniereLecture is not null && total > derniereLecture;

    /// <summary>
    /// Le joueur vient de continuer : il le sait tout de suite, et il sait quel score est certifie.
    /// Un bandeau d'information, pas une alerte : la partie continue, rien n'est refuse.
    /// </summary>
    private void AnnoncerContinue(long avant, bool parCredit = false)
    {
        if (!PartieNelfePlay()) return;
        if (!PremierBandeauDeCoupe()) return;
        var langue = Langue();
        var cle = parCredit ? "scoring_credit" : "scoring_continue";
        _overlay?.ShowTop(
            "SCORING",
            string.Format(CabinetAnnounceText.Get(cle + "_title", langue), ScoreAffiche(avant, langue)),
            CabinetAnnounceText.Get(cle + "_sub", langue),
            8000,
            alerte: true);
        Trace($"continue detecte ({(parCredit ? "credit consomme" : "chiffre des credits")}) : score certifie {avant}, la partie continue");
    }

    private DateTime _dernierBandeauDeCoupe = DateTime.MinValue;
    private string _dernierPrevolAffiche = "";
    // Compte les lancements (ui.game.started) : le bandeau du prevol ne se tait que dans le meme.
    private long _lancement;

    /// <summary>La cle d'un prevol affiche : le lancement, le jeu et ce que dit le bandeau.</summary>
    internal static string CleDuPrevol(long lancement, string systemId, string romGroup, string titre, string? detail)
        => $"{lancement}|{systemId}/{romGroup}|{titre}|{detail}";

    /// <summary>
    /// Ce bandeau a-t-il deja ete montre pour CE lancement ? Aucune duree (regle user 2026-10-05) :
    /// le second attesteur d'une partie (pont Lua apres le wrapper) se tait, et chaque nouveau
    /// lancement a son bandeau, aussi vite qu'il suive le precedent.
    /// </summary>
    internal static bool PrevolDejaDit(string cle, string derniereCle) => cle == derniereCle;

    /// <summary>
    /// UN CONTINUE, UN BANDEAU. 19xx a deux temoins du meme continue : le chiffre des credits de
    /// Capcom et la ligne CREDITS. Les deux parlaient, a deux secondes d'ecart (2026-09-30). Un
    /// bandeau de coupe dans les 10 s qui suivent un autre est le meme evenement : il se tait.
    /// </summary>
    private bool PremierBandeauDeCoupe()
    {
        lock (_sync)
        {
            var maintenant = DateTime.UtcNow;
            if (maintenant - _dernierBandeauDeCoupe < TimeSpan.FromSeconds(10))
            {
                Trace("bandeau de coupe deja montre il y a moins de 10 s : meme continue, rien de plus");
                return false;
            }

            _dernierBandeauDeCoupe = maintenant;
            return true;
        }
    }

    /// <summary>Un score lisible sur l'ecran : espaces en francais, points en espagnol, virgules sinon.</summary>
    internal static string ScoreAffiche(long score, string langue)
    {
        var separateur = langue switch { "fr" => " ", "es" => ".", _ => "," };
        var format = new System.Globalization.NumberFormatInfo { NumberGroupSeparator = separateur };
        return score.ToString("#,0", format);
    }

    /// <summary>
    /// La trajectoire sans ce qui suit un continue, d'un jeu a chiffre des credits : de chaque
    /// continue jusqu'a la partie suivante (le score qui retombe), rien ne compte. Une nouvelle
    /// partie jouee ensuite dans la meme session concourt normalement. Entiere sans continue.
    /// </summary>
    internal static List<(long frame, long total)> SansLesContinues(IReadOnlyList<(long frame, long total)> trajectoire)
    {
        var gardees = new List<(long frame, long total)>(trajectoire.Count);
        long? propre = null;
        long? precedente = null;
        var apresContinue = false;
        foreach (var point in trajectoire)
        {
            var total = point.total;
            if (apresContinue)
            {
                if (precedente is { } p && total < p)
                {
                    apresContinue = false;   // le score retombe : une nouvelle partie
                    propre = null;
                }
                else
                {
                    precedente = total;
                    continue;
                }
            }
            else if (EstUnContinue(propre, precedente, total))
            {
                apresContinue = true;
                precedente = total;
                continue;
            }

            gardees.Add(point);
            if (total % 10 == 0 && total > 0 && (propre is null || total > propre)) propre = total;
            precedente = total;
        }

        return gardees;
    }

    // ── Fin de partie : assembler + signer + soumettre ───────────────────────

    private async Task OnSessionAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var systemId = GetString(payload, "SystemId") ?? "";
        var romGroup = GetString(payload, "Rom") ?? "";
        var sessionJson = GetString(payload, "Session");
        Trace($"session reçue sys={systemId} rom={romGroup} sessionLen={sessionJson?.Length ?? -1}");
        int demoAuBilan;
        lock (_sync)
        {
            _finDeLaSession = DateTime.UtcNow;
            demoAuBilan = ConfirmerLaDemoSiSilence(_finDeLaSession);
        }
        if (demoAuBilan >= 0) Trace($"signal de demo confirme en fin de session : {demoAuBilan} lecture(s) ecartee(s)");
        // Une session est arrivee. Elle ne vaut PAS quittance a elle seule : celles d'Altered
        // Beast sous MAME arrivaient vides, sans le moindre score. C'est le chemin de soumission
        // qui decidera, un peu plus bas, s'il y avait quelque chose a mesurer.
        _sessionRecue = true;
        if (sessionJson is null) return;
        if (!PartieNelfePlay())
        {
            Trace(SousAtelier()
                ? "STOP: atelier NelfeScoreLab, rien n'est mesure ni envoye"
                : "STOP: partie lancee hors NelfePlay (ni collection World Scoring, ni fonction NelfePlay)");
            return;
        }
        RetroBat.Api.Netplay.NetplayGuestService.Role roleInvite;
        int? placeInvite;
        string? seanceInvite;
        lock (_sync) { roleInvite = _invite; placeInvite = _placeInvite; seanceInvite = _seanceInvite; }
        if (roleInvite == RetroBat.Api.Netplay.NetplayGuestService.Role.Joueur && placeInvite is { } place)
        {
            // L'INVITE JOUEUR N'A PAS DE 1CC SOLO : il a rejoint une partie commencee. Il a son 1CC
            // MULTI, celui du joueur de sa place (decision user du 2026-09-30).
            var credentialInvite = ResolveCredential();
            if (string.IsNullOrEmpty(credentialInvite))
            {
                Trace("STOP: pas de credential (ni appairé ni anonyme)");
                return;
            }
            await SoumettreMultiAsync(systemId, romGroup, sessionJson, credentialInvite, place, seanceInvite, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (roleInvite != RetroBat.Api.Netplay.NetplayGuestService.Role.Aucun)
        {
            // Le score lu ici est celui du joueur 1, sur la borne de l'hote. Le soumettre sous le
            // nom de cette borne donnait a un spectateur, ou a l'invite, le 1CC de l'hote.
            Trace($"STOP: partie rejointe en netplay ({roleInvite}) : le score est celui de l'hote, rien a soumettre");
            return;
        }
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

        // LES MODES PAR DRAPEAUX (2026-10-06) : Bubble Bobble dit son mode par trois octets 0/1. Les
        // profils du jeu disent les drapeaux de chaque mode ; chaque lecture est relue avec eux avant
        // tout decoupage. Une combinaison sans classement ne se soumet pas.
        bool aDesDrapeaux;
        lock (_sync) aDesDrapeaux = _contextes.Any(c => c.Drapeaux.Count > 0);
        if (aDesDrapeaux)
        {
            var modesParDrapeaux = RetroBat.Api.Scoring.ModesDeJeu.ModesParDrapeaux(
                await FetchProfilesAsync(credential, systemId, romGroup, cancellationToken).ConfigureAwait(false));
            if (modesParDrapeaux.Count > 0)
            {
                lock (_sync)
                {
                    for (var i = 0; i < _contextes.Count; i++)
                        _contextes[i] = RetroBat.Api.Scoring.ModesDeJeu.Resoudre(_contextes[i], modesParDrapeaux);
                    _contexte = RetroBat.Api.Scoring.ModesDeJeu.Resoudre(_contexte, modesParDrapeaux);
                }
                Trace($"modes par drapeaux : {modesParDrapeaux.Count} classement(s), lectures relues");
            }
        }

        // UN JEU A MODES : UN PASSEPORT PAR MODE JOUE (2026-09-29). Trois parties de Tetris, A puis
        // B puis A : une seule etait soumise, la meilleure toutes confondues, et le type B se
        // perdait. Chaque mode a son classement, chacun recoit le meilleur run joue dans ce mode.
        // Un jeu sans modes ne forme qu'un groupe : rien ne change pour lui.
        List<RetroBat.Api.Scoring.ContexteDeJeu> tous;
        lock (_sync) tous = new List<RetroBat.Api.Scoring.ContexteDeJeu>(_contextes);
        if (tous.Any(c => c.Mode is not null))
        {
            var profils = await FetchProfilesAsync(credential, systemId, romGroup, cancellationToken).ConfigureAwait(false);
            var parDefaut = RetroBat.Api.Scoring.ModesDeJeu.ChoisirProfil(profils, null) is { } defaut
                ? RetroBat.Api.Scoring.ModesDeJeu.ModeDuProfil(defaut)
                : null;
            var modes = RetroBat.Api.Scoring.ModesDeJeu.ModesJoues(tous, parDefaut);
            if (modes.Count > 1)
            {
                foreach (var mode in modes)
                {
                    Trace($"mode {(mode?.ToString() ?? "non mesure")} : ses parties seules");
                    await SoumettreAsync(systemId, romGroup, sessionJson, credential,
                        new RetroBat.Api.Scoring.FiltreDeMode(mode, parDefaut), cancellationToken).ConfigureAwait(false);
                }
                await SoumettreMultiSiOuverteAsync(systemId, romGroup, sessionJson, credential, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await SoumettreAsync(systemId, romGroup, sessionJson, credential, null, cancellationToken).ConfigureAwait(false);
        await SoumettreMultiSiOuverteAsync(systemId, romGroup, sessionJson, credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// L'HOTE D'UNE PARTIE OUVERTE AUX JOUEURS : en plus de son 1CC solo (le score fait seul avant
    /// l'arrivee des autres), son 1CC MULTI, sur tout son credit (decision user du 2026-09-30).
    /// </summary>
    private async Task SoumettreMultiSiOuverteAsync(string systemId, string romGroup, string sessionJson, string credential, CancellationToken ct)
    {
        if (!RetroBat.Api.Netplay.NetplayHostService.OuverteAuxJoueursPendant(sessionJson)) return;
        await SoumettreMultiAsync(systemId, romGroup, sessionJson, credential, 1, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Le proces-verbal vu depuis un autre port : les compteurs d'entrees de la racine (ceux que le
    /// passeport lit) remplaces par ceux de ce port (wrapper 0.340). Null si ce port n'a recu aucun
    /// appui, ou si le wrapper ne les detaille pas.
    /// </summary>
    internal static string? SessionDuPort(string sessionJson, int port)
    {
        var session = JsonNode.Parse(sessionJson)?.AsObject();
        if (session?["ports"] is not JsonArray ports) return null;
        var entree = ports.OfType<JsonObject>().FirstOrDefault(p => (int?)p["port"] == port);
        if (entree is null) return null;
        foreach (var cle in new[] { "impossible_inputs", "press_count", "press_frames_sum", "press_frames_sq", "macro_repeats", "macro_windows" })
        {
            session[cle] = entree[cle]?.DeepClone() ?? 0;
        }
        return session.ToJsonString();
    }

    /// <summary>Les joueurs qui ont appuye pendant la partie, d'apres le wrapper (0.340) ; 0 s'il ne le dit pas.</summary>
    internal static int JoueursActifs(string sessionJson)
    {
        var session = JsonNode.Parse(sessionJson)?.AsObject();
        if (session?["ports"] is not JsonArray ports) return 0;
        return ports.OfType<JsonObject>().Count(p => ((long?)p["press_count"] ?? 0) > 0 || ((long?)p["impossible_inputs"] ?? 0) > 0);
    }

    /// <summary>
    /// LE 1CC MULTI D'UN JOUEUR (2026-09-30, docs/14 du site). Chaque borne certifie SON joueur : la
    /// place 1 pour l'hote, celle que la plateforme lui a donnee pour l'invite. Son score court de son
    /// depart a son propre continue ; le credit consomme par un autre joueur, sans START de ce panel,
    /// ne coupe rien ici. Le port de la borne, retrouve a ses appuis, doit etre celui de sa place.
    /// </summary>
    private async Task SoumettreMultiAsync(string systemId, string romGroup, string sessionJson, string credential,
        int place, string? seance, CancellationToken cancellationToken)
    {
        var port = place - 1;
        var joueurs = JoueursActifs(sessionJson);
        if (joueurs < 2)
        {
            Trace($"1CC MULTI : STOP, {(joueurs == 0 ? "le wrapper ne detaille pas les ports (avant 0.340)" : "un seul joueur a appuye")}");
            return;
        }
        var sessionDuJoueur = SessionDuPort(sessionJson, port);
        if (sessionDuJoueur is null)
        {
            Trace($"1CC MULTI : STOP, le port {port} (place {place}) n'a recu aucun appui");
            return;
        }
        var estime = _portLocal.Estimer();
        if (estime is { } portVu && portVu != port)
        {
            Trace($"1CC MULTI : STOP, les appuis de ce panel suivent le port {portVu}, pas celui de la place {place} ({port})");
            return;
        }
        Trace($"1CC MULTI : place {place}, port {port} {(estime is null ? "(non tranche aux appuis, la place fait foi)" : "confirme aux appuis")}, {joueurs} joueurs");

        string? listenerSha, coreSha, memSha, contentSha, contentMd5, contentSha1, contentSet, wrapperVersion, coreName, coreVersion;
        List<(long frame, long total)> trajectory;
        List<EvenementDeCredit> credits;
        List<DepartDeJoueur> departs;
        List<LectureDeContinues> continuesConsole;
        long? creditsMuets;
        long? hoteParti;
        lock (_sync)
        {
            credits = new List<EvenementDeCredit>(_credits);
            departs = new List<DepartDeJoueur>(_departs);
            continuesConsole = new List<LectureDeContinues>(_continuesConsole);
            hoteParti = _hotePartiFrame;
            creditsMuets = _creditsMuets;
            listenerSha = _listenerSha256; coreSha = _coreSha256; memSha = _memSha256;
            contentSha = _contentSha256; contentMd5 = _contentMd5; contentSha1 = _contentSha1; contentSet = _contentSet; wrapperVersion = _wrapperVersion;
            coreName = _coreName; coreVersion = _coreVersion;
            if (place == 1)
            {
                var brutes = new List<(long, long)>(_trajectory);
                var horsJeu = new List<bool>(_horsJeu);
                trajectory = _startVu ? FiltrerEnJeu(brutes, horsJeu) : brutes;
            }
            else
            {
                trajectory = _trajectoiresAutres.TryGetValue(place, out var lectures) ? new List<(long, long)>(lectures) : [];
            }
        }
        if (creditsMuets is not null)
        {
            Trace("1CC MULTI : STOP, la ligne des credits a ete coupee : les continues ne se voient plus");
            return;
        }

        // Une partie a plusieurs bornes : un credit consomme sans START de ce panel revient a un
        // autre joueur (son arrivee, ou son continue) et ne coupe rien ici.
        var bilan = ContinuesParCredits.Calculer(credits, departs, ouverteAuxJoueurs: true);
        if (place > 1)
        {
            // L'invite part a la premiere lecture de sa ligne CONTINUES (Neo-Geo : son PLAYER_MOD
            // passe a 1), sinon au premier credit consomme avec un START de son panel : son score
            // ne compte qu'a partir de la (la place a pu servir a un autre avant lui).
            if ((ContinuesParCompteur.DepartDe(continuesConsole, place) ?? bilan.Depart) is not { } depart)
            {
                Trace("1CC MULTI : STOP, le depart de ce joueur n'a pas ete vu (ni sa ligne CONTINUES, ni un credit consomme avec un START de ce panel)");
                return;
            }
            trajectory = trajectory.Where(l => l.frame >= depart).ToList();
            // L'hote parti, RetroArch continuait en local : ce qui suit n'est plus la partie en ligne.
            if (hoteParti is { } fin)
            {
                trajectory = trajectory.Where(l => l.frame <= fin).ToList();
                Trace($"1CC MULTI : l'hote est parti a la frame {fin}, la place s'arrete la");
            }
        }
        // Ses continues seulement : ceux d'un autre joueur, lus sur sa propre ligne, ne coupent rien.
        var coupesDuCompteur = ContinuesParCompteur.CoupesDeLaPlace(continuesConsole, place);
        var coupes = bilan.Coupes.Concat(coupesDuCompteur).OrderBy(f => f).ToList();
        if (coupes.Count > 0)
        {
            Trace($"1CC MULTI : continue de ce joueur a la frame {coupes[0]}");
            trajectory = ContinuesParCredits.AvantLePremierContinue(trajectory, coupes);
        }
        if (!ScoreAMonte(trajectory))
        {
            Trace("1CC MULTI : STOP, le score de ce joueur n'a jamais monte");
            return;
        }
        var bestRun = SelectBestRun(trajectory, coupes);
        var runPeak = bestRun.Count > 0 ? bestRun[^1].total : trajectory[^1].total;
        if (listenerSha is null)
        {
            Trace("1CC MULTI : STOP, pas d'attestation");
            return;
        }
        if (string.IsNullOrEmpty(contentSha1) && !string.IsNullOrEmpty(romGroup))
        {
            contentSha1 = GamelistIdentity.DeclaredSha1(systemId, romGroup, SetArcade(systemId, contentSet));
        }

        var (profils, profilsDuSite) = await ProfilsAsync(credential, systemId, romGroup, cancellationToken).ConfigureAwait(false);
        // LE MODE DE LA PARTIE A PLUSIEURS (2026-10-06) : un jeu a modes peut avoir un 1CC MULTI par
        // mode (1cc-multi-super...). Le mode vaut pour toute la machine : celui des lectures du joueur 1
        // au debut du run retenu.
        var contexteMulti = ContexteDepuis(bestRun.Count > 0 ? bestRun[0].frame : (trajectory.Count > 0 ? trajectory[0].frame : 0));
        JsonElement? profile = RetroBat.Api.Scoring.ModesDeJeu.ChoisirProfilMulti(profils, contexteMulti.Mode);
        if (profile is null && profilsDuSite)
        {
            Trace($"1CC MULTI : STOP, pas de classement {CategorieMulti} ouvert pour {romGroup}"
                + $"{(contexteMulti.Mode is { } m ? $" en mode {m}" : "")} (score du joueur {place} : {runPeak})");
            return;
        }

        JsonArray? nvram = null;
        if (_nvram is not null)
        {
            try { nvram = await _nvram.PourLePasseportAsync(NvramSnapshotService.EpinglesDuProfil(profile), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { Trace($"NVRAM indisponible : {ex.Message}"); }
        }
        JsonObject? bios = null;
        if (_bios is not null && profile is { } profilConnu)
        {
            try { bios = _bios.PourLePasseport(profilConnu); }
            catch (Exception ex) { Trace($"BIOS indisponible : {ex.Message}"); }
        }

        // Le 1CC MULTI garde le replay de la partie entiere.
        string? replay;
        lock (_sync) replay = _replayDeLaPartie;
        var coupe = FinDuSolo.Premiere((FinDuSolo.Continue, bilan.Coupes), (FinDuSolo.ContinueConsole, coupesDuCompteur));
        Trace($"1CC MULTI : partie du joueur {place}, {runPeak} points");
        var regleMulti = profile is { } choisi && choisi.TryGetProperty("ruleset", out var rg) && rg.ValueKind == JsonValueKind.String
            ? rg.GetString() ?? CategorieMulti
            : CategorieMulti;
        var brouillon = NouveauBrouillon("multi", systemId, romGroup, sessionDuJoueur, listenerSha, coreSha, memSha,
            contentSha, contentMd5, contentSha1, wrapperVersion, coreName, coreVersion, runPeak, bestRun, trajectory,
            nvram, bios, contexteMulti, joueurs, coupe, place, seance, replay, regleMulti);
        await PoserEtEnvoyerAsync(brouillon, runPeak, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>La categorie (ruleset) des parties a plusieurs, chacun sur son credit.</summary>
    public const string CategorieMulti = "1cc-multi";

    /// <summary>
    /// Le contexte de jeu (le mode) de la premiere lecture du joueur 1 prise a partir de cette image ;
    /// a defaut, le contexte courant. Les modes de Bubble Bobble valent pour toute la machine.
    /// </summary>
    private RetroBat.Api.Scoring.ContexteDeJeu ContexteDepuis(long frame)
    {
        lock (_sync)
        {
            for (var i = 0; i < _trajectory.Count && i < _contextes.Count; i++)
            {
                if (_trajectory[i].frame >= frame) return _contextes[i];
            }
            return _contexte;
        }
    }

    /// <summary>
    /// Le passeport d'une session, ou d'un de ses modes (<paramref name="filtre"/>) : seules les
    /// lectures prises dans ce mode comptent, et le profil est celui de ce mode.
    /// </summary>
    private async Task SoumettreAsync(string systemId, string romGroup, string sessionJson, string credential,
        RetroBat.Api.Scoring.FiltreDeMode? filtre, CancellationToken cancellationToken)
    {
        string? listenerSha, coreSha, memSha, contentSha, contentMd5, contentSha1, contentSet, wrapperVersion, coreName, coreVersion;
        long? finalTotal;
        List<(long frame, long total)> trajectory;
        List<(long frame, long total)> lecturesBrutes;
        List<RetroBat.Api.Scoring.ContexteDeJeu> contextes;
        RetroBat.Api.Scoring.ContexteDeJeu contexteCourant;
        List<EvenementDeVie> vies;
        List<EvenementDeCredit> credits;
        List<DepartDeJoueur> departs;
        bool chiffreCredits;
        lock (_sync)
        {
            credits = new List<EvenementDeCredit>(_credits);
            departs = new List<DepartDeJoueur>(_departs);
            // Le mode retenu : seulement les lectures prises dans ce mode, avec leur fenetre de jeu
            // et leur contexte. Sans filtre, toutes.
            var retenues = Enumerable.Range(0, _trajectory.Count)
                .Where(i => filtre is null || filtre.Garde(i < _contextes.Count ? _contextes[i] : RetroBat.Api.Scoring.ContexteDeJeu.Vide))
                .ToList();
            lecturesBrutes = retenues.Select(i => _trajectory[i]).ToList();
            var horsJeu = retenues.Select(i => i < _horsJeu.Count && _horsJeu[i]).ToList();
            contextes = retenues.Select(i => i < _contextes.Count ? _contextes[i] : RetroBat.Api.Scoring.ContexteDeJeu.Vide).ToList();
            contexteCourant = _contexte;
            listenerSha = _listenerSha256; coreSha = _coreSha256; memSha = _memSha256;
            contentSha = _contentSha256; contentMd5 = _contentMd5; contentSha1 = _contentSha1; contentSet = _contentSet; wrapperVersion = _wrapperVersion;
            finalTotal = filtre is null ? _finalTotal : (lecturesBrutes.Count > 0 ? lecturesBrutes[^1].total : null);
            coreName = _coreName; coreVersion = _coreVersion;
            trajectory = new List<(long, long)>(lecturesBrutes);
            vies = new List<EvenementDeVie>(_vies);
            chiffreCredits = _chiffreCredits;

            // La fenêtre de jeu : un START a été vu, donc on sait ce qui est joué. Ce qui ne l'est
            // pas (démo avant la partie, attract après le game over) sort de la trajectoire.
            if (_startVu)
            {
                var gardes = FiltrerEnJeu(lecturesBrutes, horsJeu);
                if (gardes.Count != trajectory.Count)
                {
                    Trace($"fenetre de jeu : {trajectory.Count - gardes.Count} lecture(s) hors jeu ecartee(s) (demo, attract)");
                }
                trajectory = gardes;
                // Rien de joué : pas de filet sur le dernier total, qui serait celui de l'attract.
                if (trajectory.Count == 0) finalTotal = null;
                // Le score au depart ouvre la partie (sans filtre de mode : son mode n'est pas connu).
                if (filtre is null) trajectory = AvecLeScoreAuDepart(trajectory, _scoreAuDepart);
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
        // Chiffre des credits : on soumet la partie jusqu'au premier continue. La plateforme refait le
        // meme calcul sur les lectures signees ; le faire ici garde le passeport coherent avec l'annonce.
        if (chiffreCredits)
        {
            var sans = SansLesContinues(trajectory);
            if (sans.Count < trajectory.Count)
            {
                Trace($"chiffre des credits : {trajectory.Count - sans.Count} lecture(s) de continue ecartee(s)");
                trajectory = sans;
            }
        }

        // LE CREDIT FAIT FOI (charte de la partie certifiee) : on ne coupe plus sur les vies. Rien de
        // ce qui suit le premier continue ne concourt, ni ne part dans le passeport : la session
        // fait foi, et la plateforme ne doit pas pouvoir y retrouver un « meilleur » segment.
        var ouverteAuxJoueurs = RetroBat.Api.Netplay.NetplayHostService.OuverteAuxJoueursPendant(sessionJson);
        var bilanCredits = ContinuesParCredits.Calculer(credits, departs, ouverteAuxJoueurs);
        // Un continue ou l'arrivee d'un joueur ferme le 1CC solo : on garde ce qui a ete fait seul
        // avant (cumulatif, decision user du 2026-09-30), la suite ne part pas.
        var finsDeRun = bilanCredits.FinsDuSolo;
        long? creditsMuets;
        List<LectureDeContinues> continuesConsole;
        string? definitionChargee;
        lock (_sync)
        {
            creditsMuets = _creditsMuets;
            continuesConsole = new List<LectureDeContinues>(_continuesConsole);
            definitionChargee = _definitionChargee;
        }
        var coupesConsole = ContinuesParCompteur.CoupesDeLaPlace(continuesConsole, 1);
        if (coupesConsole.Count > 0)
        {
            Trace($"continues console (compteur du jeu) : {string.Join(", ", coupesConsole)}");
            finsDeRun = finsDeRun.Concat(coupesConsole).OrderBy(f => f).ToList();
        }
        // L'arrivee d'un joueur lue sur sa ligne CONTINUES (Neo-Geo) ferme le 1CC solo comme
        // celle qu'un credit consomme avec son START fait voir.
        var arriveesConsole = ContinuesParCompteur.Arrivees(continuesConsole);
        if (arriveesConsole.Count > 0)
        {
            Trace($"arrivee d'un joueur sur sa ligne CONTINUES (frame {arriveesConsole[0]}) : le score fait seul jusque-la reste un 1CC");
            finsDeRun = finsDeRun.Concat(arriveesConsole).OrderBy(f => f).ToList();
        }
        var arrivees = bilanCredits.Arrivees.Concat(arriveesConsole).ToList();
        var finDuSolo = FinDuSolo.Premiere(
            (FinDuSolo.Continue, bilanCredits.Coupes),
            (FinDuSolo.JoueurRejoint, arrivees),
            (FinDuSolo.ContinueConsole, coupesConsole),
            (FinDuSolo.CreditsIllisibles, creditsMuets is { } coupeMuette ? new[] { coupeMuette } : Array.Empty<long>()));
        if (creditsMuets is { } muets)
        {
            Trace($"la ligne des credits a ete coupee a la frame {muets} : le 1CC s'arrete la");
            finsDeRun = finsDeRun.Append(muets).OrderBy(f => f).ToList();
        }
        if (bilanCredits.Coupes.Count > 0)
        {
            Trace($"continues (credit consomme apres le depart) : {string.Join(", ", bilanCredits.Coupes)}");
        }
        if (bilanCredits.Arrivees.Count > 0)
        {
            Trace($"arrivee d'un joueur (frame {bilanCredits.Arrivees[0]}) : le score fait seul jusque-la reste un 1CC, la suite passe a plusieurs");
        }
        if (finsDeRun.Count > 0)
        {
            var avantContinue = ContinuesParCredits.AvantLePremierContinue(trajectory, finsDeRun);
            if (avantContinue.Count < trajectory.Count)
            {
                Trace($"fin du 1CC solo : {trajectory.Count - avantContinue.Count} lecture(s) ecartee(s) apres elle");
                trajectory = avantContinue;
            }
            if (!ScoreAMonte(trajectory))
            {
                Trace("STOP: rien de marque seul avant la fin du 1CC solo (depart a deux, ou continue sans point)");
                return;
            }
        }
        var anciennesFins = FinsDeRun.Calculer(vies);
        if (anciennesFins.Count > 0)
        {
            Trace($"information : l'ancienne regle des vies aurait coupe a {string.Join(", ", anciennesFins)} (plus appliquee)");
        }
        // Ouverte aux joueurs et restee seule : un 1CC (decision user du 2026-09-30). Qu'un joueur
        // la rejoigne se lit au credit qu'il consomme, et la trajectoire s'arrete deja la. Sans
        // ligne de credits, on ne le verrait pas : la partie ouverte reste alors hors classement
        // solo, comme avant.
        // Une ligne CONTINUES de joueur 2 au .MEM fait voir l'arrivee sans ligne de credits.
        var partieADeux = ouverteAuxJoueurs && credits.Count == 0 && !VoitArriverLesJoueurs(LireMem(definitionChargee));
        if (partieADeux)
        {
            Trace("partie ouverte aux joueurs sans ligne de credits ni ligne CONTINUES de joueur 2 : on ne verrait pas un joueur arriver, hors classement solo");
        }
        else if (ouverteAuxJoueurs && arrivees.Count == 0)
        {
            Trace("partie ouverte aux joueurs, restee seule : 1CC");
        }

        var bestRun = SelectBestRun(trajectory, finsDeRun);
        // LE REPLAY DU MEILLEUR RUN (2026-10-02) : une partie peut en avoir plusieurs (arret a la
        // baisse du score, rearmement). Le score certifie se rattache a celui qui le contient.
        List<(string Id, long Debut, long? Fin)> enregistrements;
        lock (_sync) { enregistrements = new List<(string Id, long Debut, long? Fin)>(_enregistrementsDeLaPartie); }
        if (ReplayDuMeilleurRun(enregistrements, bestRun) is { } replayDuRun)
        {
            lock (_sync) { _replayDuMeilleurRun = replayDuRun; }
            if (enregistrements.Count > 1) Trace($"replay du meilleur run : {replayDuRun} ({enregistrements.Count} enregistrements dans la partie)");
        }
        long runPeak = bestRun.Count > 0 ? bestRun[^1].total : (finalTotal ?? 0);
        Trace($"segmentation : meilleur run {bestRun.Count}/{trajectory.Count} pts, pic={runPeak} (total global {finalTotal})");
        var contexteRun = RetroBat.Api.Scoring.ModesDeJeu.ContexteDuRun(lecturesBrutes, contextes, bestRun, contexteCourant);
        if (contexteRun.Mode is not null || contexteRun.Difficulte.Count > 0)
        {
            Trace($"contexte du run : mode={(contexteRun.Mode?.ToString() ?? "non mesure")} difficulte=[{string.Join(", ", contexteRun.Difficulte.Select(p => p.Key + "=" + p.Value))}]");
        }

        // Rien à certifier sans score ni attestation : on s'arrête AVANT de consommer
        // quoi que ce soit (démo, navigation, jeu non joué).
        Trace($"état: listener={listenerSha is not null} core={coreSha is not null} content={contentSha is not null} finalTotal={finalTotal} trajPts={trajectory.Count} inDemo={_inDemo} scoresRecus={_scoresRecus} dontDemo={_scoresEnDemo} demosIgnorees={_demosIgnorees}");
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
            if (_prevolCertifiable && joue && filtre is null)
            {
                _overlay?.ShowTop(
                    "SCORING",
                    Texte("scoring_none_measured"),
                    Texte("scoring_none_measured_sub"),
                    9000,
                    alerte: true);
                _logger?.LogWarning(
                    "Scoring : partie annoncee certifiable terminee sans aucun score mesure "
                    + "(listener={Listener}, total={Total}). Le coeur employe n'expose probablement rien a lire.",
                    listenerSha is not null, finalTotal);
                _prevolCertifiable = false;
            }

            return;
        }

        // Le profil du MODE joue : un jeu a modes a un classement par mode (ModesDeJeu). Il dit aussi les
        // NVRAM et les BIOS a joindre ; site muet, la copie gardee sur le disque le dit a sa place.
        var (profils, profilsDuSite) = await ProfilsAsync(credential!, systemId, romGroup, cancellationToken).ConfigureAwait(false);
        var profile = RetroBat.Api.Scoring.ModesDeJeu.ChoisirProfil(profils, contexteRun.Mode);
        if (profile is null)
        {
            var pourquoi = profils.Count == 0
                ? $"profil {romGroup} non ouvert"
                : $"mode {(contexteRun.Mode?.ToString() ?? "non mesure")} de {romGroup} sans classement ouvert";
            // Mode laboratoire (NelfeScoreLab) : on soumet quand meme. La plateforme refuse le score
            // mais garde la tentative signee, sans laquelle aucun profil ne peut s'ouvrir.
            if (RetroBat.Api.Scoring.ScoreLabLabMode.IsActive(DateTime.UtcNow, out var labo))
            {
                Trace($"{pourquoi}, soumission de laboratoire ({labo})");
                profile = RetroBat.Api.Scoring.ScoreLabLabMode.PlaceholderProfile();
            }
            else if (profilsDuSite)
            {
                Trace($"STOP: {pourquoi}");
                return;
            }
            else
            {
                // Site muet et aucune copie : la partie est gardee quand meme (le jeu est dans la
                // collection World Scoring), sans epingles de NVRAM ni BIOS. Le site jugera.
                Trace($"{pourquoi} : site injoignable et aucune copie gardee, brouillon sans profil");
            }
        }

        if (runPeak <= 0) runPeak = finalTotal.Value;   // filet : aucun segment exploitable

        // Les NVRAM, lues APRES la fermeture de l'emulateur (voir NvramSnapshotService) : elles font
        // partie de la mesure, donc du brouillon.
        JsonArray? nvram = null;
        if (_nvram is not null)
        {
            try { nvram = await _nvram.PourLePasseportAsync(NvramSnapshotService.EpinglesDuProfil(profile), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { Trace($"NVRAM indisponible : {ex.Message}"); }
        }

        // Les BIOS que le profil exige (none quand le jeu n'en utilise pas).
        JsonObject? bios = null;
        if (_bios is not null && profile is { } profilConnu)
        {
            try { bios = _bios.PourLePasseport(profilConnu); }
            catch (Exception ex) { Trace($"BIOS indisponible : {ex.Message}"); }
        }

        // Le replay du meilleur run part avec le brouillon : le lien score-replay ne dependra plus de
        // la partie en cours au moment du verdict, qui peut arriver bien plus tard.
        string? replay;
        lock (_sync) { replay = _replayDuMeilleurRun ?? _replayDeLaPartie; _replayDuMeilleurRun = null; }

        var brouillon = NouveauBrouillon("solo", systemId, romGroup, sessionJson, listenerSha, coreSha, memSha,
            contentSha, contentMd5, contentSha1, wrapperVersion, coreName, coreVersion, runPeak, bestRun, trajectory,
            nvram, bios, contexteRun, partieADeux ? 2 : 1, finDuSolo, null, null, replay,
            profile is { } profilChoisi && profilChoisi.TryGetProperty("ruleset", out var regleChoisie) ? regleChoisie.GetString() : null);
        await PoserEtEnvoyerAsync(brouillon, runPeak, cancellationToken).ConfigureAwait(false);

        // LE 1LC (decision user du 2026-10-09) : le score de la premiere vie, mesure dans la meme partie
        // et soumis a son propre classement, avec le meme replay. Une partie ouverte aux joueurs qu'on
        // ne verrait pas arriver reste hors du 1LC comme du 1CC solo.
        if (!partieADeux && RetroBat.Api.Scoring.ModesDeJeu.ChoisirProfil1LC(profils, contexteRun.Mode) is { } profil1lc)
        {
            // Les compteurs de vies du .MEM coupent le 1LC des qu'ils ont parle : un drapeau (sante a
            // zero, image ou son de mort) peut parler sans mort et couperait le joueur trop tot.
            var compteurs = RetroBat.Api.Scoring.PremiereVie.Compteurs(LireMem(definitionChargee));
            var pertes = RetroBat.Api.Scoring.PremiereVie.SurLesCompteurs(
                vies.Where(v => v.Perte).Select(v => (v.Address, v.Frame, v.Player)).ToList(), compteurs);
            Trace($"1LC : compteurs de vies du .MEM [{string.Join(", ", compteurs)}], {pertes.Count} perte(s) retenue(s) sur {vies.Count(v => v.Perte)}");
            var (run1lc, mort) = RetroBat.Api.Scoring.PremiereVie.Couper(bestRun, pertes);
            if (!RetroBat.Api.Scoring.PremiereVie.AMarque(run1lc))
            {
                Trace($"1LC : rien de marque avant la premiere vie perdue (frame {mort}), pas de soumission");
                return;
            }
            var pic1lc = run1lc[^1].total;
            Trace(mort is { } perdue
                ? $"1LC : premiere vie perdue a la frame {perdue}, score de la premiere vie {pic1lc}"
                : $"1LC : aucune vie perdue dans le run, le 1LC est le run entier ({pic1lc})");
            // Les lectures du passeport s'arretent a la mort : rien de ce qui suit ne doit pouvoir y
            // etre retrouve par la plateforme.
            var brouillon1lc = NouveauBrouillon("1lc", systemId, romGroup, sessionJson, listenerSha, coreSha, memSha,
                contentSha, contentMd5, contentSha1, wrapperVersion, coreName, coreVersion, pic1lc, run1lc, run1lc,
                nvram, bios, contexteRun, 1, mort is { } m ? (RetroBat.Api.Scoring.PremiereVie.Raison, m) : finDuSolo, null, null, replay,
                profil1lc.TryGetProperty("ruleset", out var regle1lc) ? regle1lc.GetString() : null);
            // OU LA LECTURE DU 1LC SE FIGE, en frames du replay : la mort moins le depart de
            // l'enregistrement. Le lecteur emploie le vrai coeur, sans wrapper : il ne voit pas la mort,
            // il lui faut sa frame.
            if (mort is { } finDeVie && replay is { } replay1lc
                && enregistrements.FirstOrDefault(e => string.Equals(e.Id, replay1lc, StringComparison.Ordinal)) is { Id: not null } enr
                && finDeVie >= enr.Debut)
            {
                brouillon1lc["arret_du_replay"] = finDeVie - enr.Debut;
                Trace($"1LC : la lecture du replay {replay1lc} se figera a sa frame {finDeVie - enr.Debut}");
            }
            await PoserEtEnvoyerAsync(brouillon1lc, pic1lc, cancellationToken).ConfigureAwait(false);
        }
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
        List<(long frame, long total)>? toutesLesLectures = null, JsonArray? nvram = null, JsonObject? bios = null,
        RetroBat.Api.Scoring.ContexteDeJeu? contexte = null, int joueurs = 1, (string Raison, long Frame)? finDuSolo = null,
        int? place = null, string? seance = null,
        string? sessionId = null, DateTime? finUtc = null,
        (NelfePlayScoringSessionService.SessionPlayer? Joueur, bool Fige)? joueurFige = null,
        bool? labo = null, string? versionApi = null, string? etatWrapper = null, long? arretDuReplay = null)
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
        var sessionPlayer = joueurFige is { } fige ? fige.Joueur : _scoringSession?.Get();
        var world = sessionPlayer?.World ?? "home";
        JsonObject? contextVenue = sessionPlayer is not null
            && (sessionPlayer.VenueName is not null || sessionPlayer.VenueCity is not null)
            ? new JsonObject { ["name"] = sessionPlayer.VenueName, ["city"] = sessionPlayer.VenueCity }
            : null;

        // Le mode et la difficulte du run (ModesDeJeu) : absents pour un jeu qui n'en declare pas.
        var jeu = new JsonObject
        {
            ["system_id"] = systemId, ["rom_group"] = romGroup, ["engine"] = engine,
            ["ruleset"] = ruleset, ["profile_version"] = profileVersion,
            ["manifest_commit"] = manifestCommit, ["profile_document_sha256"] = profileDocSha,
        };
        var ctx = contexte ?? RetroBat.Api.Scoring.ContexteDeJeu.Vide;
        if (RetroBat.Api.Scoring.ModesDeJeu.ModePourLePasseport(profile, ctx) is { } modeJoue) jeu["mode"] = modeJoue;
        // Une partie a plusieurs n'entre pas au classement solo (charte, decision du 2026-09-30).
        if (joueurs > 1) jeu["players"] = joueurs;
        if (RetroBat.Api.Scoring.ModesDeJeu.DifficultePourLePasseport(profile, ctx) is { } difficulte) jeu["difficulty"] = difficulte;
        // Ce qui a ferme le 1CC solo (continue, arrivee d'un joueur...) : garde et signe, jamais
        // affiche (decision user du 2026-09-30). Absent quand rien n'a coupe la partie.
        if (finDuSolo is { } fin)
        {
            jeu["cut"] = new JsonObject { ["reason"] = fin.Raison, ["frame"] = fin.Frame };
            // Le 1LC dit ou sa lecture s'arrete, en frames du replay (2026-10-09).
            if (arretDuReplay is { } arret) jeu["cut"]!["replay_frame"] = arret;
        }
        // 1CC MULTI : la place du joueur certifie (1 pour l'hote, 2 a 4 pour les invites) et le direct
        // ou elle a ete attribuee. La plateforme les confronte a ses places.
        if (place is { } siege) jeu["seat"] = siege;
        if (!string.IsNullOrEmpty(seance)) jeu["netplay_session"] = seance;

        // L'heure de fin est celle de la PARTIE (brouillon), pas celle de l'assemblage : un brouillon
        // envoye au retour du site garde sa date.
        var finDeLaPartie = finUtc ?? DateTime.UtcNow;
        var document = new JsonObject
        {
            ["protocol"] = 1,
            ["session_id"] = sessionId ?? Guid.NewGuid().ToString(),
            ["ticket"] = JsonNode.Parse(ticket.GetRawText()),
            ["game"] = jeu,
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
                ["lab"] = (labo ?? RetroBat.Api.Scoring.ScoreLabLabMode.IsActive(DateTime.UtcNow, out _)) ? true : null,
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
                ["apiexpose"] = versionApi ?? CabinetState.Version,
                ["wrapper_state"] = etatWrapper ?? CabinetState.Wrapper,
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
                ["started_at"] = finDeLaPartie.AddMilliseconds(-monotonicMs).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["ended_at"] = finDeLaPartie.ToString("yyyy-MM-ddTHH:mm:ssZ"),
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

    /// <summary>Le profil par defaut du jeu (celui du mode de demarrage) : le prevol n'a pas besoin de plus.</summary>
    private async Task<JsonElement?> FetchProfileAsync(string credential, string systemId, string romGroup, CancellationToken cancellationToken)
    {
        var profils = await FetchProfilesAsync(credential, systemId, romGroup, cancellationToken).ConfigureAwait(false);
        // Le profil d'une partie seule : jamais le 1CC MULTI, ni le 1LC, meme s'ils venaient en tete.
        var seules = RetroBat.Api.Scoring.ModesDeJeu.DuSolo(profils);
        return seules.Count > 0 ? seules[0] : null;
    }

    /// <summary>
    /// Les profils ouverts du jeu, celui par defaut en tete. Un jeu a modes en a un par mode ; une
    /// plateforme d'avant les modes ne renvoie que « profile ».
    /// </summary>
    /// <summary>
    /// LES PROFILS D'UN JEU, GARDES SUR DISQUE (2026-10-03). Chaque reponse du site (jeu ouvert ou
    /// non) se garde dans state/nelfeplay/profils ; quand le site ne repond pas, la borne relit la
    /// derniere. Avant, une panne rendait une liste vide : la partie passait pour « non ouverte »
    /// et son score etait perdu. C'est la premiere brique de la file d'envoi (CDC infra, piste B) :
    /// a l'envoi, c'est le serveur qui juge, avec les memes controles qu'en ligne.
    /// </summary>
    private async Task<List<JsonElement>> FetchProfilesAsync(string credential, string systemId, string romGroup, CancellationToken cancellationToken)
        => (await ProfilsAsync(credential, systemId, romGroup, cancellationToken).ConfigureAwait(false)).Profils;

    /// <summary>
    /// Les profils du jeu, et s'ils viennent du SITE. Une liste vide du site dit « pas ouvert » ;
    /// une liste vide faute de site et de copie ne dit rien (piste B : le brouillon attend).
    /// </summary>
    private async Task<(List<JsonElement> Profils, bool DuSite)> ProfilsAsync(string credential, string systemId, string romGroup, CancellationToken cancellationToken)
    {
        var copie = CheminDuProfil(systemId, romGroup);
        try
        {
            var query = $"system_id={Uri.EscapeDataString(systemId)}&rom_group={Uri.EscapeDataString(romGroup)}";
            var appel = await AppelerLeCentralAsync(HttpMethod.Get, "scores/profile", query, null, null, credential,
                garder: false, resume: null, cancellationToken).ConfigureAwait(false);
            if (appel is { } reponse)
            {
                if (reponse.Statut is >= 200 and < 300 && ProfilsDuCorps(reponse.Corps) is { } profils)
                {
                    GarderLeProfil(copie, reponse.Corps);
                    return (profils, true);
                }
                Trace($"profil {systemId}/{romGroup} : le site repond {reponse.Statut}{reponse.Voie}, lecture de la copie gardee");
            }
            else
            {
                Trace($"profil {systemId}/{romGroup} : site injoignable, lecture de la copie gardee");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : résolution du profil impossible.");
            Trace($"profil {systemId}/{romGroup} : site injoignable ({ex.GetType().Name}), lecture de la copie gardee");
        }
        try
        {
            if (File.Exists(copie) && ProfilsDuCorps(await File.ReadAllTextAsync(copie, cancellationToken).ConfigureAwait(false)) is { } gardes)
            {
                return (gardes, false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : copie du profil illisible.");
        }
        return (new List<JsonElement>(), false);
    }

    /// <summary>
    /// Les profils d'une reponse du site : vide pour un jeu non ouvert, null pour un corps
    /// illisible (ce n'est pas une reponse : on ne la garde pas).
    /// </summary>
    internal static List<JsonElement>? ProfilsDuCorps(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var racine = doc.RootElement;
            if (racine.ValueKind != JsonValueKind.Object) return null;
            var sortie = new List<JsonElement>();
            if (!racine.TryGetProperty("open", out var open) || open.ValueKind != JsonValueKind.True) return sortie;
            if (racine.TryGetProperty("profiles", out var liste) && liste.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in liste.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.Object) sortie.Add(p.Clone());
                }
            }
            if (sortie.Count == 0 && racine.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
            {
                sortie.Add(profile.Clone());
            }
            return sortie;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CheminDuProfil(string systemId, string romGroup)
    {
        var cle = (systemId + "|" + romGroup).ToLowerInvariant();
        var nom = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(cle)))[..32].ToLowerInvariant();
        return System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "profils", nom + ".json");
    }

    private void GarderLeProfil(string chemin, string body)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(chemin)!);
            var temporaire = chemin + ".tmp";
            File.WriteAllText(temporaire, body, new UTF8Encoding(false));
            File.Move(temporaire, chemin, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : copie du profil non ecrite.");
        }
    }

    /// <summary>
    /// Envoie un passeport et dit ce que vaut la reponse (piste B, 2026-10-04). Seul un verdict
    /// definitif est range, annonce et rattache a son replay ; sans reponse, en 5xx, 429 ou sur
    /// une page HTML, la partie reste en file. Avant, une reponse 503 etait rangee comme verdict
    /// et une coupure perdait la partie. <paramref name="annoncer"/> : la partie vient de finir ;
    /// faux pour un envoi differe, qui s'annonce en groupe (voir EnvoyerLesBrouillonsAsync).
    /// </summary>
    private async Task<(RetroBat.Api.Scoring.IssueDEnvoi Issue, string? Statut, int? Rang)> SubmitAsync(
        string credential, JsonObject passport, string? replayId, bool annoncer, CancellationToken cancellationToken)
    {
        try
        {
            var corps = passport.ToJsonString();
            var sessionId = (string?)passport["session_id"] ?? "";
            var deviceId = (string?)(passport["device"] as JsonObject)?["device_id"] ?? "";
            // Ce qu'un relais peut lire s'il garde la partie : le jeu, le score, l'heure. Pas le joueur.
            var resume = new JsonObject
            {
                ["session_id"] = sessionId,
                ["system_id"] = (string?)(passport["game"] as JsonObject)?["system_id"],
                ["rom_group"] = (string?)(passport["game"] as JsonObject)?["rom_group"],
                ["ruleset"] = (string?)(passport["game"] as JsonObject)?["ruleset"],
                ["score"] = (string?)(passport["metric"] as JsonObject)?["value"],
                ["ended_at"] = (string?)(passport["timing"] as JsonObject)?["ended_at"],
            };
            var appel = await AppelerLeCentralAsync(HttpMethod.Post, "scores/submissions", null, corps, "application/json",
                credential, garder: true, resume, cancellationToken, partie: sessionId).ConfigureAwait(false);
            if (appel is not { } reponse)
            {
                Trace("envoi : ni le site ni un relais n'ont rendu de verdict : la partie reste en file");
                return (RetroBat.Api.Scoring.IssueDEnvoi.ARetenter, null, null);
            }
            var body = reponse.Corps;
            var statutHttp = reponse.Statut;
            if (reponse.Voie.Length > 0) Trace($"verdict venu{reponse.Voie}");
            // L'outil de diagnostic rattache « VERDICT HTTP » a la partie qu'il lit : un verdict
            // differe porte un autre libelle pour ne pas tomber sur la partie d'apres.
            Trace($"{(annoncer ? "VERDICT HTTP" : "VERDICT DIFFERE HTTP")} {statutHttp} - {body}");
            if (RetroBat.Api.Scoring.BrouillonDeScore.Classer(statutHttp, body) == RetroBat.Api.Scoring.IssueDEnvoi.ARetenter)
            {
                // Le site ne connait pas la cle (base restauree d'avant son inscription) : on la
                // reinscrit, et la partie repartira au prochain essai de la file.
                if (body.Contains("session.device_unknown", StringComparison.Ordinal)) _enrolledKeyId = null;
                _logger?.LogInformation("Scoring : pas de verdict du site (HTTP {Status}), la partie reste en file.", statutHttp);
                return (RetroBat.Api.Scoring.IssueDEnvoi.ARetenter, null, null);
            }

            // PAS DE VERDICT SIGNE, PAS DE VERDICT (CDC infra §15.3, 2026-10-08) : la partie ne quitte la file
            // qu'a un verdict signe par une cle de la carte, pour sa session, son appareil et ses octets.
            if (statutHttp is >= 200 and < 300)
            {
                var lecture = RetroBat.Api.Reseau.VerdictSigne.Verifier(body, ClesDeVerdict(), sessionId, deviceId, Encoding.UTF8.GetBytes(corps));
                if (!lecture.Valide)
                {
                    Trace($"verdict NON RETENU ({lecture.Raison}) : la partie reste en file");
                    _logger?.LogWarning("Scoring : verdict sans signature valable ({Raison}), la partie reste en file.", lecture.Raison);
                    // Une cle de verdict changee arrive avec une carte plus recente.
                    if (lecture.Raison.StartsWith("cle_inconnue", StringComparison.Ordinal) && _cartes is not null)
                        _ = _cartes.RafraichirAsync(CancellationToken.None);
                    return (RetroBat.Api.Scoring.IssueDEnvoi.ARetenter, null, null);
                }
                body = RetroBat.Api.Reseau.VerdictSigne.AvecLesChampsSignes(body, lecture.Contenu!);
            }

            // Un renvoi apres une reponse perdue revient en « duplicate » avec le verdict d'origine :
            // c'est lui qui compte, pour le classement local comme pour le lien du replay.
            body = VerdictDOrigine(body);
            _logger?.LogInformation("Scoring : verdict serveur {Status} - {Body}", statutHttp, body);
            PersistCertified(passport, body);
            if (annoncer)
            {
                MaybeShowClaimOverlay(passport, body);
                await NotifyVerdictAsync(passport, body, cancellationToken).ConfigureAwait(false);
            }
            CaptureReplayLinkOnPublished(passport, body, replayId);
            PublishVerdict(passport, body);

            string? statut = null;
            int? rang = null;
            try
            {
                var verdict = JsonNode.Parse(body) as JsonObject;
                statut = (string?)(verdict?["status"] ?? verdict?["verdict"]);
                rang = verdict?["rank"] is JsonValue r && r.TryGetValue<int>(out var rk) ? rk : null;
            }
            catch (JsonException) { }
            return (RetroBat.Api.Scoring.IssueDEnvoi.Definitif, statut, rang);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scoring : soumission impossible, la partie reste en file.");
            Trace($"envoi impossible ({ex.GetType().Name}) : la partie reste en file");
            return (RetroBat.Api.Scoring.IssueDEnvoi.ARetenter, null, null);
        }
    }

    /// <summary>
    /// Le verdict d'origine d'un doublon : « duplicate » accompagne de original_status et
    /// original_reason (site, 2026-10-03) devient ce verdict-la, rang compris. Sinon, inchange.
    /// </summary>
    internal static string VerdictDOrigine(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject verdict) return body;
            if ((string?)verdict["status"] != "duplicate" || verdict["original_status"] is not JsonValue origine
                || !origine.TryGetValue<string>(out var statut) || string.IsNullOrEmpty(statut))
            {
                return body;
            }
            verdict["status"] = statut;
            verdict["reason"] = (string?)verdict["original_reason"] ?? "";
            verdict["duplicate"] = true;
            return verdict.ToJsonString();
        }
        catch (JsonException)
        {
            return body;
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
            // Le 1LC arrive juste apres le verdict du 1CC de la meme partie : il se nomme.
            var quoi = RetroBat.Api.Scoring.ModesDeJeu.Est1LC((string?)(passport["game"] as JsonObject)?["ruleset"])
                ? "Score 1LC"
                : "Score";

            var message = status switch
            {
                "published" => $"{quoi} certifié : {score:N0} publié" + (rank is int r ? $" (#{r})" : ""),
                // Signalé : gardé sur le compte du joueur, jamais classé ni ancré. Il sait pourquoi.
                "held" => $"{quoi} {score:N0} signalé, non classé : {ReasonToText(reason)}",
                // La quarantaine n'est PAS un refus : le score est garde avec son passeport
                // signe et entrera au classement des que l'emulateur sera reconnu. Le dire
                // ainsi change tout pour le joueur, qui a joue et qui garde quelque chose.
                "quarantined" => reason == "settings.unknown"
                    ? $"{quoi} {score:N0} enregistré, en attente de conformité des réglages"
                    : $"{quoi} {score:N0} enregistré, en attente : ton émulateur n'est pas encore reconnu",
                "expired" => $"{quoi} {score:N0} non classé : {ReasonToText(reason)}",
                "refused" => $"{quoi} {score:N0} refusé : {ReasonToText(reason)}",
                _ => $"{quoi} non transmis : {ReasonToText(reason)}",
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
    /// <summary>La langue de l'ecran : celle du joueur identifie, sinon celle d'EmulationStation, sinon l'anglais.</summary>
    private string Langue()
    {
        string? es = null;
        try
        {
            if (_esSettings is not null && _esSettings.ReadAllSettings().TryGetValue("Language", out var langue)) es = langue;
        }
        catch (Exception)
        {
            // Des reglages illisibles : la langue du joueur, sinon l'anglais.
        }
        return CabinetAnnounceText.Resolve(_scoringSession?.Get()?.Locale, es);
    }

    private string Texte(string cle) => CabinetAnnounceText.Get(cle, Langue());

    /// <summary>
    /// L'annonce au lancement dans la langue donnee. En francais, elle redonne mot pour mot ce que
    /// le journal ecrit.
    /// </summary>
    internal static (string Titre, string Detail) AnnonceLocalisee(string langue, bool certifiable, string reason, IReadOnlyList<string> dangers, bool force,
        IReadOnlyList<string>? remisDUsine = null)
    {
        string T(string cle) => CabinetAnnounceText.Get(cle, langue);
        if (!certifiable && reason == "profile.core_mismatch") return (T("scoring_emulator_pending"), T("scoring_emulator_pending_sub"));
        if (!certifiable && reason == "profile.core_options_mismatch") return (T("scoring_settings_pending"), T("scoring_settings_pending_sub"));
        if (!certifiable)
        {
            return (T("scoring_not_certifiable"), CabinetAnnounceText.Find("reason_" + reason.Replace('.', '_'), langue) ?? ReasonToText(reason));
        }
        if (dangers.Count > 0)
        {
            return (T("scoring_not_certifiable"), string.Format(T("scoring_frontend_off"), string.Join(", ", dangers.Select(d => T("scoring_danger_" + d)))));
        }
        if (remisDUsine is { Count: > 0 })
        {
            return (T("scoring_certifiable"), string.Format(T("scoring_factory_reset"), string.Join(", ", remisDUsine)));
        }
        return (T("scoring_certifiable"), T(force ? "scoring_forced" : "scoring_for_ranking"));
    }

    /// <summary>
    /// Les REGLAGES DU JEU que le forcage a remis d'usine (2026-10-07) : les DIP switches de la liste
    /// « cle=valeur;... » du wrapper, par leur nom (« fbneo-dipswitch-altbeast-Energy_Meter » donne
    /// « Energy Meter »). Les options de l'emulateur (vitesse, ROM patchees) n'y sont pas : le joueur
    /// ne les a pas choisies pour ce jeu.
    /// </summary>
    internal static IReadOnlyList<string> ReglagesDuJeuRemisDUsine(string? forcees)
    {
        const string Dip = "-dipswitch-";
        var noms = new List<string>();
        foreach (var paire in (forcees ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cle = paire.Split('=', 2)[0];
            var i = cle.IndexOf(Dip, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            // « fbneo-dipswitch-<jeu>-<Nom> » : le nom suit le tiret qui ferme le nom du jeu.
            var reste = cle[(i + Dip.Length)..];
            var tiret = reste.IndexOf('-');
            var nom = (tiret >= 0 ? reste[(tiret + 1)..] : reste).Replace('_', ' ').Trim();
            if (nom.Length > 0 && !noms.Contains(nom)) noms.Add(nom);
        }
        return noms;
    }

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
        // Le bandeau dit deja « signale, non classe » : le motif ne le redit pas (2026-10-06).
        "plausibility.macro_detected" => "séquence rejouée à l'identique (macro)",
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
        "game.mode_unmeasured" => "mode de jeu non mesuré : mets APIExpose à jour",
        "profile.api_outdated" => "version d'APIExpose trop ancienne pour ce classement : mets APIExpose à jour",
        "game.mode_mismatch" => "mode de jeu différent de ce classement",
        "game.difficulty_unmeasured" => "difficulté non mesurée",
        "game.difficulty_not_allowed" => "difficulté non autorisée pour ce classement",
        "game.multiplayer" => "partie à plusieurs : pas de classement solo",
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
            if (await InscrireLaCleAsync(credential, cancellationToken).ConfigureAwait(false))
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

    /// <summary>Inscrit la clé de l'appareil auprès du site, sous ce secret.</summary>
    private async Task<bool> InscrireLaCleAsync(string credential, CancellationToken cancellationToken)
    {
        try
        {
            using var deviceKey = CngDeviceKey.OpenOrCreate(ScoringKeyName);
            var appel = await AppelerLeCentralAsync(HttpMethod.Post, "scores/enroll-key", null, deviceKey.PublicKeyPem,
                "application/x-pem-file", credential, garder: false, resume: null, cancellationToken).ConfigureAwait(false);
            Trace($"enroll HTTP {(appel is { } r ? r.Statut.ToString() + r.Voie : "-")} key_id={deviceKey.KeyId}");
            return appel is { Statut: >= 200 and < 300 };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : enrôlement impossible.");
            return false;
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

    /// <summary>Une reponse du central : statut, corps, et le relais qui l'a apportee (vide en direct).</summary>
    private readonly record struct ReponseDuCentral(int Statut, string Corps, string Voie);

    /// <summary>
    /// UNE REQUETE DE L'AGENT AU CENTRAL, EN DIRECT OU PAR UN RELAIS (CDC infra §15.5, 2026-10-08). En direct
    /// d'abord. Si le central ne repond pas lui-meme (pas de connexion, delai depasse, 502 ou 504 d'un
    /// intermediaire, page qui n'est pas de lui), par les noeuds « relay » de la carte, dans une enveloppe scellee
    /// que seul le central ouvre. Un passeport (garder) part aussi aux relais sur un 503 : ils le gardent et le
    /// feront suivre au retour du central ; leurs recus signes se rangent a cote du brouillon. Null : personne
    /// n'a rendu de reponse du central.
    /// </summary>
    private async Task<ReponseDuCentral?> AppelerLeCentralAsync(HttpMethod methode, string chemin, string? query, string? corps,
        string? typeDeCorps, string credential, bool garder, JsonObject? resume, CancellationToken ct, string? partie = null)
    {
        int? statutDirect = null;
        var corpsDirect = "";
        try
        {
            using var client = CreateClient(credential);
            using var requete = new HttpRequestMessage(methode, "/api/v1/agent/" + chemin + (string.IsNullOrEmpty(query) ? "" : "?" + query));
            if (corps is not null) requete.Content = new StringContent(corps, Encoding.UTF8, typeDeCorps ?? "application/json");
            using var reponse = await client.SendAsync(requete, ct).ConfigureAwait(false);
            corpsDirect = await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            statutDirect = (int)reponse.StatusCode;
            if (!CentralMuet(statutDirect.Value, corpsDirect) && !(garder && statutDirect == 503))
                return new ReponseDuCentral(statutDirect.Value, corpsDirect, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Trace($"{chemin} : central injoignable en direct ({ex.GetType().Name})");
        }

        if (_relais is null || !_relais.Disponible)
            return statutDirect is { } seul ? new ReponseDuCentral(seul, corpsDirect, "") : null;

        var issue = await _relais.EnvoyerAsync(methode.Method, chemin, query, corps, credential, null, garder, resume, ct).ConfigureAwait(false);
        if (issue.Reponse is { } relayee)
        {
            // Le central a repondu, par un detour : la liaison avec NelfePlay tient (pastille du panneau).
            if (relayee.Statut < 500) LiaisonNelfePlay.Noter(true);
            Trace($"{chemin} : reponse du central par le relais {relayee.Noeud} (HTTP {relayee.Statut})");
            return new ReponseDuCentral(relayee.Statut, relayee.Corps, " par " + relayee.Noeud);
        }
        if (issue.Recus.Count > 0 && partie is not null) GarderLesRecus(partie, issue.Recus);
        Trace($"{chemin} : aucun relais n'a rendu de reponse du central ({issue.Detail})");
        return statutDirect is { } direct ? new ReponseDuCentral(direct, corpsDirect, "") : null;
    }

    /// <summary>Le central ne repond pas lui-meme : 502 ou 504 d'un intermediaire, ou une erreur qui n'est pas du JSON.</summary>
    internal static bool CentralMuet(int statut, string? corps)
    {
        if (statut is 502 or 504) return true;
        if (statut < 500) return false;
        try
        {
            return string.IsNullOrWhiteSpace(corps) || JsonNode.Parse(corps) is not JsonObject;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Les cles qui signent les verdicts : celles de la carte en vigueur, ou de la carte livree.</summary>
    private IReadOnlyCollection<RetroBat.Api.Reseau.ClePublique> ClesDeVerdict()
        => (IReadOnlyCollection<RetroBat.Api.Reseau.ClePublique>?)_cartes?.ClesDeVerdict ?? RetroBat.Api.Reseau.CarteDuReseau.ParDefaut.ClesDeVerdict;

    /// <summary>Les recus des relais qui gardent une partie, a cote de son brouillon (brouillons/recus).</summary>
    private void GarderLesRecus(string partie, IReadOnlyList<RetroBat.Api.Reseau.ClientDeRelais.Recu> recus)
    {
        try
        {
            var fichier = _brouillons.FichierDesRecus(partie);
            var liste = File.Exists(fichier) && JsonNode.Parse(File.ReadAllText(fichier)) is JsonArray deja ? deja : new JsonArray();
            foreach (var recu in recus)
            {
                liste.Add(new JsonObject
                {
                    ["node"] = recu.Noeud, ["host"] = recu.Hote, ["id"] = recu.Id, ["sha256"] = recu.Sha256,
                    ["received_at"] = recu.RecuLe, ["receipt"] = JsonNode.Parse(recu.Enveloppe),
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
            File.WriteAllText(fichier + ".tmp", liste.ToJsonString());
            File.Move(fichier + ".tmp", fichier, overwrite: true);
            Trace($"partie {partie} gardee par {string.Join(", ", recus.Select(r => r.Noeud))} (recus signes)");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Scoring : recus des relais non ecrits.");
        }
    }

    /// <summary>Le ticket du lancement vaut-il pour une partie finie a cette heure ? Il doit expirer apres elle.</summary>
    internal static bool TicketValablePour(JsonNode? ticket, DateTime finUtc)
    {
        var expire = (string?)(ticket as JsonObject)?["expires_at"];
        return expire is not null
            && DateTime.TryParse(expire, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var fin)
            && fin >= finUtc;
    }

    /// <summary>
    /// Le ticket de la partie, pris au lancement (CDC infra §15.6) : sans lui, une partie finie central tombe ne
    /// ferait qu'un brouillon ; avec lui, un passeport complet qu'un relais peut garder. Le central injoignable au
    /// lancement, on garde celui d'avant s'il vaut encore.
    /// </summary>
    private async Task PrendreLeTicketDuLancementAsync(string credential)
    {
        try
        {
            if (await TicketAsync(credential, CancellationToken.None).ConfigureAwait(false) is { } ticket)
            {
                var noeud = JsonNode.Parse(ticket.GetRawText());
                lock (_sync) _ticketDuLancement = noeud;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : ticket du lancement indisponible.");
        }
    }

    private HttpClient CreateClient(string credential)
    {
        var client = _httpFactory.CreateClient(nameof(NelfePlayScoringReporter));
        client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrEmpty(credential)) client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
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

    // ── Brouillons scelles et file d'envoi (piste B, 2026-10-04) ─────────────────

    /// <summary>
    /// Les parties mesurees, signees, en attente de leur verdict (voir BrouillonDeScore). Un seul
    /// envoi a la fois : la fin d'une partie et la boucle de fond ne se croisent pas.
    /// </summary>
    private readonly RetroBat.Api.Scoring.FileDesBrouillons _brouillons =
        new(System.IO.Path.Combine(AppContext.BaseDirectory, "state", "nelfeplay", "brouillons"));
    private readonly SemaphoreSlim _envoi = new(1, 1);

    /// <summary>La vraie fin de la partie : l'arrivee de sa session, pas l'assemblage du passeport.</summary>
    private DateTime _finDeLaSession = DateTime.UtcNow;

    private static JsonArray Points(IEnumerable<(long frame, long total)> points)
        => new(points.Select(p => (JsonNode?)new JsonArray(JsonValue.Create(p.frame), JsonValue.Create(p.total))).ToArray());

    private static List<(long frame, long total)> LirePoints(JsonNode? noeud)
        => noeud is JsonArray tableau
            ? tableau.OfType<JsonArray>().Where(p => p.Count >= 2).Select(p => ((long)p[0]!, (long)p[1]!)).ToList()
            : new List<(long frame, long total)>();

    /// <summary>
    /// Le brouillon d'une partie : tout ce que le passeport tirera de la mesure, fige a la fin de la
    /// partie. Le reseau (profil, ticket) et la signature du passeport viendront a l'envoi.
    /// </summary>
    private JsonObject NouveauBrouillon(
        string genre, string systemId, string romGroup, string sessionJson,
        string listenerSha, string? coreSha, string? memSha, string? contentSha, string? contentMd5, string? contentSha1,
        string? wrapperVersion, string? coreName, string? coreVersion, long pic,
        List<(long frame, long total)> run, List<(long frame, long total)> lectures, JsonArray? nvram, JsonObject? bios,
        RetroBat.Api.Scoring.ContexteDeJeu? contexte, int joueurs, (string Raison, long Frame)? finDuSolo,
        int? place, string? seance, string? replay, string? regle)
    {
        DateTime fin;
        JsonNode? ticketDuLancement;
        lock (_sync)
        {
            fin = _finDeLaSession;
            ticketDuLancement = _ticketDuLancement;
            _ticketDuLancement = null;
        }
        var joueur = _scoringSession?.Get();
        var difficulte = new JsonObject();
        foreach (var (cle, valeur) in contexte?.Difficulte ?? new Dictionary<string, int>()) difficulte[cle] = valeur;
        return new JsonObject
        {
            ["schema"] = RetroBat.Api.Scoring.BrouillonDeScore.Schema,
            ["id"] = Guid.NewGuid().ToString(),
            ["genre"] = genre,
            // La regle du profil choisi a la fin de la partie, pour MES RECORDS ; l'envoi rechoisit.
            ["regle"] = regle,
            ["fin_le"] = fin.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["cree_le"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["systeme"] = systemId,
            ["rom_group"] = romGroup,
            ["session"] = sessionJson,
            ["listener"] = listenerSha,
            ["coeur"] = coreSha,
            ["mem"] = memSha,
            ["contenu"] = new JsonObject { ["sha256"] = contentSha, ["md5"] = contentMd5, ["sha1"] = contentSha1 },
            ["wrapper"] = wrapperVersion,
            ["coeur_nom"] = coreName,
            ["coeur_version"] = coreVersion,
            ["pic"] = pic,
            ["run"] = Points(run),
            ["lectures"] = Points(lectures),
            ["nvram"] = nvram?.DeepClone(),
            ["bios"] = bios?.DeepClone(),
            ["mode"] = contexte?.Mode,
            ["difficulte"] = difficulte,
            ["joueurs"] = joueurs,
            ["fin_du_solo"] = finDuSolo is { } coupe ? new JsonObject { ["raison"] = coupe.Raison, ["frame"] = coupe.Frame } : null,
            ["place"] = place,
            ["seance"] = seance,
            ["joueur_de_session"] = joueur is null ? null : new JsonObject
            {
                ["code"] = joueur.PlayerCode, ["monde"] = joueur.World, ["salle"] = joueur.VenueName, ["ville"] = joueur.VenueCity,
                ["chaine"] = joueur.Channel, ["contest"] = joueur.ContestId, ["langue"] = joueur.Locale,
            },
            ["labo"] = RetroBat.Api.Scoring.ScoreLabLabMode.IsActive(fin, out _),
            ["replay"] = replay,
            ["apiexpose"] = CabinetState.Version,
            ["etat_wrapper"] = CabinetState.Wrapper,
            ["ticket_du_lancement"] = TicketValablePour(ticketDuLancement, fin) ? ticketDuLancement : null,
        };
    }

    /// <summary>
    /// Signe le brouillon, le pose sur le disque et l'envoie tout de suite. En ligne, rien ne change
    /// pour le joueur : verdict et annonce arrivent comme avant. Site muet : le brouillon reste, la
    /// boucle de fond le renverra, et le joueur l'apprend.
    /// </summary>
    private async Task PoserEtEnvoyerAsync(JsonObject brouillon, long score, CancellationToken ct)
    {
        try
        {
            using var cle = CngDeviceKey.OpenOrCreate(ScoringKeyName);
            RetroBat.Api.Scoring.BrouillonDeScore.Signer(brouillon, cle.KeyId, cle.SignB64Url);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scoring : brouillon impossible a signer.");
            Trace("STOP: brouillon impossible a signer : " + ex.Message);
            return;
        }

        var id = (string)brouillon["id"]!;
        var pose = _brouillons.Poser(brouillon);
        Trace(pose
            ? $"brouillon {id} pose : {score} points, fin de partie {(string?)brouillon["fin_le"]}"
            : $"brouillon {id} : le disque refuse le fichier, envoi direct sans filet");

        await _envoi.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (issue, _, _) = await EnvoyerUnBrouillonAsync(id, brouillon, annoncer: true, ct).ConfigureAwait(false);
            if (issue == RetroBat.Api.Scoring.IssueDEnvoi.Definitif)
            {
                if (pose) _brouillons.Retirer(id);
                return;
            }

            if (!pose)
            {
                Trace($"STOP: brouillon {id} ni envoye ni pose : score perdu");
                return;
            }

            var delai = _brouillons.Reporter(id);
            Trace($"brouillon {id} garde sur la borne, nouvel essai dans {delai.TotalSeconds:0} s");
            if (_esNotify is not null)
            {
                try { await _esNotify.NotifyAsync(string.Format(Texte("scoring_kept_offline"), ScoreAffiche(score, Langue())), ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogDebug(ex, "Scoring : annonce du brouillon garde impossible."); }
            }
        }
        finally
        {
            _envoi.Release();
        }
    }

    /// <summary>
    /// La boucle de fond : les brouillons restes en file repartent, du plus ancien au plus recent,
    /// chacun a son heure. Jamais pendant une partie (le reseau du joueur, et une annonce qui
    /// ramenerait ES devant). Au retour du site, une seule annonce pour tous les envois.
    /// </summary>
    private async Task BoucleDesBrouillonsAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await EnvoyerLesBrouillonsAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Scoring : tour de la file des brouillons en echec.");
                }
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task EnvoyerLesBrouillonsAsync(CancellationToken ct)
    {
        var attente = _brouillons.EnAttente();
        if (attente.Count == 0 || !attente.Any(b => _brouillons.EstDu(b.Id))) return;
        if (EmulatorForeground.EmulateurTourne()) return;

        await _envoi.WaitAsync(ct).ConfigureAwait(false);
        var envoyes = 0;
        int? meilleurRang = null;
        try
        {
            foreach (var (id, brouillon) in _brouillons.EnAttente())
            {
                if (!_brouillons.EstDu(id)) continue;
                if (EmulatorForeground.EmulateurTourne()) break;   // une partie commence : on reprendra apres
                var (issue, statut, rang) = await EnvoyerUnBrouillonAsync(id, brouillon, annoncer: false, ct).ConfigureAwait(false);
                if (issue == RetroBat.Api.Scoring.IssueDEnvoi.Definitif)
                {
                    _brouillons.Retirer(id);
                    if (statut is not null) envoyes++;
                    if (rang is { } r && (meilleurRang is null || r < meilleurRang)) meilleurRang = r;
                    continue;
                }

                var delai = _brouillons.Reporter(id);
                Trace($"brouillon {id} : site toujours muet, nouvel essai dans {delai.TotalSeconds:0} s");
                break;   // le site ne repond pas : inutile d'essayer les suivants maintenant
            }
        }
        finally
        {
            _envoi.Release();
        }

        if (envoyes > 0 && _esNotify is not null)
        {
            var message = meilleurRang is { } meilleur
                ? string.Format(Texte("scoring_deferred_sent_rank"), envoyes, meilleur)
                : string.Format(Texte("scoring_deferred_sent"), envoyes);
            Trace($"file des brouillons : {envoyes} partie(s) envoyee(s) au retour du site" + (meilleurRang is { } m ? $", meilleur rang {m}" : ""));
            try { await _esNotify.NotifyAsync(message, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Scoring : annonce des envois differes impossible."); }
        }
    }

    /// <summary>
    /// Un brouillon vers son verdict : verification de sa signature, profil, ticket, passeport signe,
    /// envoi. Rend l'issue, le statut du verdict et le rang quand il y en a un.
    /// </summary>
    private async Task<(RetroBat.Api.Scoring.IssueDEnvoi Issue, string? Statut, int? Rang)> EnvoyerUnBrouillonAsync(
        string id, JsonObject brouillon, bool annoncer, CancellationToken ct)
    {
        const RetroBat.Api.Scoring.IssueDEnvoi Retenter = RetroBat.Api.Scoring.IssueDEnvoi.ARetenter;
        using var cle = CngDeviceKey.OpenOrCreate(ScoringKeyName);
        if (!RetroBat.Api.Scoring.BrouillonDeScore.Verifier(brouillon, cle.SpkiDer))
        {
            Trace($"brouillon {id} : signature invalide (fichier modifie ou autre cle), mis de cote sans envoi");
            _logger?.LogWarning("Scoring : brouillon {Id} modifie ou signe par une autre cle, ecarte.", id);
            _brouillons.Ecarter(id, "refuses");
            return (RetroBat.Api.Scoring.IssueDEnvoi.Definitif, null, null);
        }

        var credential = ResolveCredential();
        if (string.IsNullOrEmpty(credential))
        {
            Trace($"brouillon {id} : pas de credential, nouvel essai plus tard");
            return (Retenter, null, null);
        }

        var systemId = (string?)brouillon["systeme"] ?? "";
        var romGroup = (string?)brouillon["rom_group"] ?? "";
        var multi = (string?)brouillon["genre"] == "multi";
        var uneVie = (string?)brouillon["genre"] == "1lc";
        var mode = (int?)brouillon["mode"];
        var (profils, duSite) = await ProfilsAsync(credential, systemId, romGroup, ct).ConfigureAwait(false);
        JsonElement? profile = multi
            ? RetroBat.Api.Scoring.ModesDeJeu.ChoisirProfilMulti(profils, mode)
            : uneVie
                ? RetroBat.Api.Scoring.ModesDeJeu.ChoisirProfil1LC(profils, mode)
                : RetroBat.Api.Scoring.ModesDeJeu.ChoisirProfil(profils, mode);
        if (profile is null)
        {
            if ((bool?)brouillon["labo"] == true)
            {
                profile = RetroBat.Api.Scoring.ScoreLabLabMode.PlaceholderProfile();
            }
            else if (duSite)
            {
                Trace($"brouillon {id} : plus aucun classement ouvert pour {romGroup}{(multi ? " " + CategorieMulti : uneVie ? " 1LC" : "")}, retire sans envoi");
                return (RetroBat.Api.Scoring.IssueDEnvoi.Definitif, null, null);
            }
            else
            {
                Trace($"brouillon {id} : profil de {romGroup} inconnu et site injoignable, nouvel essai plus tard");
                return (Retenter, null, null);
            }
        }

        var ticket = await TicketAsync(credential, ct).ConfigureAwait(false);
        // Le site muet : le ticket pris au lancement fait un passeport complet, qu'un relais pourra garder.
        if (ticket is null && brouillon["ticket_du_lancement"] is JsonObject secours
            && DateTime.TryParse((string?)brouillon["fin_le"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var finDuBrouillon)
            && TicketValablePour(secours, finDuBrouillon))
        {
            using var copieDuTicket = JsonDocument.Parse(secours.ToJsonString());
            ticket = copieDuTicket.RootElement.Clone();
            Trace($"brouillon {id} : site muet, passeport fait avec le ticket pris au lancement");
        }
        var deviceId = ticket is { } t && t.TryGetProperty("device_id", out var did) ? did.GetString() : null;
        if (ticket is null || string.IsNullOrEmpty(deviceId))
        {
            Trace($"brouillon {id} : ticket indisponible, nouvel essai plus tard");
            return (Retenter, null, null);
        }

        JsonObject passport;
        try
        {
            var difficulte = new Dictionary<string, int>(StringComparer.Ordinal);
            if (brouillon["difficulte"] is JsonObject d)
            {
                foreach (var (k, v) in d) if (v is JsonValue jv && jv.TryGetValue<int>(out var n)) difficulte[k] = n;
            }
            var contexte = multi ? null : new RetroBat.Api.Scoring.ContexteDeJeu(mode, difficulte);
            (string Raison, long Frame)? finDuSolo = brouillon["fin_du_solo"] is JsonObject f
                ? ((string?)f["raison"] ?? "", (long?)f["frame"] ?? 0)
                : null;
            NelfePlayScoringSessionService.SessionPlayer? joueur = brouillon["joueur_de_session"] is JsonObject j
                ? new NelfePlayScoringSessionService.SessionPlayer((string?)j["code"] ?? "", (string?)j["monde"] ?? "home",
                    (string?)j["salle"], (string?)j["ville"], (string?)j["chaine"], (string?)j["contest"], (string?)j["langue"])
                : null;
            var contenu = brouillon["contenu"] as JsonObject;
            var fin = DateTime.Parse((string)brouillon["fin_le"]!, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

            passport = BuildPassport(
                systemId, romGroup, (string?)brouillon["session"] ?? "{}", ticket.Value, profile.Value,
                deviceId!, cle, (string?)brouillon["listener"] ?? "", (string?)brouillon["coeur"], (string?)brouillon["mem"],
                (string?)contenu?["sha256"], (string?)contenu?["md5"], (string?)contenu?["sha1"], (string?)brouillon["wrapper"],
                (string?)brouillon["coeur_nom"], (string?)brouillon["coeur_version"],
                (long?)brouillon["pic"] ?? 0, LirePoints(brouillon["run"]), LirePoints(brouillon["lectures"]),
                brouillon["nvram"]?.DeepClone() as JsonArray, brouillon["bios"]?.DeepClone() as JsonObject,
                contexte, (int?)brouillon["joueurs"] ?? 1, finDuSolo, (int?)brouillon["place"], (string?)brouillon["seance"],
                sessionId: id, finUtc: fin, joueurFige: (joueur, true), labo: (bool?)brouillon["labo"] == true,
                versionApi: (string?)brouillon["apiexpose"], etatWrapper: (string?)brouillon["etat_wrapper"],
                arretDuReplay: (long?)brouillon["arret_du_replay"]);
            var corps = passport.DeepClone()!.AsObject();
            corps.Remove("signature");
            passport["signature"] = cle.SignB64Url(Jcs.CanonicalBytes(corps));
        }
        catch (Exception ex)
        {
            // Un brouillon qu'on ne sait pas assembler ne s'assemblera pas mieux plus tard.
            _logger?.LogWarning(ex, "Scoring : assemblage du passeport du brouillon {Id} impossible.", id);
            Trace($"brouillon {id} : assemblage impossible ({ex.Message}), mis de cote");
            _brouillons.Ecarter(id, "refuses");
            return (RetroBat.Api.Scoring.IssueDEnvoi.Definitif, null, null);
        }

        var essais = _brouillons.Essais(id);
        Trace($"brouillon {id} : envoi{(essais > 0 ? $" (essai {essais + 1})" : "")}, {(string?)brouillon["genre"]} {romGroup} {(long?)brouillon["pic"]} points");
        return await SubmitAsync(credential, passport, (string?)brouillon["replay"], annoncer, ct).ConfigureAwait(false);
    }

    /// <summary>Un ticket de soumission, ou null quand le site ne le donne pas maintenant.</summary>
    private async Task<JsonElement?> TicketAsync(string credential, CancellationToken cancellationToken)
    {
        await EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var appel = await AppelerLeCentralAsync(HttpMethod.Post, "scores/ticket", null, null, null, credential,
                garder: false, resume: null, cancellationToken).ConfigureAwait(false);
            if (appel is not { } reponse || reponse.Statut is < 200 or >= 300)
            {
                Trace(appel is { } r ? $"ticket : HTTP {r.Statut}{r.Voie}" : "ticket : site injoignable");
                return null;
            }
            using var doc = JsonDocument.Parse(reponse.Corps);
            return doc.RootElement.TryGetProperty("ticket", out var ticket) ? ticket.Clone() : null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Scoring : demande de ticket impossible.");
            Trace($"ticket : site injoignable ({ex.GetType().Name})");
            return null;
        }
    }

    // ── Lien replay ↔ score ──────────────────────────────────────────────────

    // Le recorder annonce le replay en cours : on retient son id (gardé même après
    // finalize, le temps qu'un score de fin de partie arrive).
    private void CaptureActiveReplay(JsonElement payload)
    {
        var id = GetString(payload, "ReplayId");
        if (string.IsNullOrEmpty(id)) return;
        lock (_sync)
        {
            _activeReplayId = id;
            _enregistrementEnCours = true;
            _monteeDansLEnregistrement = false;
            _attenteMontee = false;
            _baisse.Oublier();
            _enregistrements.Add((id!, _lastFrame, null));
            if (_enregistrements.Count > 20) _enregistrements.RemoveAt(0);
        }
    }

    // Replay finalisé (objet scellé) : on retient son sha puis on tente le
    // rapprochement (un score « published » a pu arriver avant OU après).
    private void OnReplayFinalized(JsonElement payload)
    {
        var id = GetString(payload, "ReplayId");
        var sha = GetString(payload, "Sha256");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(sha)) return;
        lock (_sync)
        {
            if (string.Equals(id, _activeReplayId, StringComparison.Ordinal)) _enregistrementEnCours = false;
            for (var i = 0; i < _enregistrements.Count; i++)
            {
                if (string.Equals(_enregistrements[i].Id, id, StringComparison.Ordinal) && _enregistrements[i].Fin is null)
                {
                    _enregistrements[i] = (_enregistrements[i].Id, _enregistrements[i].Debut, _lastFrame);
                }
            }
            PruneReplayLinks(); _finalizedReplay[id!] = (sha!, DateTime.UtcNow); SauverLesLiens();
        }
        TryRegisterReplayLink(id!);
        TryRegisterReplayLink(id + LienDu1LC);
    }

    // Score PUBLIÉ : le record est public → son replay le devient aussi (il s'affiche
    // sur le classement). On rattache le score au replay ACTIF (celui de cette partie).
    /// <summary>Le replay du meilleur run du passeport solo, choisi a la soumission.</summary>
    private string? _replayDuMeilleurRun;

    /// <summary>
    /// L'enregistrement qui contient le pic du run (le run est monotone : son pic est sa derniere
    /// lecture). Le dernier qui le couvre, s'il y en a plusieurs ; null si aucun.
    /// </summary>
    internal static string? ReplayDuMeilleurRun(IReadOnlyList<(string Id, long Debut, long? Fin)> enregistrements,
        IReadOnlyList<(long frame, long total)> run)
    {
        if (run.Count == 0) return null;
        var pic = run[^1].frame;
        string? choisi = null;
        foreach (var e in enregistrements)
        {
            if (e.Debut <= pic && (e.Fin is null || pic <= e.Fin)) choisi = e.Id;
        }
        return choisi;
    }

    private void CaptureReplayLinkOnPublished(JsonObject passport, string responseBody, string? replayId)
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

            // Le replay vient du brouillon : celui du meilleur run pour le solo, celui de la partie
            // pour le 1CC MULTI, choisi a la fin de la partie et non plus au moment du verdict.
            if (string.IsNullOrEmpty(replayId)) return;
            // Le 1LC partage le replay du 1CC : son rapprochement a sa propre cle.
            var cle = RetroBat.Api.Scoring.ModesDeJeu.Est1LC((string?)(passport["game"] as JsonObject)?["ruleset"])
                ? replayId + LienDu1LC
                : replayId!;
            lock (_sync)
            {
                PruneReplayLinks();
                _pendingScoreLink[cle] = (sessionId!, "public", score, rank, DateTime.UtcNow);
                SauverLesLiens();
            }
            TryRegisterReplayLink(cle);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Replay-link : capture du score publié impossible.");
        }
    }

    // Rapprochement : quand le score publié ET le replay finalisé sont là pour le même id, on
    // enregistre le lien. Les deux entrees ne partent qu'a la REPONSE du serveur : une coupure
    // reseau ou un arret de l'API laisse le lien sur disque, et il repart au tour suivant.
    private void TryRegisterReplayLink(string cle)
    {
        var replayId = ReplayDuLien(cle);
        string sessionId, visibility, sha;
        long? score; int? rank;
        lock (_sync)
        {
            if (_liensEnCours.Contains(cle)) return;
            if (!_pendingScoreLink.TryGetValue(cle, out var p)) return;
            if (!_finalizedReplay.TryGetValue(replayId, out var f)) return;
            sessionId = p.sessionId; visibility = p.visibility; score = p.score; rank = p.rank; sha = f.sha256;
            _liensEnCours.Add(cle);
        }
        // La carte du replay reste celle du 1CC : le 1LC ne la reecrit pas.
        if (string.Equals(cle, replayId, StringComparison.Ordinal)) StampReplayCard(replayId, score, rank);
        // Le semis est idempotent (la file ignore un replay deja inscrit) : il part tout de suite.
        if (string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase)) SemerReplayCertifie(replayId, sha);
        _ = Task.Run(async () =>
        {
            var termine = await RegisterReplayLinkAsync(sessionId, replayId, sha, visibility, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                _liensEnCours.Remove(cle);
                if (termine)
                {
                    _pendingScoreLink.Remove(cle);
                    // L'objet finalise sert encore tant qu'un autre score du meme replay attend.
                    if (!_pendingScoreLink.Keys.Any(k => string.Equals(ReplayDuLien(k), replayId, StringComparison.Ordinal)))
                        _finalizedReplay.Remove(replayId);
                    SauverLesLiens();
                }
            }
        });
    }

    /// <summary>
    /// LE 1LC PARTAGE LE REPLAY DU 1CC (2026-10-09) : deux scores, deux sessions, un seul replay. Le
    /// rapprochement du 1LC se range sous « replay|1lc » ; l'objet finalise reste sous le replay.
    /// </summary>
    private const string LienDu1LC = "|1lc";

    /// <summary>Le replay d'une cle de rapprochement.</summary>
    internal static string ReplayDuLien(string cle)
    {
        var barre = cle.IndexOf('|');
        return barre < 0 ? cle : cle[..barre];
    }

    /// <summary>Retente les liens complets restes en attente (redemarrage, reseau coupe).</summary>
    private void RessayerLesLiens()
    {
        List<string> complets;
        lock (_sync)
        {
            complets = _pendingScoreLink.Keys
                .Where(cle => _finalizedReplay.ContainsKey(ReplayDuLien(cle)) && !_liensEnCours.Contains(cle))
                .ToList();
        }
        foreach (var id in complets) TryRegisterReplayLink(id);
    }

    private sealed record LienEnAttente(
        string ReplayId, string? SessionId, string? Visibility, long? Score, int? Rank, DateTime? ScoreAt,
        string? Sha256, DateTime? FinalizedAt);

    /// <summary>Appele sous _sync. Ecrit les rapprochements en attente, d'un bloc (fichier temporaire puis remplacement).</summary>
    private void SauverLesLiens()
    {
        try
        {
            var liens = _pendingScoreLink.Keys.Union(_finalizedReplay.Keys).Select(id =>
            {
                var p = _pendingScoreLink.TryGetValue(id, out var x) ? x : ((string, string, long?, int?, DateTime)?)null;
                var f = _finalizedReplay.TryGetValue(id, out var y) ? y : ((string, DateTime)?)null;
                return new LienEnAttente(id, p?.Item1, p?.Item2, p?.Item3, p?.Item4, p?.Item5, f?.Item1, f?.Item2);
            }).ToList();
            var chemin = CheminDesLiens;
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            var temporaire = chemin + ".tmp";
            File.WriteAllText(temporaire, JsonSerializer.Serialize(liens), new UTF8Encoding(false));
            File.Move(temporaire, chemin, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Replay-link : rapprochements non ecrits sur disque.");
        }
    }

    /// <summary>Appele sous _sync. Relit les rapprochements d'avant le redemarrage.</summary>
    private void ChargerLesLiens()
    {
        try
        {
            var chemin = CheminDesLiens;
            if (!File.Exists(chemin)) return;
            var liens = JsonSerializer.Deserialize<List<LienEnAttente>>(File.ReadAllText(chemin)) ?? new();
            foreach (var l in liens)
            {
                if (string.IsNullOrEmpty(l.ReplayId)) continue;
                if (!string.IsNullOrEmpty(l.SessionId) && l.ScoreAt is { } quand)
                    _pendingScoreLink[l.ReplayId] = (l.SessionId!, l.Visibility ?? "public", l.Score, l.Rank, quand);
                if (!string.IsNullOrEmpty(l.Sha256) && l.FinalizedAt is { } scelle)
                    _finalizedReplay[l.ReplayId] = (l.Sha256!, scelle);
            }
            PruneReplayLinks();
            if (liens.Count > 0) Trace($"REPLAY-LINK {liens.Count} rapprochement(s) repris du disque");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Replay-link : rapprochements illisibles sur disque.");
        }
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

    // Appelé sous _sync : oublie les rapprochements jamais complétés au bout de sept jours
    // (partie sans score publié, ou replay jamais finalisé).
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

    /// <summary>
    /// Envoie le lien. Rend vrai quand le serveur a REPONDU (lien pose, ou refus definitif :
    /// score inconnu, pas a cette borne), faux s'il faut retenter (reseau, serveur indisponible).
    /// </summary>
    private async Task<bool> RegisterReplayLinkAsync(
        string sessionId, string replayId, string sha256, string visibility, CancellationToken cancellationToken,
        string? secret = null)
    {
        var credential = secret ?? ResolveCredential();
        if (string.IsNullOrEmpty(credential)) return false;
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
            if (response.IsSuccessStatusCode) NoterLeLienDansLeRecord(sessionId, body);
            return response.IsSuccessStatusCode || (int)response.StatusCode is >= 400 and < 500 and not 408 and not 429;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Replay-link : envoi impossible, nouvel essai plus tard.");
            return false;
        }
    }

    /// <summary>
    /// Garde le lien replay dans le record de la partie (certified/) : si la base du site est
    /// restaurée d'avant ce lien, l'épisode de récupération le redéclare avec la partie.
    /// </summary>
    private static void NoterLeLienDansLeRecord(string sessionId, JsonObject lien)
    {
        try
        {
            var chemin = System.IO.Path.Combine(CertifiedDir(), sessionId + ".json");
            if (!System.IO.File.Exists(chemin)) chemin += ".sent";
            if (!System.IO.File.Exists(chemin)) return;
            if (JsonNode.Parse(System.IO.File.ReadAllText(chemin)) is not JsonObject record) return;
            record["replay_link"] = lien.DeepClone();
            System.IO.File.WriteAllText(chemin, record.ToJsonString(), new UTF8Encoding(false));
        }
        catch { /* le lien est pose sur le site ; seul son double local manque */ }
    }

    private static JsonElement ToJson(object? payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;


}
