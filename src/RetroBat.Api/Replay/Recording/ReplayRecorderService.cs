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

    /// <summary>
    /// UN DEFI QUI PART SUR UN ETAT (01/11) : le film attend le chargement de l'etat. Commence au demarrage du jeu, il garderait
    /// le BIOS et le titre, et le chargement en pleine prise ne se rejoue pas.
    /// </summary>
    private volatile bool _etatDeDepartAttendu;

    /// <summary>
    /// LA RELANCE D'UN DEFI OUVRE SON REPLAY TOUT DE SUITE (05/11). Le replay de la tentative suivante attendait la fin de la
    /// precedente (deux sondages), sa finalisation (jusqu'a 13 s sur Sonic), puis un sondage : il partait 4 a 13 s apres le
    /// depart du run. Sans le debut, il n'est ni une preuve complete ni un fantome (le replay du record de Green Hill 1
    /// montrait le debut de la tentative suivante). L'etat recharge reveille la boucle ; le neuf s'ouvre, l'ancien se scelle
    /// ensuite.
    /// </summary>
    private volatile bool _relanceDuDefi;

    /// <summary>Un speedrun est en cours : ses relances decoupent les replays, pas la baisse du score.</summary>
    private volatile bool _speedrun;

    private readonly SemaphoreSlim _reveil = new(0, 1);

    private void Reveiller()
    {
        try { if (_reveil.CurrentCount == 0) _reveil.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Les verrous que le wrapper a attestes pour le jeu en cours (D18) ; null sans verrou. Le manifeste les garde.</summary>
    private volatile string? _verrousAttestes;
    private volatile string? _reglesAttestees;
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
        /// <summary>Le dossier de sauvegarde de RetroArch a l'ouverture (celui du coeur) : le .replay s'y cherche.</summary>
        public string? Dossier;
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

    /// <summary>Les annonces de l'enregistreur, en file : un abonne lent ne retient plus la relance (<see cref="FileDEvenements"/>).</summary>
    private readonly FileDEvenements _annonces;

    /// <summary>
    /// L'ENREGISTREMENT DEMANDE, PAS ENCORE CONFIRME (12/11). Le 12/11 a 09:23:04, RetroArch a ouvert le replay de la relance
    /// environ 0,9 s apres RECORD_REPLAY ; le controle unique a 300 ms l'a dit « non confirme » (journal de debogage, muet), le
    /// replay courant est reste vide, et la boucle, qui n'ouvre rien tant que RetroArch enregistre, ne l'a jamais repris : le
    /// fichier (2353 images, le run de 0:32.56 entier) est reste orphelin dans saves, et le temps est parti sans replay
    /// (« aucun des 13 enregistrement(s) »). L'heure de la demande permet d'adopter ensuite l'enregistrement qu'elle a ouvert.
    /// </summary>
    private DateTime? _recordDemandeUtc;

    /// <summary>Combien de temps on attend que RetroArch confirme un enregistrement demande.</summary>
    internal static readonly TimeSpan AttenteDeLaConfirmation = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan PasDeLaConfirmation = TimeSpan.FromMilliseconds(150);

    /// <summary>Au-dela, un enregistrement que RetroArch tient n'est plus attribue a notre demande.</summary>
    internal static readonly TimeSpan FenetreDAdoption = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Attend que RetroArch confirme l'enregistrement (active_replay en enregistrement), un sondage par <paramref name="pas"/>,
    /// au plus <paramref name="max"/>. Rend la derniere lecture (confirmee ou non).
    /// </summary>
    internal static async Task<RaActiveReplay?> AttendreLaConfirmationAsync(Func<CancellationToken, Task<RaActiveReplay?>> lire,
        TimeSpan pas, TimeSpan max, CancellationToken ct)
    {
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        RaActiveReplay? lu;
        do
        {
            await Task.Delay(pas, ct).ConfigureAwait(false);
            lu = await lire(ct).ConfigureAwait(false);
            if (lu is { Recording: true }) return lu;
        }
        while (chrono.Elapsed < max);
        return lu;
    }

    /// <summary>
    /// RetroArch enregistre, aucun replay n'est suivi, et notre demande date de moins de <paramref name="fenetre"/> : c'est
    /// l'enregistrement qu'elle a ouvert, en retard. Pur.
    /// </summary>
    internal static bool EnregistrementAAdopter(RaActiveReplay? active, DateTime? demandeUtc, DateTime maintenant, TimeSpan fenetre)
        => active is { Recording: true } && demandeUtc is { } demande && maintenant >= demande && maintenant - demande <= fenetre;

    public ReplayRecorderService(RetroArchReplayClient ra, ReplayStore store, ReplayCoreTimingProbe timing,
        IEventBus bus, RetroBat.Api.Replay.Playback.ReplayPlaybackService playback, ILogger<ReplayRecorderService> logger,
        IConfiguration config, RetroBat.Api.Media.InstalledGameCatalog? catalogue = null,
        RetroBat.Api.Infrastructure.PartieNelfePlayService? partie = null)
    {
        _ra = ra; _store = store; _timing = timing; _bus = bus; _playback = playback; _logger = logger;
        _config = config; _catalogue = catalogue; _partie = partie;
        _garde = new GardeDesPlantages(Path.Combine(Path.GetDirectoryName(store.ActiveRecordingPath) ?? ".", "sans-enregistrement.json"));
        _annonces = new FileDEvenements(bus, logger);
        Courant = this;
    }

    /// <summary>L'enregistreur en service : la session d'un speedrun lui ouvre le replay d'une relance (<see cref="OuvrirApresRelanceAsync"/>).</summary>
    internal static ReplayRecorderService? Courant { get; private set; }

    /// <summary>Un seul tour a la fois : la boucle, ou l'ouverture demandee par une relance.</summary>
    private readonly SemaphoreSlim _tour = new(1, 1);

    /// <summary>Le dernier etat de RetroArch vu avec un jeu charge : la relance ouvre son replay sans le redemander.</summary>
    private RaStatus? _dernierStatut;

    /// <summary>Quand une relance a ouvert son replay elle-meme : l'evenement qui suit ne la rejoue pas.</summary>
    private DateTime _ouvertALaRelance = DateTime.MinValue;

    /// <summary>
    /// LA RELANCE ANNONCEE (08/11). La session lance <see cref="OuvrirApresRelanceAsync"/> en tache detachee puis publie
    /// aussitot « speedrun.etat.charge » ; <see cref="_ouvertALaRelance"/> n'etait pose qu'une fois le replay ouvert, 150 a
    /// 500 ms plus tard. L'evenement passait avant, et le tour suivant de la boucle arretait le replay qui venait de s'ouvrir
    /// (journal d'un joueur du 07/11 : ouvert a 16:01:05.296, « arrete a la relance » a 16:01:05.377, puis « en attente du
    /// depart ») : la tentative suivante n'etait enregistree qu'en partie, ou pas du tout, et une meilleure tentative tombee
    /// dans ce trou repartait avec le dernier replay ouvert, un bout de 29 images, sans bornes ni film. La session l'annonce
    /// ici AVANT de lancer la tache : l'evenement qui suit laisse la relance faire.
    /// </summary>
    private DateTime _relanceAnnoncee = DateTime.MinValue;

    /// <summary>Le temps pendant lequel une relance annoncee ou ouverte couvre l'evenement « speedrun.etat.charge ».</summary>
    internal static readonly TimeSpan RelanceCouvreLEvenement = TimeSpan.FromSeconds(10);

    /// <summary>La session va ouvrir le replay de sa relance (<see cref="OuvrirApresRelanceAsync"/>) : a appeler avant.</summary>
    public void AnnoncerRelance() => _relanceAnnoncee = DateTime.UtcNow;

    /// <summary>
    /// L'evenement « speedrun.etat.charge » est-il deja pris en charge par la relance ? Oui si elle a ete annoncee ou a ouvert
    /// son replay il y a moins de <see cref="RelanceCouvreLEvenement"/>. Pur.
    /// </summary>
    internal static bool RelanceCouvreLEtatCharge(DateTime maintenant, DateTime annoncee, DateTime ouverte)
        => maintenant - annoncee < RelanceCouvreLEvenement || maintenant - ouverte < RelanceCouvreLEvenement;

    /// <summary>
    /// LA SESSION OUVRE LE REPLAY DE SA RELANCE (05/11, ta course de 25,66 s : replay ouvert 1,75 s apres l'etat recharge, le
    /// depart de Mario etait deja passe). Appele juste apres l'etat de depart, sans attendre le tour de la boucle ni redemander
    /// l'etat de RetroArch : l'ancien replay se repere, le neuf s'ouvre, l'ancien se scelle ensuite.
    /// </summary>
    public async Task OuvrirApresRelanceAsync(CancellationToken ct)
    {
        var etapes = new EtapesDeLaRelance();
        var tourPris = await _tour.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        etapes.Noter("tour");
        if (!tourPris)
        {
            // la boucle prend le relais, une seule fois : l'annonce couvre encore l'evenement (passe ou a venir), et on fait
            // ici ce qu'il aurait fait
            _logger.LogInformation("Replay : relance du defi, la boucle tient son tour depuis plus d'une seconde ; elle ouvrira le replay.");
            NoterLEtatCharge();
            return;
        }
        try
        {
            _speedrun = true;
            _etatDeDepartAttendu = false;
            _runTermine = false;
            _arretParBaisse = false;
            _relanceDuDefi = false;
            _sourceDuDepart = "etat de depart du defi";
            _departEnAttente = true;
            var precedent = _current;
            var statut = _dernierStatut;
            string? fichier = null;
            _logger.LogInformation("Replay : relance du defi, ouverture du replay (en cours : {Precedent}).", precedent?.ReplayId ?? "aucun");
            if (precedent is not null)
            {
                await _ra.HaltAsync(ct).ConfigureAwait(false);
                etapes.Noter("arret");
                fichier = FindReplayFile(precedent);
                etapes.Noter("fichier");
                _current = null;
                await PublishAsync("replay.recording.stopped", new { precedent.ReplayId, precedent.SessionId, precedent.LastFrame }, ct).ConfigureAwait(false);
                etapes.Noter("annonce");
                _logger.LogInformation("Replay : {ReplayId} arrete a la relance du defi, le suivant s'ouvre sur l'etat de depart.", precedent.ReplayId);
            }

            if (statut is { ContentLoaded: true } && !_playback.IsBusy)
            {
                _attenteAnnoncee = "";
                await StartAsync(statut, ct).ConfigureAwait(false);
                etapes.Noter("ouverture");
                if (_current is not null) _ouvertALaRelance = DateTime.UtcNow;
            }
            else
            {
                // (12/11) une relance qui n'ouvrait rien ne le disait nulle part
                _logger.LogWarning("Replay : relance du defi sans replay ouvert (RetroArch {Etat}, lecture en cours {Lecture}).",
                    statut?.State ?? "jamais vu", _playback.IsBusy);
            }

            if (precedent is not null)
            {
                // le scellement de l'ancien (jusqu'a une dizaine de secondes) ne retient ni la session ni la relance suivante :
                // il ne touche plus au replay en cours (FinalizeAsync ne vide _current que s'il est encore le sien)
                _ = Task.Run(async () =>
                {
                    try { await FinalizeAsync(precedent, recovered: false, CancellationToken.None, fichier, dejaArrete: true).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Replay : scellement de {ReplayId} en echec", precedent.ReplayId); }
                });
            }
        }
        finally
        {
            _tour.Release();
            if (tourPris)
            {
                if (etapes.Lente() is { } lente)
                    _logger.LogWarning("Replay : relance du defi lente, etape {Etape} en {Ms} ms ({Detail}).", lente.Nom, lente.Ms, etapes.Detail());
                else
                    _logger.LogInformation("Replay : relance du defi en {Ms} ms ({Detail}).", etapes.TotalMs, etapes.Detail());
            }
        }
    }

    /// <summary>
    /// LES ETAPES CHRONOMETREES DE LA RELANCE (12/11) : le banc de 18:17 a montre 34 s entre l'entree et l'arret de l'ancien
    /// replay, sans dire ou. Chaque etape est mesuree ; une etape au-dela de <see cref="FileDEvenements.SeuilDeLenteur"/> est
    /// signalee.
    /// </summary>
    internal sealed class EtapesDeLaRelance
    {
        private readonly System.Diagnostics.Stopwatch _chrono = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<(string Nom, long Ms)> _etapes = new();
        private long _precedent;

        public void Noter(string nom)
        {
            var maintenant = _chrono.ElapsedMilliseconds;
            _etapes.Add((nom, maintenant - _precedent));
            _precedent = maintenant;
        }

        public long TotalMs => _chrono.ElapsedMilliseconds;

        public IReadOnlyList<(string Nom, long Ms)> Etapes => _etapes;

        /// <summary>La plus longue etape au-dela du seuil, ou null.</summary>
        public (string Nom, long Ms)? Lente()
        {
            var seuil = (long)FileDEvenements.SeuilDeLenteur.TotalMilliseconds;
            (string Nom, long Ms)? pire = null;
            foreach (var e in _etapes)
                if (e.Ms > seuil && (pire is null || e.Ms > pire.Value.Ms)) pire = e;
            return pire;
        }

        public string Detail() => string.Join(", ", _etapes.Select(e => $"{e.Nom} {e.Ms} ms"));
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
        // RETROARCH NELFEPLAY (2026-10-16) : la nocturne posee par install-retroarch-nelfeplay.bat porte le correctif de
        // l'encodeur ; sous elle, ni le coeur MAME libretro ni TGM2 ne sont ecartes. La garde des plantages veille encore.
        var corrige = RetroArchNelfePlayInstalle();
        if (!corrige && await CoeurSansEnregistrementAsync(status, ct).ConfigureAwait(false)) return true;
        if (!corrige && JeuSansEnregistrement(JeuxSansEnregistrement, status.Game))
        {
            if (!string.Equals(_jeuAnnonce, status.Game, StringComparison.OrdinalIgnoreCase))
            {
                _jeuAnnonce = status.Game;
                _logger.LogInformation(
                    "Replay : pas d'enregistrement de {Game} : RetroArch 1.22.2 plante en ouvrant son replay (etat trop gros pour son encodeur) ; la partie compte toujours et se filme.",
                    status.Game);
            }
            return true;
        }

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
    /// LES JEUX SOUS LESQUELS ON N'ENREGISTRE PAS, noms de ROM separes par des virgules (2026-10-16). Le meme defaut de
    /// RetroArch 1.22.2 que sous MAME libretro frappe un gros etat sous FBNeo : TGM2 (Psikyo SH-2, 1,2 Mo de memoire)
    /// plantait RetroArch a la seconde ou le replay s'ouvrait, au credit, a chaque partie, chez deux joueurs (0xC0000005
    /// dans memcpy de msvcrt.dll). Les jeux ouverts jusque-la enregistrent sans encombre. Vide = aucun.
    /// </summary>
    private string JeuxSansEnregistrement => _config["Replay:Record:JeuxSansEnregistrement"] ?? "tgm2,tgm2p";
    private string _jeuAnnonce = "";

    /// <summary>
    /// La marque que pose install-retroarch-nelfeplay.bat (emulators\retroarch\.nelfeplay\installe.txt) ET un
    /// retroarch.exe qui est bien celui qu'elle a installe (ligne « exe= », son SHA-256) : une mise a jour de RetroBat
    /// qui remettrait la 1.22.2 laisse la marque, jamais la bonne empreinte. Le desinstalleur retire la marque.
    /// </summary>
    internal static bool RetroArchNelfePlayInstalle(string? dossierRetroArch = null)
    {
        try
        {
            var dossier = dossierRetroArch ?? Path.GetDirectoryName(RetroBat.Domain.Paths.RetroBatPaths.RetroArchConfigPath) ?? "";
            var marque = Path.Combine(dossier, ".nelfeplay", "installe.txt");
            var exe = Path.Combine(dossier, "retroarch.exe");
            if (dossier.Length == 0 || !File.Exists(marque) || !File.Exists(exe)) return false;
            var attendue = File.ReadAllLines(marque).Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("exe=", StringComparison.OrdinalIgnoreCase))?[4..].Trim() ?? "";
            if (attendue.Length != 64) return false;
            var info = new FileInfo(exe);
            var cle = (exe, info.Length, info.LastWriteTimeUtc);
            lock (EmpreinteVerrou)
            {
                if (_empreinteLue is { } lue && lue.Cle == cle) return string.Equals(lue.Sha, attendue, StringComparison.OrdinalIgnoreCase);
                using var flux = File.OpenRead(exe);
                var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(flux));
                _empreinteLue = (cle, sha);
                return string.Equals(sha, attendue, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    // L'empreinte de retroarch.exe, relue seulement quand l'exe change (taille ou date).
    private static readonly object EmpreinteVerrou = new();
    private static ((string Exe, long Taille, DateTime Date) Cle, string Sha)? _empreinteLue;

    /// <summary>Ce jeu (le nom de sa ROM) est-il dans la liste ? Pur.</summary>
    internal static bool JeuSansEnregistrement(string liste, string? jeu)
        => !string.IsNullOrWhiteSpace(jeu) && liste.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(j => string.Equals(j, Path.GetFileNameWithoutExtension(jeu.Trim()), StringComparison.OrdinalIgnoreCase));

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
                _verrousAttestes = null;   // l'attestation de ce jeu-ci les redira
                _reglesAttestees = null;
                break;
            case "ui.game.ended":
                _garde.JeuTermine();
                break;
            case "scoring.listener.session":
                _garde.FinDePartieRecue();
                break;
            case "scoring.listener.attestation":
                try
                {
                    var attestation = System.Text.Json.JsonSerializer.SerializeToElement(e.Payload);
                    _garde.EcouteVue(GardeDesPlantages.CoeurDeLAttestation(attestation));
                    _verrousAttestes = VerrousDeLAttestation(attestation);
                    _reglesAttestees = ReglesDeLAttestation(attestation);
                }
                catch { _garde.EcouteVue("?"); }
                break;
        }

        if (string.Equals(e.Type, "scoring.run.ended", StringComparison.Ordinal))
        {
            _runTermine = true;
            return;
        }

        switch (e.Type)
        {
            case "speedrun.etat.attendu":
                _etatDeDepartAttendu = true;
                _speedrun = true;
                return;
            case "speedrun.etat.charge":
                // la relance ouvre (ou a deja ouvert) son replay (OuvrirApresRelanceAsync) : rien a rejouer
                if (RelanceCouvreLEtatCharge(DateTime.UtcNow, _relanceAnnoncee, _ouvertALaRelance)) return;
                NoterLEtatCharge();
                return;
            case "ui.game.ended":
                _etatDeDepartAttendu = false;
                _speedrun = false;
                break;
            case "ui.game.started":
                _speedrun = false;
                break;
        }

        // La baisse du score d'un speedrun vient de sa relance (l'etat recharge), qui a deja ouvert le replay suivant.
        if (_speedrun && string.Equals(e.Type, "scoring.replay.stop", StringComparison.Ordinal)) return;

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
        // plus rien (player, 2026-10-02 : « en attente d'un START », puis plus rien).
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

    /// <summary>L'etat de depart recharge sans relance ouverte par la session : le tour suivant arrete l'ancien replay et ouvre le neuf.</summary>
    private void NoterLEtatCharge()
    {
        _etatDeDepartAttendu = false;
        _speedrun = true;
        _sourceDuDepart = "etat de depart du defi";
        _departEnAttente = true;
        // l'etat recharge ouvre une nouvelle tentative : rien de la precedente (fin, baisse du score) ne la retient
        _runTermine = false;
        _arretParBaisse = false;
        _relanceDuDefi = true;
        Reveiller();
    }

    /// <summary>Un START assez recent pour qu'on enregistre. Le dit une fois par attente.</summary>
    private bool StartRecent(RaStatus status)
    {
        if (!StartRequis)
        {
            _sourceDuDepart = "chargement du jeu";
            return true;
        }
        if (_etatDeDepartAttendu) return false;
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
            try
            {
                await _tour.WaitAsync(stoppingToken).ConfigureAwait(false);
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                finally { _tour.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay recorder : tick en erreur"); }
            try { await _reveil.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // Arrêt propre : finaliser une session en cours si possible.
        if (_current is not null)
            try { await FinalizeAsync(_current, recovered: false, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Replay : finalisation à l'arrêt échouée"); }
        await _annonces.ViderAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
            _dernierStatut = status;
            var contenu = status.System + "/" + status.Game;
            if (!string.Equals(_contenuVu, contenu, StringComparison.Ordinal))
            {
                // Un AUTRE jeu que celui qu'on suivait : son depart ne vaut pas pour celui-ci. Le
                // premier jeu vu apres un lancement garde le sien (il ne peut venir que de lui).
                if (_contenuVu.Length > 0) _departEnAttente = false;
                if (_contenuVu.Length > 0) _recordDemandeUtc = null;
                _contenuVu = contenu;
                _dejaEnregistre = false;
            }
        }

        if (_relanceDuDefi)
        {
            _relanceDuDefi = false;
            if (_current is { } precedent)
            {
                // La session a deja arrete le replay de la tentative (HALT_REPLAY avant l'etat) ; son fichier se repere
                // AVANT d'en ouvrir un autre (le plus recent du dossier), puis le neuf s'ouvre, et l'ancien se scelle apres.
                await _ra.HaltAsync(ct).ConfigureAwait(false);
                var fichier = FindReplayFile(precedent);
                _current = null;
                // la capture video de la tentative s'arrete avant que celle de la suivante demarre (ReplayCaptureService)
                await PublishAsync("replay.recording.stopped", new { precedent.ReplayId, precedent.SessionId, precedent.LastFrame }, ct).ConfigureAwait(false);
                _logger.LogInformation("Replay : {ReplayId} arrete a la relance du defi, le suivant s'ouvre sur l'etat de depart.", precedent.ReplayId);
                if (status is { ContentLoaded: true } && !_playback.IsBusy && StartRecent(status))
                {
                    _attenteAnnoncee = "";
                    await StartAsync(status, ct).ConfigureAwait(false);
                }

                await FinalizeAsync(precedent, recovered: false, ct, fichier, dejaArrete: true).ConfigureAwait(false);
                return;
            }
        }

        if (_current is null)
        {
            // L'enregistrement demande et confirme en retard (12/11) : on le suit au lieu de l'ignorer.
            if (status is { ContentLoaded: true } && EnregistrementAAdopter(active, _recordDemandeUtc, DateTime.UtcNow, FenetreDAdoption))
            {
                var version = await _ra.GetVersionAsync(ct).ConfigureAwait(false) ?? "unknown";
                var demande = _recordDemandeUtc!.Value;
                _logger.LogWarning("Replay : RetroArch a ouvert l'enregistrement demande {Secondes:0.0} s plus tot sans le confirmer a temps ; il est repris ({Frames} images deja enregistrees).",
                    (DateTime.UtcNow - demande).TotalSeconds, active!.Frame);
                await OuvrirAsync(status, version, active.Frame, demande, adopte: true, ct).ConfigureAwait(false);
                return;
            }

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

            // La partie d'un autre, regardee ou rejointe en netplay (2026-10-16) : ni replay ni film ici.
            if (status is { ContentLoaded: true } && !RetroBat.Api.Netplay.NetplayGuestService.PartieALaBorne)
            {
                var cle = "netplay:" + status.System + "/" + status.Game;
                if (!string.Equals(_attenteAnnoncee, cle, StringComparison.Ordinal))
                {
                    _attenteAnnoncee = cle;
                    _logger.LogInformation("Replay : {Game} est la partie de l'hote (netplay), pas d'enregistrement.", status.Game);
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
                    // du départ » juste après qu'on en a vu un. Le jeu a eu son départ : un bouton ne
                    // réarme plus (le film de la partie ne repartirait pas sur la démo).
                    _departEnAttente = false;
                    _attenteAnnoncee = status.System + "/" + status.Game;
                    _dejaEnregistre = true;
                    // La partie se filme quand même, sans replay (ReplayCaptureService) : filmer ne
                    // passe pas par l'encodeur d'état qui plante (décision du 14 octobre 2026).
                    await PublishAsync("replay.recording.skipped", new { status.System, status.Game }, ct).ConfigureAwait(false);
                    return;
                }
                await StartAsync(status, ct).ConfigureAwait(false);
            }
            return;
        }

        if (_runTermine)
        {
            _logger.LogInformation("Replay : {ReplayId} arrete au continue, la suite de la partie ne compte pas.", _current.ReplayId);
            await ArreterPuisScellerAsync(_current, ct).ConfigureAwait(false);
            return;
        }

        if (_arretParBaisse)
        {
            _arretParBaisse = false;
            _logger.LogInformation("Replay : {ReplayId} arrete, le score a baisse (nouvelle partie ou remise a zero).", _current.ReplayId);
            await ArreterPuisScellerAsync(_current, ct).ConfigureAwait(false);
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
            await ArreterPuisScellerAsync(_current, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ARRETER DANS LE TOUR, SCELLER A COTE (12/11). Le scellement (stabilisation du fichier, import) tient jusqu'a une dizaine
    /// de secondes ; fait dans le tour de la boucle, il retenait le semaphore, et la relance d'un defi, qui ne l'attend qu'une
    /// seconde, laissait la boucle ouvrir son replay apres : le 12/11 a 09:22, ouvert a 23,4 s pour un depart pris a 22,2 s.
    /// L'arret et le reperage du fichier restent ici (avant tout autre enregistrement) ; le reste part en tache, comme a la
    /// relance.
    /// </summary>
    private async Task ArreterPuisScellerAsync(Recording rec, CancellationToken ct)
    {
        await _ra.HaltAsync(ct).ConfigureAwait(false);
        // La capture video de la partie s'arrete avec son replay (ReplayCaptureService).
        await PublishAsync("replay.recording.stopped", new { rec.ReplayId, rec.SessionId, rec.LastFrame }, ct).ConfigureAwait(false);
        var fichier = FindReplayFile(rec);
        if (ReferenceEquals(_current, rec)) _current = null;
        _ = Task.Run(async () =>
        {
            try { await FinalizeAsync(rec, recovered: false, CancellationToken.None, fichier, dejaArrete: true).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Replay : scellement de {ReplayId} en echec", rec.ReplayId); }
        });
    }

    private async Task StartAsync(RaStatus status, CancellationToken ct)
    {
        var version = await _ra.GetVersionAsync(ct).ConfigureAwait(false) ?? "unknown";
        // LA GARDE S'ARME AVANT L'ORDRE (2026-10-16) : RetroArch plantait PENDANT l'ouverture du replay (TGM2), avant la
        // confirmation ; armee apres elle, la garde ne voyait rien et le jeu replantait a chaque partie. Un arret normal
        // garde sa fin de session (rien a juger) ; seul un plantage fait renoncer au replay de ce jeu.
        _garde.EnregistrementLance(CleDuJeu(status, version), status.Game, EmpreinteDeRetroArch(version));
        var demande = DateTime.UtcNow;
        _recordDemandeUtc = demande;
        await _ra.RecordAsync(ct).ConfigureAwait(false);

        // Confirmer que l'enregistrement a bien démarré (active_replay flags=8). Une seule lecture a 300 ms ne suffisait pas sur
        // une borne chargee (12/11 : 0,9 s) ; au-dela de l'attente, la boucle reprend l'enregistrement s'il s'ouvre (adoption).
        var check = await AttendreLaConfirmationAsync(_ra.GetActiveReplayAsync, PasDeLaConfirmation, AttenteDeLaConfirmation, ct).ConfigureAwait(false);
        if (check is not { Recording: true })
        {
            _logger.LogWarning("Replay : RECORD_REPLAY non confirme apres {Secondes} s (active={Active}) ; repris s'il s'ouvre, sinon redemande au prochain tour.",
                AttenteDeLaConfirmation.TotalSeconds, check);
            return;
        }

        await OuvrirAsync(status, version, check.Frame, demande, adopte: false, ct).ConfigureAwait(false);
    }

    /// <summary>Le replay que RetroArch enregistre devient le replay courant (ouvert par nous, ou adopte en retard).</summary>
    private async Task OuvrirAsync(RaStatus status, string version, long imagesDejaEnregistrees, DateTime demandeUtc, bool adopte, CancellationToken ct)
    {
        _recordDemandeUtc = null;
        var rec = new Recording
        {
            ReplayId = Ulid.NewReplayId(),
            SessionId = Ulid.NewSessionId(),
            System = status.System,
            Game = status.Game,
            Crc32 = status.Crc32,
            // l'heure de la demande : le fichier se cherche a partir d'elle (FindReplayFile), meme adopte plus tard
            StartedAtUtc = demandeUtc,
            LastFrame = imagesDejaEnregistrees,
            RetroArchVersion = version.Trim(),
            Categorie = _categorieSuivante,
        };
        _categorieSuivante = null;
        rec.CoreFps = ProbeCoreFps(rec.Crc32, rec.Game);
        rec.LastSampleUtc = DateTime.UtcNow;
        _current = rec;

        _store.WriteJsonAtomic(_store.ActiveRecordingPath, new ActiveRecordingState(
            "nelfe.replay.active-recording.v1", rec.ReplayId, rec.SessionId, rec.System, rec.Game,
            rec.Crc32, rec.StartedAtUtc, rec.RetroArchVersion));

        _logger.LogInformation("Replay : enregistrement démarré {ReplayId} ({System}/{Game}{Categorie}), départ : {Source}{Adopte}.",
            rec.ReplayId, rec.System, rec.Game, rec.Categorie is null ? "" : ", " + rec.Categorie,
            _sourceDuDepart.Length > 0 ? _sourceDuDepart : "START", adopte ? " (repris en retard)" : "");
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
        // Frame : les images deja enregistrees a l'annonce ; le rapporteur en recale le debut dans l'horloge du wrapper.
        await PublishAsync("replay.recording.started",
            new { rec.ReplayId, rec.SessionId, rec.System, rec.Game, Frame = rec.LastFrame }, ct).ConfigureAwait(false);
        // apres l'annonce : RetroArch enregistre deja, une question de plus ne retarde rien
        rec.Dossier = await _ra.GetSavestateDirectoryAsync(ct).ConfigureAwait(false);
    }

    /// <param name="fichier">Le fichier deja repere (relance d'un defi : le replay suivant est ouvert, le plus recent n'est plus lui).</param>
    /// <param name="dejaArrete">RetroArch ne l'enregistre plus et la capture a ete prevenue (relance d'un defi).</param>
    /// <summary>L'enregistrement en cours sur disque (reprise apres un arret brutal) n'est plus : sauf s'il est deja celui d'un replay ouvert depuis.</summary>
    private void OublierLEnregistrementActif()
    {
        if (_current is null) _store.DeleteQuiet(_store.ActiveRecordingPath);
    }

    private async Task FinalizeAsync(Recording rec, bool recovered, CancellationToken ct, string? fichier = null, bool dejaArrete = false)
    {
        // on sort de l'état "en cours" immédiatement (idempotence) ; un replay ouvert depuis (relance d'un defi) reste le sien
        if (ReferenceEquals(_current, rec)) _current = null;
        try
        {
            if (!dejaArrete)
            {
                await _ra.HaltAsync(ct).ConfigureAwait(false);
                // La capture video de la partie s'arrete avec son replay (ReplayCaptureService).
                await PublishAsync("replay.recording.stopped", new { rec.ReplayId, rec.SessionId, rec.LastFrame }, ct).ConfigureAwait(false);
            }

            var file = fichier ?? FindReplayFile(rec);
            if (file is null)
            {
                _logger.LogWarning("Replay : aucun fichier .replay trouvé pour {ReplayId} — abandon.", rec.ReplayId);
                OublierLEnregistrementActif();
                return;
            }

            if (!await StabilizeAsync(file, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Replay : fichier {File} non stabilisé — conservé pour diagnostic.", file);
                OublierLEnregistrementActif();
                return;
            }

            var obj = await _store.ImportObjectAsync(file, ct).ConfigureAwait(false);
            var hint = BuildLaunchHint(file);
            var manifest = BuildManifest(rec, obj, hint);
            _store.SaveManifest(manifest);
            _store.SaveMeta(ReplayLocalMetadata.Fresh(rec.ReplayId, hint) with { Categorie = rec.Categorie });
            _store.RebuildIndex();
            OublierLEnregistrementActif();

            _logger.LogInformation("Replay finalisé {ReplayId} : sha256={Sha} taille={Size} frames={Frames}{Rec}.",
                rec.ReplayId, obj.Sha256, obj.Size, rec.LastFrame, recovered ? " (recovery)" : "");
            // (11/11) LE MODE DES ENTREES : un replay enregistre touche par touche (le coeur n'a pas eu le masque) se relit par le
            // wrapper sans masque. Le declencheur n'est pas encore connu : la trace le relie a son lancement.
            if (Playback.ModeDesEntrees.Lire(_store.ObjectPath(obj.Sha256)) == Playback.ModeDesEntrees.Mode.Touches)
                _logger.LogWarning("Replay {ReplayId} enregistre TOUCHE PAR TOUCHE (le coeur {Coeur} n'a pas eu le masque des touches) : "
                    + "relu par le wrapper sans masque. Jeu {Jeu}, lancement {Lancement}.", rec.ReplayId, manifest.Runtime.CoreName,
                    manifest.Game.RomGroup ?? manifest.Game.GameId, hint?.CoreDll ?? "?");
            await PublishAsync("replay.finalized", new { rec.ReplayId, rec.SessionId, obj.Sha256, obj.Size }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : finalisation de {ReplayId} en échec", rec.ReplayId);
        }
    }

    /// <summary>Les verrous d'une attestation du wrapper (« Locks »), null s'il n'y en a pas (ou wrapper d'avant 0.343).</summary>
    internal static string? VerrousDeLAttestation(System.Text.Json.JsonElement attestation)
        => attestation.ValueKind == System.Text.Json.JsonValueKind.Object
           && attestation.TryGetProperty("Locks", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String
           && l.GetString() is { Length: > 0 } lignes ? lignes : null;

    /// <summary>Les regles d'une attestation du wrapper (« Rules », 0.346), null s'il n'y en a pas.</summary>
    internal static string? ReglesDeLAttestation(System.Text.Json.JsonElement attestation)
        => attestation.ValueKind == System.Text.Json.JsonValueKind.Object
           && attestation.TryGetProperty("Rules", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String
           && r.GetString() is { Length: > 0 } regles ? regles : null;

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
            CoreName: string.IsNullOrEmpty(hint?.Core) ? null : hint!.Core,
            // Les fichiers du zip et leurs CRC : une autre borne reconnait ainsi le meme jeu zippe autrement.
            RomFiles: Playback.ContenuDArchive.Lire(hint?.RomPath),
            // Les verrous d'un defi : la lecture les remet, sinon la partie diverge (D18).
            Locks: _verrousAttestes,
            // Les regles d'un challenge : la lecture les remet aussi (wrapper 0.346).
            Rules: _reglesAttestees);

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

    /// <summary>Le dernier enregistrement echantillonne, pour la cadence de la partie (<see cref="CadenceDeLaPartie"/>).</summary>
    private static Recording? _derniereCadence;
    private static DateTime _derniereCadenceUtc;

    /// <summary>
    /// LA VITESSE REELLE DE LA PARTIE (11/11, ROM NTSC forcee a 50 Hz par l'affichage) : la cadence annoncee par le coeur et la
    /// mediane de la cadence observee (images emulees par seconde reelle, une mesure par tick, pauses ecartees). Null sans
    /// enregistrement echantillonne depuis <paramref name="fraicheur"/>.
    /// </summary>
    public static (double? CoeurFps, double? Mediane, int Echantillons, string Jeu)? CadenceDeLaPartie(TimeSpan fraicheur)
    {
        var rec = _derniereCadence;
        if (rec is null || DateTime.UtcNow - _derniereCadenceUtc > fraicheur) return null;
        List<double> copie;
        lock (rec.RateSamples) copie = [.. rec.RateSamples];
        if (copie.Count == 0) return (rec.CoreFps, null, 0, rec.Game);
        copie.Sort();
        var mediane = copie.Count % 2 == 1 ? copie[copie.Count / 2] : (copie[copie.Count / 2 - 1] + copie[copie.Count / 2]) / 2;
        return (rec.CoreFps, mediane, copie.Count, rec.Game);
    }

    /// <summary>Un échantillon de cadence par tick : combien de frames émulées par seconde réelle.</summary>
    private static void SampleRate(Recording rec, long frame)
    {
        _derniereCadence = rec;
        _derniereCadenceUtc = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var dt = (now - rec.LastSampleUtc).TotalSeconds;
        var df = frame - rec.LastFrame;
        rec.LastSampleUtc = now;
        if (dt <= 0.5 || dt > 10 || df <= 0) return; // tick anormal, jeu en pause, ou compteur remis à zéro
        var rate = df / dt;
        if (rate is > MinPlausibleFps and < MaxPlausibleFps) lock (rec.RateSamples) rec.RateSamples.Add(rate);
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

    /// <summary>Le fichier .replay le plus récent écrit depuis le début de l'enregistrement, dans tout saves (reprise apres un arret brutal).</summary>
    private static string? FindReplayFile(DateTime startedAtUtc)
        => LePlusRecent(RetroBatPaths.SavesRoot, startedAtUtc);

    /// <summary>
    /// LE FICHIER D'UN ENREGISTREMENT, DANS LE DOSSIER DU COEUR (12/11). Le parcours de tout saves, fait pendant la relance
    /// d'un defi, ne sert qu'en repli : le dossier de sauvegarde retenu a l'ouverture suffit.
    /// </summary>
    private string? FindReplayFile(Recording rec)
    {
        if (rec.Dossier is { Length: > 0 } dossier && LePlusRecent(dossier, rec.StartedAtUtc) is { } trouve) return trouve;
        _logger.LogInformation("Replay : {ReplayId} cherche dans tout saves (dossier du coeur {Dossier} vide ou inconnu).",
            rec.ReplayId, rec.Dossier ?? "inconnu");
        return FindReplayFile(rec.StartedAtUtc);
    }

    /// <summary>Le .replay le plus recent d'un dossier (et de ses sous-dossiers), ecrit au plus 2 s avant <paramref name="depuisUtc"/>.</summary>
    internal static string? LePlusRecent(string dossier, DateTime depuisUtc)
    {
        var margin = depuisUtc.AddSeconds(-2);
        string? best = null; DateTime bestTime = DateTime.MinValue;
        if (!Directory.Exists(dossier)) return null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dossier, "*.replay*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t >= margin && t > bestTime) { best = f; bestTime = t; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return best; }
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

    /// <summary>En file, dans l'ordre : rend la main tout de suite (<see cref="FileDEvenements"/>).</summary>
    private Task PublishAsync(string type, object payload, CancellationToken ct)
    {
        _ = ct;
        _annonces.Publier(type, payload);
        return Task.CompletedTask;
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
