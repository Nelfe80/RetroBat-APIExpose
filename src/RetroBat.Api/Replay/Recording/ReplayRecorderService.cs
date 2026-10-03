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
    private readonly RetroBat.Api.Media.InstalledGameCatalog? _catalogue;

    /// <summary>
    /// POURQUOI ON ATTEND UN DEPART (2026-09-11) : en enregistrant des le chargement, on gardait
    /// l'ecran titre et la demo. Le « film coupe par RetroArch au game over » invoque alors etait
    /// en fait notre propre detection de fin, qui prenait un sondage sans reponse pour un arret
    /// (corrige le 2026-10-02). `Replay:Record:RequireStart=false` enregistre des le chargement,
    /// pour une machine sans panel.
    ///
    /// LE DEPART DE LA PARTIE (2026-10-02, plan valide par le user). L'enregistrement part sur le
    /// premier signal : n'importe quel bouton du panel, presse une fois le jeu charge (l'appui qui
    /// l'a lance depuis ES n'en est pas un) ; un credit consomme ou GAME_START (memoire du jeu) ;
    /// en filet, le score qui monte. Un bouton ne vaut que pour le PREMIER enregistrement du jeu :
    /// ensuite le joueur peut se promener dans les options, seuls credit, GAME_START et score
    /// rearment. Le START seul ne suffisait pas : clavier, manette non lue, Master System.
    ///
    /// PAS DE FENETRE DE TEMPS. Un depart reste EN ATTENTE, rattache au jeu charge, jusqu'a ce que
    /// RetroArch puisse enregistrer ; il ne s'annule que si le jeu change ou qu'un autre est lance.
    /// La fenetre de vingt secondes d'avant laissait compter un START de dix-neuf secondes et
    /// perdait celui de vingt et une.
    /// </summary>
    private volatile bool _departEnAttente;
    private string _sourceDuDepart = "";
    private string _contenuVu = "";
    private bool _dejaEnregistre;

    /// <summary>Le score a baisse apres avoir monte : nouvelle partie ou remise a zero (rapporteur).</summary>
    private volatile bool _arretParBaisse;

    /// <summary>
    /// UNE FIN SE CONFIRME. Un sondage sans reponse etait pris pour la fin de l'enregistrement : le
    /// replay etait finalise en pleine partie (le « film coupe au game over » du 2026-09-11, que
    /// RetroArch ne fait pas). Deux reponses explicites, ou six secondes sans reponse.
    /// </summary>
    private int _finsVues;
    private int _sansReponse;
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
        /// <summary>« 1cc-multi » pour la partie a plusieurs ; null pour le 1CC solo.</summary>
        public string? Categorie;

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
        IConfiguration config, RetroBat.Api.Media.InstalledGameCatalog? catalogue = null,
        RetroBat.Api.Infrastructure.PartieNelfePlayService? partie = null)
    {
        _ra = ra; _store = store; _timing = timing; _bus = bus; _playback = playback; _logger = logger;
        _config = config; _catalogue = catalogue; _partie = partie;
        _garde = new GardeDesPlantages(Path.Combine(Path.GetDirectoryName(store.ActiveRecordingPath) ?? ".", "sans-enregistrement.json"));
    }

    /// <summary>
    /// Les jeux dont l'enregistrement a fait planter RetroArch sur cette borne : on ne les enregistre
    /// plus, pour ne plus perdre la partie (voir <see cref="GardeDesPlantages"/>).
    /// </summary>
    private readonly GardeDesPlantages _garde;
    private string _renoncementAnnonce = "";

    /// <summary>
    /// L'empreinte de RetroArch : sa version et son executable. Les nocturnes s'annoncent elles aussi
    /// « 1.22.2 » ; l'executable, lui, change a chaque mise a jour.
    /// </summary>
    private static string EmpreinteDeRetroArch(string? version)
    {
        // « unknown » : ce que StartAsync retient quand RetroArch ne dit pas sa version.
        var nom = string.IsNullOrWhiteSpace(version) || version.Trim() == "unknown" ? "?" : version.Trim();
        try
        {
            var exe = new FileInfo(Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "retroarch.exe"));
            if (exe.Exists) return $"{nom}+{exe.Length}+{exe.LastWriteTimeUtc:yyyyMMddHHmmss}";
        }
        catch (Exception)
        {
            // Sans l'executable, la version seule.
        }
        return nom;
    }

    private string CleDuJeu(RaStatus status, string? version)
        => GardeDesPlantages.Cle(status.System, status.Game, status.Crc32, _garde.CoeurEnCours, EmpreinteDeRetroArch(version));

    /// <summary>
    /// Vrai si l'on renonce a enregistrer ce jeu : son coeur est dans la liste, ou RetroArch y a deja
    /// plante pendant un enregistrement sur cette borne. Dit une fois par jeu.
    /// </summary>
    private async Task<bool> RenoncerAEnregistrerAsync(RaStatus status, CancellationToken ct)
    {
        if (await CoeurSansEnregistrementAsync(status, ct).ConfigureAwait(false)) return true;

        var cle = CleDuJeu(status, await _ra.GetVersionAsync(ct).ConfigureAwait(false));
        if (_garde.RenoncementPour(cle) is not { } renoncement) return false;
        if (!string.Equals(_renoncementAnnonce, cle, StringComparison.Ordinal))
        {
            _renoncementAnnonce = cle;
            _logger.LogInformation(
                "Replay : pas d'enregistrement de {Game}. RetroArch a plante pendant son enregistrement le {Le:yyyy-MM-dd HH:mm} (coeur {Coeur}, RetroArch {RetroArch}) ; la partie compte toujours. Pour reessayer, supprimer {Fichier}.",
                status.Game, renoncement.Le.ToLocalTime(), renoncement.Coeur, renoncement.RetroArch, _garde.Chemin);
        }
        return true;
    }

    /// <summary>
    /// SEULE UNE PARTIE NELFEPLAY S'ENREGISTRE (regle user 2026-09-27). Jusque-la, toute partie
    /// RetroArch l'etait des le START, meme un jeu lance pour le plaisir depuis son systeme : un
    /// testeur l'a vu. Hors NelfePlay, le jeu garde le comportement que le joueur a regle.
    /// </summary>
    private readonly RetroBat.Api.Infrastructure.PartieNelfePlayService? _partie;

    private bool StartRequis => _config.GetValue("Replay:Record:RequireStart", true);

    /// <summary>
    /// Les cœurs sous lesquels on N'ENREGISTRE PAS, séparés par des virgules. Vide = aucun.
    ///
    /// RetroArch 1.22.2 (la dernière stable, celle que livre RetroBat) plante dès l'ouverture d'un
    /// enregistrement sous le cœur libretro MAME : son encodeur de points de contrôle écrit le
    /// dernier bloc de l'état sur 16 Ko entiers, lus au-delà de la fin du tampon. Sur un gros état
    /// (Altered Beast), la lecture tombe dans une page non mappée : 0xC0000005 dans memcpy, au
    /// START, à chaque partie. Prouvé par sept vidages le 25 septembre 2026, et corrigé en amont par
    /// le commit 8a8cc448b du 24 juin 2026 (« Fix replay seek while recording v2 »), absent de la
    /// 1.22.2. Une nocturne l'a tenu : deux replays de 5,7 et 9,4 Mo, aucun plantage.
    ///
    /// On ne trie pas par version : les nocturnes s'annoncent elles aussi « 1.22.2 ». Le jour où
    /// RetroBat livre un RetroArch corrigé, `Replay:Record:SansEnregistrement` vide lève la porte.
    /// </summary>
    private string CoeursSansEnregistrement => _config["Replay:Record:SansEnregistrement"] ?? "mame_libretro";
    private string _porteAnnoncee = "";

    /// <summary>
    /// Le cœur d'après le dossier de sauvegarde que RetroBat pose au lancement :
    /// <c>…\saves\mame\libretro.mame</c> donne « mame ». null si le chemin ne suit pas la forme.
    /// </summary>
    internal static string? CoeurDuDossier(string? dossier)
    {
        if (string.IsNullOrWhiteSpace(dossier)) return null;
        foreach (var segment in dossier.Trim().TrimEnd('\\', '/').Split('\\', '/').Reverse())
        {
            if (segment.StartsWith("libretro.", StringComparison.OrdinalIgnoreCase) && segment.Length > "libretro.".Length)
                return segment["libretro.".Length..].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Ce cœur est-il dans la liste ? On compare le nom NU : « mame_libretro », « mame » et
    /// « libretro.mame » désignent le même, et « mame2003_plus » reste un autre cœur.
    /// </summary>
    internal static bool EstSansEnregistrement(string? coeur, string? liste)
    {
        if (string.IsNullOrWhiteSpace(coeur) || string.IsNullOrWhiteSpace(liste)) return false;
        static string Nu(string n)
        {
            var t = n.Trim().ToLowerInvariant();
            if (t.StartsWith("libretro.", StringComparison.Ordinal)) t = t["libretro.".Length..];
            if (t.EndsWith("_libretro.dll", StringComparison.Ordinal)) t = t[..^"_libretro.dll".Length];
            if (t.EndsWith("_libretro", StringComparison.Ordinal)) t = t[..^"_libretro".Length];
            return t;
        }
        var cible = Nu(coeur);
        return liste.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(e => Nu(e) == cible);
    }

    /// <summary>
    /// Vrai si l'on doit renoncer à enregistrer CE jeu sous CE cœur. Interroge RetroArch une fois,
    /// juste avant d'enregistrer ; sans réponse on ne bloque rien, la porte ne vise qu'un plantage
    /// identifié.
    /// </summary>
    private async Task<bool> CoeurSansEnregistrementAsync(RaStatus status, CancellationToken ct)
    {
        var liste = CoeursSansEnregistrement;
        if (string.IsNullOrWhiteSpace(liste)) return false;
        var coeur = CoeurDuDossier(await _ra.GetSavestateDirectoryAsync(ct).ConfigureAwait(false));
        if (!EstSansEnregistrement(coeur, liste)) return false;

        var cle = coeur + "|" + status.Game;
        if (!string.Equals(_porteAnnoncee, cle, StringComparison.Ordinal))
        {
            _porteAnnoncee = cle;
            _logger.LogInformation(
                "Replay : pas d'enregistrement de {Game} sous le coeur {Coeur}. RetroArch 1.22.2 plante en encodant l'etat de ce coeur (corrige en amont, commit 8a8cc448b). Reglage Replay:Record:SansEnregistrement.",
                status.Game, coeur);
        }
        return true;
    }

    /// <summary>
    /// LE REPLAY S'ARRETE AU CONTINUE (demande user 2026-09-27). Le score certifie s'arrete avant
    /// le continue ; le replay le suivait jusqu'au bout de la partie et montrait des continues que
    /// le classement n'admet pas. Le rapporteur dit la fin de la partie certifiee
    /// (scoring.run.ended) ; on arrete alors l'enregistrement, et on n'en relance aucun avant le
    /// lancement suivant.
    /// </summary>
    private volatile bool _runTermine;

    /// <summary>
    /// QUAND LE REPLAY 1CC S'ARRETE A L'ARRIVEE D'UN JOUEUR, CELUI DU 1CC MULTI COMMENCE (demande
    /// user 2026-09-30). Le rapporteur l'annonce (scoring.run.multi) : relancer apres avoir scelle
    /// le replay solo, ou marquer celui en cours quand rien n'a ete fait seul (depart a deux).
    /// </summary>
    private volatile bool _relanceMulti;
    private volatile string? _categorieSuivante;

    private void OnBusEvent(EventEnvelope e)
    {
        // Le garde-fou suit chaque jeu : son ecoute, sa fin de partie, sa fin vue par ES.
        switch (e.Type)
        {
            case "ui.game.started":
                _garde.JeuLance();
                break;
            case "ui.game.ended":
                _garde.JeuTermine();
                break;
            case "scoring.listener.session":
                _garde.FinDePartieRecue();
                break;
            case "scoring.listener.attestation":
                try { _garde.EcouteVue(GardeDesPlantages.CoeurDeLAttestation(System.Text.Json.JsonSerializer.SerializeToElement(e.Payload))); }
                catch { _garde.EcouteVue("?"); }
                break;
        }

        if (string.Equals(e.Type, "scoring.run.ended", StringComparison.Ordinal))
        {
            _runTermine = true;
            return;
        }

        if (string.Equals(e.Type, "scoring.run.multi", StringComparison.Ordinal))
        {
            var relancer = false;
            try
            {
                var el = System.Text.Json.JsonSerializer.SerializeToElement(e.Payload);
                relancer = el.TryGetProperty("Relancer", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch
            {
                // Illisible : on marque la partie en cours, sans rien relancer.
            }

            if (relancer)
            {
                _relanceMulti = true;
            }
            else if (_current is { } enCours)
            {
                enCours.Categorie = ReplayLocalMetadata.CategorieMulti;
            }
            else
            {
                _categorieSuivante = ReplayLocalMetadata.CategorieMulti;
            }
            return;
        }

        // Une nouvelle partie (lancement, ou score qui retombe dans la meme session) : on peut de
        // nouveau enregistrer, au prochain depart.
        if (string.Equals(e.Type, "ui.game.started", StringComparison.Ordinal)
            || string.Equals(e.Type, "scoring.run.reset", StringComparison.Ordinal))
        {
            _runTermine = false;
            if (string.Equals(e.Type, "ui.game.started", StringComparison.Ordinal))
            {
                _relanceMulti = false;
                _categorieSuivante = null;
                _contenuVu = "";
                _dejaEnregistre = false;
                _departEnAttente = false;
            }
            return;
        }

        // LE DEPART LU DANS LA MEMOIRE DU JEU (credit consomme, GAME_START du .MEM) vaut un START :
        // une borne jouee au clavier, ou dont la manette n'est pas lue par l'API, n'enregistrait
        // plus rien (theJim, 2026-10-02 : « en attente d'un START », puis plus rien).
        if (string.Equals(e.Type, "scoring.partie.depart", StringComparison.Ordinal))
        {
            var source = "memoire du jeu";
            try
            {
                var el = System.Text.Json.JsonSerializer.SerializeToElement(e.Payload);
                if (el.TryGetProperty("Source", out var s) && s.GetString() is { Length: > 0 } lue) source = lue;
            }
            catch
            {
                // La source n'est qu'un mot pour le journal.
            }
            _sourceDuDepart = source;
            _departEnAttente = true;
            return;
        }

        if (string.Equals(e.Type, "scoring.replay.stop", StringComparison.Ordinal))
        {
            _arretParBaisse = true;
            return;
        }

        // N'importe quel bouton du panel, le jeu charge : le depart du premier enregistrement du jeu.
        if (string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal)
            && _contenuVu.Length > 0 && !_dejaEnregistre && _current is null && !_departEnAttente)
        {
            _sourceDuDepart = "bouton du panel";
            _departEnAttente = true;
        }
    }

    /// <summary>Un START assez recent pour qu'on enregistre. Le dit une fois par attente.</summary>
    private bool StartRecent(RaStatus status)
    {
        if (!StartRequis)
        {
            _sourceDuDepart = "chargement du jeu";
            return true;
        }
        if (_departEnAttente) return true;
        var cle = status.System + "/" + status.Game;
        if (!string.Equals(_attenteAnnoncee, cle, StringComparison.Ordinal))
        {
            _attenteAnnoncee = cle;
            _logger.LogInformation("Replay : {Game} charge, en attente du depart de la partie (bouton, credit, GAME_START ou score).", status.Game);
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
        if (_garde.Juger() is { } renoncement)
        {
            _logger.LogWarning(
                "Replay : RetroArch s'est arrete sans fin de partie pendant que {Jeu} s'enregistrait (coeur {Coeur}, RetroArch {RetroArch}). Ce jeu ne s'enregistrera plus sur cette borne tant que RetroArch et ce coeur restent dans ces versions, pour ne plus perdre de partie. Pour reessayer, supprimer {Fichier}.",
                renoncement.Jeu, renoncement.Coeur, renoncement.RetroArch, _garde.Chemin);
        }

        var status = await _ra.GetStatusAsync(ct).ConfigureAwait(false);
        var active = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);

        if (status is { ContentLoaded: true })
        {
            var contenu = status.System + "/" + status.Game;
            if (!string.Equals(_contenuVu, contenu, StringComparison.Ordinal))
            {
                // Un AUTRE jeu que celui qu'on suivait : son depart ne vaut pas pour celui-ci. Le
                // premier jeu vu apres un lancement garde le sien (il ne peut venir que de lui).
                if (_contenuVu.Length > 0) _departEnAttente = false;
                _contenuVu = contenu;
                _dejaEnregistre = false;
            }
        }

        if (_current is null)
        {
            // Démarrage : un jeu RetroArch est chargé, aucun replay actif, on n'est pas en lecture,
            // et le joueur vient d'appuyer sur START (sinon on enregistrerait la demo).
            // Hors NelfePlay, on ne guette meme pas le START : le journal le dit une fois par jeu,
            // au lieu d'annoncer une attente qui n'aboutira jamais.
            // La partie certifiee est finie (continue) : la suite se joue sans replay. Sauf si elle
            // a ete close par l'arrivee d'un joueur : la suite est le 1CC MULTI, on l'enregistre
            // tout de suite, sans attendre un START (celui de l'invite n'est vu que sur sa borne).
            if (_runTermine)
            {
                if (!_relanceMulti) return;
                _relanceMulti = false;
                _runTermine = false;
                _categorieSuivante = ReplayLocalMetadata.CategorieMulti;
                _sourceDuDepart = "un joueur rejoint";
                _departEnAttente = true;
                _logger.LogInformation("Replay : un joueur a rejoint la partie, le replay du 1CC MULTI commence.");
            }

            if (status is { ContentLoaded: true } && _partie is { EstNelfePlay: false } && !_playback.IsBusy)
            {
                var cle = "hors:" + status.System + "/" + status.Game;
                if (!string.Equals(_attenteAnnoncee, cle, StringComparison.Ordinal))
                {
                    _attenteAnnoncee = cle;
                    _logger.LogInformation("Replay : {Game} n'est pas une partie NelfePlay, pas d'enregistrement.", status.Game);
                }

                return;
            }

            if (status is { ContentLoaded: true } && active is { Active: false } && !_playback.IsBusy
                && StartRecent(status))
            {
                _attenteAnnoncee = "";
                // Le cœur est vérifié AU DERNIER MOMENT, pas à chaque sondage : une requête de plus
                // par partie, et la porte suit le cœur même si le joueur en change entre deux jeux.
                if (await RenoncerAEnregistrerAsync(status, ct).ConfigureAwait(false))
                {
                    // Le départ est consommé : sans cela on reposerait la question à chaque sondage.
                    // Et l'attente est marquée comme annoncée, sinon le journal dirait « en attente
                    // du départ » juste après qu'on en a vu un.
                    _departEnAttente = false;
                    _attenteAnnoncee = status.System + "/" + status.Game;
                    return;
                }
                await StartAsync(status, ct).ConfigureAwait(false);
            }
            return;
        }

        if (_runTermine)
        {
            _logger.LogInformation("Replay : {ReplayId} arrete au continue, la suite de la partie ne compte pas.", _current.ReplayId);
            await FinalizeAsync(_current, recovered: false, ct).ConfigureAwait(false);
            return;
        }

        if (_arretParBaisse)
        {
            _arretParBaisse = false;
            _logger.LogInformation("Replay : {ReplayId} arrete, le score a baisse (nouvelle partie ou remise a zero).", _current.ReplayId);
            await FinalizeAsync(_current, recovered: false, ct).ConfigureAwait(false);
            return;
        }

        // En cours d'enregistrement : suivre la dernière frame connue.
        if (active is { Recording: true })
        {
            SampleRate(_current, active.Frame);
            _current.LastFrame = active.Frame;
        }

        // Fin : RetroArch fermé, jeu changé, ou l'enregistrement s'est arrêté. Confirmée : deux
        // réponses explicites, ou six secondes sans réponse (un sondage perdu n'est pas une fin).
        var fin = (status is { } s && (!s.ContentLoaded || !string.Equals(s.Game, _current.Game, StringComparison.Ordinal)))
                  || active is { Recording: false };
        if (fin) { _finsVues++; _sansReponse = 0; }
        else if (status is null || active is null) { _sansReponse++; }
        else { _finsVues = 0; _sansReponse = 0; }

        if (_finsVues >= 2 || _sansReponse >= 4)
        {
            _logger.LogInformation(_finsVues >= 2
                    ? "Replay : {ReplayId} termine (jeu ferme ou change, ou RetroArch ne l'enregistre plus)."
                    : "Replay : {ReplayId} termine (RetroArch ne repond plus).",
                _current.ReplayId);
            _finsVues = 0;
            _sansReponse = 0;
            await FinalizeAsync(_current, recovered: false, ct).ConfigureAwait(false);
        }
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
            Categorie = _categorieSuivante,
        };
        _categorieSuivante = null;
        _garde.EnregistrementLance(CleDuJeu(status, version), status.Game, EmpreinteDeRetroArch(version));
        rec.CoreFps = ProbeCoreFps(rec.Crc32, rec.Game);
        rec.LastSampleUtc = DateTime.UtcNow;
        _current = rec;

        _store.WriteJsonAtomic(_store.ActiveRecordingPath, new ActiveRecordingState(
            "nelfe.replay.active-recording.v1", rec.ReplayId, rec.SessionId, rec.System, rec.Game,
            rec.Crc32, rec.StartedAtUtc, rec.RetroArchVersion));

        _logger.LogInformation("Replay : enregistrement démarré {ReplayId} ({System}/{Game}{Categorie}), départ : {Source}.",
            rec.ReplayId, rec.System, rec.Game, rec.Categorie is null ? "" : ", " + rec.Categorie,
            _sourceDuDepart.Length > 0 ? _sourceDuDepart : "START");
        // Le depart est consomme : il ne relancera pas un enregistrement apres un arret. Et ce jeu a
        // eu le sien : un bouton ne rearme plus.
        _departEnAttente = false;
        _sourceDuDepart = "";
        _dejaEnregistre = true;
        _finsVues = 0;
        _sansReponse = 0;
        _arretParBaisse = false;
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
            _store.SaveMeta(ReplayLocalMetadata.Fresh(rec.ReplayId, hint) with { Categorie = rec.Categorie });
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
            // L'identite du JEU au sens du scoring (systeme + contenu, jamais un nom). Avec elle,
            // une borne qui n'a pas ce fichier-la mais un autre dump du meme jeu peut quand meme
            // rejouer, en prevenant. Sans elle (pas de .MEM), il ne reste que l'empreinte.
            RomGroup: RomGroupDe(hint),
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
            ReplayFormat: "bsv",
            CoreName: string.IsNullOrEmpty(hint?.Core) ? null : hint!.Core);

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

            // Le VRAI core, pas le wrapper de scoring qui le remplace dans cores/ : tous les
            // wrappers ont la meme empreinte, et un manifeste qui la portait faisait choisir, a
            // la lecture, le premier core de la liste (2048 pour un replay de Sonic, 2026-09-17).
            var coreDll = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores_real", core + "_libretro.dll");
            if (!File.Exists(coreDll))
            {
                coreDll = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "cores", core + "_libretro.dll");
            }
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

    private string? RomGroupDe(ReplayLaunchHint? hint)
    {
        if (_catalogue is null || hint is null || string.IsNullOrEmpty(hint.RomPath)) return null;
        try { return _catalogue.RomGroupOf(hint.SystemFolder, hint.RomPath); }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : groupe du jeu non resolu pour {Rom}.", hint.RomPath); return null; }
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
