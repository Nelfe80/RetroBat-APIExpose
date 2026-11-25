using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Client Live Contest natif : une fois le playToken remis par la page de
/// participation (POST /api/v1/livecontest/enroll), CE service orchestre tout
/// en local - la page web ne pilote rien. Il interroge la plateforme en
/// HTTPS SORTANT uniquement : resolution du jeu dans la ludotheque LOCALE
/// (le chemin du streamer ne circule jamais), lancement via EmulationStation,
/// pause au signal GAME_PLAYING (vraie partie), ready, comptage natif de la
/// metrique sur le bus d'evenements, depart simultane a startAt, push de la
/// progression, submit automatique, overlay in-game et fermeture de RetroArch.
///
/// UN CONCOURS SUR SCORE CERTIFIE (15/11) : <c>/play/contest</c> porte <c>certified {game, system, ruleset}</c>. Le jeu
/// est alors celui de la collection World Scoring pour ce classement (<see cref="LiveContestCertifie"/>), lance comme une
/// partie NelfePlay (cœur impose), avec la seance {stream, chaine, concours} du joueur LIE ; le client ne compte plus
/// rien, c'est le scoring certifie qui envoie la partie. Un jeu, un mode ou un compte qui manque se refuse. Un concours
/// declare (<c>certified</c> null) part comme avant, par son <c>gameSlug</c>.
///
/// UN CONCOURS SUR UNE EPREUVE (15/11) : <c>certified.challenge</c> nomme une epreuve de l'index des defis
/// (<see cref="LiveContestEpreuve"/>). Elle se lance AU DEPART, pour tout le monde en meme temps, par le lancement type d'un defi
/// (<see cref="RetroBat.Api.Speedrun.SpeedrunSessionService"/>) : pas de pause d'attente, la session du defi la leverait (une
/// pause tenue relance la tentative), et le temps de l'epreuve se compte en images, pas depuis le GO. La seance du concours
/// s'arme au lancement et tient jusqu'au verdict de l'epreuve. A la cloture, le jeu se ferme : le meilleur temps du lancement
/// part, certifie, au classement de l'epreuve, avec le concours.
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
    private bool _quitDone;
    private double _value;
    private double _lastSent = -1;
    private long _seq;
    private long _startAtMs;
    private string _phase = "idle";
    private string? _system, _slug, _resolvedRom, _lastError, _brief;
    private string _reportedPhase = "";

    // concours sur score certifie : le classement de NelfePlay, jamais une valeur comptee ici
    private LiveContestCertifie.Preparation? _certifie;
    // concours sur une epreuve : l'epreuve de l'index des defis, lancee au depart
    private LiveContestEpreuve.Preparation? _epreuve;
    private string? _channel;
    private readonly RetroBat.Api.Speedrun.SpeedrunSessionService? _speedrun;
    private readonly RetroBat.Api.Speedrun.CatalogueDesDefis? _defis;
    private string? _contestId;
    private readonly SeanceDuConcoursCertifie _seanceCertifiee;
    private readonly NelfePlayScoringCollectionSyncService? _collection;
    private readonly IGamelistStore? _gamelists;
    private readonly RomCanonicalResolver? _canonical;

    private readonly NelfePlayScoringSessionService _scoringSession;
    private readonly NelfePlayAgentService _agent;

    private readonly PartieNelfePlayService? _partie;

    public LiveContestClientService(
        IEventBus eventBus,
        LiveContestOverlayService overlay,
        IHttpClientFactory httpFactory,
        NelfePlayScoringSessionService scoringSession,
        NelfePlayAgentService agent,
        ILogger<LiveContestClientService> logger,
        PartieNelfePlayService? partie = null,
        NelfePlayScoringCollectionSyncService? collection = null,
        IGamelistStore? gamelists = null,
        RomCanonicalResolver? canonical = null,
        RetroBat.Api.Speedrun.SpeedrunSessionService? speedrun = null,
        RetroBat.Api.Speedrun.CatalogueDesDefis? defis = null)
    {
        _speedrun = speedrun;
        _defis = defis;
        _partie = partie;
        _collection = collection;
        _gamelists = gamelists;
        _canonical = canonical;
        _seanceCertifiee = new SeanceDuConcoursCertifie(scoringSession);
        _eventBus = eventBus;
        _overlay = overlay;
        _httpFactory = httpFactory;
        _scoringSession = scoringSession;
        _agent = agent;
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
                lastError = _lastError,
                // le classement NelfePlay d'un concours certifie ; null pour un concours declare. `challenge` : l'epreuve d'un
                // concours sur epreuve, null pour un concours sur score.
                certified = CertifiePourLeStatut(_certifie, _epreuve)
            };
        }
    }

    /// <summary>Le bloc <c>certified</c> de <see cref="Status"/> : null pour un concours declare. Pur.</summary>
    internal static object? CertifiePourLeStatut(LiveContestCertifie.Preparation? certifie, LiveContestEpreuve.Preparation? epreuve)
        => epreuve is { } e
            ? new { game = e.Jeu, system = e.Systeme, ruleset = e.Regle, challenge = (string?)e.Epreuve }
            : certifie is { } c
                ? new { game = c.Jeu, system = c.Systeme, ruleset = c.Regle, challenge = (string?)null }
                : null;

    /// <summary>
    /// Option RetroBat « ENABLE LIVE CONTEST » (menus ES -> es_settings) :
    /// absente ou a 1 = actif ; a 0 = les inscriptions sont refusees.
    /// </summary>
    public bool IsLiveContestEnabled()
    {
        try
        {
            var esSettings = Path.Combine(RetroBatPaths.RetroBatRoot,
                "emulationstation", ".emulationstation", "es_settings.cfg");
            if (!File.Exists(esSettings))
            {
                return true;
            }

            return !File.ReadAllText(esSettings).Contains(
                "name=\"global.apiexpose.game_events.live_contest.enabled\" value=\"0\"",
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return true;
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

        // option appliquee des que le Live Contest devient actif
        DisablePauseNonactive();
        _logger.LogInformation("livecontest : inscription recue ({Platform})", platformBase);
        _overlay.Show(null, T("Inscription reçue !", "Enrolled!"), T("Préparation de ton jeu…", "Getting your game ready…"), 6000);
    }

    public void Withdraw() => Withdraw(garderLaSeance: false);

    /// <param name="garderLaSeance">Concours certifie clos : la seance reste armee jusqu'a son score
    /// (<see cref="SeanceDuConcoursCertifie"/>).</param>
    private void Withdraw(bool garderLaSeance)
    {
        lock (_gate)
        {
            _enrollment = null;
            ResetRound(garderLaSeance);
            SaveEnrollment();
        }

        _overlay.Hide();
    }

    private void ResetRound(bool garderLaSeance = false)
    {
        _prepared = _ready = _started = _finished = false;
        _waitingGamePlaying = false;
        _targetReached = false;
        _quitDone = false;
        _metricTarget = 0;
        _value = 0;
        _lastSent = -1;
        _seq = 0;
        _startAtMs = 0;
        _phase = "idle";
        _certifie = null;
        _epreuve = null;
        _channel = null;
        _contestId = null;
        if (garderLaSeance)
        {
            return;
        }

        _seanceCertifiee.Oublier();

        // ÉTAGE 3 - on désarme la session STREAM du contest (désenrôlement /
        // ré-enrôlement). On ne touche PAS une session STATION (armée par le hub) :
        // Live Contest et check-in salle sont mutuellement exclusifs, mais on reste
        // prudent pour ne jamais clobber le contexte salle.
        if (_scoringSession.Get()?.World == "stream")
        {
            _scoringSession.Clear();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _eventBus.Subscribe<EventEnvelope>(OnBusEvent);
        if (_enrollment is not null)
        {
            // Live Contest deja actif au demarrage d'APIExpose : l'option
            // anti-pause est (re)appliquee dynamiquement
            DisablePauseNonactive();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_seanceCertifiee.Echeance())
                {
                    _logger.LogInformation("livecontest : concours certifie clos, seance desarmee a l'echeance");
                }

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
        var (statut, doc) = await LireLeConcoursAsync(http, enrollment, cancellationToken);
        if (statut == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogInformation("livecontest : playToken expire, desinscription");
            Withdraw();
            return;
        }

        if (doc is null)
        {
            return;
        }

        using var concours = doc;
        var root = concours.RootElement;
        var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        _metricKind = Get(root, "objective", "metric", "kind") ?? "score";
        _metricSignal = Get(root, "objective", "metric", "signal");
        _brief = Get(root, "brief");
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
            // START, la pause tombe sur GAME_PLAYING - chacun se met pret a
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
                // Une epreuve part au depart, sans pause d'attente ; un concours sur score depause le jeu deja pret.
                _ = _epreuve is not null ? DepartDeLEpreuveAsync(cancellationToken) : CountdownAndGoAsync(cancellationToken);
                break;

            case "closed":
                await FinishAsync(enrollment, cancellationToken);
                break;
        }

        if (_started && !_finished && Math.Abs(_value - _lastSent) > 0.0001)
        {
            await PushProgressAsync(enrollment, cancellationToken);
        }

        if (_phase != _reportedPhase)
        {
            _reportedPhase = _phase;
            _ = ReportPhaseAsync(enrollment, _phase, _lastError);
        }
    }

    /// <summary>
    /// <c>GET /play/contest</c> : le statut, et le concours (null hors d'un 2xx lisible).
    /// </summary>
    internal static async Task<(System.Net.HttpStatusCode Statut, JsonDocument? Concours)> LireLeConcoursAsync(
        HttpClient http, Enrollment enrollment, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, enrollment.PlatformBase + "/play/contest");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrollment.PlayToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        try
        {
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)));
        }
        catch (JsonException)
        {
            return (response.StatusCode, null);
        }
    }

    /// <summary>
    /// CE QUE LA MANCHE VA LANCER. Un concours certifie (<c>certified</c> present) : le jeu du classement, ou son refus ;
    /// jamais de repli sur le <c>gameSlug</c>. Un concours declare : systeme + <c>gameSlug</c>, comme avant.
    /// </summary>
    internal sealed record Plan(
        LiveContestCertifie.Preparation? Certifie, string? System, string? Slug, string? ContestId, string? Channel,
        LiveContestEpreuve.Preparation? Epreuve = null)
    {
        public bool EstCertifie => Certifie is not null || Epreuve is not null;

        /// <summary>Un concours sur une epreuve (<c>certified.challenge</c>).</summary>
        public bool EstUneEpreuve => Epreuve is not null;
    }

    internal static Plan Planifier(JsonElement contest, string? playerCode,
        Func<LiveContestCertifie.Demande, LiveContestCertifie.Preparation> resoudre,
        Func<LiveContestCertifie.Demande, LiveContestEpreuve.Preparation>? resoudreLEpreuve = null)
    {
        var contestId = Get(contest, "contestId");
        var channel = Get(contest, "channel");
        if (LiveContestCertifie.Lire(contest) is not { } demande)
        {
            return new Plan(null, Get(contest, "system"), Get(contest, "gameSlug"), contestId, channel);
        }

        // UN CONCOURS SUR UNE EPREUVE : memes exigences (compte lie, concours identifie), puis l'epreuve de l'index des defis.
        if (demande.EstUneEpreuve)
        {
            var epreuve = string.IsNullOrWhiteSpace(playerCode)
                ? LiveContestEpreuve.Refuser(demande, LiveContestCertifie.RefusSansCompte, "machine non liee a un compte NelfePlay")
                : string.IsNullOrWhiteSpace(contestId)
                    ? LiveContestEpreuve.Refuser(demande, LiveContestCertifie.RefusIncomplet, "concours sans identifiant")
                    : resoudreLEpreuve?.Invoke(demande)
                        ?? LiveContestEpreuve.Refuser(demande, LiveContestEpreuve.RefusPasPrete, "pas de defis sur cette borne");
            return new Plan(null, epreuve.Systeme, epreuve.Jeu, contestId, channel, epreuve);
        }

        // Le compte d'abord : une partie d'une machine non liee est certifiee mais anonyme, et le classement du concours
        // ne garde que les scores qui ont un proprietaire. Lancer quand meme ferait jouer pour rien.
        var preparation = string.IsNullOrWhiteSpace(playerCode)
            ? new LiveContestCertifie.Preparation(null, demande.Systeme, demande.Jeu, demande.Regle,
                LiveContestCertifie.RefusSansCompte, "machine non liee a un compte NelfePlay")
            : string.IsNullOrWhiteSpace(contestId)
                ? new LiveContestCertifie.Preparation(null, demande.Systeme, demande.Jeu, demande.Regle,
                    LiveContestCertifie.RefusIncomplet, "concours sans identifiant")
                : resoudre(demande);
        return new Plan(preparation, preparation.Systeme, preparation.Jeu, contestId, channel);
    }

    /// <summary>La resolution sur CETTE borne : la collection World Scoring, son index et ses manques.</summary>
    private LiveContestCertifie.Preparation Resoudre(LiveContestCertifie.Demande demande)
        => LiveContestCertifie.Resoudre(
            demande,
            _collection?.JeuxOuverts(),
            _collection?.CheminsDuWorldScoring,
            _gamelists is not null && _canonical is not null
                ? (systeme, jeu) => RomDuGroupe.Trouver(_gamelists, _canonical, systeme, jeu)
                : null,
            chemin => File.Exists(chemin) || Directory.Exists(chemin),
            _collection?.Status.Missing);

    /// <summary>L'epreuve du concours sur CETTE borne : l'index des defis et le fichier que World Scoring a retenu pour ses defis.</summary>
    private LiveContestEpreuve.Preparation ResoudreLEpreuve(LiveContestCertifie.Demande demande)
        => LiveContestEpreuve.Resoudre(demande, _defis?.Tous ?? [], RomDeLEpreuve);

    /// <summary>Le fichier d'une epreuve, celui du panneau et de la route de lancement (<see cref="RetroBat.Api.Speedrun.RomDeLEpreuve"/>).</summary>
    private string? RomDeLEpreuve(RetroBat.Api.Speedrun.Reglement reglement)
        => RetroBat.Api.Speedrun.RomDeLEpreuve.Choisir(_collection?.CheminsParJeu, reglement.SystemId, reglement.RomGroup,
            () => RomAdmission.ReferenceDuSpeedrun(reglement.SystemId, reglement.RomGroup, RomAdmission.ReferentielSurDisque.Borne) is not null,
            () => _gamelists is not null && _canonical is not null
                ? RomDuGroupe.Trouver(_gamelists, _canonical, reglement.SystemId, reglement.RomGroup)
                : null,
            chemin => File.Exists(chemin) || Directory.Exists(chemin));

    /// <summary>Le client compte-t-il lui-meme la metrique ? Jamais pour un concours certifie : le scoring envoie la partie.</summary>
    internal static bool CompteLaMetrique(bool certifie, bool demarre) => demarre && !certifie;

    /// <summary>Le streamer voit la phase de chaque joueur (jeu introuvable...).</summary>
    private async Task ReportPhaseAsync(Enrollment enrollment, string phase, string? detail)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                enrollment.PlatformBase + "/play/phase");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrollment.PlayToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { phase, detail }),
                Encoding.UTF8, "application/json");
            await http.SendAsync(request, CancellationToken.None);
        }
        catch (Exception)
        {
            _reportedPhase = ""; // reessaiera au prochain changement/tick
        }
    }

    /// <summary>Lock streamer : lancer le jeu local, inviter a appuyer sur START.</summary>
    private async Task PrepareAsync(JsonElement contest, CancellationToken cancellationToken)
    {
        _phase = "preparing";
        var plan = Planifier(contest, _agent.Status.PlayerCode, Resoudre, ResoudreLEpreuve);
        if (plan.EstUneEpreuve)
        {
            PrepareEpreuve(plan);
            return;
        }

        if (plan.EstCertifie)
        {
            await PrepareCertifieAsync(plan, cancellationToken);
            return;
        }

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

        // ÉTAGE 3 - armer la session certifiée STREAM pour ce contest : le record
        // joué portera context.world=stream + channel + contest_id, attribué au
        // compte RGPC LIÉ (playerCode du /link). Machine non liée → playerCode null
        // → record stream anonyme (le contest marche quand même). La session sera
        // effacée à la fin de la manche (submit) ou au désenrôlement.
        var channel = Get(contest, "channel");
        var contestId = Get(contest, "contestId");
        if (!string.IsNullOrWhiteSpace(contestId))
        {
            _scoringSession.Set(_agent.Status.PlayerCode, null, null, "stream", channel, contestId);
            _logger.LogInformation(
                "livecontest : session STREAM armée (contest={Contest} channel={Channel} player={Player})",
                contestId, channel, _agent.Status.PlayerCode ?? "(non lié)");
        }

        DisablePauseNonactive();
        ForceAutoEmulator(system); // meme emulateur (RetroArch) pour tous
        var romPath = _resolvedRom = ResolveLocalRom(system, slug);
        if (romPath is null)
        {
            _phase = "rom-not-found";
            _lastError = "aucun fichier ne matche « " + slug + " » dans roms\\" + system;
            _overlay.Show(null, T("Jeu introuvable dans ta ludothèque", "Game not found in your library"),
                slug + " (" + system + ") - " + T("lance-le toi-même puis appuie sur START.", "launch it yourself then press START."), 0);
        }
        else
        {
            _overlay.Show(null, T("Lancement du jeu…", "Launching the game…"), slug, 0);
            await LancerParEsAsync(romPath, cancellationToken);
        }

        _ = FocusRetroArchWhenUpAsync();
        _waitingGamePlaying = true;
        _phase = "press-start";
        _overlay.Show(null, T("Appuie sur START !", "Press START!"),
            T("Ta partie sera mise en pause, prête pour le départ…", "Your run will be paused, ready for the countdown…"), 0);
    }

    /// <summary>
    /// Le jeu par EmulationStation, comme une partie NelfePlay : annonce « concours », cœur de la collection impose a la
    /// fiche du jeu (FBNeo pour un set qu'il connait), puis <c>/launch</c>.
    /// </summary>
    private async Task LancerParEsAsync(string romPath, CancellationToken cancellationToken)
    {
        FocusProcess("emulationstation"); // sinon RetroArch demarre en arriere-plan
        try
        {
            using var es = new HttpClient { BaseAddress = EmulationStationBaseUri };
            using var content = new StringContent(romPath, Encoding.UTF8, "text/plain");
            // Une partie de concours est une partie NelfePlay, d'ou que le carrousel la lance.
            _partie?.AnnoncerLancement("concours");
            if (_partie is not null)
            {
                await _partie.ImposerLeCoeurAsync(romPath, cancellationToken);
            }
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

    /// <summary>
    /// UN CONCOURS CERTIFIE : le jeu du classement, lance comme un lancement World Scoring, avec la seance du concours sur
    /// le joueur lie. L'emulateur du systeme n'est pas force en AUTO (reglage du joueur) : le cœur est celui que la
    /// collection impose a la fiche du jeu. Un refus se dit, et rien ne se lance.
    /// </summary>
    private async Task PrepareCertifieAsync(Plan plan, CancellationToken cancellationToken)
    {
        var preparation = plan.Certifie!;
        _certifie = preparation;
        _contestId = plan.ContestId;
        _system = preparation.Systeme;
        _slug = preparation.Jeu;
        _resolvedRom = preparation.Rom;
        var classement = RetroBat.Api.Scoring.ModesDeJeu.NomDuClassement(preparation.Regle);
        if (!preparation.Prete)
        {
            _phase = preparation.Refus ?? LiveContestCertifie.RefusIncomplet;
            _lastError = preparation.Detail;
            _logger.LogWarning("livecontest : concours certifie refuse ({Refus}) : {Detail}", _phase, preparation.Detail);
            var (titre, sousTitre) = MessageDeRefus(preparation, classement);
            _overlay.Show(null, titre, sousTitre, 0);
            return;
        }

        if (_seanceCertifiee.Armer(_agent.Status.PlayerCode, plan.Channel, plan.ContestId))
        {
            _logger.LogInformation(
                "livecontest : concours certifie {Contest} : {Systeme}/{Jeu} en {Regle}, seance STREAM armee (channel={Channel})",
                plan.ContestId, preparation.Systeme, preparation.Jeu, preparation.Regle, plan.Channel);
        }

        DisablePauseNonactive();
        _overlay.Show(null, T("Lancement du jeu…", "Launching the game…"), preparation.Jeu + " · " + classement, 0);
        await LancerParEsAsync(preparation.Rom!, cancellationToken);

        string? corps = null;
        try
        {
            var copie = NelfePlayScoringReporter.CheminDuProfil(preparation.Systeme, preparation.Jeu);
            if (File.Exists(copie))
            {
                corps = await File.ReadAllTextAsync(copie, cancellationToken);
            }
        }
        catch (IOException)
        {
        }

        var mode = LiveContestCertifie.ModeAChoisir(corps, preparation.Regle, Fr);
        _ = FocusRetroArchWhenUpAsync();
        _waitingGamePlaying = true;
        _phase = "press-start";
        _overlay.Show(null, T("Appuie sur START !", "Press START!"),
            mode is null
                ? T("Classement " + classement + " : ta partie certifiée compte. Elle sera mise en pause, prête pour le départ…",
                    classement + " board: your certified run counts. It will be paused, ready for the countdown…")
                : T("Choisis le mode « " + mode + " » (classement " + classement + "). Ta partie sera mise en pause, prête pour le départ…",
                    "Pick the « " + mode + " » mode (" + classement + " board). Your run will be paused, ready for the countdown…"), 0);
    }

    /// <summary>Ce que le joueur lit quand un concours certifie ne peut pas partir.</summary>
    private static (string Titre, string SousTitre) MessageDeRefus(LiveContestCertifie.Preparation preparation, string classement)
        => preparation.Refus switch
        {
            LiveContestCertifie.RefusSansCompte => (T("Compte NelfePlay requis", "NelfePlay account required"),
                T("Ce concours se joue sur score certifié : lie ta borne à ton compte NelfePlay, puis rejoins-le.",
                  "This contest is played on certified scores: link your machine to your NelfePlay account, then join it.")),
            LiveContestCertifie.RefusModeInconnu or LiveContestCertifie.RefusModeNonJouable => (T("Mode introuvable", "Mode not found"),
                T("Le classement " + classement + " de " + preparation.Jeu + " n'est pas ouvert ici. Rien n'est lancé.",
                  "The " + classement + " board of " + preparation.Jeu + " is not open here. Nothing was launched.")),
            LiveContestCertifie.RefusRomAbsente => (T("Jeu introuvable dans ta collection World Scoring", "Game not found in your World Scoring collection"),
                (preparation.Detail ?? preparation.Jeu) + T(" - rien n'est lancé.", " - nothing was launched.")),
            LiveContestCertifie.RefusJeuFerme => (T("Jeu non ouvert au World Scoring", "Game not open to World Scoring"),
                preparation.Jeu + T(" - rien n'est lancé.", " - nothing was launched.")),
            _ => (T("Concours incomplet", "Incomplete contest"),
                T("Le streamer doit recréer le concours (jeu ou classement manquant).", "The streamer must recreate the contest (missing game or board).")),
        };

    /// <summary>
    /// UN CONCOURS SUR UNE EPREUVE, a la preparation : les controles de l'epreuve (index des defis, compte, version, fichier,
    /// .MEM, etat de depart), puis « pret ». Rien ne se lance avant le depart, et la seance du concours ne s'arme qu'a lui : une
    /// partie jouee avant le GO n'entre pas au concours. Un refus se dit, au joueur et au streamer (phase <c>certified-*</c>).
    /// </summary>
    private void PrepareEpreuve(Plan plan)
    {
        var preparation = plan.Epreuve!;
        _epreuve = preparation;
        _contestId = plan.ContestId;
        _channel = plan.Channel;
        _system = preparation.Systeme;
        _slug = preparation.Jeu;
        _resolvedRom = preparation.Rom;
        if (!preparation.Prete)
        {
            RefuserLEpreuve(preparation.Refus ?? LiveContestCertifie.RefusIncomplet, preparation.Detail, preparation.Epreuve);
            return;
        }

        // Les controles rapides du lancement type d'un defi, deja : un .MEM ou un etat de depart qui ne correspond pas se dit
        // pendant la preparation. Un jeu en cours ne se juge qu'au depart.
        if (_speedrun is null)
        {
            RefuserLEpreuve(LiveContestEpreuve.RefusPasPrete, "pas de session de defi sur cette borne", preparation.Epreuve);
            return;
        }

        var (_, refus) = _speedrun.Verifier(preparation.Reglement!.ChallengeId, (_, _) => preparation.Rom);
        if (refus is not null && refus.Raison != "game_running")
        {
            RefuserLEpreuve(LiveContestEpreuve.PhaseDuLancement(refus.Raison),
                refus.Raison + (string.IsNullOrEmpty(refus.Detail) ? "" : " : " + refus.Detail), preparation.Epreuve);
            return;
        }

        DisablePauseNonactive();
        _ready = true;
        _phase = "ready";
        _ = EnvoyerPretAsync();
        var titre = preparation.Reglement.Titre(Fr ? "fr" : "en");
        _logger.LogInformation("livecontest : concours {Contest} sur l'epreuve {Epreuve} ({Regle}), prete, lancee au depart",
            plan.ContestId, preparation.Epreuve, preparation.Regle);
        _overlay.Show(null, T("Épreuve : ", "Challenge: ") + titre,
            T("Elle se lance au départ, pour tout le monde en même temps. Ton meilleur temps certifié compte.",
              "It launches at the start, for everyone at once. Your best certified time counts."), 0);
    }

    private void RefuserLEpreuve(string phase, string? detail, string epreuve)
    {
        _phase = phase;
        _lastError = detail;
        _logger.LogWarning("livecontest : concours sur epreuve refuse ({Refus}) : {Detail}", phase, detail);
        var (titre, sousTitre) = phase switch
        {
            LiveContestCertifie.RefusSansCompte => (T("Compte NelfePlay requis", "NelfePlay account required"),
                T("Ce concours se joue sur une épreuve certifiée : lie ta borne à ton compte NelfePlay, puis rejoins-le.",
                  "This contest is played on a certified challenge: link your machine to your NelfePlay account, then join it.")),
            LiveContestEpreuve.RefusEpreuveFermee => (T("Épreuve non ouverte ici", "Challenge not open here"),
                epreuve + T(" - rien n'est lancé.", " - nothing was launched.")),
            LiveContestEpreuve.RefusEntrainement => (T("Entraînement non classé", "Training is not ranked"),
                epreuve + T(" - rien n'est lancé.", " - nothing was launched.")),
            LiveContestCertifie.RefusRomAbsente => (T("Jeu introuvable dans ta collection World Scoring", "Game not found in your World Scoring collection"),
                (detail ?? epreuve) + T(" - rien n'est lancé.", " - nothing was launched.")),
            LiveContestEpreuve.RefusJeuEnCours => (T("Un jeu tourne déjà", "A game is already running"),
                T("Quitte ton jeu : l'épreuve n'a pas pu partir.", "Quit your game: the challenge could not start.")),
            LiveContestEpreuve.RefusPasPrete or LiveContestEpreuve.RefusAutreJeu => (T("Épreuve impossible à lancer", "Challenge cannot be launched"),
                (detail ?? epreuve) + T(" - rien n'est lancé.", " - nothing was launched.")),
            _ => (T("Concours incomplet", "Incomplete contest"),
                T("Le streamer doit recréer le concours (épreuve introuvable).", "The streamer must recreate the contest (challenge not found).")),
        };
        _overlay.Show(null, titre, sousTitre, 0);
    }

    /// <summary>
    /// LE DEPART D'UNE EPREUVE : decompte, puis le lancement type du defi (cœur du reglement, verrous, etat de depart), avec la
    /// seance du concours armee juste avant. Un refus (un jeu tourne, .MEM change entre-temps) desarme la seance et se dit.
    /// </summary>
    private async Task DepartDeLEpreuveAsync(CancellationToken cancellationToken)
    {
        var preparation = _epreuve;
        var speedrun = _speedrun;
        if (preparation is not { Prete: true } || speedrun is null)
        {
            return;
        }

        await DecompterAsync(cancellationToken);
        lock (_gate)
        {
            _value = 0;
            _lastSent = -1;
        }

        _overlay.ShowCenter("GO !", T("Ton épreuve se lance…", "Your challenge is launching…"), 1800);
        var armee = _seanceCertifiee.Armer(_agent.Status.PlayerCode, _channel, _contestId);
        RetroBat.Api.Speedrun.ReponseLancement reponse;
        try
        {
            // Le lancement attend les fantomes, le Deck et les manettes (jusqu'a 26 s sur une borne chargee) : hors du tick.
            reponse = await Task.Run(() =>
            {
                var (verifie, refus) = speedrun.Verifier(preparation.Reglement!.ChallengeId, (_, _) => preparation.Rom);
                return verifie is null ? refus! : speedrun.Partir(verifie, pages: false);
            }, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "livecontest : lancement de l'epreuve en echec");
            reponse = new RetroBat.Api.Speedrun.ReponseLancement(false, "launcher_failed", e.Message);
        }

        if (reponse.Lance)
        {
            _logger.LogInformation("livecontest : epreuve {Epreuve} lancee pour le concours {Contest} (seance {Seance})",
                preparation.Epreuve, _contestId, armee ? "armee" : "non armee");
            _phase = "playing";
            return;
        }

        // Rien n'est parti : la seance du concours ne doit pas suivre une autre partie.
        _seanceCertifiee.Oublier();
        if (_scoringSession.Get() is { World: "stream" } seance && seance.ContestId == _contestId)
        {
            _scoringSession.Clear();
        }

        RefuserLEpreuve(LiveContestEpreuve.PhaseDuLancement(reponse.Raison),
            reponse.Raison + (string.IsNullOrEmpty(reponse.Detail) ? "" : " : " + reponse.Detail), preparation.Epreuve);
    }

    /// <summary>Signal GAME_PLAYING : la vraie partie demarre -> pause + ready.</summary>
    private void OnBusEvent(EventEnvelope envelope)
    {
        // Concours certifie clos : sa seance attend le score de son classement (ou le lancement suivant).
        if (_seanceCertifiee.EnAttente && envelope.Type is "ui.game.started" or "scoring.verdict")
        {
            string? jeu = null, regle = null;
            try
            {
                var verdict = JsonSerializer.SerializeToElement(envelope.Payload);
                jeu = Get(verdict, "RomGroup");
                regle = Get(verdict, "Ruleset");
            }
            catch (Exception)
            {
            }

            if (_seanceCertifiee.SurEvenement(envelope.Type, jeu, regle))
            {
                _logger.LogInformation("livecontest : concours certifie clos, seance desarmee ({Evenement})", envelope.Type);
            }
        }

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

        // comptage de la metrique UNIQUEMENT apres le GO, et jamais pour un concours certifie
        if (!CompteLaMetrique(_certifie is not null || _epreuve is not null, _started) || actionType is null)
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

        await EnvoyerPretAsync();

        _ready = true;
        _phase = "ready";
        _overlay.ShowCenter(T("Tiens-toi prêt !", "Get ready!"),
            string.IsNullOrWhiteSpace(_brief)
                ? T("Le départ sera donné pour tout le monde en même temps - attends le décompte.",
                    "The start fires for everyone at once - wait for the countdown.")
                : _brief!, 0);
    }

    /// <summary><c>/play/ready</c> : le streamer voit le joueur pret.</summary>
    private async Task EnvoyerPretAsync()
    {
        var enrollment = _enrollment;
        if (enrollment is null)
        {
            return;
        }

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

    /// <summary>Le decompte 5-4-3-2-1 jusqu'a startAt pile (deja passe : rien a attendre).</summary>
    private async Task DecompterAsync(CancellationToken cancellationToken)
    {
        _phase = "countdown";
        for (var n = 5; n >= 1; n--)
        {
            var wait = _startAtMs - n * 1000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken);
            }

            _overlay.ShowCenter(n.ToString(), T("Départ dans…", "Starting in…"), 0);
        }

        var final = _startAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (final > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(final), cancellationToken);
        }
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
                _certifie is not null
                    ? T("Ton jeu n’était pas lancé : lance-le depuis World Scoring, ta partie certifiée compte quand même !",
                        "Your game was not running: launch it from World Scoring, your certified run still counts!")
                    : T("Ton jeu n’était pas lancé : lance-le et joue, tes points comptent quand même !", "Your game was not running: launch it and play, your points still count!"), 8000);
            _phase = "playing";
            return;
        }

        await DecompterAsync(cancellationToken);

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
        // GO! puis la scene centrale disparait : place au jeu
        _overlay.ShowCenter("GO !", "", 1800);
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
        _overlay.ShowCenter(T("🏁 Objectif atteint !", "🏁 Target reached!"),
            T("Ton temps est enregistré - regarde le classement sur le stream !",
              "Your time is in - watch the stream for the standings!"), 0);

        // le joueur a fini : on le sort du jeu tout de suite, le streamer
        // peut prendre son temps pour clore le contest
        await Task.Delay(4500);
        _overlay.Show(null, T("À toute sur Twitch !!!", "See you on Twitch!!!"),
            T("Le jeu va se fermer…", "The game is about to close…"), 4500);
        await Task.Delay(4000);
        await SendRetroArchAsync("QUIT", expectResponse: false);
        _quitDone = true;
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
    /// <summary>L'issue d'un depot final.</summary>
    internal enum IssueDuDepot
    {
        /// <summary>Recu (2xx).</summary>
        Recu,
        /// <summary>400 : deja depose ou trop tard (LiveContest donne volontairement la meme reponse). On s'arrete.</summary>
        DejaDeposeOuTropTard,
        /// <summary>Reseau, delai, 5xx, 408, 429 : un nouvel essai a un sens.</summary>
        AReessayer,
    }

    /// <summary>Les attentes entre les essais du depot final : tous dans les 2 min de LiveContest.</summary>
    internal static readonly TimeSpan[] AttentesDesEssais = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(40)];

    /// <summary>Range une reponse de <c>/play/submit</c>. Pur.</summary>
    internal static IssueDuDepot IssueDe(int? statut) => statut switch
    {
        >= 200 and < 300 => IssueDuDepot.Recu,
        408 or 429 => IssueDuDepot.AReessayer,
        >= 400 and < 500 => IssueDuDepot.DejaDeposeOuTropTard,
        _ => IssueDuDepot.AReessayer,
    };

    private async Task<IssueDuDepot> DeposerLaValeurFinaleAsync(Enrollment enrollment, double valeur, CancellationToken cancellationToken)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                enrollment.PlatformBase + "/play/submit");
            request.Headers.Authorization = new("Bearer", enrollment.PlayToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { finalValue = valeur }),
                Encoding.UTF8, "application/json");
            using var reponse = await http.SendAsync(request, cancellationToken);
            var issue = IssueDe((int)reponse.StatusCode);
            if (issue == IssueDuDepot.DejaDeposeOuTropTard)
            {
                _logger.LogInformation("livecontest : depot final refuse ({Statut}) : deja depose ou trop tard, on s'arrete.", (int)reponse.StatusCode);
            }
            return issue;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IssueDuDepot.Recu;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "livecontest : submit final en echec");
            return IssueDuDepot.AReessayer;
        }
    }

    private async Task ReessayerLeDepotAsync(Enrollment enrollment, double valeur)
    {
        foreach (var attente in AttentesDesEssais)
        {
            try { await Task.Delay(attente).ConfigureAwait(false); } catch { return; }
            var issue = await DeposerLaValeurFinaleAsync(enrollment, valeur, CancellationToken.None).ConfigureAwait(false);
            if (issue != IssueDuDepot.AReessayer)
            {
                _logger.LogInformation("livecontest : depot final, nouvel essai : {Issue}.", issue);
                return;
            }
        }
        _logger.LogWarning("livecontest : depot final perdu apres {N} nouveaux essais.", AttentesDesEssais.Length);
    }

    private async Task FinishAsync(Enrollment enrollment, CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _phase = "finished";
        var valeurFinale = _value;
        if (await DeposerLaValeurFinaleAsync(enrollment, valeurFinale, cancellationToken) == IssueDuDepot.AReessayer)
        {
            // NOUVEAUX ESSAIS DU DEPOT FINAL (15/11) : LiveContest recoit un PREMIER depot dans les 2 min qui suivent la
            // cloture. Les essais partent a cote, pour ne pas retarder la fin de partie a l'ecran.
            _ = Task.Run(() => ReessayerLeDepotAsync(enrollment, valeurFinale), CancellationToken.None);
        }

        var certifie = _certifie;
        var epreuve = _epreuve;
        var concours = _contestId;
        if (_started && !_quitDone)
        {
            _overlay.Show(null, T("BRAVO !!!", "WELL PLAYED!!!"), epreuve is not null
                ? T("Ton meilleur temps certifié fait foi : regarde le classement sur le stream !", "Your best certified time is what counts: watch the stream for the standings!")
                : certifie is not null
                ? T("Ton score certifié fait foi : regarde le classement sur le stream !", "Your certified score is what counts: watch the stream for the standings!")
                : T("Score final : ", "Final score: ") + _value, 0);
            await Task.Delay(4000, cancellationToken);
            _overlay.Show(null, T("À toute sur Twitch !!!", "See you on Twitch!!!"), T("Le jeu va se fermer…", "The game is about to close…"), 4500);
            await Task.Delay(4500, cancellationToken);
            await SendRetroArchAsync("QUIT", expectResponse: false);
        }

        _overlay.Hide();
        // Concours certifie : le score part apres la fermeture du jeu, et son brouillon lit la seance a ce moment-la. Elle
        // tient jusqu'a lui (SeanceDuConcoursCertifie) ; un concours declare la retire tout de suite, comme avant.
        // Une epreuve : le meilleur temps du lancement part a la fermeture du jeu ; la seance tient jusqu'au verdict de son ruleset.
        var classement = ClassementDuConcours(certifie, epreuve);
        var garder = classement is not null && concours is { Length: > 0 }
            && _scoringSession.Get() is { World: "stream" } seance && seance.ContestId == concours;
        if (garder)
        {
            _seanceCertifiee.DesarmerApresLaPartie(concours!, classement!.Value.Jeu, classement.Value.Regle);
        }

        Withdraw(garder);
    }

    /// <summary>
    /// Le jeu et le ruleset dont le verdict desarme la seance d'un concours certifie clos : ceux de l'epreuve (le passeport d'un
    /// defi porte le rom_group et le ruleset de l'epreuve), sinon ceux du concours sur score ; null pour un concours declare. Pur.
    /// </summary>
    internal static (string Jeu, string Regle)? ClassementDuConcours(LiveContestCertifie.Preparation? certifie, LiveContestEpreuve.Preparation? epreuve)
        => epreuve is { Prete: true, Reglement: { } reglement }
            ? (reglement.RomGroup, reglement.Ruleset)
            : certifie is { Prete: true } ? (certifie.Jeu, certifie.Regle) : null;

    /// <summary>
    /// RetroBat/RetroArch pausent le jeu quand la fenetre perd le focus
    /// (pause_nonactive) : incompatible avec le depart pilote. Le canal
    /// DURABLE est es_settings.cfg (global.retroarch.pause_nonactive=false,
    /// injecte par RetroBat dans retroarch.cfg a CHAQUE lancement - un
    /// template, lui, peut etre reecrit). On corrige aussi la config live
    /// pour un jeu deja lance.
    /// </summary>
    private void DisablePauseNonactive()
    {
        var esSettings = EsSettingsPath;
        try
        {
            if (File.Exists(esSettings))
            {
                var xml = File.ReadAllText(esSettings);
                const string entry =
                    "  <string name=\"global.retroarch.pause_nonactive\" value=\"false\" />";
                if (xml.Contains("global.retroarch.pause_nonactive", StringComparison.Ordinal))
                {
                    xml = System.Text.RegularExpressions.Regex.Replace(xml,
                        "<string name=\"global\\.retroarch\\.pause_nonactive\" value=\"[^\"]*\" />",
                        "<string name=\"global.retroarch.pause_nonactive\" value=\"false\" />");
                }
                else
                {
                    xml = xml.Replace("</config>", entry + "\n</config>");
                }

                File.WriteAllText(esSettings, xml);
                _logger.LogInformation("livecontest : global.retroarch.pause_nonactive=false (es_settings)");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "livecontest : es_settings non modifiable");
        }

        // effet immediat si RetroArch a deja une config generee
        var live = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.cfg");
        try
        {
            if (File.Exists(live))
            {
                var content = File.ReadAllText(live);
                if (content.Contains("pause_nonactive = \"true\"", StringComparison.Ordinal))
                {
                    File.WriteAllText(live, content.Replace(
                        "pause_nonactive = \"true\"", "pause_nonactive = \"false\""));
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "livecontest : retroarch.cfg live non modifiable");
        }
    }

    /// <summary>
    /// Ramene une application au premier plan. ES doit avoir le focus AVANT
    /// le launch (sinon RetroArch demarre en arriere-plan), puis RetroArch
    /// des qu'il apparait et au GO.
    /// </summary>
    private static void FocusProcess(string processName)
    {
        try
        {
            foreach (var process in processName == "retroarch" ? RetroBat.Api.Infrastructure.RetroArchProcessus.Tous() : System.Diagnostics.Process.GetProcessesByName(processName))
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

    private static void FocusRetroArch() => FocusProcess("retroarch");

    private static string EsSettingsPath => Path.Combine(RetroBatPaths.RetroBatRoot,
        "emulationstation", ".emulationstation", "es_settings.cfg");

    /// <summary>Emulateur force dans es_settings pour ce systeme (null = AUTO).</summary>
    public static string? EmulatorOverrideFor(string? system)
    {
        if (string.IsNullOrWhiteSpace(system) || !File.Exists(EsSettingsPath))
        {
            return null;
        }

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(EsSettingsPath),
                "<string name=\"" + System.Text.RegularExpressions.Regex.Escape(system) +
                "\\.emulator\" value=\"([^\"]+)\" />");
            return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// EMULATOR: AUTO pour le systeme du contest - un override (emulateur
    /// standalone) casserait le pilotage RetroArch et l'equite. L'entree
    /// es_settings est retiree dynamiquement au moment de la preparation.
    /// </summary>
    private void ForceAutoEmulator(string? system)
    {
        if (string.IsNullOrWhiteSpace(system) || !File.Exists(EsSettingsPath))
        {
            return;
        }

        try
        {
            var xml = File.ReadAllText(EsSettingsPath);
            var cleaned = System.Text.RegularExpressions.Regex.Replace(xml,
                "\\s*<string name=\"" + System.Text.RegularExpressions.Regex.Escape(system) +
                "\\.(emulator|core)\" value=\"[^\"]*\" />", "");
            if (cleaned != xml)
            {
                File.WriteAllText(EsSettingsPath, cleaned);
                _logger.LogInformation("livecontest : emulateur force en AUTO pour {System}", system);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "livecontest : emulateur AUTO non applicable");
        }
    }

    /// <summary>Focus RetroArch des que sa fenetre existe (boot apres launch).</summary>
    private static async Task FocusRetroArchWhenUpAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(2000);
            if (RetroBat.Api.Infrastructure.RetroArchProcessus.Tous()
                .Any(pr => pr.MainWindowHandle != IntPtr.Zero))
            {
                FocusRetroArch();
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Resolution LOCALE du jeu : le contest donne systeme + slug, on cherche
    /// dans roms\{system} du RetroBat du viewer (normalisation des noms -
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
        // GET_STATUS fait planter RetroArch 1.22.2 avec un coeur sans identifiant de systeme.
        if (command.StartsWith("GET_STATUS", StringComparison.Ordinal) && !RetroBat.Api.Replay.Runtime.GardeGetStatus.SansDanger())
            return string.Empty;
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
            if (!File.Exists(_statePath))
            {
                return null;
            }

            var loaded = JsonSerializer.Deserialize<Enrollment>(File.ReadAllText(_statePath));
            // Un contest est court : on NE reprend PAS un enrôlement trop vieux au
            // redémarrage - sinon un simple restart ré-ouvre un contest déjà fini et
            // bloque la borne sur « Tiens-toi prêt ». Périmé (>30 min) → on l'efface.
            if (loaded is null || DateTimeOffset.UtcNow - loaded.EnrolledAt > TimeSpan.FromMinutes(30))
            {
                try { File.Delete(_statePath); } catch (Exception) { }
                return null;
            }

            return loaded;
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
