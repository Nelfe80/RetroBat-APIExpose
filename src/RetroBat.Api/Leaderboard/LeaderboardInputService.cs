using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Ce qui ouvre le panneau de classement et le pilote : les boutons de la borne.
///
/// L'appui LONG sur « valider » ouvre le menu de jeu d'EmulationStation ; on l'observe en
/// meme temps que lui, et on affiche notre panneau a cote. ES ne sait rien de nous, et nous ne
/// lui prenons rien : le canal panel lit la manette en direct, sans dependre du focus.
///
/// LES BOUTONS SE DESIGNENT PAR LEUR SLOT, jamais par une lettre. Le slot vient de la
/// cartographie mesuree par le wizard LedManagerSetup sur CETTE borne, et c'est aussi celui
/// qu'eclairent les LED : sur la borne de reference, le bouton « valider » remonte sous
/// l'identite `b` et le slot 1. Coder « A » en dur donnerait un panneau qui s'ouvre sur le
/// mauvais bouton d'une borne a l'autre.
///
/// Le panneau ne s'ouvre QUE dans le menu (`state == browsing`), jamais en jeu ni pendant une
/// lecture de replay : devant un score en cours, rien ne doit passer devant l'ecran.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LeaderboardInputService : IHostedService, IDisposable
{
    /// <summary>Les slots de la cartographie de la borne, pas des lettres de manette.</summary>
    private const int SlotValider = 1;
    private const int SlotAnnuler = 2;
    private const int SlotAgir = 3;
    private const int SlotPageHaut = 5;
    private const int SlotPageBas = 6;

    private readonly IEventBus _bus;
    private readonly ApiContext _context;
    private readonly LeaderboardOverlayService _overlay;
    private readonly LeaderboardClient _client;
    private readonly NelfePlayAgentService _agent;
    private readonly NelfePlayScoringSessionService _session;
    private readonly Replay.Playback.ReplayPlaybackService _playback;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly InterfaceTextService _textes;
    private readonly EmulationStationSettingsService _reglages;
    private readonly NelfePlayDeviceStore _machine;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Replay.Sharing.ReplayViewerSession _viewer;
    private readonly LeaderboardSocialClient _social;
    private readonly RetroBat.Api.Netplay.NetplayGuestService _invite;
    private readonly RetroBat.Api.Netplay.NetplayHostService _hote;
    private readonly ChallengeHudService _defi;
    private readonly Replay.Storage.ReplayStore _replays;
    private readonly LeaderboardRankHistory _historique;
    /// <summary>Les rangs de la consultation PRECEDENTE : fixes pendant toute l'ouverture du panneau.</summary>
    private IReadOnlyDictionary<string, int> _rangsPrecedents = new Dictionary<string, int>();
    private IReadOnlySet<long> _replaysEnPreparation = new HashSet<long>();
    private readonly EsControllerService _es;
    private bool _seanceArmeeParLeDefi;
    private bool _directAnnonceParLeDefi;
    private readonly IEmulationStationNotificationService _notifications;
    private readonly RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider _wrapper;
    private string _replayLance = "";
    private readonly ILogger<LeaderboardInputService> _logger;

    private readonly LeaderboardPanelModel _modele = new();
    private readonly object _gate = new();
    private IDisposable? _abonnement;
    private CancellationTokenSource? _arret;
    private DateTime? _validerDepuis;
    private DateTime? _appuiDebut;

    /// <summary>La duree d'appui qui ouvre le menu de jeu d'ES (HOLD_TIME, MultiStateInput.cpp).</summary>
    private const int DureeDuMenuEs = 1000;

    /// <summary>Le menu de jeu d'ES est-il ouvert a cote du panneau ?</summary>
    private bool _menuEsOuvert;
    private bool _menuEsConnu;
    private System.Diagnostics.Stopwatch? _depuisLAppui;
    private IReadOnlyList<LeaderboardClient.Ligne> _monde = Array.Empty<LeaderboardClient.Ligne>();
    private string _etatDuMonde = LeaderboardClient.EtatAucunScore;

    /// <summary>La regle des onglets ordinaires (« 1cc », sinon la premiere ouverte) ; vide : toutes.</summary>
    private string _reglePrincipale = "";

    /// <summary>Les parties de cette borne (passeports gardes), pour MES RECORDS.</summary>
    private readonly LocalPlaysIndex _partiesLocales = new();

    /// <summary>Les lignes de MES RECORDS : les parties du joueur courant sur ce jeu, la meilleure en haut.</summary>
    private IReadOnlyList<LeaderboardClient.Ligne> _mesParties = Array.Empty<LeaderboardClient.Ligne>();

    /// <summary>Les onglets des AUTRES regles du jeu (1CC MULTI, 1LC) et la regle de chacun.</summary>
    private IReadOnlyList<(LeaderboardPanelModel.Vue Vue, string Regle)> _ongletsDeRegle = Array.Empty<(LeaderboardPanelModel.Vue, string)>();

    /// <summary>Le classement mondial de chacune de ces regles, et les rangs de la consultation d'avant.</summary>
    private Dictionary<LeaderboardPanelModel.Vue, (IReadOnlyList<LeaderboardClient.Ligne> Lignes, string Etat)> _autresMondes = new();
    private Dictionary<LeaderboardPanelModel.Vue, IReadOnlyDictionary<string, int>> _rangsDesAutres = new();
    private string _jeuAffiche = "";
    private string _systemeAffiche = "";
    private string _romGroup = "";
    private string _maVille = "";
    private string _monPays = "";

    /// <summary>
    /// MA VILLE et MON PAYS ne s'affichent pas pour le moment (decision user 2026-10-03). Tout
    /// reste en place (PoserLesLieux, filtres) : les remettre, c'est passer ce reglage a vrai.
    /// </summary>
    private static readonly bool LieuxAffiches = false;
    private string _maSalle = "";

    public LeaderboardInputService(
        IEventBus bus,
        ApiContext context,
        LeaderboardOverlayService overlay,
        LeaderboardClient client,
        NelfePlayAgentService agent,
        NelfePlayScoringSessionService session,
        Replay.Playback.ReplayPlaybackService playback,
        IOptionsMonitor<ApiExposeOptions> options,
        InterfaceTextService textes,
        EmulationStationSettingsService reglages,
        NelfePlayDeviceStore machine,
        IHttpClientFactory httpFactory,
        Replay.Sharing.ReplayViewerSession viewer,
        LeaderboardSocialClient social,
        RetroBat.Api.Netplay.NetplayGuestService invite,
        RetroBat.Api.Netplay.NetplayHostService hote,
        ChallengeHudService defi,
        Replay.Storage.ReplayStore replays,
        LeaderboardRankHistory historique,
        EsControllerService es,
        IEmulationStationNotificationService notifications,
        RetroBat.Providers.RetroArchWrapper.RetroArchWrapperProvider wrapper,
        ILogger<LeaderboardInputService> logger,
        Infrastructure.NelfePlayScoringCollectionSyncService? collection = null,
        Infrastructure.PartieNelfePlayService? partie = null)
    {
        _collection = collection;
        _partie = partie;
        _wrapper = wrapper;
        _social = social;
        _invite = invite;
        _hote = hote;
        _defi = defi;
        _replays = replays;
        _historique = historique;
        _es = es;
        _notifications = notifications;
        _textes = textes;
        _reglages = reglages;
        _machine = machine;
        _httpFactory = httpFactory;
        _viewer = viewer;
        _bus = bus;
        _context = context;
        _overlay = overlay;
        _client = client;
        _agent = agent;
        _session = session;
        _playback = playback;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Leaderboard.Enabled) return Task.CompletedTask;
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        _arret = new CancellationTokenSource();
        _ = Task.Run(() => ChienDeGardeAsync(_arret.Token), CancellationToken.None);
        _ = Task.Run(() => GuetterLesDirectsSuivisAsync(_arret.Token), CancellationToken.None);
        _logger.LogInformation("Classement : panneau arme (appui long sur le slot {Slot}).", SlotValider);

        // Les pictogrammes se preparent maintenant, pas a la premiere ouverture : le joueur ne
        // doit jamais attendre qu'ImageMagick travaille pour voir son classement.
        _ = Task.Run(() =>
        {
            try
            {
                var style = EsMenuStyle.Lire(_logger);
                _style = style;
                _overlay.Prechauffer(style);
                PrechaufferLesSystemes();
                PreparerLaCle();
                // Le dictionnaire d'interface (16 langues) se charge maintenant, pas au moment
                // ou le joueur attend son classement.
                _textes.Text("leaderboard.tab.world", Langue());
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Classement : prechauffage impossible."); }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arret?.Cancel();
        _abonnement?.Dispose();
        _overlay.Fermer();
        return Task.CompletedTask;
    }

    // ── Les entrees ──────────────────────────────────────────────────────────

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            // Un jeu demarre : le panneau n'a plus rien a faire la, et rien ne doit passer
            // devant l'ecran de jeu.
            // SECURITE : si la selection change sous nos pieds (le joueur est ressorti dans la
            // liste, a change de systeme), le panneau ne parle plus du jeu affiche. On ferme.
            if (string.Equals(e.Type, "ui.game.selected", StringComparison.Ordinal))
            {
                // La cle du prochain classement se prepare tout de suite : a l'ouverture, il
                // sera trop tard pour faire attendre le joueur.
                PreparerLaCle();
            }
            if (string.Equals(e.Type, "ui.game.selected", StringComparison.Ordinal)
                && _modele.Etat != LeaderboardPanelModel.Foyer.Ferme)
            {
                var (systemeVu, jeuVu) = JeuSelectionne();
                if (jeuVu.Length > 0 && (!string.Equals(jeuVu, _jeuAffiche, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(systemeVu, _systemeAffiche, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Classement : la selection a change ({Jeu}), le panneau se ferme.", jeuVu);
                    Fermer();
                }
                return;
            }
            if (string.Equals(e.Type, "ui.game.ended", StringComparison.Ordinal))
            {
                // Une partie peut avoir change le classement : on ne ressert pas l'ancien.
                _client.Oublier();
                if (_seanceArmeeParLeDefi)
                {
                    // Desarmer, sinon la partie suivante serait comptee pour le contest.
                    _seanceArmeeParLeDefi = false;
                    _session.Clear();
                    _logger.LogInformation("Classement : fin du defi, seance de contest desarmee.");
                }
                _ = RetirerLeDirectAsync();
                return;
            }
            if (string.Equals(e.Type, "ui.game.started", StringComparison.Ordinal))
            {
                // L'emulateur prend le premier plan : on s'efface sans le disputer a ES.
                _modele.Fermer();
                _overlay.FermerPourLeJeu();
                return;
            }
            // Un replay lance par le panneau ne passe PAS par ES : aucun ui.game.started
            // n'arrive. C'est le lecteur de replay qui rythme : on s'efface quand l'emulateur
            // est a l'ecran, et on rend la main a ES quand la lecture est finie.
            if (string.Equals(e.Type, "replay.started", StringComparison.Ordinal) && _replayLance.Length > 0)
            {
                _ = SEffacerQuandLEmulateurEstLaAsync();
                return;
            }
            if (string.Equals(e.Type, "replay.finished", StringComparison.Ordinal) && _replayLance.Length > 0)
            {
                _replayLance = "";
                _ = RendreEsApresLeReplayAsync();
                return;
            }
            // La lecture n'aura pas lieu : le bandeau du lecteur dit pourquoi, la boite « lancement »
            // n'a plus rien a attendre (elle restait 45 s, sans un mot).
            if (string.Equals(e.Type, "replay.failed", StringComparison.Ordinal) && _replayLance.Length > 0)
            {
                _logger.LogInformation("Classement : la lecture du replay {Replay} n'aura pas lieu, le panneau se ferme.", _replayLance);
                _replayLance = "";
                Fermer();
                return;
            }

            var appuye = string.Equals(e.Type, "panel.input.pressed", StringComparison.Ordinal);
            var relache = string.Equals(e.Type, "panel.input.released", StringComparison.Ordinal);
            if (!appuye && !relache) return;

            var (identite, systeme, slot) = LireBouton(e.Payload);
            if (_modele.Etat != LeaderboardPanelModel.Foyer.Ferme)
            {
                _logger.LogDebug("Classement : {Type} identite={Identite} systeme={Systeme} slot={Slot} (etat {Etat})",
                    e.Type, identite ?? "-", systeme ?? "-", slot?.ToString() ?? "-", _modele.Etat);
            }

            // Les directions arrivent par le canal DPAD, sans slot.
            if (string.Equals(systeme, "DPAD", StringComparison.OrdinalIgnoreCase))
            {
                if (appuye) Direction(identite);
                return;
            }
            if (slot is null) return;

            if (slot == SlotValider)
            {
                if (appuye)
                {
                    _validerDepuis = DateTime.UtcNow;
                    _appuiDebut = DateTime.UtcNow;
                    return;
                }
                // Le menu de jeu d'ES s'ouvre a HOLD_TIME = 1000 ms d'appui (MultiStateInput) :
                // l'appui qui a ouvert notre panneau l'a-t-il aussi ouvert ? Mesure au relachement,
                // parce que le chien de garde oublie `_validerDepuis` des qu'il ouvre le panneau.
                if (_appuiDebut is { } debut && _modele.Etat != LeaderboardPanelModel.Foyer.Ferme && !_menuEsConnu)
                {
                    _menuEsOuvert = (DateTime.UtcNow - debut).TotalMilliseconds >= DureeDuMenuEs;
                    _menuEsConnu = true;
                }
                _appuiDebut = null;
                // Un appui COURT sur valider, quand nous avons la main, agit sur la ligne :
                // c'est le bouton qui « choisit » partout ailleurs dans ES. L'appui long, lui,
                // ouvre le panneau (chien de garde) et ne doit pas agir en plus au relachement.
                var court = _validerDepuis is { } depuis
                    && (DateTime.UtcNow - depuis).TotalMilliseconds < Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs);
                _validerDepuis = null;
                if (court && _modele.Etat == LeaderboardPanelModel.Foyer.Panneau)
                {
                    Appliquer(_modele.Entree(EntreePanneau.Agir));
                    return;
                }

                // ES A LA MAIN ET LE JOUEUR VALIDE : le panneau s'efface.
                //
                // Dans cet etat, notre fenetre est affichee a cote du menu mais c'est ES qui
                // navigue. Valider y ouvre une fenetre d'ES - les options avancees du jeu, par
                // exemple - et notre panneau, qui est topmost, la recouvre : le joueur se
                // retrouve devant un menu invisible (signale le 2026-09-23). Ce que le joueur
                // vient de demander passe devant ce que nous montrons.
                if (court && _modele.Etat == LeaderboardPanelModel.Foyer.MenuEs)
                {
                    _logger.LogInformation("Classement : validation dans le menu d'ES, le panneau s'efface.");
                    Fermer();
                }
                return;
            }

            if (!appuye) return;
            var entree = slot == SlotDeLIdentite("y") ? EntreePanneau.Defier
                : slot == SlotDeLIdentite("x") ? EntreePanneau.Suivre
                : slot switch
                {
                    SlotAnnuler => EntreePanneau.Annuler,
                    SlotPageHaut => EntreePanneau.PageHaut,
                    SlotPageBas => EntreePanneau.PageBas,
                    _ => (EntreePanneau?) null,
                };
            if (entree is not null) Appliquer(_modele.Entree(entree.Value));
        }
        catch (Exception ex)
        {
            // Une erreur ici ne doit jamais empecher la borne de repondre a sa manette.
            _logger.LogWarning(ex, "Classement : entree ignoree.");
        }
    }

    private void Direction(string? identite)
    {
        var entree = (identite ?? "").ToLowerInvariant() switch
        {
            "left" => EntreePanneau.Gauche,
            "right" => EntreePanneau.Droite,
            "up" => EntreePanneau.Haut,
            "down" => EntreePanneau.Bas,
            _ => (EntreePanneau?) null,
        };
        if (entree is null) return;
        var avant = _modele.Etat;
        var effet = _modele.Entree(entree.Value);
        _logger.LogDebug("Classement : {Entree} ({Avant} -> {Apres}) = {Effet}", entree, avant, _modele.Etat, effet);
        Appliquer(effet);
    }

    /// <summary>
    /// Le seuil d'ouverture, surveille a part : `pressed` arrive des l'appui, donc on peut ouvrir
    /// PENDANT que le bouton est encore tenu, au moment meme ou ES ouvre son menu. Attendre le
    /// relache ferait apparaitre le panneau apres coup.
    /// </summary>
    private async Task ChienDeGardeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(80, ct).ConfigureAwait(false);

                // Un appui long vu alors que le panneau se croit deja ouvert ne s'ouvre pas : on le
                // dit, sinon « rien ne s'affiche » ne laisse aucune trace (2026-09-30).
                if (_validerDepuis is { } tenu
                    && _modele.Etat != LeaderboardPanelModel.Foyer.Ferme
                    && (DateTime.UtcNow - tenu).TotalMilliseconds >= Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs))
                {
                    _validerDepuis = null;
                    _logger.LogInformation("Classement : appui long ignore, le panneau est deja ouvert (etat {Etat}).", _modele.Etat);
                }

                if (_validerDepuis is { } depuis
                    && _modele.Etat == LeaderboardPanelModel.Foyer.Ferme
                    && (DateTime.UtcNow - depuis).TotalMilliseconds >= Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs))
                {
                    _validerDepuis = null;
                    _depuisLAppui = System.Diagnostics.Stopwatch.StartNew();
                    await OuvrirAsync(ct).ConfigureAwait(false);
                }

                // Le filet : si nous tenons la manette et que plus rien n'arrive, on la rend.
                if (_overlay.TropLongSilence())
                {
                    // On rend la main a ES sans fermer : le classement reste sous les yeux, et
                    // le joueur retrouve sa manette. Fermer coupait la lecture d'un coup.
                    _modele.RendreLaMain();
                    Appliquer(LeaderboardPanelModel.Effet.RendreLeFocus);
                }

                // LE PANNEAU NE SURVIT PAS A LA PERTE DU PREMIER PLAN.
                //
                // Deux defauts n'en faisaient qu'un. Notre fenetre est topmost : laissee ouverte,
                // elle reste par-dessus l'application suivante. Et `RendreLaMain` fait passer le
                // modele de « Panneau » a « MenuEs », jamais a « Ferme » -- or le chien de garde
                // n'ouvre QUE depuis « Ferme ». Le panneau devenait donc definitivement sourd a
                // l'appui long, sans une ligne de journal, puisque `OuvrirAsync` n'etait meme plus
                // appele. Mesure sur borne le 2026-09-24 : ouvert a 16:59:44, plus une seule
                // ouverture jusqu'a 18:06, et aucun refus enregistre.
                //
                // L'invariant qui manquait : l'etat du modele ne survit pas a la visibilite de la
                // fenetre. Ni ES ni nous au premier plan, donc le joueur est parti ailleurs.
                if (_modele.Etat != LeaderboardPanelModel.Foyer.Ferme
                    && !EsEstDevant()
                    && !_overlay.EstAuPremierPlan())
                {
                    _logger.LogInformation("Classement : le premier plan est parti ailleurs, le panneau se ferme.");
                    Fermer();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Classement : cycle de surveillance en echec.");
            }
        }
    }

    // ── Ouvrir, charger, agir ────────────────────────────────────────────────

    private async Task OuvrirAsync(CancellationToken ct)
    {
        var chronoConditions = System.Diagnostics.Stopwatch.StartNew();
        if (!PeutSOuvrir(out var systeme, out var nomDuJeu, out var cheminDuJeu))
        {
            // Une ouverture refusee doit se VOIR : sans cela, « rien ne s'affiche » ne se
            // distingue pas d'« on attend encore ».
            if (_depuisLAppui is not null)
            {
                _logger.LogInformation("Classement : ouverture refusee (conditions non reunies) apres {Ms} ms.", _depuisLAppui.ElapsedMilliseconds);
                _depuisLAppui = null;
            }
            return;
        }
        var msConditions = chronoConditions.ElapsedMilliseconds;

        // La cle du classement est celle de la plateforme, donc celle que le WRAPPER pose sur
        // une partie : alias.json du systeme, nom normalise, repli arcade. Le slug du nom
        // affiche par ES en differait d'un emulateur a l'autre (19xx visible sous mame, muet
        // sous fbneo) alors que la plateforme n'a qu'un seul « arcade ».
        var chronoCle = System.Diagnostics.Stopwatch.StartNew();
        _romGroup = CleDuJeu(cheminDuJeu, systeme, nomDuJeu);
        var msCle = chronoCle.ElapsedMilliseconds;
        _jeuAffiche = nomDuJeu;
        _systemeAffiche = systeme;
        if (_romGroup.Length == 0) return;
        _reglesDuJeu = _collection?.ReglesOuvertes(_romGroup) ?? Array.Empty<string>();
        _reglePrincipale = LeaderboardClient.ReglePrincipale(_reglesDuJeu);
        _ongletsDeRegle = OngletsDeRegle(_reglesDuJeu, _reglePrincipale);

        var session = _session.Get();
        _maSalle = session?.VenueName ?? "";
        _maVille = session?.VenueCity ?? "";
        _monPays = "";

        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var style = EsMenuStyle.Lire(_logger);
        _style = style;
        var msCharte = chrono.ElapsedMilliseconds;
        lock (_gate)
        {
            _monde = Array.Empty<LeaderboardClient.Ligne>();
            _etatDuMonde = "";
            _autresMondes = new();
            _rangsDesAutres = new();
            _mesParties = Array.Empty<LeaderboardClient.Ligne>();
        }
        _menuEsOuvert = false;
        _menuEsConnu = false;
        _modele.Ouvrir(salleConnue: _maSalle.Length > 0, villeConnue: LieuxAffiches && _maVille.Length > 0, paysConnu: false, aDesRecords: true);
        MontrerLeDernierConnu();
        _ = Task.Run(async () =>
        {
            await _social.RafraichirLesSuivisAsync().ConfigureAwait(false);
            _overlay.PoserLesSuivis(_social.Suivis);
            Rafraichir();
        });
        _overlay.Ouvrir(style, Composer());
        _logger.LogInformation(
            "Classement : panneau ouvert sur {Jeu} ({Systeme}) - conditions {MsConditions} ms, cle {MsCle} ms, charte {MsCharte} ms, contenu+fenetre {MsFenetre} ms, total {MsTotal} ms.",
            _romGroup, systeme, msConditions, msCle, msCharte, chrono.ElapsedMilliseconds - msCharte,
            _depuisLAppui?.ElapsedMilliseconds ?? -1);
        _overlay.ChronometrerLePremierDessin(_depuisLAppui);

        await ChargerAsync(ct).ConfigureAwait(false);
    }

    /// <summary>La derniere raison de refus deja ecrite, pour ne pas repeter la meme ligne.</summary>
    private string _dernierRefus = "";

    /// <summary>
    /// Le dernier jeu que l'interface a annonce comme selectionne.
    ///
    /// ES efface sa selection au retour d'un jeu ou d'un replay sans la reannoncer : ce
    /// souvenir est ce qui permet de rouvrir le panneau sur la fiche que le joueur a sous les
    /// yeux, au lieu de lui demander de bouger d'un cran pour rien.
    /// </summary>
    private RetroBat.Domain.Models.GameReference? _dernierJeu;

    /// <summary>
    /// Le systeme du CARROUSEL au moment ou ce jeu a ete vu, qui n'est pas le systeme du jeu.
    ///
    /// Dans une collection - « NELFEPLAY WORLD SCORING » en est une - le carrousel affiche le
    /// nom de la collection tandis que le jeu garde le sien, « arcade ». Comparer les deux
    /// aurait fait echouer la reprise precisement la ou le joueur passe son temps.
    /// </summary>
    private string _dernierCarrousel = "";

    /// <summary>Qui sait quels jeux sont ouverts au scoring sur cette borne.</summary>
    private readonly Infrastructure.NelfePlayScoringCollectionSyncService? _collection;
    private readonly Infrastructure.PartieNelfePlayService? _partie;

    /// <summary>Les conditions d'ouverture. Toutes doivent tenir : un panneau qui s'ouvre au mauvais moment se ferme mal.</summary>
    private bool PeutSOuvrir(out string systeme, out string nomDuJeu, out string cheminDuJeu)
    {
        cheminDuJeu = "";
        systeme = "";
        nomDuJeu = "";
        if (_playback.IsBusy) return Refus("une lecture de replay est en cours");
        if (EmulatorForeground.EmulateurTourne()) return Refus("un jeu tourne");
        if (!EsEstDevant()) return Refus("EmulationStation n'est pas la fenetre active");

        var ui = _context.Ui;
        if (!string.Equals(ui.State, "browsing", StringComparison.OrdinalIgnoreCase))
        {
            return Refus($"l'interface est en etat « {ui.State} » et non « browsing »");
        }

        var jeu = ui.Selected;
        if (jeu is null)
        {
            // LA SELECTION SE PERD AU RETOUR D'UN JEU OU D'UN REPLAY.
            //
            // ES emet alors `system-selected`, qui efface la selection, et ne reemet pas
            // `game-selected` puisque la fiche affichee n'a pas change. Le joueur, lui, voit
            // toujours son jeu : un appui long ne faisait donc rien, et il fallait bouger d'un
            // cran puis revenir pour que le panneau consente a s'ouvrir (signale le 2026-09-23).
            //
            // On retient donc la derniere selection connue et on la reprend, a condition que le
            // carrousel soit reste sur le MEME systeme : changer de systeme est un vrai
            // changement de contexte, et ressortir un jeu d'un autre systeme serait faux.
            if (_dernierJeu is null && JeuDEventsIni() is { } relais)
            {
                // AU DEMARRAGE DE L'API (2026-10-04) : aucun evenement d'ES encore recu, mais ES a
                // ecrit le jeu choisi dans events.ini. Le joueur qui n'avait pas bouge depuis le
                // lancement de l'API faisait un appui long sans effet.
                jeu = relais;
                _logger.LogInformation("Classement : aucun evenement d'ES depuis le demarrage, jeu repris d'events.ini ({Jeu}).", jeu.GameName);
            }
            else if (_dernierJeu is null
                || !string.Equals(_dernierCarrousel, ui.SelectedSystem?.Name ?? "", StringComparison.OrdinalIgnoreCase))
            {
                return Refus("aucun jeu selectionne (carrousel des systemes, ou selection perdue)");
            }
            else
            {
                jeu = _dernierJeu;
                _logger.LogInformation(
                    "Classement : selection effacee par ES, reprise du dernier jeu connu ({Jeu}).",
                    jeu.GameName);
            }
        }
        else
        {
            _dernierJeu = jeu;
            _dernierCarrousel = ui.SelectedSystem?.Name ?? "";
        }

        systeme = jeu.SystemId ?? "";
        nomDuJeu = jeu.GameName ?? "";
        cheminDuJeu = jeu.GamePath ?? "";
        if (nomDuJeu.Length == 0)
        {
            return Refus("le jeu selectionne n'a pas de nom");
        }

        // LE PANNEAU EST CELUI DU SCORING MONDIAL : il n'a rien a dire d'un jeu qui n'y est pas
        // ouvert (demande user 2026-09-23). L'ouvrir ailleurs montrait un classement vide et
        // laissait croire a une panne. La liste est la collection World Scoring elle-meme ;
        // quand la borne n'en a aucune, on ne restreint pas - voir EstOuvertAuScoring.
        if (_collection?.EstOuvertAuScoring(cheminDuJeu) == false)
        {
            return Refus("ce jeu n'est pas ouvert au scoring mondial");
        }

        _dernierRefus = "";
        return true;
    }

    /// <summary>
    /// Le refus se dit. Sans cette trace, un appui long sans effet ne laissait rien dans le
    /// journal et il fallait deviner laquelle des cinq conditions avait bloque. La meme raison
    /// repetee n'est ecrite qu'une fois : le lecteur d'entrees passe ici en boucle.
    /// </summary>
    private bool Refus(string raison)
    {
        if (!string.Equals(_dernierRefus, raison, StringComparison.Ordinal))
        {
            _dernierRefus = raison;
            _logger.LogInformation("Classement : panneau non ouvert, {Raison}.", raison);
        }

        return false;
    }

    /// <summary>
    /// EmulationStation est-il la fenetre active ? Le panneau ne s'invite pas par-dessus autre
    /// chose : si le joueur est dans une autre application, l'appui long ne nous regarde pas.
    /// </summary>
    /// <summary>
    /// EmulationStation a-t-il le premier plan ? On lit le processus de la fenetre devant, sans
    /// parcourir tous les processus (voir EmulatorForeground.EmulateurTourne).
    /// </summary>
    private static bool EsEstDevant() => EmulatorForeground.DevantEst("emulationstation");

    /// <summary>
    /// Le jeu choisi qu'ES a ecrit dans events.ini depuis son propre demarrage ; null sinon. Ne
    /// sert que tant que l'API n'a recu aucune selection (voir PeutSOuvrir).
    /// </summary>
    private static GameReference? JeuDEventsIni()
    {
        DateTime? demarrageEs = null;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("emulationstation"))
        {
            try { demarrageEs = p.StartTime.ToUniversalTime(); } catch (Exception) { }
            p.Dispose();
        }
        if (demarrageEs is not { } depuis) return null;
        return RetroBat.Domain.Paths.EventsIniFile.TryReadGameSelected(RetroBat.Domain.Paths.RetroBatPaths.EventsIniPath, depuis, out var systeme, out var chemin, out var nom)
            ? new GameReference { SystemId = systeme, GamePath = chemin, GameName = nom }
            : null;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Guette les directs des joueurs suivis et prevent la borne par une notification NATIVE
    /// d'EmulationStation, qui nomme le joueur, le jeu et la machine. Les directs deja en cours
    /// au demarrage sont retenus sans bruit : un redemarrage de l'API ne doit pas reannoncer ce
    /// que le joueur a deja vu.
    /// </summary>
    private async Task GuetterLesDirectsSuivisAsync(CancellationToken ct)
    {
        var connus = new HashSet<string>(StringComparer.Ordinal);
        var premierTour = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var directs = await _social.DirectsSuivisAsync(ct).ConfigureAwait(false);
                var enCours = new HashSet<string>(directs.Select(d => d.Session), StringComparer.Ordinal);
                foreach (var d in directs)
                {
                    if (!connus.Add(d.Session) || premierTour) continue;
                    var message = _textes.Format("leaderboard.live_notice", Langue(),
                        ("player", d.Pseudo.Length > 0 ? d.Pseudo : d.Poignee),
                        ("game", d.NomDuJeu.Length > 0 ? d.NomDuJeu : d.Jeu),
                        ("system", NomDuSysteme(d.Systeme)));
                    _logger.LogInformation("Classement : {Message}", message);
                    await _notifications.NotifyAsync(message, ct).ConfigureAwait(false);
                }
                connus.IntersectWith(enCours);   // un direct fini pourra etre reannonce s'il reprend
                premierTour = false;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogDebug(ex, "Classement : guet des directs suivis en echec."); }

            try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private IReadOnlyList<LeaderboardSocialClient.Direct> _directs = Array.Empty<LeaderboardSocialClient.Direct>();
    /// <summary>Peut-on jouer dans chaque direct, et la place : consulte sans rien reserver.</summary>
    private Dictionary<string, LeaderboardSocialClient.EtatDuDirect?> _etatsDesDirects = new(StringComparer.Ordinal);
    /// <summary>Les regles ouvertes du jeu affiche (« 1cc », « 1cc-multi »...), relues a l'ouverture.</summary>
    private IReadOnlyList<string> _reglesDuJeu = Array.Empty<string>();
    private IReadOnlyList<LeaderboardSocialClient.Contest> _contests = Array.Empty<LeaderboardSocialClient.Contest>();

    /// <summary>Les directs et contests du jeu affiche. L'onglet n'existe que s'il y en a.</summary>
    private async Task ChargerLesEvenementsAsync(CancellationToken ct)
    {
        var jeu = _romGroup;
        var directs = await _social.DirectsDuJeuAsync(jeu, ct).ConfigureAwait(false);
        var contests = await _social.ContestsDuJeuAsync(jeu, _jeuAffiche, ct).ConfigureAwait(false);
        if (!string.Equals(jeu, _romGroup, StringComparison.Ordinal)) return;   // le joueur a change de jeu
        // Peut-on JOUER dans chaque direct ? Consulte sans rien reserver : la ligne dit JOIN ou WATCH.
        var etats = new Dictionary<string, LeaderboardSocialClient.EtatDuDirect?>(StringComparer.Ordinal);
        foreach (var d in directs.Where(d => !string.Equals(d.Type, "react", StringComparison.Ordinal)))
        {
            etats[d.Session] = await _social.EtatDuDirectAsync(d.Session, ct).ConfigureAwait(false);
        }
        lock (_gate)
        {
            _directs = directs;
            _contests = contests;
            _etatsDesDirects = etats;
        }
        if (_modele.Etat == LeaderboardPanelModel.Foyer.Ferme) return;
        if (_modele.PoserLesEvenements(directs.Count + contests.Count > 0))
        {
            _logger.LogInformation("Classement : {Directs} direct(s) et {Contests} contest(s) sur {Jeu}.", directs.Count, contests.Count, jeu);
        }
        Rafraichir();
    }

    /// <summary>Les lignes de l'onglet LIVE & CONTEST : les directs d'abord, ils ne durent pas.</summary>
    private IReadOnlyList<LeaderboardOverlayService.Evenement> Evenements(string langue)
    {
        lock (_gate)
        {
            var lignes = new List<LeaderboardOverlayService.Evenement>();
            foreach (var d in _directs)
            {
                var (joignable, raison) = Joignabilite(d);
                var detail = NomDuSysteme(d.Systeme);
                if (raison.Length > 0) detail = detail.Length > 0 ? detail + " · " + Texte(raison, langue) : Texte(raison, langue);
                IReadOnlyList<LeaderboardOverlayService.Aide> actions = string.Equals(d.Type, "react", StringComparison.Ordinal)
                    ? Array.Empty<LeaderboardOverlayService.Aide>()
                    : joignable
                        ? new[]
                        {
                            new LeaderboardOverlayService.Aide(Glyphe(SlotValider), Texte("leaderboard.join", langue)),
                            new LeaderboardOverlayService.Aide(Glyphe(SlotDeLIdentite("x")), Texte("leaderboard.watch", langue)),
                        }
                        : new[] { new LeaderboardOverlayService.Aide(Glyphe(SlotValider), Texte("leaderboard.watch", langue)) };
                lignes.Add(new(_textes.Text("leaderboard.live", langue), d.Pseudo.Length > 0 ? d.Pseudo : d.Poignee, detail, true, actions));
            }
            foreach (var c in _contests)
            {
                lignes.Add(new(c.Titre.Length > 0 ? c.Titre : _textes.Text("leaderboard.contest", langue), c.Organisateur, NomDuSysteme(_systemeAffiche), c.Statut == "live"));
            }
            return lignes;
        }
    }

    /// <summary>
    /// Peut-on JOUER dans ce direct, et sinon pourquoi (cle de texte). Sans reponse de la plateforme,
    /// on propose JOIN comme avant : c'est elle qui tranche au moment de rejoindre, et un refus fait
    /// rejoindre en spectateur avec le bandeau d'arrivee.
    /// </summary>
    private (bool Joignable, string Raison) Joignabilite(LeaderboardSocialClient.Direct d)
    {
        if (string.Equals(d.Type, "react", StringComparison.Ordinal)) return (false, "leaderboard.live.no_netplay");
        LeaderboardSocialClient.EtatDuDirect? etat;
        lock (_gate) etat = _etatsDesDirects.TryGetValue(d.Session, out var e) ? e : null;
        if (etat is null) return (true, "");
        if (etat.PeutJouer && etat.Place is "free" or "reserved" or "playing") return (true, "");
        if (etat.PeutJouer && etat.Place == "wait") return (false, "leaderboard.live.wait");
        if (etat.PeutJouer && etat.Place == "full") return (false, "leaderboard.live.full");
        if (etat.PeutJouer) return (true, "");
        return string.Equals(d.Type, "followers", StringComparison.Ordinal)
            ? (false, "leaderboard.live.followers")
            : (false, "leaderboard.live.watch_only");
    }

    /// <summary>
    /// Un texte du panneau, avec son repli anglais : les textes voyagent par le Data Pack, qui peut
    /// arriver apres le programme, et une cle manquante s'afficherait telle quelle.
    /// </summary>
    private string Texte(string cle, string langue)
    {
        var texte = _textes.Text(cle, langue);
        if (!string.Equals(texte, cle, StringComparison.Ordinal)) return texte;
        return cle switch
        {
            "leaderboard.watch" => "Watch",
            "leaderboard.join" => "Join",
            "leaderboard.live.full" => "Full",
            "leaderboard.live.wait" => "Seat pending",
            "leaderboard.live.followers" => "Followed players only",
            "leaderboard.live.watch_only" => "Watch only",
            "leaderboard.live.no_netplay" => "Reactions only",
            _ => cle,
        };
    }

    /// <summary>Le defi que le bouton CHALLENGE lance (demande user 2026-10-02).</summary>
    internal enum ModeDuDefi { Prive, Live, Multi }

    /// <summary>
    /// D'apres les reglages du joueur : sans partage en direct, un 1CC prive ; en direct, 1CC LIVE si
    /// personne ne rejoint, 1CC MULTI si ses suivis ou tout le monde peuvent jouer. Le MULTI n'existe
    /// que si le jeu a un classement 1CC MULTI : sinon un joueur arrive fermerait le 1CC solo pour un
    /// score qui ne compte nulle part. Sur l'onglet LIVE & CONTEST, le defi est toujours en direct.
    /// </summary>
    internal static ModeDuDefi ModeDuDefiPour(bool surLeLive, bool partageLive, string politique, bool jeuMulti)
    {
        if (!surLeLive && !partageLive) return ModeDuDefi.Prive;
        return politique is "followed" or "everyone" && jeuMulti ? ModeDuDefi.Multi : ModeDuDefi.Live;
    }

    private ModeDuDefi ModeCourant()
    {
        var reglages = _options.CurrentValue.Leaderboard;
        return ModeDuDefiPour(_modele.VueCourante == LeaderboardPanelModel.Vue.LiveEtContest,
            reglages.ChallengeShareLive, reglages.ChallengeJoinPolicy ?? "none", _reglesDuJeu.Contains("1cc-multi"));
    }

    /// <summary>Le libelle du mode : la regle du jeu (1CC, ou 1LC pour un jeu ouvert en 1LC), puis LIVE ou MULTI.</summary>
    internal static string LibelleDuMode(ModeDuDefi mode, IReadOnlyList<string> regles)
    {
        var regle = regles.Contains("1lc") && !regles.Contains("1cc") ? "1LC" : "1CC";
        return mode switch
        {
            ModeDuDefi.Live => regle + " LIVE",
            ModeDuDefi.Multi => regle + " MULTI",
            _ => regle,
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> NomsDeSystemes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Le nom d'une machine tel qu'EmulationStation l'affiche (<c>fullname</c> de es_systems.cfg).
    /// La plateforme ne connait que l'identifiant du systeme : c'est ES qui sait le nommer.
    /// </summary>
    internal static string NomDuSysteme(string systeme)
    {
        if (string.IsNullOrWhiteSpace(systeme)) return "";
        return NomsDeSystemes.GetOrAdd(systeme, id =>
        {
            try
            {
                var fichier = Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot, "emulationstation", ".emulationstation", "es_systems.cfg");
                if (!File.Exists(fichier)) return id;
                var doc = System.Xml.Linq.XDocument.Load(fichier);
                var nom = doc.Descendants("system")
                    .FirstOrDefault(e => string.Equals((string?) e.Element("name"), id, StringComparison.OrdinalIgnoreCase))
                    ?.Element("fullname")?.Value;
                return string.IsNullOrWhiteSpace(nom) ? id : nom.Trim();
            }
            catch (Exception) { return id; }
        });
    }

    /// <summary>
    /// Les scores de CE jeu dont cette borne a enregistre le replay. Une ligne a nous, sans
    /// replay cote plateforme mais dont le score est ici, a un replay EN COURS D'ENVOI.
    /// </summary>
    private IReadOnlySet<long> ReplaysLocaux(string cheminDuJeu)
    {
        var scores = new HashSet<long>();
        if (cheminDuJeu.Length == 0) return scores;
        var cible = Path.GetFullPath(cheminDuJeu.Replace('/', '\\'));
        try
        {
            foreach (var manifeste in _replays.ListManifests())
            {
                var meta = _replays.GetMeta(manifeste.ReplayId);
                if (meta is null || !meta.CreatedByThisDevice || meta.ScoreValue is not { } score) continue;
                var rom = meta.Launch?.RomPath ?? "";
                if (rom.Length == 0) continue;
                if (string.Equals(Path.GetFullPath(rom), cible, StringComparison.OrdinalIgnoreCase)) scores.Add(score);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Classement : replays locaux illisibles."); }
        return scores;
    }

    /// <summary>MES RECORDS : les parties de CETTE borne, lues sur le disque (2026-10-03).</summary>
    private IReadOnlyList<LeaderboardClient.Ligne> MesPartiesLocales()
    {
        try
        {
            string chemin;
            try { chemin = _context.Ui.Selected?.GamePath ?? ""; }
            catch (Exception) { chemin = ""; }
            var langue = Langue();
            return LocalPlaysIndex.MesParties(_partiesLocales.Toutes(), _romGroup, _reglePrincipale,
                _session.Get()?.PlayerCode ?? "", ReplaysDeCeJeu(chemin), utc => DateDeLaPartie(utc, langue));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Classement : parties locales illisibles.");
            return Array.Empty<LeaderboardClient.Ligne>();
        }
    }

    /// <summary>
    /// Les replays enregistres par cette borne sur ce jeu, pour MES RECORDS : leur fin, leur duree
    /// (images / cadence du core) et le score que le rapporteur leur a attache a la publication.
    /// </summary>
    private IReadOnlyList<LocalPlaysIndex.ReplayLocal> ReplaysDeCeJeu(string cheminDuJeu)
    {
        var replays = new List<LocalPlaysIndex.ReplayLocal>();
        var cible = cheminDuJeu.Length > 0 ? Path.GetFullPath(cheminDuJeu.Replace('/', '\\')) : "";
        try
        {
            foreach (var manifeste in _replays.ListManifests())
            {
                var meta = _replays.GetMeta(manifeste.ReplayId);
                if (meta is null || !meta.CreatedByThisDevice) continue;
                var rom = meta.Launch?.RomPath ?? "";
                var memeJeu = string.Equals(manifeste.Game.RomGroup, _romGroup, StringComparison.OrdinalIgnoreCase)
                    || (cible.Length > 0 && rom.Length > 0 && string.Equals(Path.GetFullPath(rom), cible, StringComparison.OrdinalIgnoreCase));
                if (!memeJeu) continue;
                var images = Math.Max(0, manifeste.Frames.ReplayEnd - manifeste.Frames.Start);
                var duree = manifeste.Frames.NominalFps > 0 ? TimeSpan.FromSeconds(images / manifeste.Frames.NominalFps) : TimeSpan.Zero;
                var fin = manifeste.CreatedAt.Kind == DateTimeKind.Local ? manifeste.CreatedAt.ToUniversalTime() : DateTime.SpecifyKind(manifeste.CreatedAt, DateTimeKind.Utc);
                replays.Add(new LocalPlaysIndex.ReplayLocal(manifeste.ReplayId, fin, duree, meta.ScoreValue));
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Classement : replays locaux illisibles."); }
        return replays;
    }

    /// <summary>La date d'une partie, a l'heure de la borne, comme la langue de la borne l'ecrit.</summary>
    private static string DateDeLaPartie(DateTime utc, string langue)
    {
        System.Globalization.CultureInfo culture;
        try { culture = System.Globalization.CultureInfo.GetCultureInfo(langue); }
        catch (System.Globalization.CultureNotFoundException) { culture = System.Globalization.CultureInfo.InvariantCulture; }
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", culture);
    }

    private CancellationTokenSource? _relecture;

    /// <summary>
    /// Tant qu'un replay enregistre ici n'est pas encore sur la plateforme, on relit le classement
    /// toutes les 20 s : « REPLAY » passe du gris au bouton sans que le joueur ait a rouvrir.
    /// </summary>
    private void RelireTantQueLeReplayArrive()
    {
        _relecture?.Cancel();
        var jeton = new CancellationTokenSource();
        _relecture = jeton;
        var jeu = _romGroup;
        _ = Task.Run(async () =>
        {
            var limite = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (!jeton.IsCancellationRequested && DateTime.UtcNow < limite)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(20), jeton.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (_modele.Etat == LeaderboardPanelModel.Foyer.Ferme || !string.Equals(jeu, _romGroup, StringComparison.Ordinal)) return;
                _client.Oublier();
                await ChargerAsync(jeton.Token, relecture: true).ConfigureAwait(false);
                if (!UnReplayEstEnAttente()) return;
            }
        });
    }

    private bool UnReplayEstEnAttente()
    {
        lock (_gate)
        {
            return _monde.Any(l => l.CestMoi && l.ReplayId is not { Length: > 0 } && _replaysEnPreparation.Contains(l.Valeur));
        }
    }

    /// <summary>
    /// LE DERNIER CLASSEMENT CONNU S'AFFICHE DES L'OUVERTURE (2026-10-03) : le classement frais le
    /// remplace a son arrivee. Avant, le panneau s'ouvrait vide (« chargement ») le temps de
    /// l'aller-retour au site, et restait vide apres un redemarrage de l'API ou sans reseau. Les
    /// onglets des autres regles, de la ville et du pays arrivent avec lui.
    /// </summary>
    private void MontrerLeDernierConnu()
    {
        try
        {
            if (_client.DernierConnu(_romGroup, _reglePrincipale) is not { Lignes.Count: > 0 } connu) return;
            string ville, pays;
            lock (_gate)
            {
                _monde = connu.Lignes;
                _etatDuMonde = LeaderboardClient.EtatOk;
                var mienne = _monde.FirstOrDefault(l => l.CestMoi);
                if (mienne is not null)
                {
                    if (_maVille.Length == 0) _maVille = mienne.Ville;
                    _monPays = mienne.Pays;
                }
                foreach (var (vue, regle) in _ongletsDeRegle)
                {
                    if (_client.DernierConnu(_romGroup, regle) is { Lignes.Count: > 0 } autre) _autresMondes[vue] = (autre.Lignes, LeaderboardClient.EtatOk);
                }
                ville = _maVille;
                pays = _monPays;
            }
            if (LieuxAffiches) _modele.PoserLesLieux(ville.Length > 0, pays.Length > 0);
            _modele.PoserLesRegles(_autresMondes.Keys.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Classement : dernier classement connu illisible.");
        }
    }

    private async Task ChargerAsync(CancellationToken ct, bool relecture = false)
    {
        if (!relecture) _ = ChargerLesEvenementsAsync(ct);
        // MES RECORDS ne depend que du disque : il s'affiche sans attendre le reseau.
        var mesParties = MesPartiesLocales();
        lock (_gate) { _mesParties = mesParties; }
        if (!relecture && _modele.Etat != LeaderboardPanelModel.Foyer.Ferme) Rafraichir();
        var pseudo = _agent.Status.Pseudo ?? "";
        var resultat = await _client.MondeAsync(_romGroup, pseudo, ct, _reglePrincipale).ConfigureAwait(false);
        if (!relecture)
        {
            // La reference des fleches : la consultation d'AVANT. On la fige pour toute cette
            // ouverture, puis on retient ce qu'on voit maintenant pour la prochaine fois.
            _rangsPrecedents = _historique.Lire(_romGroup);
            if (resultat.Etat == LeaderboardClient.EtatOk) _historique.Enregistrer(_romGroup, resultat.Lignes);
        }

        // Les autres regles du jeu, chacune son classement et son historique de rangs.
        var autres = new Dictionary<LeaderboardPanelModel.Vue, (IReadOnlyList<LeaderboardClient.Ligne> Lignes, string Etat)>();
        var rangsDesAutres = new Dictionary<LeaderboardPanelModel.Vue, IReadOnlyDictionary<string, int>>();
        foreach (var (vue, regle) in _ongletsDeRegle)
        {
            var r = await _client.MondeAsync(_romGroup, pseudo, ct, regle).ConfigureAwait(false);
            autres[vue] = (r.Lignes, r.Etat);
            var cleHistorique = _romGroup + "#" + regle;
            if (!relecture)
            {
                rangsDesAutres[vue] = _historique.Lire(cleHistorique);
                if (r.Etat == LeaderboardClient.EtatOk) _historique.Enregistrer(cleHistorique, r.Lignes);
            }
        }

        lock (_gate)
        {
            _monde = resultat.Lignes;
            _etatDuMonde = resultat.Etat;
            _autresMondes = autres;
            if (!relecture) _rangsDesAutres = rangsDesAutres;
            // On apprend sa ville et son pays de SA PROPRE ligne : la borne ne les connait pas
            // autrement, et un onglet qu'on ne peut pas remplir ne doit pas exister.
            var mienne = _monde.FirstOrDefault(l => l.CestMoi);
            if (mienne is not null)
            {
                if (_maVille.Length == 0) _maVille = mienne.Ville;
                _monPays = mienne.Pays;
            }
        }
        try
        {
            var jeu = _context.Ui.Selected;
            _replaysEnPreparation = ReplaysLocaux(jeu?.GamePath ?? "");
        }
        catch (Exception) { _replaysEnPreparation = new HashSet<long>(); }
        if (_modele.Etat == LeaderboardPanelModel.Foyer.Ferme) return;
        bool cetteBorneDistincte;
        lock (_gate)
        {
            cetteBorneDistincte = !LeaderboardClient.Tailler(_monde, LeaderboardPanelModel.Vue.CetteBorne, _maVille, _monPays, _maSalle)
                .SequenceEqual(LeaderboardClient.Tailler(_monde, LeaderboardPanelModel.Vue.MesRecords, _maVille, _monPays, _maSalle));
        }
        _modele.PoserCetteBorne(cetteBorneDistincte);
        // Ville et pays se lisent sur la ligne du joueur : leurs onglets arrivent avec elle.
        string ville, pays;
        lock (_gate) { ville = _maVille; pays = _monPays; }
        if (LieuxAffiches) _modele.PoserLesLieux(ville.Length > 0, pays.Length > 0);
        // L'onglet d'une autre regle n'existe que si son classement a des scores.
        _modele.PoserLesRegles(autres.Where(a => a.Value.Lignes.Count > 0).Select(a => a.Key).ToList());
        Rafraichir();
        if (!relecture && UnReplayEstEnAttente()) RelireTantQueLeReplayArrive();
    }

    private void Appliquer(LeaderboardPanelModel.Effet effet)
    {
        switch (effet)
        {
            case LeaderboardPanelModel.Effet.PrendreLeFocus:
                _logger.LogInformation("Classement : le joueur entre dans le panneau.");
                if (!_overlay.PrendreLeFocus())
                {
                    // Refuse : on retourne a l'etat ou ES navigue, sinon le panneau aurait l'air
                    // actif sans repondre.
                    _modele.Entree(EntreePanneau.Droite);
                }
                Rafraichir();
                break;

            case LeaderboardPanelModel.Effet.RendreLeFocus:
                _overlay.RendreLeFocus();
                Rafraichir();
                break;

            case LeaderboardPanelModel.Effet.Fermer:
                Fermer();
                break;

            case LeaderboardPanelModel.Effet.ChargerLaVue:
                Rafraichir();
                break;

            case LeaderboardPanelModel.Effet.Defier:
                Defier();
                break;

            case LeaderboardPanelModel.Effet.BasculerLeSuivi:
                // Sur LIVE & CONTEST, X ne suit personne : il REGARDE le direct de la ligne.
                if (_modele.VueCourante == LeaderboardPanelModel.Vue.LiveEtContest) Rejoindre(regarder: true);
                else BasculerLeSuivi();
                break;

            case LeaderboardPanelModel.Effet.AgirSurLaLigne:
                Agir();
                break;

            default:
                Rafraichir();
                break;
        }
    }

    private void Fermer()
    {
        if (_modele.Etat == LeaderboardPanelModel.Foyer.Ferme && !_overlay.Affiche) return;
        _modele.Fermer();
        _overlay.Fermer();
    }

    /// <summary>
    /// Defier le jeu : on lance la partie. C'est EmulationStation qui lance, par sa propre API,
    /// avec le chemin de la ROM qu'il a lui-meme selectionnee - la borne ne recompose jamais une
    /// ligne de commande d'emulateur.
    /// </summary>
    private void Defier(LeaderboardSocialClient.Contest? contest = null)
    {
        var (_, jeu) = JeuSelectionne();
        string chemin;
        try { chemin = _context.Ui.Selected?.GamePath ?? ""; }
        catch (Exception) { chemin = ""; }
        if (chemin.Length == 0)
        {
            _logger.LogInformation("Classement : defi impossible, aucun jeu selectionne.");
            return;
        }
        // Le panneau NE DISPARAIT PAS en silence : entre l'appui et l'image du jeu il se passe
        // plusieurs secondes, et pendant ce temps un ecran qui se vide se lit comme une panne -
        // ou pire, comme si l'appui suivant avait lance la partie.
        _logger.LogInformation("Classement : defi lance sur {Jeu}.", jeu);
        // Le cartouche, arme avec le classement du monde tel que le panneau l'a charge.
        IReadOnlyList<LeaderboardClient.Ligne> monde;
        lock (_gate) monde = _monde;
        _defi.Armer(monde, _style ?? EsMenuStyle.Lire(_logger));

        // Au-dessus de « Lancement de la partie » : c'est un DEFI, et voici l'objectif.
        var langue = Langue();
        var objectif = _defi.Objectif().Cible is { } cible
            ? cible.CestMoi
                ? _textes.Format("leaderboard.challenge_goal_own", langue,
                    ("score", cible.Valeur.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)))
                : _textes.Format("leaderboard.challenge_goal", langue,
                    ("rank", cible.Rang), ("player", cible.Joueur),
                    ("score", cible.Valeur.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)))
            : _textes.Text("leaderboard.challenge_goal_top", langue);
        _overlay.Attendre(_textes.Text("leaderboard.starting_game", langue), _textes.Text("leaderboard.challenge_title", langue), objectif);

        // EmulationStation doit avoir le premier plan AVANT /launch : sa requete ne lance rien
        // elle-meme, elle attend le prochain tour de sa boucle d'interface, qui ne tourne pas
        // quand c'est notre panneau qui a la main. Mesure : plus de deux minutes d'attente.
        // C'est ce que fait deja le lancement depuis le site.
        _modele.RendreLaMain();
        _overlay.RendreLeFocus();

        // Un contest rejoint : la seance de scoring porte le contest, pour que le score certifie
        // lui soit attribue. Elle sera desarmee a la fin de la partie.
        if (contest is not null) ArmerLaSeanceDuContest(contest);

        var reglages = _options.CurrentValue.Leaderboard;
        // Le mode se decide a l'APPUI : l'onglet compte (LIVE & CONTEST = toujours en direct). Un
        // contest garde les reglages du joueur : c'est LiveContest qui le diffuse, pas nous.
        var mode = contest is null
            ? ModeCourant()
            : ModeDuDefiPour(false, reglages.ChallengeShareLive, reglages.ChallengeJoinPolicy ?? "none", _reglesDuJeu.Contains("1cc-multi"));
        _logger.LogInformation("Classement : defi {Mode} ({Libelle}).", mode, LibelleDuMode(mode, _reglesDuJeu));
        _ = Task.Run(async () =>
        {
            try
            {
                // Le menu de jeu d'ES se ferme AVANT tout lancement, diffuse ou non : le defi
                // diffuse lance lui aussi par `/launch` (NetplayLaunch), et sous le menu ES
                // acceptait la demande sans jamais lancer (2026-10-01, defi bloque sur
                // « Lancement de la partie », emulateur jamais apparu).
                await FermerLeMenuEsAsync().ConfigureAwait(false);
                if (mode != ModeDuDefi.Prive
                    && await DiffuserLeDefiAsync(chemin, mode == ModeDuDefi.Multi ? reglages.ChallengeJoinPolicy : "none").ConfigureAwait(false))
                {
                    await AttendreLeJeuPuisSEffacerAsync("defi en direct").ConfigureAwait(false);
                    return;
                }
                EmulatorForeground.FocusEmulationStation();
                using var client = _httpFactory.CreateClient();
                client.BaseAddress = new Uri("http://127.0.0.1:1234");
                client.Timeout = TimeSpan.FromSeconds(10);
                using var corps = new StringContent(chemin, System.Text.Encoding.UTF8, "text/plain");
                // Un defi est une partie NelfePlay, meme lance depuis le systeme du jeu.
                _partie?.AnnoncerLancement("defi");
                using var reponse = await client.PostAsync("/launch", corps).ConfigureAwait(false);
                if (!reponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Classement : EmulationStation a refuse le lancement ({Statut}).", (int) reponse.StatusCode);
                    Fermer();
                    return;
                }
                // Le depart du jeu ferme le panneau par `ui.game.started` ; le filet fait le reste.
                await AttendreLeJeuPuisSEffacerAsync("defi").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Classement : le defi n'a pas demarre.");
                Fermer();
            }
        });
    }

    /// <summary>
    /// Rejoindre la ligne choisie. Un DIRECT passe par le service d'invite deja eprouve depuis
    /// le site : la plateforme dit ce que l'hote autorise (jouer, ou seulement regarder). Un
    /// CONTEST lance le jeu, comme « Defier ».
    /// </summary>
    private void Rejoindre(bool regarder)
    {
        LeaderboardSocialClient.Direct? direct;
        LeaderboardSocialClient.Contest? contest;
        lock (_gate)
        {
            direct = _modele.Ligne < _directs.Count ? _directs[_modele.Ligne] : null;
            contest = direct is null ? _contests.ElementAtOrDefault(_modele.Ligne - _directs.Count) : null;
        }
        if (contest is not null)
        {
            if (regarder) return;   // X sur un contest : rien a regarder
            _logger.LogInformation("Classement : contest {Contest} rejoint sur {Jeu}.", contest.Id, _jeuAffiche);
            Defier(contest);
            return;
        }
        if (direct is null) return;
        if (string.Equals(direct.Type, "react", StringComparison.Ordinal)) return;   // pas de netplay : rien a rejoindre
        // A sur une ligne ou l'on ne peut pas jouer : on regarde, un appui fait toujours quelque chose.
        if (!regarder && !Joignabilite(direct).Joignable) regarder = true;

        _logger.LogInformation("Classement : {Geste} le direct de {Joueur} ({Session}).", regarder ? "regarder" : "rejoindre en joueur", direct.Pseudo, direct.Session);
        _overlay.Attendre(_textes.Text("leaderboard.starting_game", Langue()));
        _ = Task.Run(async () =>
        {
            try
            {
                // Meme raison que pour un defi : l'invite lance par `/launch`, qui attend sous le menu.
                await FermerLeMenuEsAsync().ConfigureAwait(false);
                var echec = await _invite.RejoindreAsync(direct.Session, hote: direct.Pseudo.Length > 0 ? direct.Pseudo : direct.Poignee, regarderSeulement: regarder).ConfigureAwait(false);
                if (echec != RetroBat.Api.Netplay.NetplayGuestService.Echec.Aucun)
                {
                    _logger.LogWarning("Classement : impossible de rejoindre ({Raison}).", echec);
                    Fermer();
                    return;
                }
                await AttendreLeJeuPuisSEffacerAsync("direct rejoint").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Classement : le direct n'a pas pu etre rejoint.");
                Fermer();
            }
        });
    }

    /// <summary>
    /// Ferme le menu de jeu d'ES : ES au premier plan, puis « retour » par le controleur ES
    /// d'APIExpose. Sans cela `/launch` attend : ES ne fait avancer que le sommet de sa pile
    /// d'interface, et la transition de lancement reste figee sous le menu.
    ///
    /// « Retour » est envoye SANS CONDITION. Mesurer la duree de l'appui pour savoir si le menu
    /// s'est ouvert s'est revele faux a l'essai : ES cumule le temps de SES images (une image
    /// lente compte pour beaucoup) et franchit ses 1000 ms avant notre horloge. Or c'est sans
    /// risque pour le lancement : `/launch` part de n'importe quelle vue d'ES, la vue restant
    /// au bas de la pile ; un « retour » de trop fait seulement remonter ES d'un cran.
    /// </summary>
    private async Task FermerLeMenuEsAsync()
    {
        EmulatorForeground.FocusEmulationStation();
        await Task.Delay(150).ConfigureAwait(false);
        var resultat = await _es.TapAsync(new EsControllerTapRequest { Input = "back" }, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Classement : « retour » envoye a ES avant le lancement ({Resultat} ; appui mesure {Mesure}).",
            resultat.Success ? "ok" : resultat.Reason, _menuEsConnu ? (_menuEsOuvert ? ">= 1000 ms" : "< 1000 ms") : "inconnu");
        _menuEsOuvert = false;
        // Laisser ES retirer le menu de sa pile avant de poster le lancement.
        await Task.Delay(300).ConfigureAwait(false);
    }

    /// <summary>
    /// Arme la seance de scoring pour un contest : code joueur du compte lie, monde (« stream »
    /// pour un contest de streamer, « station » pour une manche de salle), chaine, contest. Sans
    /// code joueur ou sans cle certifiee, on ne pretend rien : le defi se joue, non attribue.
    /// </summary>
    private void ArmerLaSeanceDuContest(LeaderboardSocialClient.Contest contest)
    {
        var code = _agent.Status.PlayerCode;
        if (string.IsNullOrWhiteSpace(code) || contest.CleCertifiee.Length == 0)
        {
            _logger.LogWarning("Classement : contest {Contest} non attribuable (code joueur {Code}, cle certifiee {Cle}).",
                contest.Id, string.IsNullOrWhiteSpace(code) ? "absent" : "present", contest.CleCertifiee.Length == 0 ? "absente" : "presente");
            return;
        }
        var actuelle = _session.Get();
        var monde = contest.Genre == "stream" ? "stream" : "station";
        _session.Set(code, actuelle?.VenueName, actuelle?.VenueCity, monde,
            contest.Genre == "stream" ? contest.Hote : actuelle?.Channel, contest.CleCertifiee, Langue());
        _seanceArmeeParLeDefi = true;
        _logger.LogInformation("Classement : seance armee pour le contest {Contest} ({Monde}).", contest.CleCertifiee, monde);
    }

    /// <summary>
    /// Diffuse le defi comme un direct : annonce sur la plateforme (route agent), puis
    /// hebergement netplay, le chemin du site quand on coche « Diffuser en live ». Rend faux si
    /// la diffusion n'a pas pu se faire : le defi se lance alors normalement.
    /// </summary>
    private async Task<bool> DiffuserLeDefiAsync(string chemin, string politique)
    {
        var join = politique is "followed" or "everyone" ? politique : "none";
        try
        {
            var credential = _machine.GetCredential();
            if (string.IsNullOrEmpty(credential)) return false;
            using var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
            using var corps = new StringContent(
                JsonSerializer.Serialize(new { game = _romGroup, system = _systemeAffiche, join }),
                System.Text.Encoding.UTF8, "application/json");
            using var reponse = await client.PostAsync("/api/v1/agent/live/announce", corps).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Classement : direct du defi non annonce ({Statut}), defi joue sans diffusion.", (int) reponse.StatusCode);
                return false;
            }
            _directAnnonceParLeDefi = true;
            var echec = await _hote.HebergerAsync(chemin, _agent.Status.Pseudo ?? "", join != "none").ConfigureAwait(false);
            if (echec != RetroBat.Api.Netplay.NetplayHostService.Echec.Aucun)
            {
                _logger.LogWarning("Classement : hebergement du defi impossible ({Raison}), defi joue sans diffusion.", echec);
                await RetirerLeDirectAsync().ConfigureAwait(false);
                return false;
            }
            _logger.LogInformation("Classement : defi diffuse en direct (qui peut rejoindre : {Politique}).", join);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Classement : diffusion du defi impossible.");
            return false;
        }
    }

    private async Task RetirerLeDirectAsync()
    {
        if (!_directAnnonceParLeDefi) return;
        _directAnnonceParLeDefi = false;
        try
        {
            var credential = _machine.GetCredential();
            if (string.IsNullOrEmpty(credential)) return;
            using var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
            using var vide = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var _ = await client.PostAsync("/api/v1/agent/live/withdraw", vide).ConfigureAwait(false);
            _logger.LogInformation("Classement : direct du defi retire.");
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Classement : retrait du direct impossible."); }
    }

    /// <summary>Suivre ou ne plus suivre le joueur de la ligne choisie.</summary>
    private void BasculerLeSuivi()
    {
        var ligne = LignesDeLaVue().ElementAtOrDefault(_modele.Ligne);
        if (ligne?.Poignee is not { Length: > 0 } poignee)
        {
            _logger.LogDebug("Classement : cette ligne n'a pas de joueur a suivre.");
            return;
        }
        _ = Task.Run(async () =>
        {
            var suivi = await _social.BasculerLeSuiviAsync(poignee).ConfigureAwait(false);
            _overlay.PoserLesSuivis(_social.Suivis);
            _logger.LogInformation("Classement : {Joueur} {Etat}.", ligne.Joueur, suivi ? "suivi" : "plus suivi");
            Rafraichir();
        });
    }

    private void Agir()
    {
        if (_modele.VueCourante == LeaderboardPanelModel.Vue.LiveEtContest)
        {
            Rejoindre(regarder: false);
            return;
        }
        var ligne = LignesDeLaVue().ElementAtOrDefault(_modele.Ligne);
        if (ligne?.ReplayId is not { Length: > 0 } replay)
        {
            return;
        }
        // Le replay se lance par le moteur, jamais par une ligne de commande recomposee : c'est
        // lui qui verifie l'identite de la ROM et qui va chercher l'objet s'il manque.
        _logger.LogInformation("Classement : lecture du replay {Replay} demandee.", replay);
        // La boite « WORKING… » d'ES, le temps que l'emulateur demarre : sans elle, le panneau
        // disparait et rien ne se passe pendant plusieurs secondes, ce qui se lit comme une panne.
        _overlay.Attendre(_textes.Text("leaderboard.launching", Langue()));
        _replayLance = replay;
        _ = Task.Run(async () =>
        {
            try
            {
                await OuvrirLaSeanceDuSpectateurAsync(replay).ConfigureAwait(false);
                await _playback.PlayAsync(replay, CancellationToken.None).ConfigureAwait(false);
                // Le filet : si le lecteur n'a rien lance en 45 s, la boite ne reste pas, et
                // ES reprend la main (il n'y a pas d'emulateur a qui la disputer).
                await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                if (_overlay.EnAttente && !EmulatorForeground.EmulateurTourne())
                {
                    _replayLance = "";
                    Fermer();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Classement : le replay {Replay} n'a pas demarre.", replay);
                _replayLance = "";
                Fermer();
            }
        });
    }

    /// <summary>
    /// LE JEU EST LA, LA BOITE S'EN VA. La boite « Lancement de la partie » ne se fermait que sur
    /// `ui.game.started` ; son filet, 45 s plus tard, ne la fermait que si AUCUN emulateur ne
    /// tournait. Un jeu lance sans que l'evenement arrive laissait donc la boite au premier plan,
    /// par-dessus la partie, pour toujours (signale le 2026-09-30 sur un defi). On guette
    /// l'emulateur : des qu'il est la, la boite s'efface, evenement ou pas. Sans emulateur au bout
    /// de 60 s, on ferme tout, comme avant.
    /// </summary>
    private async Task AttendreLeJeuPuisSEffacerAsync(string quoi)
    {
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < limite && _overlay.EnAttente)
        {
            if (EmulatorForeground.EmulateurTourne())
            {
                // La fenetre suit son processus, et `ui.game.started` a pu fermer entre-temps.
                await Task.Delay(1500).ConfigureAwait(false);
                if (_overlay.EnAttente)
                {
                    _modele.Fermer();
                    _overlay.FermerPourLeJeu();
                    _logger.LogInformation("Classement : {Quoi}, le jeu est la sans « partie commencee » d'ES : la boite d'attente s'efface.", quoi);
                }
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }

        if (_overlay.EnAttente)
        {
            _logger.LogWarning("Classement : {Quoi}, aucun jeu apres 60 s : la boite d'attente se ferme.", quoi);
            Fermer();
        }
    }

    /// <summary>La boite d'attente reste jusqu'a ce que l'emulateur soit la, puis on s'efface.</summary>
    private async Task SEffacerQuandLEmulateurEstLaAsync()
    {
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < limite && !EmulatorForeground.EmulateurTourne())
        {
            await Task.Delay(250).ConfigureAwait(false);
        }
        // Un instant de plus : la fenetre de l'emulateur suit son processus.
        await Task.Delay(1200).ConfigureAwait(false);
        _modele.Fermer();
        _overlay.FermerPourLeJeu();
        _logger.LogInformation("Classement : replay a l'ecran, le panneau s'efface.");
    }

    /// <summary>Apres la lecture, EmulationStation reprend la main : son menu est reste ouvert.</summary>
    private async Task RendreEsApresLeReplayAsync()
    {
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < limite && EmulatorForeground.EmulateurTourne())
        {
            await Task.Delay(300).ConfigureAwait(false);
        }
        _modele.Fermer();
        _overlay.FermerPourLeJeu();
        for (var essai = 0; essai < 4; essai++)
        {
            if (EmulatorForeground.FocusEmulationStation()) { _logger.LogInformation("Classement : replay termine, EmulationStation reprend la main."); return; }
            await Task.Delay(400).ConfigureAwait(false);
        }
        _logger.LogWarning("Classement : replay termine, EmulationStation n'a pas repris le premier plan.");
    }

    /// <summary>
    /// Le spectateur d'un replay lance ICI, c'est le compte lie a la borne. La plateforme lui
    /// emet un jeton opaque (comme pour un direct) ; sans compte lie, la seance s'ouvre sans
    /// spectateur et les reactions ne sont pas retenues, ce qui est la regle.
    /// </summary>
    private async Task OuvrirLaSeanceDuSpectateurAsync(string replayId)
    {
        string? jeton = null;
        try
        {
            var credential = _machine.GetCredential();
            if (!string.IsNullOrEmpty(credential))
            {
                var client = _httpFactory.CreateClient();
                client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
                client.Timeout = TimeSpan.FromSeconds(6);
                client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);
                using var reponse = await client.GetAsync($"/api/v1/agent/replay/{Uri.EscapeDataString(replayId)}/viewer").ConfigureAwait(false);
                if (reponse.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("viewer", out var v) && v.ValueKind == JsonValueKind.String) jeton = v.GetString();
                }
                else
                {
                    _logger.LogInformation("Classement : pas de spectateur pour ce replay ({Statut}) ; les reactions ne seront pas retenues.", (int) reponse.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Classement : jeton de spectateur indisponible.");
        }
        _viewer.Open(jeton);
    }

    private IReadOnlyList<LeaderboardClient.Ligne> LignesDeLaVue()
    {
        lock (_gate)
        {
            if (_modele.VueCourante == LeaderboardPanelModel.Vue.LiveEtContest) return Array.Empty<LeaderboardClient.Ligne>();
            if (_modele.VueCourante == LeaderboardPanelModel.Vue.MesRecords) return _mesParties;
            if (RegleDeLaVue(_modele.VueCourante) is not null)
            {
                return _autresMondes.TryGetValue(_modele.VueCourante, out var autre) ? autre.Lignes : Array.Empty<LeaderboardClient.Ligne>();
            }
            return LeaderboardClient.Tailler(_monde, _modele.VueCourante, _maVille, _monPays, _maSalle);
        }
    }

    private void Rafraichir() => _overlay.Montrer(Composer());

    private LeaderboardOverlayService.Contenu Composer()
    {
        var lignes = LignesDeLaVue();
        var surLeLive = _modele.VueCourante == LeaderboardPanelModel.Vue.LiveEtContest;
        var evenements = surLeLive ? Evenements(Langue()) : null;
        _modele.PoserLesLignes(surLeLive ? evenements!.Count : lignes.Count);
        string etat;
        IReadOnlyDictionary<string, int> rangsPrecedents;
        IReadOnlyList<LeaderboardClient.Ligne> lignesDeMaPlace;
        lock (_gate)
        {
            var vue = _modele.VueCourante;
            var mesRecords = vue == LeaderboardPanelModel.Vue.MesRecords;
            // MES RECORDS n'attend que le chargement : ses lignes sont sur le disque, et une liste
            // vide doit le DIRE plutot que laisser un panneau muet.
            var etatDeLaVue = mesRecords ? (_mesParties.Count > 0 ? LeaderboardClient.EtatOk : _etatDuMonde.Length == 0 ? "" : LeaderboardClient.EtatAucunScore)
                : RegleDeLaVue(vue) is null ? _etatDuMonde
                : _autresMondes.TryGetValue(vue, out var autre) ? autre.Etat : "";
            etat = etatDeLaVue.Length == 0
                ? ""
                : lignes.Count > 0 ? LeaderboardClient.EtatOk : etatDeLaVue;
            // Pas de fleches de mouvement sur ses propres parties : leur rang est leur ordre.
            rangsPrecedents = mesRecords ? new Dictionary<string, int>()
                : RegleDeLaVue(vue) is null ? _rangsPrecedents
                : _rangsDesAutres.TryGetValue(vue, out var rangs) ? rangs : new Dictionary<string, int>();
            // « Ma place » reste celle du MONDE sur MES RECORDS : la place parmi ses propres
            // parties ne dirait rien.
            lignesDeMaPlace = mesRecords ? _monde : lignes;
        }
        var langue = Langue();
        var chezNous = _modele.Etat == LeaderboardPanelModel.Foyer.Panneau;
        var ligne = lignes.Count > 0 ? lignes[Math.Clamp(_modele.Ligne, 0, lignes.Count - 1)] : null;
        return new LeaderboardOverlayService.Contenu(
            _jeuAffiche,
            _modele.Onglets.Select(v => RegleDeLaVue(v) is null ? _textes.Text(Cle(v), langue) : "").ToList(),
            _modele.IndexOnglet,
            lignes,
            _modele.Ligne,
            Message(etat, langue),
            _modele.SurLaPorte,
            chezNous,
            Aides(chezNous, langue),
            ActionsDeLaLigne(ligne, langue),
            // ES montre deja le logo du jeu en tete de son menu : notre titre dit ce que NOUS
            // sommes, comme « SCRAPER » ou « CONTENT DOWNLOADER » en tete des menus d'ES.
            _textes.Text("leaderboard.help.open", langue),
            _textes.Text("leaderboard.help.replay", langue),
            _textes.Text("leaderboard.group.podium", langue),
            _textes.Text("leaderboard.group.ranking", langue),
            new[]
            {
                _textes.Text("leaderboard.podium.first", langue),
                _textes.Text("leaderboard.podium.second", langue),
                _textes.Text("leaderboard.podium.third", langue),
            },
            _textes.Text("leaderboard.challenge", langue),
            MaPlace(lignesDeMaPlace, langue),
            _textes.Text("leaderboard.follow", langue),
            _textes.Text("leaderboard.following", langue),
            ligne?.Poignee is { Length: > 0 } p && _social.Suit(p),
            Glyphe(SlotDeLIdentite("y")),
            Glyphe(SlotDeLIdentite("x")),
            evenements,
            _textes.Text("leaderboard.join", langue),
            Glyphe(SlotValider),
            ReplaysEnPreparation: _replaysEnPreparation,
            RangsPrecedents: rangsPrecedents,
            // LE PSEUDO VIENT DU COMPTE APPAIRE, PAS DU CLASSEMENT.
            //
            // Premiere version : je le prenais sur NOTRE ligne du classement. Il ne s'affichait
            // donc que si le joueur figurait deja dans le tableau -- c'est-a-dire jamais, au
            // moment ou on en a le plus besoin. Le contexte porte le pseudo du compte depuis
            // l'appairage, quel que soit le classement.
            Pseudo: _context.PlayerPseudo ?? "",
            DefierMode: LibelleDuMode(ModeCourant(), _reglesDuJeu),
            // Ses propres parties ne forment pas un podium (decision user 2026-10-03).
            SansPodium: _modele.VueCourante == LeaderboardPanelModel.Vue.MesRecords,
            Pastilles: _modele.Onglets.Select(PastilleDeLOnglet).ToList());
    }

    /// <summary>
    /// UN ONGLET PAR AUTRE REGLE DU JEU (2026-10-02), juste apres « Monde » : 1CC MULTI, puis 1LC
    /// quand la regle principale est le 1CC. Pur : il se teste sans panneau.
    /// </summary>
    internal static IReadOnlyList<(LeaderboardPanelModel.Vue Vue, string Regle)> OngletsDeRegle(IReadOnlyList<string> regles, string principale)
    {
        var onglets = new List<(LeaderboardPanelModel.Vue, string)>();
        foreach (var vue in new[] { LeaderboardPanelModel.Vue.MondeMulti, LeaderboardPanelModel.Vue.Monde1lc })
        {
            var regle = RegleDeLaVue(vue)!;
            if (regles.Contains(regle, StringComparer.OrdinalIgnoreCase) && !string.Equals(regle, principale, StringComparison.OrdinalIgnoreCase))
            {
                onglets.Add((vue, regle));
            }
        }
        return onglets;
    }

    /// <summary>La regle d'un onglet de regle ; null pour les onglets ordinaires.</summary>
    internal static string? RegleDeLaVue(LeaderboardPanelModel.Vue vue) => vue switch
    {
        LeaderboardPanelModel.Vue.MondeMulti => "1cc-multi",
        LeaderboardPanelModel.Vue.Monde1lc => "1lc",
        _ => null,
    };

    /// <summary>Le code d'une regle tel qu'on l'ecrit partout : « 1CC MULTI ». Jamais traduit.</summary>
    internal static string LibelleDeRegle(string regle) => regle.Replace('-', ' ').ToUpperInvariant();

    /// <summary>
    /// La pastille d'un onglet : la regle, dans un petit cadre arrondi (demande user 2026-10-02).
    /// « Monde » porte celle de la regle principale, seulement quand un onglet d'une autre regle
    /// est affiche ; sinon le panneau garde ses onglets d'avant.
    /// </summary>
    private string PastilleDeLOnglet(LeaderboardPanelModel.Vue vue)
    {
        if (RegleDeLaVue(vue) is { } regle) return LibelleDeRegle(regle);
        if (vue == LeaderboardPanelModel.Vue.Monde && _reglePrincipale.Length > 0
            && _modele.Onglets.Any(v => RegleDeLaVue(v) is not null)) return LibelleDeRegle(_reglePrincipale);
        return "";
    }

    /// <summary>
    /// Les actions possibles sur une ligne, avec le VRAI bouton de cette borne. Le replay ne se
    /// propose que s'il existe et qu'il est public : c'est la plateforme qui l'attache.
    /// </summary>
    private IReadOnlyList<LeaderboardOverlayService.Aide> ActionsDeLaLigne(LeaderboardClient.Ligne? ligne, string langue)
    {
        if (ligne?.ReplayId is not { Length: > 0 }) return Array.Empty<LeaderboardOverlayService.Aide>();
        return new[] { new LeaderboardOverlayService.Aide(Glyphe(SlotValider), _textes.Text("leaderboard.help.replay", langue)) };
    }

    /// <summary>
    /// Le glyphe d'ES du bouton qui occupe un slot de CETTE borne. La cartographie mesuree par
    /// LedManagerSetup fait foi : sur la borne de reference, le slot 3 est le bouton X - un
    /// « Y » code en dur aurait montre un bouton qui n'existe pas la ou le joueur regarde.
    /// </summary>
    private IReadOnlyDictionary<string, string>? _carto;

    /// <summary>
    /// Le slot qui porte cette identite sur CETTE borne, ou -1. La cartographie mesuree par
    /// LedManagerSetup fait foi : coder « Y = slot 3 » en dur donnerait la mauvaise touche
    /// d'une borne a l'autre (ici, « y » est le slot 4 et « x » le slot 3).
    /// </summary>
    private int SlotDeLIdentite(string identite)
    {
        try
        {
            _carto ??= CabinetCartographyStore.Read(1);
            foreach (var (slot, id) in _carto)
            {
                if (string.Equals(id, identite, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(slot, out var n))
                {
                    return n;
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Classement : cartographie illisible."); }
        return -1;
    }

    private string Glyphe(int slot)
    {
        string identite;
        try
        {
            // La cartographie est relue une seule fois : `Read` analyse appsettings.json en
            // entier, et cette methode est appelee a chaque composition du panneau.
            _carto ??= CabinetCartographyStore.Read(1);
            identite = _carto.TryGetValue(slot.ToString(), out var id) ? id.ToLowerInvariant() : "";
        }
        catch (Exception) { identite = ""; }
        if (identite.Length == 0)
        {
            identite = slot switch { 1 => "b", 2 => "a", 3 => "y", 4 => "x", 5 => "l", 6 => "r", _ => "" };
        }
        // Le pictogramme que la barre d'aide d'ES montre pour cette identite (sous-ensemble
        // d'aide actif du theme) : le joueur reconnait la meme touche en bas de l'ecran.
        var chemin = _style?.HelpIcon(identite) ?? "";
        return chemin.Length > 0 ? chemin : "button_" + (identite.Length > 0 ? identite : "1");
    }

    private EsMenuStyle? _style;

    /// <summary>Le jeu que la façade montre en ce moment (systeme, nom).</summary>
    private (string Systeme, string Jeu) JeuSelectionne()
    {
        try
        {
            var jeu = _context.Ui.Selected;
            return (jeu?.SystemId ?? "", jeu?.GameName ?? "");
        }
        catch (Exception) { return ("", ""); }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _clesConnues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// La cle du classement pour le jeu choisi. Elle est calculee A L'AVANCE, des que la
    /// selection change : le resolveur du wrapper charge l'index d'alias du systeme (des
    /// dizaines de milliers d'entrees) et le faire a l'ouverture coutait plus de trois
    /// secondes, pendant lesquelles le joueur ne voyait rien.
    /// </summary>
    private string CleDuJeu(string cheminDuJeu, string systeme, string nomDuJeu)
    {
        var memoire = systeme + "|" + (cheminDuJeu.Length > 0 ? cheminDuJeu : nomDuJeu);
        if (_clesConnues.TryGetValue(memoire, out var deja)) return deja;
        var cle = CalculerLaCle(cheminDuJeu, systeme, nomDuJeu);
        _clesConnues[memoire] = cle;
        return cle;
    }

    /// <summary>
    /// L'INDEX DES ALIAS DE CHAQUE SYSTEME DE LA COLLECTION, CHARGE AU DEMARRAGE (2026-10-03). La
    /// cle d'un jeu passe par l'alias.json de son systeme ; le premier jeu d'un systeme payait son
    /// chargement au moment de l'appui long (1,6 s pour Sonic sur Mega Drive). On le paie ici, en
    /// arriere-plan, une fois par systeme ouvert au scoring.
    /// </summary>
    private void PrechaufferLesSystemes()
    {
        foreach (var systeme in _collection?.SystemesOuverts() ?? Array.Empty<string>())
        {
            try { _ = _wrapper.ResolveDefinitionFor("__prechauffage__", systeme); }
            catch (Exception ex) { _logger.LogDebug(ex, "Classement : index des alias de {Systeme} non prechauffe.", systeme); }
        }
    }

    /// <summary>Prepare la cle du jeu choisi, en arriere-plan, sans bloquer personne.</summary>
    private void PreparerLaCle()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var jeu = _context.Ui.Selected;
                if (jeu is null) return;
                var cle = CleDuJeu(jeu.GamePath ?? "", jeu.SystemId ?? "", jeu.GameName ?? "");
                _ = PrechargerAsync(jeu.GamePath ?? "", cle);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Classement : cle du jeu non preparee."); }
        });
    }

    private int _prechargement;

    /// <summary>
    /// LE CLASSEMENT EST LA AVANT L'APPUI LONG. On s'arrete sur un jeu ouvert au scoring : apres
    /// 400 ms sans changer de jeu, on demande son classement ; le client le garde 30 s, et le
    /// panneau s'ouvre avec ses lignes au lieu d'attendre le site (120 a 210 ms mesures le
    /// 2026-09-30, bien plus a l'heure d'un pic). Seulement les jeux de la collection World
    /// Scoring : ceux-la seuls ont un panneau.
    /// </summary>
    private async Task PrechargerAsync(string cheminDuJeu, string cle)
    {
        try
        {
            if (cle.Length == 0 || !_options.CurrentValue.Leaderboard.Enabled) return;
            if (_collection?.EstOuvertAuScoring(cheminDuJeu) != true) return;
            var jeton = Interlocked.Increment(ref _prechargement);
            await Task.Delay(400).ConfigureAwait(false);
            if (jeton != Volatile.Read(ref _prechargement)) return;   // le joueur a continue de defiler
            if (_modele.Etat != LeaderboardPanelModel.Foyer.Ferme) return;
            await _client.MondeAsync(cle, _agent.Status.Pseudo ?? "", CancellationToken.None,
                LeaderboardClient.ReglePrincipale(_collection?.ReglesOuvertes(cle) ?? Array.Empty<string>())).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Classement : prechargement abandonne.");
        }
    }

    private string CalculerLaCle(string cheminDuJeu, string systeme, string nomDuJeu)
    {
        try
        {
            var brut = Path.GetFileNameWithoutExtension(cheminDuJeu.Length > 0 ? cheminDuJeu : nomDuJeu);
            if (brut.Length > 0 && systeme.Length > 0)
            {
                var def = _wrapper.ResolveDefinitionFor(brut, systeme);
                if ((def.AliasMatched || def.DefinitionExists) && !string.IsNullOrWhiteSpace(def.Rom))
                {
                    return def.Rom.Trim().ToLowerInvariant();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Classement : resolveur du wrapper indisponible pour {Jeu}.", nomDuJeu);
        }
        return GamelistIdentity.Slugifier(nomDuJeu);
    }

    private string _langue = "";

    /// <summary>
    /// La langue de l'interface, celle qu'APIExpose applique deja a ses notifications. Elle est
    /// lue une fois : `GetScrapingSettings` analyse es_settings.cfg, et le panneau la demande a
    /// chaque composition.
    /// </summary>
    private string Langue()
    {
        if (_langue.Length > 0) return _langue;
        try { _langue = _reglages.GetScrapingSettings().Language; }
        catch (Exception) { _langue = "en"; }
        return _langue;
    }

    /// <summary>La cle de traduction d'une vue (voir resources/locales/interface-texts.json).</summary>
    public static string Cle(LeaderboardPanelModel.Vue vue) => vue switch
    {
        LeaderboardPanelModel.Vue.MesRecords => "leaderboard.tab.my_records",
        LeaderboardPanelModel.Vue.CetteBorne => "leaderboard.tab.this_cabinet",
        LeaderboardPanelModel.Vue.MaSalle => "leaderboard.tab.my_venue",
        LeaderboardPanelModel.Vue.MaVille => "leaderboard.tab.my_city",
        LeaderboardPanelModel.Vue.MonPays => "leaderboard.tab.my_country",
        LeaderboardPanelModel.Vue.LiveEtContest => "leaderboard.tab.live",
        _ => "leaderboard.tab.world",
    };

    /// <summary>
    /// Ma place dans la vue affichee, et ce qu'il manque pour gagner un rang. Tout se calcule
    /// sur le classement DEJA CHARGE : pas un appel de plus pour une information de coin d'ecran.
    /// </summary>
    private string MaPlace(IReadOnlyList<LeaderboardClient.Ligne> lignes, string langue)
    {
        if (lignes.Count == 0) return "";
        var moi = lignes.FirstOrDefault(l => l.CestMoi);
        if (moi is null)
        {
            return _textes.Format("leaderboard.unranked", langue, ("total", lignes.Count));
        }
        var place = _textes.Format("leaderboard.my_place", langue, ("rank", moi.Rang), ("total", lignes.Count));
        var devant = lignes.FirstOrDefault(l => l.Rang == moi.Rang - 1);
        if (devant is null) return place;
        // Pour un score, il manque des points ; pour un temps, il faut en retirer.
        var ecart = moi.PlusBasEstMieux ? moi.Valeur - devant.Valeur : devant.Valeur - moi.Valeur;
        if (ecart <= 0) return place;
        var manque = (moi.PlusBasEstMieux ? "-" : "") + ecart.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        return place + "   " + (moi.PlusBasEstMieux
            ? _textes.Format("leaderboard.to_gain_time", langue, ("points", manque), ("rank", moi.Rang - 1))
            : _textes.Format("leaderboard.to_gain", langue, ("points", manque), ("rank", moi.Rang - 1)));
    }

    private string Message(string etat, string langue) => etat switch
    {
        "" => _textes.Text("leaderboard.loading", langue),
        LeaderboardClient.EtatHorsLigne => _textes.Text("leaderboard.offline", langue),
        LeaderboardClient.EtatOk => "",
        _ => _textes.Text("leaderboard.empty", langue),
    };

    /// <summary>
    /// La ligne d'aide, avec les glyphes d'ES : elle dit ce que la manette fait ICI. Les
    /// actions d'une ligne ne s'y repetent pas : elles s'affichent SUR la ligne choisie.
    /// </summary>
    private IReadOnlyList<LeaderboardOverlayService.Aide> Aides(bool chezNous, string langue)
    {
        if (!chezNous)
        {
            return new[] { new LeaderboardOverlayService.Aide("dpad_left", _textes.Text("leaderboard.help.open", langue)) };
        }
        return new LeaderboardOverlayService.Aide[]
        {
            new("dpad_updown", _textes.Text("leaderboard.help.choose", langue)),
            new("dpad_leftright", _textes.Text("leaderboard.help.view", langue)),
            new(Glyphe(SlotAnnuler), _textes.Text("leaderboard.help.back", langue)),
        };
    }

    private static (string? Identite, string? Systeme, int? Slot) LireBouton(object? payload)
    {
        if (payload is null) return (null, null, null);
        try
        {
            var el = JsonSerializer.SerializeToElement(payload);
            string? Texte(string nom) => el.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            // `TryGetInt32` LEVE une exception quand l'element n'est pas un nombre - et le slot
            // d'une DIRECTION est nul par nature. Sans ce controle de nature, l'exception faisait
            // retomber l'identite et le systeme a nul du meme coup, et le panneau restait sourd a
            // gauche/droite tout en repondant aux boutons.
            int? Nombre(string nom) => el.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var n) ? n : null;
            return (Texte("Identity"), Texte("System"), Nombre("Slot"));
        }
        catch
        {
            return (null, null, null);
        }
    }

    public void Dispose()
    {
        _arret?.Dispose();
        _abonnement?.Dispose();
    }
}
