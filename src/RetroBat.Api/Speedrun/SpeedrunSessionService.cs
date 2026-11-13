using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;
using ProgressionDuLancement = RetroBat.Api.Leaderboard.ProgressionDuLancement;

namespace RetroBat.Api.Speedrun;

/// <summary>Le refus d'un lancement de defi, avec sa raison (unknown_challenge, game_running, not_installed...).</summary>
public sealed record ReponseLancement(bool Lance, string? Raison, string? Detail = null);

/// <summary>
/// Un defi dont les controles rapides sont passes (<see cref="SpeedrunSessionService.Verifier"/>) : reglement, ROM et etat de
/// depart lus. Il reste a le faire partir (<see cref="SpeedrunSessionService.Partir"/>), la partie lente du lancement.
/// </summary>
public sealed record LancementVerifie(Reglement Reglement, string Rom, byte[]? Etat);

/// <summary>Ce qu'une session laisse au journal : la page de bilan part de la.</summary>
public sealed record BilanDeSession(string SessionId, string DefiId, int Tentatives, int Finies, long? Meilleur, string? DernierEtat,
    string? DerniereCause);

/// <summary>
/// LA SESSION DE SPEEDRUN SUR LA BORNE (lot S3 du prototype Sonic, §10 et §11 bis).
///
/// - Lancement type « speedrun » : le jeu part par emulatorLauncher avec le coeur du reglement et
///   RetroAchievements coupe pour la seance (D15). La partie n'envoie aucun score au World Scoring
///   (PartieNelfePlayService.EstSpeedrun), et une partie World Scoring n'ouvre jamais de session.
/// - L'evaluateur lit le flux du wrapper (retroarch.memory.changed), comme le rapporteur de scoring, sans rien
///   demander de plus au wrapper. Les evenements d'une meme frame sont juges ensemble (§9.5) ; l'horloge de
///   chaque evenement est celle de son emission par le wrapper, pas celle de sa reception ici.
/// - Les valeurs qu'une ligne « change » n'a pas encore emises (l'acte de Green Hill 1, les minutes a 0) sont
///   relevees par READ_CORE_RAM, aux seules adresses du .MEM citees par le reglement.
/// - Relancer : RESET de RetroArch, ou l'etat de depart recharge (D19), tentative neuve. Abandonner : tentative close,
///   rien ne change dans le jeu. A la manette (D29) : Hotkey + gauche relance (maintenu une seconde pendant un run),
///   Hotkey + Start quitte.
/// - Le contrat du HUD : snapshot « speedrun.session » (retenu par le WebSocket, flux « speedrun »),
///   evenements « speedrun.* » avec la sequence de la session, commandes par SpeedrunController.
///
/// Rien n'est envoye : chaque tentative partie s'ecrit au journal local (state/speedrun/tentatives.jsonl),
/// qui donne aussi le meilleur temps du joueur.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SpeedrunSessionService : IHostedService, IDisposable
{
    /// <summary>Une annonce de lancement vaut pour le jeu qui demarre dans ce delai.</summary>
    private static readonly TimeSpan ValiditeDuLancement = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Les evenements d'une frame se jugent ensemble : l'image se ferme des qu'arrive un evenement d'une frame suivante, ou sans
    /// suite apres ce delai. LE DEPART MANQUE (03/11, Green Hill 2 pendant un direct) : la borne chargee livrait les deux
    /// evenements de la frame du depart (chrono lance, secondes remises a 0) a 120 ms d'ecart ; a 40 ms ils faisaient deux images,
    /// et le chrono partait quand les secondes valaient encore 2 : depart refuse, la tentative ne commencait jamais. Le temps
    /// se compte en frames et a l'horloge du wrapper, jamais a l'arrivee : attendre ne retarde que les messages.
    /// </summary>
    internal const int AttenteDeLImageMs = 400;

    /// <summary>Hotkey + gauche pendant un run : maintenu une seconde, pour eviter la relance par accident (D29).</summary>
    private static readonly TimeSpan MaintienDeLaRelance = TimeSpan.FromSeconds(1);

    /// <summary>La touche Hotkey de RetroBat (Select sur la borne de reference, releve le 29/10).</summary>
    private const string Hotkey = "select";

    /// <summary>
    /// RELANCER : HOTKEY + GAUCHE (choix du user, 01/11). Chaque bouton du visage est deja pris avec Hotkey : en bas le menu
    /// de RetroArch, a droite le game manager de RetroBat, en haut le chargement d'une sauvegarde, a gauche son ecriture.
    /// Hotkey + gauche est le retour arriere de RetroArch, coupe pendant un defi (rewind_enable faux) ; droite est l'avance
    /// rapide, haut et bas changent d'emplacement de sauvegarde.
    /// </summary>
    private const string BoutonDeRelance = "left";

    /// <summary>Avance rapide : les frames vont 1,5 fois plus vite que l'horloge, sur au moins 1,5 seconde.</summary>
    private const double RapportDAvanceRapide = 1.5;
    private const long FenetreDAvanceRapideMs = 1500;

    private static readonly Regex HorlogeDuWrapper = new(@"^\[(\d{2}):(\d{2}):(\d{2})\.(\d{3})\]", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly ICommandesRetroArch _commandes;
    private readonly ILogger<SpeedrunSessionService> _journal;
    private readonly PartieNelfePlayService? _partie;
    private readonly CatalogueDesDefis _catalogue;
    private readonly InterfaceTextService? _textes;
    private readonly EmulationStationSettingsService? _reglages;
    private readonly IPrioritesDuJeu? _priorites;
    private readonly string _fichierDesTentatives;
    private readonly object _verrou = new();
    private readonly List<Signal> _image = new();
    private readonly HashSet<string> _tenus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(long Frame, long HorlogeMs)> _cadence = new();

    private IDisposable? _abonnement;
    private System.Threading.Timer? _vidage;
    private SessionSpeedrun? _session;
    private SessionSpeedrun? _sessionDuLancement;
    private CancellationTokenSource? _releves;
    private (Reglement Reglement, string Rom, DateTime At)? _lancement;
    private long? _frameDeLImage;
    private long? _horlogeDeLImage;
    private CancellationTokenSource? _maintien;
    private int _requetesDeLaManette;
    private DateTime? _premiereImage;
    private DateTime? _etatCharge;
    private DateTime? _etatPose;
    private TaskCompletionSource? _demandeDeChargement;
    private bool _etatAttendu;

    /// <summary>L'epreuve de la partie en cours, que le direct partage montre ; null hors epreuve.</summary>
    public Reglement? EpreuveEnCours
    {
        get { lock (_verrou) return _session?.Reglement; }
    }

    /// <summary>Sans demande de la page de preparation (fermee, absente), l'etat pose se charge seul au bout de ce delai.</summary>
    internal TimeSpan ChargementDeSecours { get; set; } = TimeSpan.FromSeconds(15);

    public SpeedrunSessionService(IEventBus bus, ApiContext contexte, ILogger<SpeedrunSessionService> journal,
        PartieNelfePlayService? partie = null, ICommandesRetroArch? commandes = null, string? fichierDesTentatives = null,
        InterfaceTextService? textes = null, EmulationStationSettingsService? reglages = null, CatalogueDesDefis? catalogue = null,
        IPrioritesDuJeu? priorites = null, FantomesDuSpeedrun? fantomes = null, RetroBat.Api.Deck.DeckLaunchService? deck = null)
    {
        _fantomes = fantomes;
        _deck = deck;
        // Sans priorites (les tests), personne ne touche aux processus de la machine.
        _priorites = priorites;
        _catalogue = catalogue ?? new CatalogueDesDefis();
        _bus = bus;
        _contexte = contexte;
        _journal = journal;
        _partie = partie;
        _textes = textes;
        _reglages = reglages;
        _commandes = commandes ?? new CommandesRetroArchUdp();
        _fichierDesTentatives = fichierDesTentatives ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "speedrun", "tentatives.jsonl");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        _vidage = new System.Threading.Timer(_ => ViderLImage(), null, Timeout.Infinite, Timeout.Infinite);
        // (11/11) les ecarts aux fantomes, segment par segment : le fichier du coeur hote relu quatre fois par seconde
        if (_fantomes is not null) _ecarts = new System.Threading.Timer(_ => PublierLesEcarts(), null, PeriodeDesEcarts, PeriodeDesEcarts);
        // Une API arretee en plein defi a laisse ES en retrait.
        if (_priorites is { } priorites) _ = Task.Run(priorites.RendreLaMain, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _ecarts?.Dispose();
        _releves?.Cancel();
        _priorites?.RendreLaMain();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        _vidage?.Dispose();
        _ecarts?.Dispose();
        _releves?.Cancel();
    }

    /// <summary>
    /// Quand le wrapper a lu la premiere image de la session en cours : le coeur tourne, le jeu est a l'ecran. Le lancement
    /// annonce le jeu des le depart de RetroArch, bien avant (la page de preparation attend ce signe-ci).
    /// </summary>
    public DateTime? PremiereImageUtc
    {
        get { lock (_verrou) return _session is null ? null : _premiereImage; }
    }

    /// <summary>Quand l'etat de depart du defi a ete charge ; null sans session ou avant le chargement.</summary>
    public DateTime? EtatChargeUtc
    {
        get { lock (_verrou) return _session is null ? null : _etatCharge; }
    }

    /// <summary>Quand l'etat de depart a ete pose dans le dossier de RetroArch, pret a charger ; null sans session ou avant.</summary>
    public DateTime? EtatPoseUtc
    {
        get { lock (_verrou) return _session is null ? null : _etatPose; }
    }

    /// <summary>
    /// La page de preparation s'efface : l'etat pose se charge dessous, et le joueur le decouvre a sa premiere image. Avant la
    /// pose, le chargement suivra la pose ; apres le chargement, rien.
    /// </summary>
    public void ChargerLEtatDeDepart()
    {
        TaskCompletionSource? demande;
        lock (_verrou) demande = _demandeDeChargement;
        demande?.TrySetResult();
    }

    /// <summary>Le snapshot de la session en cours, ou null sans session.</summary>
    public InstantaneSpeedrun? Instantane()
    {
        lock (_verrou) return _session?.Instantane();
    }

    /// <summary>
    /// LE LANCEMENT TYPE D'UN DEFI (§10). Controles avant lancement : defi connu, aucun jeu en cours, jeu installe,
    /// .MEM en vigueur identique a celui que cite le reglement. Puis lancement direct, coeur du reglement impose,
    /// RetroAchievements coupe pour la seance.
    /// </summary>
    public ReponseLancement Lancer(string? defiId, Func<string, string, string?> trouverLaRom, bool pages = false)
    {
        var (verifie, refus) = Verifier(defiId, trouverLaRom);
        return verifie is null ? refus! : Partir(verifie, pages);
    }

    /// <summary>
    /// LES CONTROLES RAPIDES DU LANCEMENT (13/11) : defi connu, aucun jeu en cours, .MEM en vigueur identique au reglement, jeu
    /// installe, etat de depart a l'empreinte du reglement. Quelques lectures de fichiers, pas de reseau ni de processus : le
    /// panneau les fait avant de montrer la page de preparation, et un refus se dit encore dans le panneau. Rend le lancement
    /// verifie, ou le refus.
    /// </summary>
    public (LancementVerifie? Verifie, ReponseLancement? Refus) Verifier(string? defiId, Func<string, string, string?> trouverLaRom)
    {
        var reglement = _catalogue.Trouver(defiId);
        if (reglement is null) return (null, new(false, "unknown_challenge"));
        if (_contexte.Ui.Running is not null) return (null, new(false, "game_running"));

        var mem = reglement.DefinitionAuLancement();
        if (mem.Length == 0 || !File.Exists(mem)) return (null, new(false, "mem_missing", mem));
        var ecarts = reglement.VerifierContre(File.ReadAllBytes(mem));
        if (ecarts.Count > 0) return (null, new(false, "mem_mismatch", string.Join(" ; ", ecarts)));

        var rom = trouverLaRom(reglement.SystemId, reglement.RomGroup);
        if (rom is null) return (null, new(false, "not_installed"));
        // Un chemin Windows : FBNeo ne retrouve pas le dossier du romset dans « E:/RetroBat/roms/fbneo/mslug3.zip » (essai du
        // 01/11, « romset unknown »), Genesis Plus GX ne voyait pas la difference.
        try { rom = Path.GetFullPath(rom); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }

        // LE DEPART SUR ETAT : l'etat porte l'empreinte du reglement, sinon rien ne part.
        byte[]? etat = null;
        if (reglement.StartPolicy != "normal_boot")
        {
            if (reglement.Etat is null) return (null, new(false, "state_missing"));
            var (octets, refus) = EtatsDesDefis.Lire(reglement.Etat);
            if (octets is null) return (null, new(false, refus ?? "state_missing", reglement.Etat.Fichier));
            etat = octets;
        }

        return (new LancementVerifie(reglement, rom, etat), null);
    }

    /// <summary>
    /// LA PARTIE LENTE DU LANCEMENT : fantomes, PlayerDeck, manettes d'ES, verrous, lanceur. Mesuree sur la borne le 12/11 :
    /// de 7 a 26 s entre l'appui sur « Lancer » et la page de preparation, quand elle attendait tout cela. Les trois preparations
    /// independantes (fantomes, Deck, manettes) partent ENSEMBLE (<see cref="PreparerEnsemble"/>) : le lancement attend la plus
    /// longue, plus leur somme.
    /// <para>
    /// <paramref name="progression"/> : la barre de la page de preparation (13/11) ; chaque etape la fait avancer quand elle finit
    /// vraiment. Marquer une etape ne fait rien attendre.
    /// </para>
    /// </summary>
    public ReponseLancement Partir(LancementVerifie verifie, bool pages = false, ProgressionDuLancement? progression = null)
    {
        var (reglement, rom, etat) = (verifie.Reglement, verifie.Rom, verifie.Etat);
        // La page de preparation a pu tenir quelques secondes : un jeu parti entre-temps (par ES) passe avant.
        if (_contexte.Ui.Running is not null) return new(false, "game_running");

        var coeur = reglement.Coeurs.FirstOrDefault() is { Length: > 0 } c ? c : null;
        var pseudo = _contexte.PlayerPseudo ?? "";
        T Etape<T>(string id, Func<T> travail) => progression is null ? travail() : progression.Mesurer(id, travail);
        var (fantomesPoses, deck, manettes) = PreparerEnsemble(
            // Les fantomes (04/11) : le coeur hote seulement s'ils sont poses et que le site l'admet, sinon le coeur du reglement.
            // Un dump que le coeur hote ne reconnait pas (autre en-tete, 08/11) part sur le coeur d'origine. Jusqu'a 2,5 s.
            () => Etape(ProgressionDuLancement.Fantomes, () => _fantomes?.Preparer(reglement, pseudo, rom) == true),
            // LE PLAYERDECK (H-D20) : LAUNCH THE SPEEDRUN / LAUNCH THE CHALLENGE part avec le Deck du jeu, s'il en a un. Le decor
            // est un reglage d'affichage : il ne touche ni au coeur, ni au .MEM, ni aux verrous, ni a l'empreinte des options.
            // Son rendu, hors cache, prend jusqu'a 5 s (4,1 s le 12/11 a 09:19).
            () => Etape(ProgressionDuLancement.Deck, () => _deck?.Preparer(RetroBat.Api.Deck.DeckModes.Speedrun, reglement.SystemId, rom, reglement.RomGroup,
                RetroBat.Api.Deck.DeckRankBoard.DeLEpreuve(reglement), epreuve: RetroBat.Api.Deck.DeckTrialPlan.De(reglement),
                // Lancee depuis le panneau, l'epreuve a ses pages de preparation et de bilan (interactives) : le Deck s'efface devant
                // elles ; lancee par la route REST, sans page, le Deck pose ses propres ecrans
                cartes: pages ? RetroBat.Api.Deck.DeckCartes.Api : RetroBat.Api.Deck.DeckCartes.Deck)),
            // Le calcul d'ES, dans un processus enfant (RetroBat.Api.exe --manettes-es) : 1,2 a 4,3 s borne au repos (13/11),
            // jusqu'a une douzaine de secondes borne chargee (12/11 a 09:19).
            () => Etape(ProgressionDuLancement.Manettes, () => RetroBat.Api.Netplay.EsManettes.PourLancer(_journal).Arguments));
        if (fantomesPoses) coeur = FantomesDuSpeedrun.CoeurDe(reglement);
        // Les verrous et le lanceur : jusqu'au retour du lanceur (le jeu demarre, ses abonnes de ui.game.started servis).
        progression?.Commencer(ProgressionDuLancement.Lanceur);
        var arguments = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur(
            manettes, reglement.SystemId, "libretro", coeur, rom, retroAchievements: false,
            // Les notifications de RetroArch coupees (11/11), avec ou sans Deck : les messages du speedrun vont au Deck ou au bandeau
            NotificationsRetroArch.Ajouter(deck?.Arguments));
        if (arguments is null)
        {
            progression?.Echouer(ProgressionDuLancement.Lanceur);
            return new(false, "bad_rules", "systeme ou coeur illisible");
        }

        // LES VERROUS (D18) : dans certified.txt avant le lanceur, le wrapper les lit au chargement du jeu. Toujours poses,
        // vides compris : un verrou reste d'un autre defi ne doit pas tenir celui-ci.
        var nomDeLaRom = Path.GetFileNameWithoutExtension(rom);
        // LE .MEM DU CHALLENGE (03/11) : la consigne de couche part avec les verrous, le wrapper charge alors ram\.defis.
        var consignes = reglement.Verrous.Select(v => v.Ligne).ToList();
        if (reglement.EstUnChallenge && RamDefinitionLayers.DefinitionDeChallenge(reglement.SystemId, reglement.RomGroup) is not null)
            consignes.Add(VerrousDuLancement.CoucheDesChallenges);
        // LES REGLES (wrapper 0.346) : le bloc du reglement sur une ligne, lu par le wrapper avec les verrous.
        if (reglement.LigneDesRegles is { } regles) consignes.Add(regles);
        VerrousDuLancement.Poser(nomDeLaRom, consignes);
        if (consignes.Count > 0)
        {
            try { RetroBat.Api.Infrastructure.CertifiedSettingsService.PoserLesVerrous(nomDeLaRom); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                VerrousDuLancement.Retirer();
                progression?.Echouer(ProgressionDuLancement.Lanceur);
                return new(false, "locks_unwritable", ex.Message);
            }
        }

        AttendreLeLancement(reglement, rom);
        _partie?.AnnoncerLancement(PartieNelfePlayService.OrigineSpeedrun);
        if (LancementDirect.Lancer(arguments, reglement.SystemId, rom, _contexte, _bus, _journal) is null)
        {
            lock (_verrou) _lancement = null;
            VerrousDuLancement.Retirer();
            progression?.Echouer(ProgressionDuLancement.Lanceur);
            return new(false, "launcher_failed");
        }

        progression?.Finir(ProgressionDuLancement.Lanceur);

        _journal.LogInformation("Speedrun : lancement de {Defi} ({Rom}, coeur {Coeur}, sans RetroAchievements).", reglement.ChallengeId, rom, coeur ?? "auto");
        if (etat is not null) PartirSurLEtat(reglement, etat, Path.GetFileNameWithoutExtension(rom));
        return new(true, null);
    }

    /// <summary>
    /// Trois preparations independantes, chacune sur son propre fil (elles bloquent : attente du budget des fantomes, rendu du
    /// Deck, processus des manettes), attendues ensemble. Une exception de l'une ressort telle quelle, comme avant, une fois les
    /// trois finies. Pur hors des fonctions passees.
    /// </summary>
    internal static (TA A, TB B, TC C) PreparerEnsemble<TA, TB, TC>(Func<TA> a, Func<TB> b, Func<TC> c)
    {
        static Task<T> Partir<T>(Func<T> f) => Task.Factory.StartNew(f, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var (ta, tb, tc) = (Partir(a), Partir(b), Partir(c));
        try { Task.WaitAll(ta, tb, tc); }
        catch (AggregateException) { /* rendue ci-dessous, la premiere dans l'ordre a, b, c */ }

        return (ta.GetAwaiter().GetResult(), tb.GetAwaiter().GetResult(), tc.GetAwaiter().GetResult());
    }

    /// <summary>Le jeu lance, son etat de depart attend : rien ne compte avant son chargement.</summary>
    internal void PartirSurLEtat(Reglement reglement, byte[] etat, string nomDuContenu)
    {
        var demande = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_verrou)
        {
            _etatAttendu = true;
            _etatPose = _etatCharge = null;
            _demandeDeChargement = demande;
        }

        _ = _bus.PublishAsync(new EventEnvelope { Type = "speedrun.etat.attendu", Payload = new { defi = reglement.ChallengeId } });
        _ = Task.Run(() => PoserLEtatAsync(reglement, etat, nomDuContenu, demande));
    }

    /// <summary>
    /// La pose de l'etat de depart, des que RetroArch joue le jeu, puis son chargement a la demande de la page de preparation.
    /// Jusque-la, le demarrage du jeu (BIOS, titre) n'arme rien : les images et les releves attendent le chargement. Le replay
    /// aussi, pour partir de l'etat charge.
    /// </summary>
    private async Task PoserLEtatAsync(Reglement reglement, byte[] etat, string nomDuContenu, TaskCompletionSource demande)
    {
        var raison = await EtatsDesDefis.PoserAsync(_commandes, etat, nomDuContenu, TimeSpan.FromSeconds(90), CancellationToken.None)
            .ConfigureAwait(false);
        if (raison is not null)
        {
            lock (_verrou)
            {
                if (!ReferenceEquals(_demandeDeChargement, demande)) return;
                _etatAttendu = false;
                _demandeDeChargement = null;
            }

            _journal.LogWarning("Speedrun : etat de depart de {Defi} non charge ({Raison}).", reglement.ChallengeId, raison);
            _ = AfficherAsync(new MessageJoueur("speedrun.msg.state_failed", "The challenge could not start: starting state not loaded."));
            return;
        }

        lock (_verrou)
        {
            if (!ReferenceEquals(_demandeDeChargement, demande)) return;
            _etatPose = DateTime.UtcNow;
        }

        await Task.WhenAny(demande.Task, Task.Delay(ChargementDeSecours)).ConfigureAwait(false);
        lock (_verrou)
        {
            // Un autre lancement depuis, ou le jeu deja ferme : cet etat ne vaut plus rien.
            if (!ReferenceEquals(_demandeDeChargement, demande) || _contexte.Ui.Running is null) return;
            _demandeDeChargement = null;
        }

        if (!demande.Task.IsCompleted) _journal.LogWarning("Speedrun : aucune demande de chargement pour {Defi}, l'etat se charge seul.", reglement.ChallengeId);
        await EtatsDesDefis.ChargerAsync(_commandes, CancellationToken.None).ConfigureAwait(false);

        // Le chargement se lit un instant apres la commande : les images d'avant ne disent plus rien du jeu.
        await Task.Delay(500).ConfigureAwait(false);
        lock (_verrou)
        {
            _image.Clear();
            _frameDeLImage = _horlogeDeLImage = null;
            _etatAttendu = false;
            _etatCharge = DateTime.UtcNow;
        }

        _journal.LogInformation("Speedrun : etat de depart {Etat} charge pour {Defi}.", reglement.Etat?.Nom, reglement.ChallengeId);
        await _bus.PublishAsync(new EventEnvelope { Type = "speedrun.etat.charge", Payload = new { defi = reglement.ChallengeId } }).ConfigureAwait(false);
    }

    /// <summary>Le prochain jeu qui demarre sur cette ROM ouvre une session de ce defi.</summary>
    internal void AttendreLeLancement(Reglement reglement, string rom)
    {
        lock (_verrou) _lancement = (reglement, rom, DateTime.UtcNow);
    }

    /// <summary>Une commande du HUD (ou de la manette) ; la relance demandee s'execute ici, la reponse dit « pending » jusque-la.</summary>
    public async Task<ReponseCommande> CommanderAsync(CommandeSpeedrun commande)
    {
        ReponseCommande reponse;
        EffetDemande effet;
        lock (_verrou)
        {
            if (_session is null) return new ReponseCommande(commande.RequestId, "rejected", "no_session", "NONE", 0);
            reponse = _session.Commander(commande, out effet);
            Publier();
        }

        _journal.LogInformation("Speedrun : commande {Action} ({Requete}) : {Statut}{Raison}.", commande.Action, commande.RequestId,
            reponse.Statut, reponse.Raison is null ? "" : " " + reponse.Raison);
        if (effet == EffetDemande.Relancer) await RelancerAsync(_sondeDeRetroArch).ConfigureAwait(false);
        return reponse;
    }

    /// <summary>Une adresse lue par le reglement, pour verifier que RetroArch ecoute avant RESET.</summary>
    private long _sondeDeRetroArch;

    /// <summary>
    /// RELANCER UN DEPART NORMAL : commande RESET de RetroArch (§10). RetroArch ne repond pas a RESET ; une
    /// lecture juste avant prouve qu'il ecoute. Sans reponse, la relance est refusee et le jeu reste ou il est.
    /// Un defi parti sur un etat (D19) recharge cet etat, toujours pose dans le dossier de RetroArch : un RESET le
    /// renverrait au demarrage du jeu (BIOS, titre, choix du soldat).
    /// </summary>
    private async Task RelancerAsync(long sonde)
    {
        string? raison = null;
        Reglement? surEtat;
        lock (_verrou) surEtat = _session?.Reglement is { Etat: not null } r ? r : null;
        try
        {
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (await _commandes.DemanderAsync($"READ_CORE_RAM {sonde:x} 1", delai.Token).ConfigureAwait(false) is null) raison = "retroarch_unreachable";
            else if (surEtat is null) await _commandes.EnvoyerAsync("RESET", delai.Token).ConfigureAwait(false);
            else
            {
                // Comme au depart : rien ne compte entre la commande et l'etat charge, le replay repart de l'etat.
                lock (_verrou) _etatAttendu = true;
                await _bus.PublishAsync(new EventEnvelope { Type = "speedrun.etat.attendu", Payload = new { defi = surEtat.ChallengeId } }).ConfigureAwait(false);
                // Le replay de la tentative s'arrete AVANT le chargement : un etat charge pendant l'enregistrement deregle
                // RetroArch (essai du 01/11, sauvegarde rechargee par Hotkey + X : un millier d'erreurs STATESTREAM par
                // image, 261 Mo de journal, plus de reponse aux commandes). L'enregistreur en ouvre un neuf sur l'etat.
                await _commandes.EnvoyerAsync("HALT_REPLAY", delai.Token).ConfigureAwait(false);
                await Task.Delay(200, delai.Token).ConfigureAwait(false);
                await EtatsDesDefis.ChargerAsync(_commandes, delai.Token).ConfigureAwait(false);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                // (05/11) le replay de la tentative s'ouvre tout de suite sur l'etat charge, avant que le joueur ait la main. A cote :
                // la session ne l'attend pas pour se rearmer (23:26, 12 s d'attente sous charge : le depart du joueur etait passe,
                // et l'entree dans la salle souterraine de Mario a ete prise pour le depart, 0:14.93).
                if (RetroBat.Api.Replay.Recording.ReplayRecorderService.Courant is { } enregistreur)
                {
                    // annoncee AVANT la tache (08/11) : sinon « speedrun.etat.charge », publie juste apres, arretait le replay neuf
                    enregistreur.AnnoncerRelance();
                    // (12/11) une exception de la tache se perdait sans trace : la relance restait sans replay, en silence
                    _ = Task.Run(async () =>
                    {
                        try { await enregistreur.OuvrirApresRelanceAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception ex) { _journal.LogWarning(ex, "Speedrun : ouverture du replay de la relance en echec."); }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            raison = "retroarch_unreachable";
            _journal.LogWarning(ex, "Speedrun : relance impossible.");
        }

        lock (_verrou)
        {
            if (surEtat is not null)
            {
                _image.Clear();
                _frameDeLImage = _horlogeDeLImage = null;
                _etatAttendu = false;
                if (raison is null) _etatCharge = DateTime.UtcNow;
            }

            if (_session is null) return;
            ViderLImageVerrouille();
            if (raison is null) _session.RelanceFaite();
            else _session.RelanceEchouee(raison);
            _cadence.Clear();
            Publier();
        }

        if (surEtat is not null && raison is null)
            await _bus.PublishAsync(new EventEnvelope { Type = "speedrun.etat.charge", Payload = new { defi = surEtat.ChallengeId } }).ConfigureAwait(false);
        _journal.LogInformation(raison is null
            ? surEtat is null ? "Speedrun : jeu replace au depart (RESET)." : "Speedrun : etat de depart recharge (relance)."
            : "Speedrun : relance refusee ({Raison}).", raison);
    }

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    Ouvrir();
                    break;
                case "ui.game.ended":
                    Fermer();
                    break;
                case "retroarch.memory.changed":
                    RecevoirSignal(e.Payload);
                    break;
                case "wrapper.watch.muted":
                    SurCoupure(e.Payload);
                    break;
                case "panel.input.pressed":
                case "panel.input.released":
                    SurBouton(e.Type == "panel.input.pressed", e.Payload);
                    break;
                case "scoring.listener.session":
                    SurBilan(e.Payload);
                    break;
                case "scoring.listener.attestation":
                    SurAttestation(e.Payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Speedrun : evenement {Type} ignore.", e.Type);
        }
    }

    private void Ouvrir()
    {
        lock (_verrou)
        {
            var lancement = _lancement;
            _lancement = null;
            if (lancement is not { } l || DateTime.UtcNow - l.At > ValiditeDuLancement) return;
            var enCours = _contexte.Ui.Running?.GamePath;
            if (enCours is { Length: > 0 } && !string.Equals(Path.GetFullPath(enCours), Path.GetFullPath(l.Rom), StringComparison.OrdinalIgnoreCase)) return;

            _session = new SessionSpeedrun(l.Reglement.ChallengeId, l.Reglement) { MeilleurTemps = MeilleurTempsDuJournal(l.Reglement.ChallengeId) };
            _etatJournalise = null;
            _signauxRecus = _signauxAvantLEtat = _signauxArmesJournalises = 0;
            _derniersReleves.Clear();
            _sessionDuLancement = _session;
            _sondeDeRetroArch = l.Reglement.OffsetDansLeCoeur(l.Reglement.AdressesCitees().FirstOrDefault());
            _image.Clear();
            _cadence.Clear();
            _tenus.Clear();
            _frameDeLImage = _horlogeDeLImage = null;
            _premiereImage = null;
            _etatCharge = null;
            _pausesLevees = 0;
            _pauseDepuis = null;
            _relanceApresLaPause = false;
            _releves?.Cancel();
            _releves = new CancellationTokenSource();
            _ = ReleverEnBoucleAsync(_session, _releves.Token);
            if (_priorites is { } priorites)
            {
                var jeton = _releves.Token;
                _ = Task.Run(() => priorites.DonnerLaMainAuJeuAsync(jeton), CancellationToken.None);
            }
            _journal.LogInformation("Speedrun : session {Session} ouverte sur {Defi}.", _session.SessionId, _session.DefiId);
            Publier();
        }
    }

    private void Fermer()
    {
        lock (_verrou)
        {
            if (_session is null) return;
            ViderLImageVerrouille();
            _session.JeuFerme();
            Publier();
            _releves?.Cancel();
            _journal.LogInformation("Speedrun : session {Session} close (jeu ferme), {Recus} signal(aux) du wrapper recu(s) apres l'etat de depart ({Avant} avant), derniers releves : {Releves}.",
                _session.SessionId, _signauxRecus, _signauxAvantLEtat,
                _derniersReleves.Count == 0 ? "aucun" : string.Join(", ", _derniersReleves.Select(r => Hex.Ecrire(r.Key) + "=" + Hex.Ecrire(r.Value))));
            _ = _bus.PublishAsync(new EventEnvelope { Type = "speedrun.session.closed", Payload = new { sessionId = _session.SessionId } });
            _session = null;
            FantomesDuSpeedrun.ViderLeDossier();
        }

        _priorites?.RendreLaMain();
    }

    private void RecevoirSignal(object? payload)
    {
        lock (_verrou)
        {
            if (_session is null) return;
        }

        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (!racine.TryGetProperty("signal", out var s) || s.ValueKind != JsonValueKind.Object) return;
        if (!s.TryGetProperty("Frame", out var f) || f.ValueKind != JsonValueKind.Number) return;
        if (!s.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Number) return;
        var nom = s.TryGetProperty("Name", out var n) ? n.GetString() : null;
        var adresse = s.TryGetProperty("Address", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(nom) || string.IsNullOrEmpty(adresse)) return;
        var horloge = HorlogeDeLaLigne(s.TryGetProperty("RawLine", out var r) ? r.GetString() : null, DateTimeOffset.Now);
        var signal = Signal.Depuis(f.GetInt64(), nom, adresse, v.GetInt32(), horloge);

        lock (_verrou)
        {
            // Le demarrage du jeu avant l'etat de depart ne dit rien de l'epreuve.
            if (_session is null) return;
            if (_etatAttendu)
            {
                _signauxAvantLEtat++;
                return;
            }
            _signauxRecus++;
            if (_frameDeLImage is { } frame && frame != signal.Frame) ViderLImageVerrouille();
            _frameDeLImage = signal.Frame;
            _horlogeDeLImage ??= horloge;
            _image.Add(signal);
            _vidage?.Change(AttenteDeLImageMs, Timeout.Infinite);
        }
    }

    private void ViderLImage()
    {
        lock (_verrou) ViderLImageVerrouille();
    }

    private void ViderLImageVerrouille()
    {
        if (_image.Count == 0 || _session is null) { _image.Clear(); return; }
        var image = _image.ToList();
        var horloge = _horlogeDeLImage;
        _image.Clear();
        _frameDeLImage = _horlogeDeLImage = null;
        // LE DEPART QUI NE VIENT PAS (essai du user du 03/11, Green Hill 2 : ARMED puis IDLE 47 s plus tard, jamais RUNNING ;
        // au banc, le meme defi part). Tant que l'epreuve est armee, chaque signal recu s'ecrit, 60 au plus par session.
        if (_session.Etat == SessionSpeedrun.Armed && _signauxArmesJournalises < 60)
        {
            foreach (var sig in image.Take(60 - _signauxArmesJournalises))
                _journal.LogInformation("Speedrun : arme, image {Frame} : {Action} {Adresse} = {Valeur}.", sig.Frame, sig.Action, Hex.Ecrire(sig.Adresse), Hex.Ecrire(sig.Valeur));
            _signauxArmesJournalises += Math.Min(image.Count, 60 - _signauxArmesJournalises);
        }
        _session.RecevoirImage(image, horloge);
        _premiereImage ??= DateTime.UtcNow;
        GuetterLAvanceRapide(image[0].Frame, horloge);
        Publier();
    }

    /// <summary>
    /// L'AVANCE RAPIDE : les frames filent plus vite que l'horloge. Jugee sur une fenetre d'au moins une seconde
    /// et demie, jamais d'un evenement a l'autre : ils sont espaces d'un quart de seconde en avance rapide.
    /// Un PC qui rame ralentit les frames, il ne les accelere jamais : aucun faux positif de ce cote.
    /// </summary>
    private void GuetterLAvanceRapide(long frame, long? horloge)
    {
        if (_session is null || horloge is not { } h || _session.Etat != SessionSpeedrun.Running) { _cadence.Clear(); return; }
        _cadence.Enqueue((frame, h));
        while (_cadence.Count > 2 && h - _cadence.Peek().HorlogeMs > 3 * FenetreDAvanceRapideMs) _cadence.Dequeue();
        var (f0, h0) = _cadence.Peek();
        var duree = h - h0;
        if (duree < FenetreDAvanceRapideMs) return;
        var tempsDesFrames = (frame - f0) * 1000.0 / _session.Reglement.Cadence;
        if (tempsDesFrames / duree > RapportDAvanceRapide) _session.Controle("fast_forward", frame);
    }

    /// <summary>Une ligne coupee par l'auto-mute du wrapper, nommee par son action : si le reglement la cite, plus rien n'est mesurable.</summary>
    private void SurCoupure(object? payload)
    {
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (!racine.TryGetProperty("Evenement", out var e) || e.GetString() is not { Length: > 0 } action) return;
        lock (_verrou)
        {
            if (_session is null) return;
            foreach (var adresse in _session.Reglement.AdressesDeLAction(action)) _session.CapteurCoupe(adresse);
            Publier();
        }
    }

    /// <summary>
    /// LES COMBINAISONS DE LA MANETTE (D29), lues par le lecteur d'entrees de l'API sur la manette du joueur 1 :
    /// Hotkey + gauche relance (aussitot avant le depart, maintenu une seconde pendant un run) ; Hotkey + Start quitte
    /// le jeu dans RetroArch, la tentative en cours est close comme abandonnee avant que le jeu ne se ferme.
    /// </summary>
    private void SurBouton(bool appui, object? payload)
    {
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (racine.TryGetProperty("Player", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() != 1) return;
        var identite = racine.TryGetProperty("Identity", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(identite)) return;

        string? action = null;
        bool enCourse;
        string sessionId;
        lock (_verrou)
        {
            if (_session is null) return;
            if (!appui)
            {
                _tenus.Remove(identite);
                _maintien?.Cancel();
                return;
            }

            _tenus.Add(identite);
            if (!_tenus.Contains(Hotkey)) return;
            if (identite.Equals(BoutonDeRelance, StringComparison.OrdinalIgnoreCase)) action = SessionSpeedrun.ActionRelancer;
            else if (identite.Equals("start", StringComparison.OrdinalIgnoreCase)) action = SessionSpeedrun.ActionAbandonner;
            enCourse = _session.Etat == SessionSpeedrun.Running;
            sessionId = _session.SessionId;
        }

        if (action is null) return;
        if (action == SessionSpeedrun.ActionAbandonner)
        {
            if (enCourse) _ = CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, action, Confirme: true));
            return;
        }

        if (!enCourse)
        {
            _ = CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, action));
            return;
        }

        CancellationTokenSource maintien;
        lock (_verrou)
        {
            _maintien?.Cancel();
            _maintien = maintien = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(MaintienDeLaRelance, maintien.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            bool tenu;
            lock (_verrou) tenu = _tenus.Contains(Hotkey) && _tenus.Contains(BoutonDeRelance);
            if (tenu) await CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, SessionSpeedrun.ActionRelancer, Confirme: true)).ConfigureAwait(false);
        });
    }

    private string IdDeLaManette() => "pad-" + Interlocked.Increment(ref _requetesDeLaManette).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// LE BILAN DU WRAPPER, a la fin de la partie : chargements d'etat, retour arriere, avance rapide, triche sur
    /// toute la seance. Il ne dit pas QUAND : il s'ecrit au journal a cote des tentatives de la session, pour le
    /// passeport (S4). Sur Sonic, un chargement en pleine course se voit deja au controle du temps du jeu.
    /// </summary>
    private void SurBilan(object? payload)
    {
        string? sessionId;
        lock (_verrou) sessionId = _session?.SessionId ?? _derniereSession;
        if (sessionId is null) return;
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        Ecrire(new JsonObject
        {
            ["kind"] = "wrapper_summary",
            ["sessionId"] = sessionId,
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["summary"] = JsonNode.Parse(racine.GetRawText()),
        });
    }

    private string? _derniereSession;

    /// <summary>
    /// LES VERROUS A L'ATTESTATION (D18), des le chargement du jeu : si le wrapper n'a pas lu ceux du reglement (wrapper
    /// d'avant 0.343, fichier perdu), le joueur le sait avant de jouer. Le defi reste jouable : son filet (l'echec au tir de
    /// Metal Slug 3) tient, mais aucun temps de ce lancement ne partira (bilan, <see cref="PasseportDeDefi.EcartDesVerrous"/>).
    /// </summary>
    private void SurAttestation(object? payload)
    {
        Reglement? reglement;
        lock (_verrou) reglement = _session?.Reglement ?? _lancement?.Reglement;
        if (reglement is null) return;
        var racine = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        var lus = racine.TryGetProperty("Locks", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
        // Les regles (wrapper 0.346) : la meme empreinte, et un bloc qui compile. Un wrapper plus ancien n'en dit rien.
        var regles = racine.TryGetProperty("RulesSha256", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() ?? "" : "";
        var reglesValides = racine.TryGetProperty("RulesValid", out var rv) && rv.ValueKind == JsonValueKind.True;
        if (!string.Equals(regles, reglement.ReglesSha256, StringComparison.Ordinal) || (reglement.Regles is not null && !reglesValides))
        {
            _journal.LogWarning("Speedrun : regles lues par le wrapper ({Lues}, valides {Valides}) au lieu de {Attendues} pour {Defi}.",
                regles, reglesValides, reglement.ReglesSha256, reglement.ChallengeId);
            _ = AfficherAsync(new MessageJoueur("speedrun.msg.locks_missing", "Challenge locks not active: no time will be sent."));
            return;
        }
        if (string.Equals(lus, reglement.LignesDesVerrous, StringComparison.Ordinal)) return;
        _journal.LogWarning("Speedrun : verrous lus par le wrapper « {Lus} » au lieu de « {Attendus} » pour {Defi}.", lus, reglement.LignesDesVerrous, reglement.ChallengeId);
        _ = AfficherAsync(new MessageJoueur("speedrun.msg.locks_missing", "Challenge locks not active: no time will be sent."));
    }

    /// <summary>
    /// RELEVER CE QUE LE WRAPPER NE DIT PAS ENCORE : quatre fois par seconde, les adresses inconnues de la tentative
    /// courante et de celle qui attend, par READ_CORE_RAM. Une adresse sans reponse reste inconnue, jamais 0.
    /// </summary>
    private async Task ReleverEnBoucleAsync(SessionSpeedrun session, CancellationToken ct)
    {
        var silencieux = false;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            IReadOnlyList<long> adresses;
            bool etatAttendu;
            lock (_verrou)
            {
                if (!ReferenceEquals(_session, session)) return;
                // Un releve d'avant l'etat de depart resterait pour toujours : un releve ne remplace jamais une valeur connue.
                adresses = _etatAttendu ? [] : session.AdressesInconnues;
                etatAttendu = _etatAttendu;
            }

            if (!etatAttendu) await LeverLaPauseAsync(session, ct).ConfigureAwait(false);

            foreach (var adresse in adresses)
            {
                // L'offset dans le bloc que le coeur expose : sous FBNeo, ni l'adresse du .MEM ni le meme ordre d'octets.
                var reponse = await _commandes.DemanderAsync($"READ_CORE_RAM {session.Reglement.OffsetDansLeCoeur(adresse):x} 1", ct).ConfigureAwait(false);
                var morceaux = reponse?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (morceaux is not { Length: >= 3 } || morceaux[0] != "READ_CORE_RAM"
                    || !int.TryParse(morceaux[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var valeur))
                {
                    if (!silencieux && reponse is not null)
                    {
                        silencieux = true;
                        _journal.LogWarning("Speedrun : READ_CORE_RAM {Adresse} refuse ({Reponse}) : le depart ne peut pas s'armer.", Hex.Ecrire(adresse), reponse.Trim());
                    }

                    continue;
                }

                lock (_verrou)
                {
                    if (!ReferenceEquals(_session, session)) return;
                    _derniersReleves[adresse] = valeur;
                    session.Relever(adresse, valeur);
                    Publier();
                }
            }
        }
    }

    /// <summary>
    /// PAS DE PAUSE PENDANT UN DEFI. RetroArch ne sait l'interdire qu'en mode hardcore de RetroAchievements, qui
    /// interdit aussi de charger l'etat de depart (D19) : la session la leve elle-meme. La touche pause et l'image par
    /// image mettent RetroArch en pause (GET_STATUS PAUSED) ; la pause est levee au releve suivant, un quart de seconde
    /// au plus apres. Pendant toute la session, entre deux tentatives aussi (essai du 01/11 : la pause apres un echec
    /// surprenait), sauf quand l'etat de depart se charge. Le menu ne se voit pas ainsi (RetroArch y repond PLAYING) :
    /// l'ecart horloge/frames le juge pendant une course.
    ///
    /// Un message le dit au joueur a chaque pause (demande du user, 01/11). LA PAUSE TENUE : PAUSE_TOGGLE est un appui
    /// virtuel sur la touche pause ; tant que le joueur la tient, RetroArch ne voit pas de nouvel appui et reste en pause.
    /// Tenue une seconde, elle vaut Hotkey + gauche (demande du user) : le joueur est prevenu, et la relance part des que
    /// la pause est levee, la touche lachee. Pas avant : rien ne garantit qu'un etat se charge pendant une pause.
    /// </summary>
    private async Task LeverLaPauseAsync(SessionSpeedrun session, CancellationToken ct)
    {
        var statut = await _commandes.DemanderAsync("GET_STATUS", ct).ConfigureAwait(false);
        if (statut is null) return;
        var enPause = statut.StartsWith("GET_STATUS PAUSED", StringComparison.Ordinal);
        bool premiere = false, tenue = false, relancer = false;
        string sessionId;
        lock (_verrou)
        {
            if (!ReferenceEquals(_session, session) || _etatAttendu) return;
            sessionId = session.SessionId;
            if (!enPause)
            {
                _pauseDepuis = null;
                relancer = _relanceApresLaPause;
                _relanceApresLaPause = false;
            }
            else
            {
                premiere = _pauseDepuis is null;
                _pauseDepuis ??= DateTime.UtcNow;
                if (!_relanceApresLaPause && DateTime.UtcNow - _pauseDepuis.Value >= PauseTenue) tenue = _relanceApresLaPause = true;
            }
        }

        if (relancer)
        {
            _journal.LogInformation("Speedrun : pause tenue, le joueur repart du depart.");
            await CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, SessionSpeedrun.ActionRelancer, Confirme: true)).ConfigureAwait(false);
            return;
        }

        if (!enPause) return;
        await _commandes.EnvoyerAsync("PAUSE_TOGGLE", ct).ConfigureAwait(false);
        if (premiere)
        {
            _ = AfficherAsync(new MessageJoueur("speedrun.msg.no_pause", "No pause during a challenge."));
            var levees = Interlocked.Increment(ref _pausesLevees);
            if (levees == 1 || levees % 20 == 0)
                _journal.LogInformation("Speedrun : pause levee pendant le defi ({Levees} depuis l'ouverture de la session).", levees);
        }

        if (tenue) _ = AfficherAsync(new MessageJoueur("speedrun.msg.pause_held", "Pause held: back to the start."));
    }

    /// <summary>Une pause tenue aussi longtemps vaut Hotkey + gauche, comme la relance pendant un run.</summary>
    private static readonly TimeSpan PauseTenue = TimeSpan.FromSeconds(1);

    private int _pausesLevees;
    private DateTime? _pauseDepuis;
    private bool _relanceApresLaPause;

    /// <summary>Publie les evenements de la session, puis son snapshot s'il a change. Sous le verrou.</summary>
    private void Publier()
    {
        if (_session is null) return;
        var evenements = _session.PrendreLesEvenements();
        foreach (var e in evenements)
        {
            var donnees = JsonSerializer.SerializeToNode(e.Donnees, Json) as JsonObject ?? new JsonObject();
            donnees["sequence"] = e.Sequence;
            _ = _bus.PublishAsync(new EventEnvelope { Type = e.Type, Payload = donnees });
        }

        foreach (var (attemptId, resultat) in _session.PrendreLesCloses()) EcrireTentative(_session, attemptId, resultat);
        foreach (var message in _session.PrendreLesMessages()) _ = AfficherAsync(message);
        if (_session.PrendreLaRelanceAutomatique() is { } delai)
        {
            // Le joueur voit sa mort ou son temps, puis l'etat de depart revient et le chrono repart de 0. Une arrivee qui bat
            // son temps arrete l'enregistrement plus tot : son replay devient le fantome de ce depart.
            var sessionId = _session.SessionId;
            var meilleur = _session.Etat == SessionSpeedrun.Finished && _session.DerniereArrivee is { } arrivee
                && _fantomes?.NouveauTemps(arrivee.Images, arrivee.Independant) == true;
            _ = Task.Run(async () =>
            {
                if (meilleur)
                {
                    await Task.Delay(ArretDuReplayApresLArrivee).ConfigureAwait(false);
                    try { await _commandes.EnvoyerAsync("HALT_REPLAY", CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
                    {
                        _journal.LogDebug(ex, "Speedrun : arret du replay impossible.");
                    }

                    await _fantomes!.AttendreLeFantomeAsync(AttenteDuFantome).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(delai).ConfigureAwait(false);
                }

                await CommanderAsync(new CommandeSpeedrun(IdDeLaManette(), sessionId, SessionSpeedrun.ActionRelancer, Confirme: true)).ConfigureAwait(false);
            });
        }
        if (_session.Etat != _etatJournalise)
        {
            _journal.LogInformation("Speedrun : session {Session} {Avant} -> {Apres}.", _session.SessionId, _etatJournalise ?? "-", _session.Etat);
            _etatJournalise = _session.Etat;
        }
        _derniereSession = _session.SessionId;
        if (evenements.Count > 0 || _snapshotPublie != _session.Sequence)
        {
            _snapshotPublie = _session.Sequence;
            var instantane = _session.Instantane();
            if (_fantomes?.Publies(_session.Reglement.Cadence) is { } fantomes) instantane = instantane with { Fantomes = fantomes };
            _ = _bus.PublishAsync(new EventEnvelope { Type = "speedrun.session", Payload = JsonSerializer.SerializeToNode(instantane, Json) });
        }
    }

    private long _snapshotPublie = -1;

    private System.Threading.Timer? _ecarts;
    private static readonly TimeSpan PeriodeDesEcarts = TimeSpan.FromMilliseconds(250);
    private int _ecartsEnCours;

    /// <summary>
    /// (11/11, lot L5 du CDC Player Deck Patterns) Pendant un run a fantomes, chaque segment que le joueur atteint donne un
    /// « speedrun.ghost.gap » par fantome passe par ce segment (ecart negatif : en avance). Le Deck l'affiche segment par segment.
    /// </summary>
    private void PublierLesEcarts()
    {
        if (Interlocked.Exchange(ref _ecartsEnCours, 1) == 1) return;
        try
        {
            double cadence;
            string? sessionId;
            lock (_verrou)
            {
                if (_session is null || _session.Etat != SessionSpeedrun.Running) return;
                cadence = _session.Reglement.Cadence;
                sessionId = _session.SessionId;
            }
            foreach (var e in _fantomes!.LireLesEcarts(cadence))
            {
                var donnees = JsonSerializer.SerializeToNode(e, Json) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                donnees["sessionId"] = sessionId;
                _ = _bus.PublishAsync(new EventEnvelope { Type = "speedrun.ghost.gap", Payload = donnees });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _journal.LogDebug(ex, "Speedrun : ecarts aux fantomes illisibles.");
        }
        finally
        {
            Interlocked.Exchange(ref _ecartsEnCours, 0);
        }
    }

    private readonly FantomesDuSpeedrun? _fantomes;
    private readonly RetroBat.Api.Deck.DeckLaunchService? _deck;

    private static readonly TimeSpan ArretDuReplayApresLArrivee = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AttenteDuFantome = TimeSpan.FromSeconds(3);


    // LE DIAGNOSTIC D'UNE SESSION (essai du user du 02/11, 22:56 : neuf minutes sans aucune tentative, sans rien pour dire
    // pourquoi). Chaque changement d'etat se journalise, et la fermeture dit ce que le wrapper a envoye et ce que les releves
    // ont lu en dernier.
    private string? _etatJournalise;
    private int _signauxRecus, _signauxAvantLEtat, _signauxArmesJournalises;
    private readonly Dictionary<long, int> _derniersReleves = new();

    /// <summary>
    /// Le meilleur temps du lancement, une seule fois, meme apres la fermeture de la session : le bilan du wrapper
    /// peut arriver avant ou apres la fin du jeu vue par ES.
    /// </summary>
    public (Reglement Reglement, TentativeFinie Tentative)? PrendreLeMeilleurTemps()
    {
        lock (_verrou)
        {
            return _sessionDuLancement is { } s && s.PrendreLaMeilleure() is { } t ? (s.Reglement, t) : null;
        }
    }

    /// <summary>Un message au joueur dans RetroArch, dans la langue d'ES. Tant que le SpeedrunHUD n'est pas la, c'est son seul retour a l'ecran.</summary>
    private async Task AfficherAsync(MessageJoueur message)
    {
        // LE DECK POSE (08/11) : le message va a sa plaque, en anglais court ; un coup recu ne se dit pas (la couche epreuve
        // le montre deja), RetroArch, muet pour ce lancement, ne le montrerait de toute facon pas.
        if (RetroBat.Api.Deck.DeckSilence.Courante is { AuDeck: true } silence)
        {
            silence.Dire(RetroBat.Api.Deck.DeckNotices.DuSpeedrun(message), true);
            return;
        }

        try
        {
            var texte = TexteDe(message);
            foreach (var (nom, valeur) in message.Valeurs) texte = texte.Replace("{" + nom + "}", valeur, StringComparison.Ordinal);
            // RETROARCH MUET SANS DECK (11/11) : le bandeau du haut de l'API dit le message, RetroArch ne le montrerait pas
            SessionSpeedrun? session;
            lock (_verrou) session = _sessionDuLancement;
            if (NotificationsRetroArch.AuBandeau(session?.Reglement.EstUnChallenge == true ? "CHALLENGE" : "SPEEDRUN", texte)) return;
            await _commandes.EnvoyerAsync("SHOW_MSG " + texte.ReplaceLineEndings(" ").Trim(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _journal.LogDebug(ex, "Speedrun : message {Cle} non affiche.", message.Cle);
        }
    }

    private string TexteDe(MessageJoueur message)
    {
        var texte = _textes?.Text(message.Cle, _reglages?.GetScrapingSettings().Language);
        return string.IsNullOrEmpty(texte) || texte == message.Cle ? message.ParDefaut : texte;
    }

    private void EcrireTentative(SessionSpeedrun session, string attemptId, ResultatTentative r)
    {
        var ligne = new JsonObject
        {
            ["kind"] = "attempt",
            ["sessionId"] = session.SessionId,
            ["attemptId"] = attemptId,
            ["challengeId"] = session.DefiId,
            ["rulesVersion"] = session.Reglement.Version,
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["state"] = SessionSpeedrun.EtatHud(r.Etat),
            ["cause"] = r.Cause,
            ["officialFrames"] = r.FramesOfficielles,
            ["official"] = r.FramesOfficielles is { } f ? Chronometrage.Afficher(f, r.Cadence) : null,
            ["aligned"] = Mesure(r.Cale),
            ["independent"] = Mesure(r.Independant),
            ["gameTimeSeconds"] = r.TempsDuJeuSecondes,
            ["gameTimeCheck"] = r.ControleTempsDuJeu,
            ["rate"] = r.Cadence,
            ["events"] = new JsonArray(r.Journal.Select(c => (JsonNode)new JsonObject
            {
                ["role"] = c.Role, ["action"] = c.Action, ["address"] = Hex.Ecrire(c.Adresse), ["value"] = c.Valeur, ["frame"] = c.Frame,
            }).ToArray()),
        };
        Ecrire(ligne);
        _journal.LogInformation("Speedrun : tentative {Tentative} {Etat}{Cause}{Temps}.", attemptId, SessionSpeedrun.EtatHud(r.Etat),
            r.Cause is null ? "" : " (" + r.Cause + ")", r.FramesOfficielles is { } t ? " en " + Chronometrage.Afficher(t, r.Cadence) : "");

        static JsonNode? Mesure(MesureChrono? m) => m is null ? null : new JsonObject { ["start"] = m.Depart, ["end"] = m.Arrivee, ["frames"] = m.Frames };
    }

    private void Ecrire(JsonObject ligne)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fichierDesTentatives)!);
            File.AppendAllText(_fichierDesTentatives, ligne.ToJsonString() + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "Speedrun : journal des tentatives non ecrit.");
        }
    }

    /// <summary>Le meilleur temps fini de ce defi dans le journal local, en frames.</summary>
    /// <summary>
    /// Le bilan du journal local par defi : tentatives parties et meilleur temps classable. Le panneau s'en sert pour dire
    /// qu'une epreuve a deja ete tentee ou reussie sur cette borne (§7.7 du CDC du panneau).
    /// </summary>
    public IReadOnlyDictionary<string, (int Tentatives, long? Meilleur)> BilanDuJournal()
    {
        var bilan = new Dictionary<string, (int Tentatives, long? Meilleur)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_fichierDesTentatives)) return bilan;
            foreach (var ligne in File.ReadLines(_fichierDesTentatives))
            {
                if (ligne.Length == 0) continue;
                using var doc = JsonDocument.Parse(ligne);
                var r = doc.RootElement;
                if (!r.TryGetProperty("kind", out var k) || k.GetString() != "attempt" || !r.TryGetProperty("challengeId", out var c)) continue;
                var id = c.GetString() ?? "";
                var (tentatives, meilleur) = bilan.GetValueOrDefault(id);
                if (r.TryGetProperty("state", out var s) && s.GetString() == SessionSpeedrun.Finished
                    && r.TryGetProperty("officialFrames", out var f) && f.ValueKind == JsonValueKind.Number
                    && !(r.TryGetProperty("gameTimeCheck", out var g) && g.GetString() == "ecart")
                    && (meilleur is not { } m || f.GetInt64() < m))
                    meilleur = f.GetInt64();
                bilan[id] = (tentatives + 1, meilleur);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return bilan;
    }

    /// <summary>Le bilan d'une session du journal local, pour la page de fin ; null si elle n'y a rien ecrit.</summary>
    public BilanDeSession? BilanDeLaSession(string sessionId)
    {
        try
        {
            return File.Exists(_fichierDesTentatives) ? LireLeBilan(File.ReadLines(_fichierDesTentatives), sessionId) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Les tentatives d'une session, celles allees jusqu'a l'arrivee, le meilleur temps classable (le temps du jeu ne le
    /// contredit pas) et la fin de la derniere. Sans disque : ce qui se teste.
    /// </summary>
    internal static BilanDeSession? LireLeBilan(IEnumerable<string> lignes, string sessionId)
    {
        string? defi = null, dernierEtat = null, derniereCause = null;
        var tentatives = 0;
        var finies = 0;
        long? meilleur = null;
        foreach (var ligne in lignes)
        {
            if (ligne.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(ligne); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (!r.TryGetProperty("kind", out var k) || k.GetString() != "attempt"
                    || !r.TryGetProperty("sessionId", out var s) || s.GetString() != sessionId) continue;
                defi ??= r.TryGetProperty("challengeId", out var c) ? c.GetString() : null;
                tentatives++;
                dernierEtat = r.TryGetProperty("state", out var e) ? e.GetString() : null;
                derniereCause = r.TryGetProperty("cause", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null;
                if (dernierEtat != SessionSpeedrun.Finished || !r.TryGetProperty("officialFrames", out var f) || f.ValueKind != JsonValueKind.Number) continue;
                finies++;
                if (r.TryGetProperty("gameTimeCheck", out var g) && g.GetString() == "ecart") continue;
                if (meilleur is not { } m || f.GetInt64() < m) meilleur = f.GetInt64();
            }
        }

        return defi is null ? null : new BilanDeSession(sessionId, defi, tentatives, finies, meilleur, dernierEtat, derniereCause);
    }

    internal long? MeilleurTempsDuJournal(string defiId)
    {
        try
        {
            if (!File.Exists(_fichierDesTentatives)) return null;
            long? meilleur = null;
            foreach (var ligne in File.ReadLines(_fichierDesTentatives))
            {
                if (ligne.Length == 0) continue;
                using var doc = JsonDocument.Parse(ligne);
                var r = doc.RootElement;
                if (r.TryGetProperty("kind", out var k) && k.GetString() == "attempt"
                    && r.TryGetProperty("challengeId", out var c) && string.Equals(c.GetString(), defiId, StringComparison.OrdinalIgnoreCase)
                    && r.TryGetProperty("state", out var s) && s.GetString() == SessionSpeedrun.Finished
                    && r.TryGetProperty("officialFrames", out var f) && f.ValueKind == JsonValueKind.Number
                    && !(r.TryGetProperty("gameTimeCheck", out var g) && g.GetString() == "ecart")
                    && (meilleur is not { } m || f.GetInt64() < m))
                    meilleur = f.GetInt64();
            }

            return meilleur;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// L'HORLOGE D'EMISSION d'une ligne du wrapper, « [hh:mm:ss.fff] » en heure locale, en millisecondes Unix. Une
    /// heure plus tard que maintenant est celle de la veille (minuit passe entre l'emission et la lecture). Sans
    /// horodatage lisible : l'heure de reception.
    /// </summary>
    internal static long HorlogeDeLaLigne(string? ligne, DateTimeOffset maintenant)
    {
        var m = ligne is null ? null : HorlogeDuWrapper.Match(ligne);
        if (m is not { Success: true }) return maintenant.ToUnixTimeMilliseconds();
        var heure = new TimeSpan(0, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
        var emission = new DateTimeOffset(maintenant.Date + heure, maintenant.Offset);
        if (emission - maintenant > TimeSpan.FromHours(1)) emission = emission.AddDays(-1);
        return emission.ToUnixTimeMilliseconds();
    }
}
