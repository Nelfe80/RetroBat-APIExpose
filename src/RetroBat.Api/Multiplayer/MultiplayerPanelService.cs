using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Ce que MULTIPLAYER montre dans ES (CDC MULTIPLAYER v2.5, lots M3 et M5), et ce que la manette y fait :
///
///   - le PANNEAU, ouvert par l'appui long sur un jeu de la collection (V24-01, V24-03) : le joueur est
///     place dans une gameroom tout de suite, deja pret, et voit qui l'attend ;
///   - la PRESENTATION, « Les joueurs se synchronisent… », du decompte jusqu'a l'arrivee du jeu ;
///   - le RESULTAT, apres la partie : les joueurs, la duree, « Rejouer » (A maintenu), retour a ES en 12 s ;
///   - en haut d'ES, le rappel de « Votre selection » (panneau ferme, le joueur attend toujours) et le
///     toast d'une invitation (A maintenu pour rejoindre, B pour refuser).
///
/// La fenetre, le focus et l'habillage sont ceux du panneau de classement
/// (<see cref="LeaderboardOverlayService"/>) ; l'appui long et la lecture de la manette restent a
/// <see cref="LeaderboardInputService"/>, qui transmet ici les gestes tant que MULTIPLAYER est a l'ecran.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class MultiplayerPanelService
{
    /// <summary>Les couleurs d'avatar, tirees de la poignee : la meme pour un joueur sur toutes les bornes.</summary>
    private static readonly string[] Couleurs = ["A98BFF", "78E5A3", "FF9F6B", "49E3CB", "FFD37A", "FF8C9A"];

    /// <summary>
    /// Le resultat ne se ferme pas d'un appui fait juste avant qu'il paraisse : en fin de rencontre, les joueurs appuient
    /// encore sur leurs boutons.
    /// </summary>
    private static readonly TimeSpan GardeDuResultat = TimeSpan.FromSeconds(1.5);

    /// <summary>Le noir qui monte sur le jeu avant qu'il se ferme (retour du user du 23/10).</summary>
    private static readonly TimeSpan DureeDuFondu = TimeSpan.FromMilliseconds(600);

    /// <summary>Le temps laisse a RetroArch pour disparaitre avant que le resultat prenne la manette.</summary>
    private static readonly TimeSpan PatienceDeLaSortie = TimeSpan.FromSeconds(20);

    private enum Mode
    {
        Ferme,
        Panneau,
        Presentation,
        Resultat,
    }

    private readonly MultiplayerSessionService _session;
    private readonly LeaderboardOverlayService _overlay;
    private readonly InterfaceTextService _textes;
    private readonly EmulationStationSettingsService _reglages;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<MultiplayerPanelService> _journal;
    private readonly MultiplayerToastOverlay? _cartes;
    private readonly IDisposable? _sortieDEs;
    private string? _message;
    private DateTime _messageJusqua;
    private readonly ApiContext? _contexte;
    private readonly MediaRuntimeState? _etatDesMedias;
    private readonly MultiplayerPanelModel _modele = new();
    private readonly object _verrou = new();
    private readonly Dictionary<string, MultiplayerRoom> _connues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invitationsVues = new(StringComparer.Ordinal);
    private Mode _mode = Mode.Ferme;
    private MultiplayerInstalledGame? _jeu;
    private readonly RetroBat.Api.Avatar.AvatarSheetStore? _planches;
    private readonly RetroBat.Api.Replay.Sharing.ReplayRelayService? _relaisDesPlanches;
    private readonly RetroAchievementsService? _retroAchievements;

    /// <summary>Les planches et les portraits deja demandes : une demande par fichier manquant, pas une par dessin.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _demandes = new(StringComparer.Ordinal);
    private IReadOnlyList<MultiplayerAmi> _amis = [];
    private readonly SemaphoreSlim _telechargementsDePortraits = new(2, 2);

    private static string DossierDuCatalogue => Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "resources", "multiplayer");

    /// <summary>Les statistiques que cette borne a vues pendant la rencontre : l'ecran de fin s'en sert sans celles du site.</summary>
    private IReadOnlyDictionary<int, StatsDeLaPlace>? _statsVues;

    /// <summary>Les statistiques du profil du jeu (lot 2) que cette borne a mesurees : l'ecran de fin s'en sert sans celles du site.</summary>
    private MultiplayerStatsResultat? _mesuresVues;

    /// <summary>
    /// Les rangs d'une fin a plusieurs lus sur cette borne (CDC Classements v1.0 §7) : l'ecran de fin les montre tant que le
    /// site n'a pas les siens.
    /// </summary>
    private IReadOnlyList<MultiplayerRang>? _rangsVus;

    /// <summary>Les statistiques d'une rencontre finie : celles de la plateforme (l'hote), sinon celles vues ici.</summary>
    private IReadOnlyDictionary<string, MultiplayerStatsPlace>? StatsDeLaRencontre(MultiplayerRoom? room)
        => room?.Ended?.Stats is { Count: > 0 } envoyees ? envoyees
            : _statsVues is { Count: > 0 } vues
                ? vues.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture),
                    p => new MultiplayerStatsPlace { Hits = p.Value.Coups, Kos = p.Value.KO, Perfects = p.Value.Perfects, Damage = p.Value.Degats })
                : null;
    private string? _refus;
    private DateTime? _validerDepuis;
    private DateTime? _annulerDepuis;
    private string _glypheValider = "button_a";
    private string _glypheAnnuler = "button_b";
    private string _glypheX = "button_x";
    private string _langue = "";

    /// <summary>Un passage d'une gameroom a l'autre a la fois (FOR PLAYERS… et FOR FRIENDS, 28/10).</summary>
    private readonly SemaphoreSlim _changementDeSalle = new(1, 1);

    /// <summary>
    /// Une rencontre MULTIPLAYER classee (ELO) tourne sur cette borne (pose par MultiplayerLaunchService) : une invitation
    /// recue n'y parait pas, elle attend le retour dans ES (decision du user du 28/10).
    /// </summary>
    public Func<bool>? RencontreClasseeEnCours { get; set; }

    private readonly IServiceProvider? _services;
    private string _roomDuResultat = "";
    private int? _dureeDuResultat;
    private DateTime _resultatDepuis;

    /// <summary>Le resultat a pris la manette : s'il la perd, il se ferme (voir RendreLaMain).</summary>
    private bool _resultatALaMain;
    private CancellationTokenSource? _retour;
    private string _texteDuBandeau = "";
    private MultiplayerInvitation? _invitation;
    private DateTime _presenteeDepuis;

    /// <summary>
    /// La presentation ne dure pas plus : au-dela, la partie ne s'est pas ouverte, le joueur retrouve ES. Le meme delai que
    /// la plateforme laisse a l'hote pour publier sa session (3 min depuis le 25/10 : 60 s pour que le jeu soit pret).
    /// </summary>
    private static readonly TimeSpan PresentationMax = TimeSpan.FromMinutes(3);

    public MultiplayerPanelService(
        MultiplayerSessionService session,
        LeaderboardOverlayService overlay,
        InterfaceTextService textes,
        EmulationStationSettingsService reglages,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<MultiplayerPanelService> journal,
        MultiplayerToastOverlay? cartes = null,
        ApiContext? contexte = null,
        MediaRuntimeState? etatDesMedias = null,
        RetroBat.Api.Avatar.AvatarSheetStore? planches = null,
        RetroBat.Api.Replay.Sharing.ReplayRelayService? relais = null,
        RetroAchievementsService? retroAchievements = null,
        RetroBat.Domain.Interfaces.IEventBus? bus = null,
        RetroBat.Domain.Interfaces.IEmulationStationNotificationService? notifications = null,
        IServiceProvider? services = null)
    {
        _services = services;
        _notifications = notifications;
        _planches = planches;
        _relaisDesPlanches = relais;
        _retroAchievements = retroAchievements;
        _session = session;
        _overlay = overlay;
        _textes = textes;
        _reglages = reglages;
        _options = options;
        _journal = journal;
        _cartes = cartes;
        _contexte = contexte;
        _etatDesMedias = etatDesMedias;
        _session.Change += SurChangement;
        _session.DansLaCollection = () => DansLaCollection && !EnJeu();
        // En partie pour le site jusqu'a la fermeture de l'ecran de fin : l'echeance d'une invitation attend le retour (30/10).
        _session.EnPartie = () =>
        {
            lock (_verrou)
            {
                return EnJeu() || _mode == Mode.Resultat;
            }
        };
        _session.Propositions += SurPropositions;
        // ES FERME, PLUS DE PANNEAU (2026-10-26, borne de falken) : le panneau reste ouvert quand il rend la main a ES ;
        // ES ferme, il restait sur le bureau jusqu'a l'arret de l'API. Il part avec ES, cartes comprises.
        _sortieDEs = bus?.Subscribe<RetroBat.Domain.Events.EventEnvelope>(e =>
        {
            switch (e.Type)
            {
                case "ui.frontend.stopped":
                    QuandEsSeFerme();
                    break;
                case "ui.game.started":
                    EndormirLaSelection();
                    break;
                case "ui.game.ended":
                    lock (_verrou)
                    {
                        _finDePartieUtc = DateTime.UtcNow;
                    }

                    RafraichirLesCartesApres(InvitationApresUnePartie + TimeSpan.FromMilliseconds(200));
                    break;
                case "panel.input.pressed" or "ui.game.selected.raw" or "ui.system.selected.raw":
                    ReveillerLaSelection();
                    break;
            }
        });
    }

    /// <summary>
    /// LA SELECTION DORT PENDANT UNE PARTIE (retour du user du 26/10 : au sortir d'une partie et de son ecran de fin, la
    /// carte « Votre selection » clignotait). Elle s'efface au lancement d'un jeu et ne revient qu'au premier geste du
    /// joueur dans ES : un appui, un jeu ou un systeme selectionne. Ni le « game-selected » qu'ES envoie de lui-meme apres
    /// une partie, ni l'appui qui ferme l'ecran de fin ne la reveillent : il faut le panneau ferme depuis 2 s et la partie
    /// finie depuis 3 s.
    /// </summary>
    private bool _selectionEndormie;
    private DateTime _fermeDepuisUtc = DateTime.MinValue;
    private DateTime _finDePartieUtc = DateTime.MinValue;

    /// <summary>
    /// L'INVITATION AU RETOUR D'UNE PARTIE (decisions du user du 30/10) : elle ne parait que 5 s apres le retour dans ES (la
    /// partie finie, l'ecran de fin ferme), le temps que le joueur se retrouve ; et seule une invitation visible se prend ou
    /// se refuse : une invitation qu'il ne voit pas ne prend jamais son appui long. Le site met son echeance en pause pendant
    /// la partie et lui laisse le temps de decider a son retour.
    /// </summary>
    private static readonly TimeSpan InvitationApresUnePartie = TimeSpan.FromSeconds(5);

    /// <summary>L'heure ou l'ecran de fin d'une partie MULTIPLAYER s'est ferme : le retour dans ES.</summary>
    private DateTime _resultatFermeUtc = DateTime.MinValue;

    /// <summary>L'invitation en attente est-elle a l'ecran ? Sous le verrou.</summary>
    private bool InvitationVisible(DateTime maintenant)
        => _invitation is not null && _mode != Mode.Resultat && !EnJeu()
           && maintenant - (_finDePartieUtc > _resultatFermeUtc ? _finDePartieUtc : _resultatFermeUtc) >= InvitationApresUnePartie;

    /// <summary>Les cartes se recomposent un peu apres : une invitation retenue y parait a son heure.</summary>
    private void RafraichirLesCartesApres(TimeSpan delai)
        => _ = Task.Run(async () =>
        {
            await Task.Delay(delai).ConfigureAwait(false);
            MettreAJourLeBandeau();
        });

    private void EndormirLaSelection()
    {
        lock (_verrou)
        {
            if (_selectionEndormie)
            {
                return;
            }
            _selectionEndormie = true;
        }
        MettreAJourLeBandeau();
    }

    private void ReveillerLaSelection()
    {
        lock (_verrou)
        {
            var maintenant = DateTime.UtcNow;
            if (!_selectionEndormie || _mode != Mode.Ferme
                || maintenant - _fermeDepuisUtc < TimeSpan.FromSeconds(2)
                || maintenant - _finDePartieUtc < TimeSpan.FromSeconds(3)
                || EnJeu())
            {
                return;
            }
            _selectionEndormie = false;
        }
        MettreAJourLeBandeau();
    }

    private bool _classementOuvert;

    /// <summary>
    /// LE CLASSEMENT DE L'APPUI LONG EST OUVERT (retour du user du 26/10) : le menu du jeu d'ES s'ouvre a droite, et la
    /// carte « Votre selection » du haut a droite en cachait le debut. Elle se tait le temps du panneau ; une invitation,
    /// elle, passe toujours.
    /// </summary>
    public void PoserLeClassementOuvert(bool ouvert)
    {
        lock (_verrou)
        {
            if (_classementOuvert == ouvert)
            {
                return;
            }
            _classementOuvert = ouvert;
        }
        MettreAJourLeBandeau();
    }

    private readonly RetroBat.Domain.Interfaces.IEmulationStationNotificationService? _notifications;
    private readonly HashSet<string> _propositionsVues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _messagesAEnvoyer = new();
    private DateTime _dernierMessageUtc = DateTime.MinValue;
    private bool _envoiEnCours;
    private static readonly TimeSpan EspacementDesMessages = TimeSpan.FromSeconds(3);

    /// <summary>
    /// « X PROPOSE DE JOUER A Y » (demande du user du 26/10) : un joueur que ce compte suit attend dans une gameroom, les
    /// deux bornes montrent la collection MULTIPLAYER, et celle-ci a le meme jeu ET le meme contenu. Un message d'ES par
    /// salle et par joueur, jamais plus d'un toutes les 3 s.
    /// </summary>
    private void SurPropositions(IReadOnlyList<MultiplayerProposition> propositions)
    {
        if (_notifications is null || !DansLaCollection || EnJeu())
        {
            return;
        }

        var langue = Langue();
        lock (_verrou)
        {
            foreach (var proposition in propositions)
            {
                if (proposition.From is not { } de || !_propositionsVues.Add(proposition.Room + "|" + de.Handle)
                    || _session.JeuCompatible(proposition.Game, proposition.Content) is not { } jeu)
                {
                    continue;
                }

                var message = _textes.Format("multiplayer.proposal", langue, ("player", de.Pseudo), ("game", jeu.Game.Name));
                _messagesAEnvoyer.Enqueue(message.StartsWith("multiplayer.", StringComparison.Ordinal)
                    ? de.Pseudo + " wants to play " + jeu.Game.Name
                    : message);
            }

            if (_messagesAEnvoyer.Count == 0 || _envoiEnCours)
            {
                return;
            }
            _envoiEnCours = true;
        }

        _ = Task.Run(EnvoyerLesPropositionsAsync);
    }

    private async Task EnvoyerLesPropositionsAsync()
    {
        while (true)
        {
            TimeSpan attente;
            lock (_verrou)
            {
                if (_messagesAEnvoyer.Count == 0)
                {
                    _envoiEnCours = false;
                    return;
                }
                attente = _dernierMessageUtc + EspacementDesMessages - DateTime.UtcNow;
            }

            if (attente > TimeSpan.Zero)
            {
                await Task.Delay(attente).ConfigureAwait(false);
            }

            string message;
            lock (_verrou)
            {
                message = _messagesAEnvoyer.Dequeue();
                _dernierMessageUtc = DateTime.UtcNow;
            }

            // Sorti de la collection ou parti jouer entre-temps : le message n'a plus lieu d'etre.
            if (!DansLaCollection || EnJeu())
            {
                continue;
            }
            try
            {
                await _notifications!.NotifyAsync(message).ConfigureAwait(false);
                _journal.LogInformation("MULTIPLAYER : proposition annoncee dans ES ({Message})", message);
            }
            catch (Exception ex)
            {
                _journal.LogDebug(ex, "MULTIPLAYER : proposition non annoncee");
            }
        }
    }

    /// <summary>ES vient de se fermer : rien du MULTIPLAYER ne reste a l'ecran.</summary>
    internal void QuandEsSeFerme()
    {
        bool ouvert;
        lock (_verrou)
        {
            ouvert = _mode != Mode.Ferme;
        }

        if (ouvert)
        {
            _journal.LogInformation("MULTIPLAYER : EmulationStation fermee, le panneau se ferme");
            Fermer();
        }
        _cartes?.Cacher();
    }

    /// <summary>MULTIPLAYER est-il a l'ecran ? Tant qu'il l'est, les gestes viennent ici.</summary>
    public bool Ouvert
    {
        get { lock (_verrou) return _mode != Mode.Ferme; }
    }

    /// <summary>Le bouton « valider » tenu depuis cet instant, quand un ecran MULTIPLAYER a l'appui ; null sinon.</summary>
    public DateTime? ValiderTenuDepuis
    {
        get { lock (_verrou) return _validerDepuis; }
    }

    /// <summary>L'ecran MULTIPLAYER montre, pour le journal : panneau, presentation (et sa tenue), resultat (et sa manette).</summary>
    public string EcranAffiche
    {
        get
        {
            lock (_verrou)
            {
                return _mode switch
                {
                    Mode.Resultat => "resultat, manette " + (_resultatALaMain ? "prise" : "laissee a ES"),
                    Mode.Presentation => "presentation" + (_tenirLeLancement ? " tenue devant le jeu" : string.Empty),
                    _ => _mode.ToString().ToLowerInvariant(),
                };
            }
        }
    }

    /// <summary>Le panneau des gamerooms est-il a l'ecran ? Ni la presentation d'un lancement, ni un resultat.</summary>
    public bool PanneauAffiche
    {
        get { lock (_verrou) return _mode == Mode.Panneau; }
    }

    /// <summary>Le panneau des gamerooms est a l'ecran et ES a la main : un « gauche » y entrerait.</summary>
    public bool AttendQuOnEntre
    {
        get { lock (_verrou) return _mode == Mode.Panneau && _modele.Etat == MultiplayerPanelModel.Foyer.MenuEs; }
    }

    /// <summary>MULTIPLAYER est-il en service sur cette borne ?</summary>
    public bool Actif => _options.CurrentValue.NelfePlay is { Enabled: true, ShowMultiplayerCollection: true };

    /// <summary>
    /// Le carrousel montre-t-il la collection MULTIPLAYER ? C'est le carrousel qui compte, pas le systeme du
    /// jeu : a la selection d'un jeu, l'API prend le systeme du jeu (« fbneo »), meme dans une collection
    /// (vu le 22/10 : l'appui long ouvrait le classement).
    /// </summary>
    public bool DansLaCollection
        => string.Equals(_etatDesMedias?.CarouselSystemId, MultiplayerCollectionSyncService.CollectionName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Le carrousel tel que l'API le connait, pour le journal de l'appui long.</summary>
    public string Carrousel => _etatDesMedias?.CarouselSystemId ?? string.Empty;

    /// <summary>Une invitation attend sa reponse : l'appui long la prend avant tout panneau.</summary>
    public bool InvitationEnAttente
    {
        get { lock (_verrou) return InvitationVisible(DateTime.UtcNow); }
    }

    // ── le panneau ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// L'APPUI LONG sur un jeu de la collection : le joueur rejoint une gameroom et le panneau s'ouvre.
    /// Rend faux quand le jeu n'est pas MULTIPLAYER sur cette borne.
    /// </summary>
    public async Task<bool> OuvrirAsync(string cheminDuJeu, string glypheValider, string glypheAnnuler, CancellationToken ct, bool rejoindre = true,
        string glypheX = "")
    {
        var jeu = _session.JeuDe(cheminDuJeu);
        if (jeu is null)
        {
            _journal.LogInformation("MULTIPLAYER : {Chemin} n'est pas un jeu de la collection", cheminDuJeu);
            return false;
        }

        // UN JEU DEMARRE SUR CETTE BORNE : l'appui long ne rouvre pas le panneau (il rejoindrait une salle, et B maintenu la
        // quitterait en plein lancement : 25/10, session refusee, invite jamais connecte). La presentation revient.
        if (_session.LancementEnCours is { } enLancement
            && _session.Etat.Rooms.FirstOrDefault(room => room.Room == enLancement) is { } salle)
        {
            _journal.LogInformation("MULTIPLAYER : {Room} est en cours de lancement, l'appui long remontre l'attente", enLancement);
            AnnoncerLeLancement(salle, _session.JeuDeLaRoom(salle));
            return true;
        }

        lock (_verrou)
        {
            _jeu = jeu;
            _refus = null;
            _amis = [];
            _glypheValider = glypheValider.Length > 0 ? glypheValider : _glypheValider;
            _glypheAnnuler = glypheAnnuler.Length > 0 ? glypheAnnuler : _glypheAnnuler;
            _glypheX = glypheX.Length > 0 ? glypheX : _glypheX;
            _langue = "";
            _mode = Mode.Panneau;
            _retour?.Cancel();
            _modele.Ouvrir();
        }

        _session.PanneauOuvert = true;
        MettreAJourLeBandeau();
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), Composer());
        _journal.LogInformation("MULTIPLAYER : panneau ouvert sur {Jeu}", jeu.Game.Id);

        if (rejoindre)
        {
            var refus = await _session.RejoindreAsync(cheminDuJeu, ct).ConfigureAwait(false);
            lock (_verrou)
            {
                _refus = refus;
                // L'appui long l'a mene dans une gameroom d'amis (la sienne, ou celle d'un ami qui l'attend) : FOR FRIENDS.
                if (RoomDuJeu() is { EntreAmis: true } && _modele.OngletCourant == MultiplayerPanelModel.Onglet.WaitingForPlayers)
                {
                    _modele.PlacerSurLOnglet(MultiplayerPanelModel.Onglet.WaitingForFriends);
                }
            }
        }

        Rafraichir();
        _ = ChargerLesAmisAsync(ct);
        return true;
    }

    /// <summary>
    /// D'UNE GAMEROOM A L'AUTRE (demande du user du 28/10) : sur WAITING FOR FRIENDS, le joueur passe dans la gameroom d'amis
    /// (celle d'un ami, sinon la sienne) ; revenu sur FOR PLAYERS…, dans une gameroom publique. INVITE ne change rien : on y
    /// arrive par FOR FRIENDS, et l'invitation fait entrer l'ami dans la gameroom d'amis.
    /// </summary>
    private async Task ChangerDeGameroomAsync()
    {
        await _changementDeSalle.WaitAsync().ConfigureAwait(false);
        try
        {
            string? sorte;
            MultiplayerInstalledGame? jeu;
            lock (_verrou)
            {
                jeu = _jeu;
                var room = RoomDuJeu();
                sorte = _mode != Mode.Panneau ? null : _modele.OngletCourant switch
                {
                    MultiplayerPanelModel.Onglet.WaitingForFriends when room is not { EntreAmis: true } => MultiplayerSortes.Amis,
                    MultiplayerPanelModel.Onglet.WaitingForPlayers when room is null or { EntreAmis: true } => MultiplayerSortes.Publique,
                    _ => null,
                };
            }

            if (sorte is null || jeu is null)
            {
                return;
            }

            var refus = await _session.RejoindreAsync(jeu.Path, CancellationToken.None, sorte).ConfigureAwait(false);
            _journal.LogInformation("MULTIPLAYER : {Jeu}, passage en gameroom {Sorte} ({Statut})", jeu.Game.Id,
                sorte == MultiplayerSortes.Amis ? "d'amis" : "publique", refus ?? "ok");
            lock (_verrou)
            {
                _refus = refus;
            }

            Rafraichir();
        }
        finally
        {
            _changementDeSalle.Release();
        }
    }

    /// <summary>
    /// LA TOUCHE X DE FOR FRIENDS (28/10) : l'ouvreur de la gameroom d'amis fait passer le nombre de joueurs attendus au
    /// suivant (jusqu'au maximum du jeu, puis retour a ceux qui sont la). Rien ailleurs.
    /// </summary>
    public void Joueurs()
    {
        MultiplayerRoom? room;
        lock (_verrou)
        {
            if (_mode != Mode.Panneau || _modele.Etat != MultiplayerPanelModel.Foyer.Panneau
                || _modele.OngletCourant != MultiplayerPanelModel.Onglet.WaitingForFriends)
            {
                return;
            }

            room = RoomDuJeu();
        }

        if (room is not { EntreAmis: true, Owner: true, State: "waiting" or "countdown" } || room.GameMax <= 2)
        {
            return;
        }

        var suivante = MultiplayerPanelModel.TailleSuivante(room.Max, room.Players.Count, room.GameMax);
        _ = Task.Run(async () =>
        {
            var refus = await _session.ReglerAsync(room.Room, suivante).ConfigureAwait(false);
            _journal.LogInformation("MULTIPLAYER : gameroom d'amis {Room}, {Joueurs} joueurs attendus ({Statut})", room.Room, suivante, refus ?? "ok");
            lock (_verrou)
            {
                _refus = refus;
            }

            Rafraichir();
        });
    }

    /// <summary>Fermer sans quitter : le jeu reste dans « Votre selection » (V24-09).</summary>
    public void Fermer()
    {
        OublierLeLancement();
        lock (_verrou)
        {
            if (_mode == Mode.Resultat)
            {
                // Le retour dans ES : une invitation qui attend parait dans 5 s (30/10).
                _resultatFermeUtc = DateTime.UtcNow;
                RafraichirLesCartesApres(InvitationApresUnePartie + TimeSpan.FromMilliseconds(200));
            }

            _modele.Fermer();
            _mode = Mode.Ferme;
            _fermeDepuisUtc = DateTime.UtcNow;
            _validerDepuis = null;
            _annulerDepuis = null;
            _retour?.Cancel();
        }

        _session.PanneauOuvert = false;
        _overlay.Fermer();
        MettreAJourLeBandeau();
    }

    /// <summary>
    /// Une autre fenetre est passee devant ES (un rendu de replay, un editeur sur un autre ecran), ou le
    /// joueur ne touche plus a rien : le panneau reste a l'ecran, on y attend un joueur. Seul le foyer revient a ES.
    /// </summary>
    public void RendreLaMain()
    {
        bool change;
        bool resultat;
        lock (_verrou)
        {
            change = _mode == Mode.Panneau && _modele.RendreLaMain();
            // Le resultat qui perd la manette ne sert plus : le « valider » du bouton OK irait aussi a ES. Avant de l'avoir
            // prise (RetroArch sort encore, ES n'est pas encore revenu), il reste.
            resultat = _mode == Mode.Resultat && _resultatALaMain && !EnJeu();
        }

        if (resultat)
        {
            _journal.LogInformation("MULTIPLAYER : le resultat perd la manette, il se ferme.");
            Fermer();
            return;
        }

        if (change)
        {
            _journal.LogInformation("MULTIPLAYER : le panneau reste ouvert et rend la main a ES.");
            Rafraichir();
        }
    }

    /// <summary>Un jeu demarre : la fenetre s'efface sans reprendre le premier plan a l'emulateur (sauf l'ecran de lancement qui tient).</summary>
    public void FermerPourLeJeu()
    {
        if (TientLeLancement)
        {
            return;
        }

        OublierLeLancement();
        lock (_verrou)
        {
            _modele.Fermer();
            _mode = Mode.Ferme;
            _fermeDepuisUtc = DateTime.UtcNow;
            _selectionEndormie = true;
        }

        _session.PanneauOuvert = false;
        _overlay.FermerPourLeJeu();
        _cartes?.Cacher();
        _texteDuBandeau = "";
    }

    /// <summary>
    /// L'emulateur est la : la presentation s'efface, sauf l'ecran de lancement qui tient devant le jeu (la boucle du depart
    /// chez l'hote, la connexion chez l'invite).
    /// </summary>
    public void EffacerPourLeJeu()
    {
        if (!TenirDevantLeJeu())
        {
            FermerPourLeJeu();
        }
    }

    // ── la presentation et le resultat ────────────────────────────────────────────────────────────

    /// <summary>
    /// Le decompte est fini : « Les joueurs se synchronisent… » jusqu'a l'arrivee du jeu (ecrans 9 et 14 de
    /// la toile). Au niveau Classique, le message de la borne d'origine s'ajoute.
    /// </summary>
    public void AnnoncerLeLancement(MultiplayerRoom room, MultiplayerInstalledGame? jeu, bool tenir = false)
    {
        var langue = Langue();
        bool deja;
        lock (_verrou)
        {
            deja = _mode == Mode.Presentation;
            // Le lancement annonce par la plateforme passe avant celui de cette borne : la tenue s'ajoute alors.
            _tenirLeLancement |= tenir && deja;
            _mode = Mode.Presentation;
            _modele.Fermer();
            _jeu = jeu ?? _jeu;
            _retour?.Cancel();
            if (!deja)
            {
                _presenteeDepuis = DateTime.UtcNow;
            }
        }

        _session.PanneauOuvert = false;
        _cartes?.Cacher();
        _texteDuBandeau = "";
        if (deja)
        {
            return;
        }

        // L'ECRAN DE LANCEMENT (le VS, 28/10) ; l'ancienne attente reste en secours, sans jeu connu.
        PreparerLeLancement(room, tenir);
        if (ComposerLEcranDeLancement() is { } ecran)
        {
            _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), ContenuDuLancement(ecran));
            return;
        }

        var classique = room.Level != MultiplayerLevels.NelfePlay;
        // NIVEAU NELFEPLAY : les personnages dans l'ordre des places (00quater.10), a la place du message du Classique.
        var personnages = string.Join("  ·  ", room.Players
            .Where(joueur => joueur.Seat is not null)
            .OrderBy(joueur => joueur.Seat)
            .Select(joueur => (Place: joueur.Seat!.Value, Nom: PersonnageDe(joueur)))
            .Where(paire => paire.Nom.Length > 0)
            .Select(paire => "P" + paire.Place.ToString(CultureInfo.InvariantCulture) + " " + paire.Nom));
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), Composer());
        _overlay.Attendre(
            _textes.Text("multiplayer.syncing", langue),
            jeu?.Game.Name ?? string.Empty,
            classique ? _textes.Text("multiplayer.classic.message", langue) : personnages);
    }

    /// <summary>
    /// Apres la partie, l'ecran de resultat (V24-17, 00quater.10) : au niveau Classique, les joueurs et la
    /// duree. Il se ferme de lui-meme au bout de 12 s ; A maintenu relance le meme jeu.
    /// </summary>
    public void MontrerLeResultat(string room, int? dureeSecondes, IReadOnlyDictionary<int, StatsDeLaPlace>? statsVues = null,
        MultiplayerStatsResultat? mesuresVues = null, IReadOnlyList<MultiplayerRang>? rangsVus = null)
    {
        lock (_verrou)
        {
            _statsVues = statsVues;
            _mesuresVues = mesuresVues;
            _rangsVus = rangsVus is { Count: > 0 } ? rangsVus : null;
        }

        _ = Task.Run(async () =>
        {
            // L'hote annonce la fin : un ou deux sondages plus tard, la gameroom est « finished ».
            for (var essai = 0; essai < 6 && !_session.Etat.Rooms.Any(r => r.Room == room && r.State == "finished"); essai++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            var retour = new CancellationTokenSource();
            lock (_verrou)
            {
                _retour?.Cancel();
                _retour = retour;
                _mode = Mode.Resultat;
                _roomDuResultat = room;
                _dureeDuResultat = dureeSecondes;
                _resultatDepuis = DateTime.UtcNow;
                _resultatALaMain = false;
                _modele.Fermer();
            }

            // EN PLEIN ECRAN, JUSQU'A OK (retour du user du 23/10). Le resultat parait des que le coeur est decharge, souvent
            // avant que RetroArch ait fini de sortir ; il prend la manette une fois l'emulateur parti, sinon le « valider »
            // du bouton OK irait aussi a ES, qui lancerait le jeu selectionne.
            _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), Composer());
            try
            {
                var limite = DateTime.UtcNow + PatienceDeLaSortie;
                while (EmulatorForeground.EmulateurDeJeuTourne() && DateTime.UtcNow < limite)
                {
                    await Task.Delay(250, retour.Token).ConfigureAwait(false);
                }

                bool encore;
                lock (_verrou)
                {
                    encore = _mode == Mode.Resultat && ReferenceEquals(_retour, retour);
                }

                if (encore)
                {
                    var pris = _overlay.PrendreLeFocus();
                    lock (_verrou)
                    {
                        _resultatALaMain = pris;
                    }

                    _journal.LogInformation("MULTIPLAYER : resultat de {Room} a l'ecran, manette {Prise}", room,
                        pris ? "prise" : "refusee par Windows (ES la garde)");
                    if (pris)
                    {
                        await GarderLePremierPlanAsync(retour).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Rejouer, ou fermer a la main : rien a faire de plus.
            }
        });
    }

    /// <summary>
    /// L'ECRAN DE FIN GARDE LE PREMIER PLAN (2026-10-26, borne de falken : manette prise, ecran pourtant invisible ; sur
    /// cette machine un panneau ne se voit que s'il est la fenetre active). Pendant 6 s, s'il lui echappe, le journal dit
    /// qui l'a pris et l'ecran le reprend, une fois.
    /// </summary>
    private async Task GarderLePremierPlanAsync(CancellationTokenSource retour)
    {
        var repris = false;
        for (var tour = 0; tour < 12; tour++)
        {
            await Task.Delay(500, retour.Token).ConfigureAwait(false);
            lock (_verrou)
            {
                if (_mode != Mode.Resultat || !ReferenceEquals(_retour, retour))
                {
                    return;
                }
            }

            if (_overlay.EstAuPremierPlan())
            {
                continue;
            }

            var qui = _overlay.QuiALePremierPlan();
            if (repris)
            {
                _journal.LogInformation("MULTIPLAYER : l'ecran de fin a encore perdu le premier plan ({Qui})", qui);
                return;
            }
            repris = true;
            var pris = _overlay.PrendreLeFocus();
            _journal.LogInformation("MULTIPLAYER : l'ecran de fin avait perdu le premier plan ({Qui}), repris : {Pris}", qui, pris ? "oui" : "non");
        }
    }

    // ── les gestes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Une direction du DPAD : <c>left</c>, <c>right</c>, <c>up</c>, <c>down</c>.</summary>
    public void Direction(string? identite)
    {
        MultiplayerPanelModel.Effet effet;
        var sens = (identite ?? string.Empty).ToLowerInvariant();
        lock (_verrou)
        {
            if (_mode != Mode.Panneau)
            {
                return;
            }

            // NIVEAU NELFEPLAY : ▲▼ change le personnage dans WAITING FOR PLAYERS (V24-04) et dans la gameroom d'amis (28/10),
            // quand nous avons la main.
            if (sens is "up" or "down" && AuChoixDuPersonnage() is ({ } salle, { } jeuNelfePlay))
            {
                _ = ChangerDePersonnageAsync(salle, jeuNelfePlay, sens == "down" ? 1 : -1);
                return;
            }

            // On entre par l'onglet de la gameroom ou l'on attend : WAITING FOR FRIENDS pour une gameroom d'amis (28/10).
            if (sens == "left" && _modele.Etat == MultiplayerPanelModel.Foyer.MenuEs)
            {
                _modele.PoserLaPorte(RoomDuJeu() is { EntreAmis: true }
                    ? MultiplayerPanelModel.Onglet.WaitingForFriends
                    : MultiplayerPanelModel.Onglet.WaitingForPlayers);
            }

            effet = sens switch
            {
                "left" => _modele.Gauche(),
                "right" => _modele.Droite(),
                "up" => _modele.Haut(),
                "down" => _modele.Bas(),
                _ => MultiplayerPanelModel.Effet.Rien,
            };
        }

        Appliquer(effet);
    }

    /// <summary>Le joueur est-il au choix de son personnage (onglet d'attente, la main, gameroom ouverte) ? La gameroom et le jeu.</summary>
    private (MultiplayerRoom Salle, MultiplayerGame Jeu)? AuChoixDuPersonnage()
        => _modele.Etat == MultiplayerPanelModel.Foyer.Panneau
           && _modele.OngletCourant is MultiplayerPanelModel.Onglet.WaitingForPlayers or MultiplayerPanelModel.Onglet.WaitingForFriends
           && _jeu is { ChoixOuverts: true, Game: var jeu }
           && RoomDuJeu() is { State: "waiting" or "countdown" } salle
            ? (salle, jeu)
            : null;

    /// <summary>Plusieurs choix par place (v1.0 §8.2) : celui que ▲▼ change, de 1 a <see cref="MultiplayerChoice.Picks"/>, par jeu.</summary>
    private (string Jeu, int Rang) _choixCourant = (string.Empty, 1);

    private int RangDuChoix(MultiplayerGame jeu)
        => _choixCourant.Jeu == jeu.Id && jeu.Choices is [{ Picks: > 1 }, ..] ? Math.Clamp(_choixCourant.Rang, 1, Math.Max(1, MesChoixDansLaSalle(jeu))) : 1;

    /// <summary>Le nombre de choix de ce joueur, a sa place et au nombre de joueurs assis de la gameroom (une partie a deux au moins).</summary>
    private int ChoixDe(MultiplayerJoueur joueur, MultiplayerGame jeu, MultiplayerChoice premier)
        => premier.PicksDe(joueur.Seat, Math.Max(2, RoomDuJeu()?.Players.Count(j => j.Seat is not null) ?? 2));

    /// <summary>Le nombre de choix de la place de cette borne ; tous, tant qu'elle n'est pas assise.</summary>
    private int MesChoixDansLaSalle(MultiplayerGame jeu)
        => jeu.Choices is not [var premier, ..] ? 1
            : RoomDuJeu()?.Players.FirstOrDefault(j => j.You) is { } moi ? ChoixDe(moi, jeu, premier) : premier.PicksDe(null, 2);

    /// <summary>Le bouton « valider » : relache, il agit sur la ligne ; au resultat, maintenu, il relance le jeu.</summary>
    public void Valider(bool appuye)
    {
        MultiplayerPanelModel.Effet effet;
        bool rejouer;
        lock (_verrou)
        {
            if (appuye)
            {
                _validerDepuis = DateTime.UtcNow;
                return;
            }

            var maintenu = Maintenu(_validerDepuis);
            _validerDepuis = null;
            if (_mode == Mode.Presentation)
            {
                // L'attente trop longue : A relance l'attente ; sinon la presentation ne s'interrompt pas.
                _ = Task.Run(() => ReessayerLeLancement());
                return;
            }

            if (maintenu && InvitationVisible(DateTime.UtcNow))
            {
                // Panneau ouvert, une invitation attend (sa carte est en haut a droite) : l'appui long la prend.
                _ = AccepterInvitationAsync();
                return;
            }

            // Plusieurs choix par place (v1.0 §8.2) : A passe au choix suivant de l'equipe, ▲▼ change celui-la.
            if (!maintenu && _mode == Mode.Panneau && AuChoixDuPersonnage() is (_, { Choices: [{ Picks: > 1 }, ..] } jeuDeLEquipe)
                && MesChoixDansLaSalle(jeuDeLEquipe) > 1)
            {
                _choixCourant = (jeuDeLEquipe.Id, RangDuChoix(jeuDeLEquipe) % MesChoixDansLaSalle(jeuDeLEquipe) + 1);
                Rafraichir();
                return;
            }

            rejouer = _mode == Mode.Resultat && maintenu;
            effet = _mode == Mode.Panneau ? _modele.Valider()
                : _mode == Mode.Resultat && !maintenu && ResultatGarde() ? MultiplayerPanelModel.Effet.Fermer
                : MultiplayerPanelModel.Effet.Rien;
        }

        if (rejouer)
        {
            _ = RejouerAsync();
            return;
        }

        Appliquer(effet);
    }

    /// <summary>Le bouton « annuler » : relache court, il rend la main ou ferme ; maintenu, il quitte la gameroom.</summary>
    public void Annuler(bool appuye)
    {
        MultiplayerPanelModel.Effet effet;
        lock (_verrou)
        {
            if (appuye)
            {
                _annulerDepuis = DateTime.UtcNow;
                return;
            }

            var maintenu = Maintenu(_annulerDepuis);
            _annulerDepuis = null;
            if (!maintenu && InvitationVisible(DateTime.UtcNow))
            {
                RefuserInvitation();
                return;
            }

            if (_mode == Mode.Resultat)
            {
                effet = ResultatGarde() ? MultiplayerPanelModel.Effet.Fermer : MultiplayerPanelModel.Effet.Rien;
            }
            else if (_mode == Mode.Panneau)
            {
                effet = maintenu ? _modele.AnnulerMaintenu() : _modele.Annuler();
            }
            else
            {
                // La presentation ne s'interrompt pas, sauf B maintenu pendant l'attente trop longue (QUITTER).
                if (maintenu)
                {
                    _ = Task.Run(() => QuitterLeLancement());
                }

                return;
            }
        }

        Appliquer(effet);
    }

    /// <summary>
    /// LE FONDU AU NOIR DE FIN DE PARTIE (retour du user du 23/10) : le noir monte sur le jeu, l'ecran de fin en sortira.
    /// Rendu une fois le noir complet ; tout de suite s'il n'a pas pu partir (un voile deja la, par exemple).
    /// </summary>
    public Task FonduAuNoirAsync()
        => _overlay.FonduAuNoir(EsMenuStyle.Lire(_journal), DureeDuFondu) ? Task.Delay(DureeDuFondu) : Task.CompletedTask;

    /// <summary>Le resultat est-il la depuis assez longtemps pour qu'un appui le ferme (voir GardeDuResultat) ?</summary>
    private bool ResultatGarde() => DateTime.UtcNow - _resultatDepuis >= GardeDuResultat;

    /// <summary>L'appui long pendant une invitation : le joueur rejoint la gameroom de son ami.</summary>
    public async Task AccepterInvitationAsync(CancellationToken ct = default)
    {
        MultiplayerInvitation? invitation;
        lock (_verrou)
        {
            invitation = _invitation;
            _invitation = null;
        }

        if (invitation is null)
        {
            return;
        }

        _cartes?.Cacher();
        _texteDuBandeau = "";
        _journal?.LogInformation("MULTIPLAYER : invitation de {Ami} pour {Jeu} acceptee", invitation.From?.Pseudo, invitation.Game);
        var jeu = _session.JeuDeLaRoom(new MultiplayerRoom { Game = invitation.Game, Content = invitation.Content });
        if (jeu is null)
        {
            // Sans le meme contenu, la partie se desynchroniserait : on refuse plutot que de rejoindre.
            await _session.RepondreAsync(invitation.Invitation, false, ct).ConfigureAwait(false);
            Signaler(_textes.Text("multiplayer.refused.content_refused", Langue()));
            return;
        }

        var refus = await _session.RepondreAsync(invitation.Invitation, true, ct).ConfigureAwait(false);
        if (refus is not null)
        {
            Signaler(_textes.Text("multiplayer.refused." + refus, Langue()));
            return;
        }

        await OuvrirAsync(jeu.Path, string.Empty, string.Empty, ct, rejoindre: false).ConfigureAwait(false);
    }

    /// <summary>B pendant une invitation : elle est refusee.</summary>
    public void RefuserInvitation()
    {
        MultiplayerInvitation? invitation;
        lock (_verrou)
        {
            invitation = _invitation;
            _invitation = null;
        }

        if (invitation is null)
        {
            return;
        }

        _cartes?.Cacher();
        _texteDuBandeau = "";
        _journal?.LogInformation("MULTIPLAYER : invitation de {Ami} pour {Jeu} refusee", invitation.From?.Pseudo, invitation.Game);
        _ = _session.RepondreAsync(invitation.Invitation, false);
        MettreAJourLeBandeau();
    }

    private bool Maintenu(DateTime? depuis)
        => depuis is { } debut && (DateTime.UtcNow - debut).TotalMilliseconds >= Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs);

    private void Appliquer(MultiplayerPanelModel.Effet effet)
    {
        switch (effet)
        {
            case MultiplayerPanelModel.Effet.PrendreLeFocus:
                _overlay.PrendreLeFocus();
                break;
            case MultiplayerPanelModel.Effet.RendreLeFocus:
                _overlay.RendreLeFocus();
                break;
            case MultiplayerPanelModel.Effet.Fermer:
                Fermer();
                return;
            case MultiplayerPanelModel.Effet.QuitterLaGameroom:
                _ = QuitterAsync();
                return;
            case MultiplayerPanelModel.Effet.AgirSurLaLigne:
                _ = InviterAsync();
                break;
            case MultiplayerPanelModel.Effet.ChargerLOnglet:
                // FOR PLAYERS… et FOR FRIENDS sont deux gamerooms : passer de l'un a l'autre en change (28/10).
                _ = Task.Run(ChangerDeGameroomAsync);
                if (_modele.OngletCourant == MultiplayerPanelModel.Onglet.InviteFriends)
                {
                    _ = ChargerLesAmisAsync(CancellationToken.None);
                }

                break;
        }

        Rafraichir();
    }

    private async Task RejouerAsync()
    {
        var jeu = _jeu;
        if (jeu is null)
        {
            Fermer();
            return;
        }

        _journal.LogInformation("MULTIPLAYER : rejouer a {Jeu}", jeu.Game.Id);
        lock (_verrou)
        {
            _resultatALaMain = false;
        }

        await OuvrirAsync(jeu.Path, string.Empty, string.Empty, CancellationToken.None).ConfigureAwait(false);
        // COMME UN APPUI LONG DANS LA COLLECTION (verifie le 28/10) : ES garde la main, le panneau s'affiche par-dessus. Sans
        // cela, la fenetre gardait la manette prise par l'ecran de fin, avec un panneau qui croyait la main a ES.
        _overlay.RendreLeFocus();
    }

    private async Task QuitterAsync()
    {
        var room = RoomDuJeu();
        Fermer();
        if (room is not null && _session.LancementEnCours == room.Room)
        {
            // Son jeu demarre sur cette borne : la quitter maintenant ferait refuser la session aux autres.
            _journal.LogInformation("MULTIPLAYER : B maintenu ignore, {Room} est en cours de lancement", room.Room);
            return;
        }

        if (room is not null)
        {
            await _session.QuitterAsync(room.Room).ConfigureAwait(false);
            _journal.LogInformation("MULTIPLAYER : gameroom {Room} quittee (B maintenu)", room.Room);
        }
    }

    private async Task InviterAsync()
    {
        var room = RoomDuJeu();
        MultiplayerAmi? ami;
        lock (_verrou)
        {
            var invitables = Invitables();
            ami = _modele.Ligne >= 0 && _modele.Ligne < invitables.Count ? invitables[_modele.Ligne] : null;
        }

        if (room is null || ami is null)
        {
            return;
        }

        var refus = await _session.InviterAsync(room.Room, ami.Handle).ConfigureAwait(false);
        _journal?.LogInformation("MULTIPLAYER : invitation envoyee a {Ami} pour {Jeu} ({Statut})", ami.Pseudo, room.Game, refus ?? "ok");
        lock (_verrou)
        {
            _refus = refus;
        }

        await ChargerLesAmisAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ChargerLesAmisAsync(CancellationToken ct)
    {
        var amis = await _session.AmisAsync(ct).ConfigureAwait(false);
        lock (_verrou)
        {
            _amis = amis;
        }

        Rafraichir();
    }

    // ── l'etat qui change ────────────────────────────────────────────────────────────────────────

    private void SurChangement(MultiplayerEtat etat)
    {
        MultiplayerRoom? aPresenter = null;
        lock (_verrou)
        {
            foreach (var room in etat.Rooms)
            {
                _connues[room.Room] = room;
                if (room.State == "launching" && room.Launch is not null && _mode != Mode.Presentation)
                {
                    aPresenter = room;
                }
            }

            // Une invitation nouvelle s'affiche une fois ; celle qui disparait (repondue ou expiree) s'oublie.
            var nouvelle = etat.Invitations.FirstOrDefault(i => !_invitationsVues.Contains(i.Invitation));
            if (nouvelle is not null)
            {
                _invitationsVues.Add(nouvelle.Invitation);
                _invitation = nouvelle;
                _journal?.LogInformation("MULTIPLAYER : invitation recue de {Ami} pour {Jeu} (panneau {Mode}, en jeu {EnJeu})",
                    nouvelle.From?.Pseudo, nouvelle.Game, _mode, EnJeu());
                if (EnJeu())
                {
                    _ = Task.Run(() => AnnoncerEnPartieAsync(nouvelle));
                }
            }
            else if (_invitation is { } courante && etat.Invitations.All(i => i.Invitation != courante.Invitation))
            {
                _invitation = null;
            }
        }

        // La presentation s'efface quand la partie ne part plus : la gameroom est revenue a l'attente (l'hote
        // n'a pas publie sa session), ou rien n'arrive depuis deux minutes. Sinon le joueur restait devant
        // « Les joueurs se synchronisent… » pour toujours.
        bool presentationPerimee;
        lock (_verrou)
        {
            presentationPerimee = _mode == Mode.Presentation && !EnJeu()
                && (etat.Rooms.All(r => r.State is not ("launching" or "playing")) || DateTime.UtcNow - _presenteeDepuis > PresentationMax);
        }

        if (presentationPerimee)
        {
            _journal.LogInformation("MULTIPLAYER : la partie ne s'est pas ouverte, la presentation s'efface.");
            Fermer();
            Signaler(_textes.Text("multiplayer.launch.failed", Langue()));
            return;
        }

        if (aPresenter is not null && !EnJeu())
        {
            // Le decompte est fini : l'hote lance, les invites attendent sa session. Tout le monde voit la presentation.
            AnnoncerLeLancement(aPresenter, _session.JeuDeLaRoom(aPresenter));
            return;
        }

        Rafraichir();
        MettreAJourLeBandeau();
    }

    private void Rafraichir()
    {
        bool lancement;
        lock (_verrou)
        {
            // L'ecran de lancement se recompose aussi (un ping, un joueur qui change).
            lancement = _mode == Mode.Presentation && _roomDuLancement is not null;
            if (!lancement && _mode is not (Mode.Panneau or Mode.Resultat))
            {
                return;
            }
        }

        if (lancement)
        {
            MontrerLeLancement();
            return;
        }

        _overlay.MontrerMultiplayer(Composer());
    }

    /// <summary>
    /// Les cartes du haut d'ES, quand le panneau est ferme (retour du user du 22/10 : petites, discretes, en
    /// haut a droite, comme le cartouche d'un defi) : l'invitation d'abord, puis un message passager, puis une
    /// carte par jeu de « Votre selection » (V24-09), avec son decompte. Jamais pendant une partie.
    /// </summary>
    private void MettreAJourLeBandeau()
    {
        if (_cartes is null)
        {
            return;
        }

        var langue = Langue();
        string T(string cle) => _textes.Text(cle, langue);
        var cartes = new List<MultiplayerToastOverlay.Carte>();
        lock (_verrou)
        {
            if (EnJeu())
            {
                cartes.Clear();
            }
            else
            {
                // Panneau ouvert, seule la selection se tait (le panneau la montre) : une invitation ne se voyait pas quand
                // l'invite attendait deja dans son panneau (retour du user du 22/10).
                if (_session.Etat is { SuspenduJusquaUtc: { } jusqua, Conduite: { } conduite } && jusqua > DateTime.UtcNow)
                {
                    // L'ABANDON : le temps qui reste, et pourquoi (retour du user du 22/10).
                    cartes.Add(new MultiplayerToastOverlay.Carte(
                        T("multiplayer.suspended.title"),
                        _textes.Format("multiplayer.suspended.reason", langue, ("n", conduite.Abandons.ToString(CultureInfo.InvariantCulture))),
                        T("multiplayer.suspended.help"),
                        jusqua,
                        Alerte: true));
                }

                if (_invitation is { } invitation && InvitationVisible(DateTime.UtcNow))
                {
                    cartes.Add(new MultiplayerToastOverlay.Carte(
                        T("multiplayer.invitation.title"),
                        _textes.Format("multiplayer.invitation", langue,
                            ("player", invitation.From?.Pseudo ?? string.Empty), ("game", NomDuJeu(invitation.Game))),
                        T("multiplayer.invitation.help"),
                        DateTime.UtcNow.AddMilliseconds(Math.Max(1000, invitation.ExpiresMs)),
                        Alerte: true));
                }

                if (_message is { } message && DateTime.UtcNow < _messageJusqua)
                {
                    cartes.Add(new MultiplayerToastOverlay.Carte("MULTIPLAYER", message, string.Empty, Alerte: true));
                }

                // Un seul bloc « Votre selection », un jeu par ligne (retour du user du 22/10).
                var lignes = _mode != Mode.Ferme || _classementOuvert || _selectionEndormie ? [] : _session.Etat.Rooms.Where(r => r.State is "waiting" or "countdown").Take(3).Select(room =>
                {
                    var absent = room.Players.Any(j => j.You && !j.Present);
                    return new MultiplayerToastOverlay.Ligne(
                        NomDuJeu(room.Game),
                        // [AMIS] devant le compte d'une gameroom d'amis, comme dans le panneau (demande du user du 29/10).
                        (room.EntreAmis ? "[" + Texte("multiplayer.selection.friends", "FRIENDS") + "]  " : string.Empty)
                        // [1CC MULTI] devant une coop qui concourt (31/10) : la partie entre au classement, sans rien a choisir.
                        + (_session.Concourt(room.Game) ? "[" + MultiplayerScoring.Etiquette + "]  " : string.Empty)
                        + room.Players.Count(j => j.Present) + "/" + room.Max + "   " + (absent ? T("multiplayer.selection.away")
                            : room.EntreAmis ? Texte("multiplayer.selection.help_friends", "waiting for friends") : T("multiplayer.selection.help")),
                        room is { State: "countdown", CountdownMs: { } ms } ? DateTime.UtcNow.AddMilliseconds(ms) : null);
                }).ToList();
                if (lignes.Count > 0)
                {
                    cartes.Add(new MultiplayerToastOverlay.Carte(T("multiplayer.selection"), string.Empty, string.Empty, Lignes: lignes));
                }
            }
        }

        // La pile ne change que si son contenu change ; le decompte, lui, se recalcule dans la carte.
        var cle = string.Join("|", cartes.Select(c => c.Surtitre + "/" + c.Titre + "/" + c.Detail + "/" + (c.FinDuDecompteUtc is null ? "" : "d")
            + string.Concat((c.Lignes ?? []).Select(l => "/" + l.Titre + "/" + l.Detail + "/" + (l.FinDuDecompteUtc is null ? "" : "d")))));
        if (cle == _texteDuBandeau && !cartes.Any(c => c.Decompte))
        {
            return;
        }

        _texteDuBandeau = cle;
        _cartes.Montrer(cartes);
    }

    /// <summary>Un message passager dans les cartes du haut (un refus), 5 s.</summary>
    private void Signaler(string message)
    {
        lock (_verrou)
        {
            _message = message;
            _messageJusqua = DateTime.UtcNow.AddSeconds(5);
        }

        MettreAJourLeBandeau();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5.5)).ConfigureAwait(false);
            MettreAJourLeBandeau();
        });
    }

    private bool EnJeu()
        => EmulatorForeground.EmulateurDeJeuTourne() || string.Equals(_contexte?.Ui.State, "playing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// L'INVITATION RECUE EN PARTIE (decision du user du 28/10) : un message discret de RetroArch, une seule fois
    /// (« Falken t'invite a Mario Kart : c'est quand tu veux ! ») ; la carte de l'invitation attend le retour dans ES. Il
    /// ne parait pas pendant une rencontre MULTIPLAYER classee, ni quand une partie certifiee approche le record (le seuil
    /// du direct de record) : seule la carte, au retour.
    /// </summary>
    private async Task AnnoncerEnPartieAsync(MultiplayerInvitation invitation)
    {
        var report = PourquoiAttendreLaFin();
        if (report is not null)
        {
            _journal.LogInformation("MULTIPLAYER : invitation de {Ami} gardee pour la fin de la partie ({Raison})", invitation.From?.Pseudo, report);
            return;
        }

        var texte = Texte("multiplayer.invitation.ingame", "{player} invites you to {game}: whenever you're ready!")
            .Replace("{player}", invitation.From?.Pseudo ?? string.Empty, StringComparison.Ordinal)
            .Replace("{game}", NomDuJeu(invitation.Game), StringComparison.Ordinal);
        await new CommandesRetroArchUdp().EnvoyerAsync("SHOW_MSG " + texte.ReplaceLineEndings(" ").Trim(), CancellationToken.None).ConfigureAwait(false);
        _journal.LogInformation("MULTIPLAYER : invitation de {Ami} annoncee dans le jeu", invitation.From?.Pseudo);
    }

    /// <summary>Pourquoi une invitation attend la fin de la partie, ou null si elle peut s'afficher dans le jeu.</summary>
    private string? PourquoiAttendreLaFin()
    {
        if (RencontreClasseeEnCours?.Invoke() == true)
        {
            return "rencontre classee";
        }

        var direct = _services?.GetService(typeof(RetroBat.Api.Replay.Video.DirectDuRecordService)) as RetroBat.Api.Replay.Video.DirectDuRecordService;
        return direct?.EnApprocheDuRecord == true ? "record en approche" : null;
    }

    private MultiplayerRoom? RoomDuJeu()
    {
        var jeu = _jeu;
        return jeu is null ? null : _session.Etat.Rooms.FirstOrDefault(room => room.Game == jeu.Game.Id && room.State != "finished");
    }

    /// <summary>Les amis qu'on peut inviter : en ligne, et pas deja avec vous.</summary>
    private List<MultiplayerAmi> Invitables() => AmisDuJeu().Where(ami => ami.State != "with_you").ToList();

    /// <summary>
    /// Les amis vus depuis le jeu du panneau. La plateforme dit « deja avec vous » des qu'un ami partage UNE de vos
    /// gamerooms ; ici, cela ne vaut que pour celle de ce jeu : un ami avec vous sur un autre jeu est seulement en ligne,
    /// et on peut l'inviter (retour du user du 22/10).
    /// </summary>
    private IEnumerable<MultiplayerAmi> AmisDuJeu()
    {
        var avecMoi = RoomDuJeu()?.Players.Select(j => j.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return _amis.Select(ami => ami.State == "with_you" && !avecMoi.Contains(ami.Handle) ? ami with { State = "in_es" } : ami);
    }

    private string NomDuJeu(string id) => _session.NomDuJeu(id);

    // ── la composition ─────────────────────────────────────────────────────────────────────────

    /// <summary>Ce que le panneau montre, compose en bloc depuis l'etat courant.</summary>
    internal LeaderboardOverlayService.ContenuMultiplayer Composer()
    {
        var langue = Langue();
        string T(string cle) => _textes.Text(cle, langue);
        lock (_verrou)
        {
            return _mode == Mode.Resultat ? ComposerLeResultat(T) : ComposerLePanneau(T);
        }
    }

    private LeaderboardOverlayService.ContenuMultiplayer ComposerLePanneau(Func<string, string> T)
    {
        var jeu = _jeu;
        // LES DEUX GAMEROOMS (28/10) : FOR PLAYERS… montre la gameroom publique, WAITING FOR FRIENDS celle des amis ; pendant
        // le passage de l'une a l'autre, l'onglet n'en montre aucune.
        var surUneSalle = _modele.OngletCourant is MultiplayerPanelModel.Onglet.WaitingForPlayers or MultiplayerPanelModel.Onglet.WaitingForFriends;
        var room = RoomDuJeu() is { } salle && (!surUneSalle
            || salle.EntreAmis == (_modele.OngletCourant == MultiplayerPanelModel.Onglet.WaitingForFriends)) ? salle : null;
        var classique = (room?.Level ?? jeu?.Game.Level) != MultiplayerLevels.NelfePlay;
        var classe = jeu?.Game.EstClasse == true;
        var max = room?.Max ?? jeu?.Game.Players ?? 2;
        var presents = room?.Players.Count(joueur => joueur.Present) ?? 0;

        IReadOnlyList<LeaderboardOverlayService.JoueurMultiplayer> lignes = surUneSalle
            ? LesPlaces(room, max, T)
            : Invitables().Select(ami => Ami(ami, T)).ToList();
        _modele.PoserLesLignes(lignes.Count(ligne => !ligne.Libre));
        var (selection, selectionCourante) = surUneSalle ? LaBandeDesPersonnages() : (null, -1);

        var message = _refus is { Length: > 0 } refus ? T("multiplayer.refused." + refus) : string.Empty;
        if (message.StartsWith("multiplayer.refused.", StringComparison.Ordinal))
        {
            message = T("multiplayer.refused.other");
        }

        return new LeaderboardOverlayService.ContenuMultiplayer(
            MultiplayerPanelModel.Onglets.Select(MultiplayerPanelModel.Titre).ToList(),
            _modele.IndexOnglet,
            _modele.Etat == MultiplayerPanelModel.Foyer.Panneau,
            classique ? T("multiplayer.badge.classic") : string.Empty,
            classique ? T(classe ? "multiplayer.info.classic_ranked" : "multiplayer.info.classic") : LeRoulement(jeu?.Game, DateTimeOffset.UtcNow, T),
            presents + "/" + max,
            room is { State: "countdown", CountdownMs: { } ms } ? DateTime.UtcNow.AddMilliseconds(ms) : null,
            lignes,
            _modele.Ligne,
            message,
            string.Empty,
            AidesDuPanneau(T),
            Selection: selection,
            SelectionCourante: selectionCourante,
            Disposition: surUneSalle
                ? DispositionPour(max)
                : LeaderboardOverlayService.DispositionDesJoueurs.Liste,
            // « VOTRE SELECTION » en haut a gauche du panneau (demande du user du 26/10), les memes jeux que la carte d'ES.
            // Une gameroom d'amis s'y dit « [AMIS] » devant son compte (question du user du 28/10).
            MaSelection: _session.Etat.Rooms.Where(r => r.State is "waiting" or "countdown").Take(3)
                .Select(r => new LeaderboardOverlayService.JeuDeMaSelection(
                    NomDuJeu(r.Game), (r.EntreAmis ? "[" + Texte("multiplayer.selection.friends", "FRIENDS") + "]  " : string.Empty)
                        + r.Players.Count(j => j.Present) + "/" + r.Max,
                    RoomDuJeu() is { } courante && r.Room == courante.Room))
                .ToList(),
            TitreDeMaSelection: T("multiplayer.selection"));
    }

    /// <summary>
    /// LE ROULEMENT AU PANNEAU (24/10) : la valeur que l'hote ecrira si la partie part maintenant, et l'heure locale de la
    /// suivante (« LIGHTNING CUP · jusqu'a 16:00 »). Le nom est celui du jeu, jamais traduit.
    /// </summary>
    internal static string LeRoulement(MultiplayerGame? jeu, DateTimeOffset maintenant, Func<string, string> T)
        => jeu is { Level: MultiplayerLevels.NelfePlay, Rotation: { } roulement } && roulement.A(maintenant) is { } enCours
            ? enCours.Valeur.Name + " · " + T("multiplayer.rotation.until").Replace("{time}",
                enCours.Jusqua.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            : string.Empty;

    /// <summary>
    /// Deux places : deux cartes cote a cote ; trois ou quatre : une grille 2 x 2 ; au-dela, une ligne par joueur
    /// (retour du user du 22/10).
    /// </summary>
    internal static LeaderboardOverlayService.DispositionDesJoueurs DispositionPour(int places) => places switch
    {
        2 => LeaderboardOverlayService.DispositionDesJoueurs.Colonnes,
        3 or 4 => LeaderboardOverlayService.DispositionDesJoueurs.Grille,
        _ => LeaderboardOverlayService.DispositionDesJoueurs.Liste,
    };

    /// <summary>
    /// Une carte par place, numerotee comme la manette du netplay : les joueurs dans l'ordre d'arrivee, puis les places
    /// libres jusqu'au nombre de joueurs du jeu (retour du user du 22/10).
    /// </summary>
    private List<LeaderboardOverlayService.JoueurMultiplayer> LesPlaces(MultiplayerRoom? room, int max, Func<string, string> T)
    {
        // ▲▼ change son propre personnage tant que la gameroom attend ou decompte : sa carte le montre par des fleches.
        var choixOuvert = room?.State is "waiting" or "countdown" && _jeu is { ChoixOuverts: true };
        var lignes = (room?.Players ?? [])
            .Select((joueur, i) => Joueur(joueur, joueur.Present ? T("multiplayer.ready") : T("multiplayer.away"), T)
                with { Place = joueur.Seat ?? i + 1, ChoixModifiable = choixOuvert && joueur.You })
            .ToList();
        if (room is null)
        {
            return lignes;
        }

        // Une place libre de la gameroom d'amis attend un ami (28/10).
        var attente = room.EntreAmis ? Texte("multiplayer.seat.waiting_friend", "waiting for a friend") : T("multiplayer.seat.waiting");
        for (var place = lignes.Count + 1; place <= max; place++)
        {
            lignes.Add(new LeaderboardOverlayService.JoueurMultiplayer(T("multiplayer.seat.free"), string.Empty, string.Empty, false, false, false,
                null, attente, Place: place, Libre: true));
        }

        return lignes;
    }

    /// <summary>NIVEAU NELFEPLAY : tous les personnages du jeu en portraits, le choix du joueur en avant (▲▼).</summary>
    private (IReadOnlyList<LeaderboardOverlayService.PortraitDeChoix>?, int) LaBandeDesPersonnages()
    {
        if (_jeu is not { ChoixOuverts: true, Game: { Choices: { } choix } jeu })
        {
            return (null, -1);
        }

        var premier = choix[0];
        var rang = RangDuChoix(jeu);
        var mien = _session.MesChoix(jeu).GetValueOrDefault(premier.CleDu(rang), premier.DefautDu(rang));
        // Les personnages de MA place (Title Fight : chaque ecran a ses quatre boxeurs) ; avant d'etre assis, ceux du choix.
        var valeurs = premier.ValeursDe(RoomDuJeu()?.Players.FirstOrDefault(j => j.You)?.Seat);
        var bande = valeurs.Select(valeur => new LeaderboardOverlayService.PortraitDeChoix(PortraitDu(valeur), valeur.Name)).ToList();
        return (bande, valeurs.ToList().FindIndex(valeur => valeur.Value == mien));
    }

    /// <summary>
    /// L'image d'un choix : celle du Data Pack d'abord (23/10), deja sur la borne. A defaut, le badge RetroAchievements en
    /// cache, telecharge a la premiere demande (une image publique, sans cle) ; le chemin est rendu tout de suite, le
    /// panneau dessine le portrait des qu'il est la.
    /// </summary>
    private string? PortraitDu(MultiplayerChoiceValue? valeur)
    {
        if (valeur?.CheminDeLImage(DossierDuCatalogue) is { } image && File.Exists(image))
        {
            return image;
        }

        var badge = valeur?.RaBadge;
        if (_retroAchievements?.CheminDuBadge(badge) is not { } chemin)
        {
            return null;
        }

        if (!File.Exists(chemin) && _demandes.TryAdd("badge:" + badge, true))
        {
            _ = Task.Run(async () =>
            {
                // Deux a la fois : une bande de vingt portraits demandee d'un coup se faisait limiter par le serveur, et
                // un portrait manque ne revenait plus avant un redemarrage de l'API (retour du user du 22/10). Un echec
                // se reessaie une minute plus tard, au dessin suivant.
                await _telechargementsDePortraits.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _retroAchievements.TelechargerLeBadgeAsync(badge!, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    _journal?.LogDebug(ex, "MULTIPLAYER : portrait {Badge} en echec", badge);
                }
                finally
                {
                    _telechargementsDePortraits.Release();
                }

                if (!File.Exists(chemin))
                {
                    _journal?.LogInformation("MULTIPLAYER : portrait {Badge} non telecharge (media.retroachievements.org), nouvel essai dans une minute", badge);
                    await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                    _demandes.TryRemove("badge:" + badge, out _);
                }

                Rafraichir();
            });
        }

        return chemin;
    }

    /// <summary>
    /// La planche d'avatar d'un joueur : la sienne si la borne la detient, sinon demandee au relais (comme pour la foule
    /// d'un direct) ; le panneau se redessine quand elle arrive.
    /// </summary>
    private string? PlancheDe(MultiplayerAvatar? avatar)
    {
        if (_planches is null || avatar?.Sheet is not { } sha || !RetroBat.Api.Avatar.AvatarSheetStore.EstSha(sha))
        {
            return null;
        }

        if (!_planches.Has(sha) && _relaisDesPlanches is not null && _demandes.TryAdd("planche:" + sha, true))
        {
            _ = Task.Run(async () =>
            {
                await _relaisDesPlanches.RequestAsync(sha, CancellationToken.None, "avatar").ConfigureAwait(false);
                for (var essai = 0; essai < 15 && !_planches.Has(sha); essai++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }

                Rafraichir();
            });
        }

        return _planches.ObjectPath(sha);
    }

    private LeaderboardOverlayService.ContenuMultiplayer ComposerLeResultat(Func<string, string> T)
    {
        _connues.TryGetValue(_roomDuResultat, out var connue);
        var finie = _session.Etat.Rooms.FirstOrDefault(r => r.Room == _roomDuResultat) ?? connue;
        var classique = (finie?.Level ?? _jeu?.Game.Level) != MultiplayerLevels.NelfePlay;
        // Classee (23/10) : une rencontre versus a fin lue, quel que soit le niveau ; sa cote et son vainqueur s'affichent.
        // Annulee sur le bilan du wrapper (22/10) : ni vainqueur, ni cote, ni ecart.
        var annulee = finie?.Ended?.Result == "void";
        // Non disputee (26/10) : un joueur n'a pas joue ; comme une rencontre annulee, ni vainqueur ni cote.
        var nonDisputee = finie?.Ended?.Result == "unplayed";
        IReadOnlyList<int> placesDeLaRencontre = finie is null ? [] : MultiplayerLaunchService.PlacesDe(finie);
        var classe = _jeu?.Game is { } jeuDuResultat && (finie is null ? jeuDuResultat.EstClasse : jeuDuResultat.ClassePour(placesDeLaRencontre))
            && !annulee && !nonDisputee;
        // NIVEAU NELFEPLAY : la place gagnante lue chez l'hote (V24-16) ; le gagnant d'abord, une defaite presentee avec
        // le meme soin (00quater.3).
        var gagnante = classe ? finie?.Ended?.WinnerSeat : null;
        // UNE FIN A PLUSIEURS (CDC Classements v1.0 §7) : le rang de chaque humain, celui du site, sinon celui lu ici.
        var rangs = classe ? RangsDuResultat(finie) : null;
        var rangDe = (int? place) => place is not null && rangs?.FirstOrDefault(r => r.Seat == place) is { } r ? r : null;
        if (rangs is not null && gagnante is null)
        {
            gagnante = MultiplayerIssue.DesRangs(rangs).Gagnante;
        }
        var elo = finie?.Ended?.Elo;
        MultiplayerEvolution? EloDe(int? place)
            => place is not null && elo is not null && elo.TryGetValue(place.Value.ToString(CultureInfo.InvariantCulture), out var e) ? e : null;
        // L'ABANDON : celui qui part prend la defaite et perd des points, l'autre ne gagne ni ne perd rien.
        var abandon = classe ? finie?.Ended?.AbandonedSeat : null;
        // RANK-03 : la cote ne s'affiche qu'une fois le resultat valide, avec « Verification en cours » avant.
        var bandeauDuResultat = finie?.Ended?.Result switch
        {
            "pending" => T("multiplayer.result.pending"),
            "disputed" => T("multiplayer.result.disputed"),
            "void" => T("multiplayer.result.void_note"),
            "unplayed" => T("multiplayer.result.unplayed_note"),
            // Amicale (26/10) : une victoire deja comptee aujourd'hui contre cet adversaire ; le vainqueur reste montre.
            "friendly" => T("multiplayer.result.friendly_note"),
            _ => string.Empty,
        };
        // En cartes, P1 reste a gauche ; en liste, le gagnant d'abord.
        var joueursDuResultat = (finie?.Players ?? []).Where(j => j.Seat is not null || finie?.State != "finished").ToList();
        var disposition = DispositionPour(finie?.Max ?? joueursDuResultat.Count);
        var enListe = disposition == LeaderboardOverlayService.DispositionDesJoueurs.Liste;
        var joueurs = joueursDuResultat
            .OrderBy(j => !enListe ? 0 : rangs is not null ? rangDe(j.Seat)?.Rank ?? 99 : gagnante is not null && j.Seat == gagnante ? 0 : 1)
            .ThenBy(j => j.Seat ?? 99)
            .Select(j =>
            {
                // Le numero de la place est dans sa pastille : l'etat n'a plus a dire « P1 ».
                var ligne = Joueur(j with { Present = true }, string.Empty, T) with { Place = j.Seat };
                if (abandon is not null && j.Seat is not null)
                {
                    var perte = EloDe(j.Seat);
                    var ecart = perte is null ? 0 : (int)Math.Round(perte.After - perte.Before);
                    return ligne with
                    {
                        Issue = j.Seat == abandon ? T("multiplayer.result.abandon") : string.Empty,
                        Abandon = j.Seat == abandon,
                        Cote = perte is null ? ligne.Cote : "ELO " + Math.Round(perte.After).ToString(CultureInfo.InvariantCulture),
                        Evolution = perte is null ? string.Empty : ecart.ToString(CultureInfo.InvariantCulture),
                        Stats = StatsDe(finie, j.Seat, T),
                    };
                }

                if (gagnante is null || j.Seat is null)
                {
                    return ligne;
                }

                // La cote d'apres la rencontre, et son ecart (RANK-03 : seulement un resultat valide en porte une).
                var evolution = EloDe(j.Seat);
                var delta = evolution is null ? 0 : (int)Math.Round(evolution.After - evolution.Before);
                return ligne with
                {
                    // Le match nul (place 0, 23/10) : ni vainqueur ni vaincu. A plusieurs, chacun a son rang (« 2E PLACE »).
                    Issue = rangs is not null && rangDe(j.Seat) is { } rang
                        ? IssueDuRang(rang, rangs, gagnante, T)
                        : T(gagnante == MultiplayerResultats.MatchNul ? "multiplayer.result.draw"
                            : j.Seat == gagnante ? "multiplayer.result.victory" : "multiplayer.result.defeat"),
                    Gagnant = EstVainqueur(j.Seat, rangs, gagnante),
                    Cote = evolution is null ? ligne.Cote : "ELO " + Math.Round(evolution.After).ToString(CultureInfo.InvariantCulture),
                    Evolution = evolution is null ? string.Empty : (delta >= 0 ? "+" : "") + delta.ToString(CultureInfo.InvariantCulture),
                    Stats = StatsDe(finie, j.Seat, T),
                };
            })
            .ToList();
        var maPlace = finie?.Players.FirstOrDefault(j => j.You)?.Seat;
        var titre = annulee ? T("multiplayer.result.void")
            : nonDisputee ? T("multiplayer.result.unplayed")
            : classique && !classe ? T("multiplayer.result.classic")
            : abandon is not null && maPlace is not null ? T(maPlace == abandon ? "multiplayer.result.abandon" : "multiplayer.result.opponent_left")
            : rangs is not null && rangDe(maPlace) is { } monRang ? IssueDuRang(monRang with { Camp = null }, rangs, gagnante, T)
            : gagnante == MultiplayerResultats.MatchNul ? T("multiplayer.result.draw")
            : gagnante is not null && maPlace is not null ? T(maPlace == gagnante ? "multiplayer.result.victory" : "multiplayer.result.defeat")
            : T("multiplayer.result.title");
        if (abandon is not null && maPlace is not null)
        {
            // Celui qui part lit ou en est son compteur ; l'autre, que sa cote ne bouge pas.
            var conduite = _session.Etat.Conduite;
            bandeauDuResultat = maPlace == abandon
                ? _textes.Format("multiplayer.abandon.counter", Langue(),
                    ("n", (conduite?.Abandons ?? 1).ToString(CultureInfo.InvariantCulture)),
                    ("limit", (conduite?.Limit ?? 3).ToString(CultureInfo.InvariantCulture)))
                : T("multiplayer.abandon.opponent");
        }
        var duree = finie?.Ended?.DurationS ?? _dureeDuResultat;
        var info = duree is { } secondes
            ? T("multiplayer.result.playtime") + " " + TimeSpan.FromSeconds(secondes).ToString(secondes >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
            : string.Empty;
        // Une coop qui concourt (31/10/2026) : le classement en badge, et sa regle en note.
        var concourt = _jeu?.Game is { } jeuFini && MultiplayerScoring.Concourt(jeuFini);
        var ecranDeFin = ComposerLEcranDeFin(finie, joueursDuResultat, gagnante, EloDe,
            annulee || nonDisputee || (classique && !classe) || (abandon is not null && maPlace is not null) ? titre : null, titre,
            concourt ? MultiplayerScoring.Etiquette : classique ? T("multiplayer.badge.classic") : string.Empty,
            concourt ? Texte("multiplayer.end.onecc_note", "Each score counts up to its player's first continue.")
                : classique && !classe && !annulee && !nonDisputee ? T("multiplayer.result.classic_note") : bandeauDuResultat, duree, T, rangs);

        return new LeaderboardOverlayService.ContenuMultiplayer(
            [titre],
            0,
            false,
            classique ? T("multiplayer.badge.classic") : string.Empty,
            info,
            string.Empty,
            null,
            joueurs,
            -1,
            string.Empty,
            classique && !classe && !annulee && !nonDisputee ? T("multiplayer.result.classic_note") : bandeauDuResultat,
            [new(_glypheValider, T("multiplayer.help.replay")), new(_glypheAnnuler, T("multiplayer.help.close"))],
            PartageDesDegats: classe ? LePartage(finie, T) : null,
            Disposition: disposition,
            // En plein ecran, jusqu'a OK (retour du user du 23/10) : « OK » se lit dans toutes les langues.
            PleinEcran: true,
            BoutonOk: "OK",
            GlypheOk: _glypheValider,
            Fin: ecranDeFin);
    }

    /// <summary>
    /// L'ECRAN DE FIN v2 (CDC Ecrans v1.0) : le mot de fin du type (une rencontre annulee, un abandon ou une partie Classique
    /// gardent le titre du resultat), le vainqueur d'abord, puis chaque joueur avec son personnage, sa lettre et sa cote
    /// d'apres la rencontre, ses mesures, ses etoiles et sa distinction ; la jauge globale ; le fanart de ce jeu sur cette borne.
    /// </summary>
    private LeaderboardOverlayService.EcranDeFin ComposerLEcranDeFin(MultiplayerRoom? finie, IReadOnlyList<MultiplayerJoueur> joueurs, int? gagnante,
        Func<int?, MultiplayerEvolution?> eloDe, string? titreImpose, string titre, string badge, string note, int? duree, Func<string, string> T,
        IReadOnlyList<MultiplayerRang>? rangs = null)
    {
        var jeu = _jeu?.Game;
        var maPlace = finie?.Players.FirstOrDefault(j => j.You)?.Seat;
        var nul = gagnante == MultiplayerResultats.MatchNul;
        var monRang = rangs?.FirstOrDefault(r => r.Seat == maPlace);
        // A plusieurs (CDC Classements v1.0 §7) : VICTOIRE au rang 1, sinon le rang de cette borne (« 3E PLACE »).
        var mot = titreImpose
            ?? (monRang is not null && MultiplayerIssue.UnitesDe(rangs!) > 2 && monRang.Rank > 1 ? TexteDuRang(monRang.Rank, T)
                : nul ? T("multiplayer.end.draw")
                : gagnante is not null && maPlace is not null ? T(EstVainqueur(maPlace, rangs, gagnante) ? "multiplayer.end.victory" : "multiplayer.end.defeat")
                : titre);
        var vainqueur = nul ? null : gagnante;
        var assis = joueurs.Where(j => j.Seat is not null)
            .OrderBy(j => rangs?.FirstOrDefault(r => r.Seat == j.Seat)?.Rank ?? (vainqueur is not null && j.Seat == vainqueur ? 0 : 99))
            .ThenBy(j => j.Seat)
            .ToList();
        var (profil, valeurs) = ValeursDeLaRencontre(finie, assis.Select(j => j.Seat!.Value));
        var calcul = profil is not null && valeurs.Count > 0 ? MultiplayerEcranDeFin.Calculer(profil, valeurs, vainqueur, T) : null;
        var milliers = new NumberFormatInfo { NumberGroupSeparator = " ", NumberGroupSizes = [3] };
        var rang = 0;
        // UNE COOP QUI CONCOURT (31/10/2026) : le score de chaque place en premiere mesure, des qu'il arrive.
        var scores = jeu is not null && MultiplayerScoring.Concourt(jeu) ? finie?.Ended?.Scores ?? new Dictionary<string, IReadOnlyDictionary<string, MultiplayerScoreDeFin>>() : null;
        MultiplayerScoreDeFin? ScoreDe(int place, string regle)
            => scores is not null && scores.TryGetValue(place.ToString(CultureInfo.InvariantCulture), out var deLaPlace)
               && deLaPlace.TryGetValue(regle, out var score) && MultiplayerScoring.PlaceAffichee(score) is not null ? score : null;
        var meilleur = scores is null ? 0 : assis.Select(j => ScoreDe(j.Seat!.Value, "1cc-multi")?.Score ?? 0).DefaultIfEmpty(0).Max();
        var fiches = assis.Select(j =>
        {
            var place = j.Seat!.Value;
            var mesures = calcul?.Mesures.GetValueOrDefault(place) ?? [];
            if (scores is not null)
            {
                var score = ScoreDe(place, "1cc-multi");
                mesures = [new LeaderboardOverlayService.MesureDeFin("trophee", MultiplayerScoring.Etiquette, score?.Score,
                    score is null || meilleur <= 0 ? 0 : (double)score.Score / meilleur, score is not null && score.Score == meilleur && meilleur > 0,
                    Milliers: true, Suffixe: score is null ? string.Empty : MultiplayerScoring.PlaceAffichee(score)!), .. mesures.Take(2)];
            }
            var apres = eloDe(place);
            var cote = apres?.After ?? j.Rating?.Value;
            var ecart = apres is null ? 0 : (int)Math.Round(apres.After - apres.Before);
            return new LeaderboardOverlayService.JoueurDeFin(
                place,
                j.Pseudo.Length > 0 ? j.Pseudo : j.Handle,
                PersonnageDe(j),
                PortraitDeJoueur(j),
                cote is null ? string.Empty : j.Rating is { Provisional: true } ? "?" : MultiplayerCote.LettreDe(cote.Value),
                cote is null ? string.Empty : Math.Round(cote.Value).ToString("#,0", milliers),
                apres is null ? string.Empty : (ecart >= 0 ? "+" : "") + ecart.ToString(CultureInfo.InvariantCulture),
                EstVainqueur(place, rangs, vainqueur),
                rangs?.FirstOrDefault(r => r.Seat == place)?.Rank ?? (vainqueur is null ? null : ++rang),
                calcul?.Distinctions.GetValueOrDefault(place) ?? string.Empty,
                mesures,
                CoequipiersDe(j));
        }).ToList();

        // Le score de ce joueur, mis en avant a la place du vainqueur, des que le sien est arrive.
        LeaderboardOverlayService.ScoreDeFin? monScore = null;
        if (scores is not null && maPlace is { } moi && ScoreDe(moi, "1cc-multi") is { } mien)
        {
            var uneVie = ScoreDe(moi, "1lc-multi");
            var placeDuMien = MultiplayerScoring.PlaceAffichee(mien)!;
            monScore = new LeaderboardOverlayService.ScoreDeFin(
                Texte("multiplayer.end.your_score", "Your score"),
                MultiplayerScoring.Milliers(mien.Score),
                MultiplayerScoring.Etiquette + (placeDuMien.Length > 0 ? " · " + placeDuMien : string.Empty),
                uneVie is null ? string.Empty : MultiplayerScoring.EtiquetteUneVie + "  " + MultiplayerScoring.Milliers(uneVie.Score)
                    + (MultiplayerScoring.PlaceAffichee(uneVie) is { Length: > 0 } placeUneVie ? " · " + placeUneVie : string.Empty),
                mien.Status == "published" && mien.Rank == 1);
        }

        return new LeaderboardOverlayService.EcranDeFin(
            _roomDuResultat,
            StylesDesEcrans.TypeDe(jeu?.EloType, jeu?.Family),
            mot,
            jeu?.Name ?? string.Empty,
            duree is { } s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture) : string.Empty,
            FanartDuJeu(),
            fiches,
            calcul?.Jauge,
            note,
            badge,
            new(T("multiplayer.end.over"), T("multiplayer.end.winner"), T("multiplayer.end.tagline"), T("multiplayer.end.hold"),
                T("multiplayer.end.rematch"), T("multiplayer.end.quit")),
            _glypheValider,
            _glypheAnnuler,
            // E4 (26/10) : une police imposee par le catalogue l'emporte sur celle du type.
            jeu?.Ecran?.Police is { Length: > 0 } police ? police : null,
            monScore);
    }

    /// <summary>Les rangs d'une fin a plusieurs : ceux que le site a recus de l'hote, sinon ceux lus sur cette borne ; null en duel.</summary>
    private IReadOnlyList<MultiplayerRang>? RangsDuResultat(MultiplayerRoom? finie)
    {
        if (_jeu?.Game.RankingEnd is null)
        {
            return null;
        }

        if (finie?.Ended?.Ranks is { Count: > 0 } duSite)
        {
            return duSite.Select(r => new MultiplayerRang(r.Seat, r.Rank, r.Camp)).ToList();
        }

        lock (_verrou)
        {
            return _rangsVus;
        }
    }

    /// <summary>Cette place a-t-elle gagne ? Son rang le dit (tout le camp vainqueur), sinon la place gagnante ; jamais un match nul.</summary>
    internal static bool EstVainqueur(int? place, IReadOnlyList<MultiplayerRang>? rangs, int? gagnante)
        => gagnante is not null && gagnante != MultiplayerResultats.MatchNul
           && (rangs?.FirstOrDefault(r => r.Seat == place) is { } rang ? rang.Rank == 1 : place == gagnante);

    /// <summary>
    /// Ce que dit la ligne d'un joueur classe : VICTOIRE au rang 1 (MATCH NUL si plusieurs y sont sans camp commun), DEFAITE entre
    /// deux joueurs ou deux camps, sinon son rang (« 3E PLACE ») ; son equipe a la suite dans un jeu en equipes.
    /// </summary>
    internal static string IssueDuRang(MultiplayerRang rang, IReadOnlyList<MultiplayerRang> rangs, int? gagnante, Func<string, string> T)
    {
        var issue = rang.Rank == 1
            ? T(gagnante == MultiplayerResultats.MatchNul ? "multiplayer.result.draw" : "multiplayer.result.victory")
            : MultiplayerIssue.UnitesDe(rangs) > 2 ? TexteDuRang(rang.Rank, T) : T("multiplayer.result.defeat");
        if (rang.Camp is not { } camp)
        {
            return issue;
        }

        var equipe = T("multiplayer.result.team");
        var numero = camp.ToString(CultureInfo.InvariantCulture);
        return issue + " · " + (equipe == "multiplayer.result.team" ? numero : equipe.Replace("{camp}", numero));
    }

    /// <summary>« 2E PLACE », « 3RD PLACE »... (cles <c>multiplayer.rank.N</c>) ; « #N » si la cle manque encore au Data Pack.</summary>
    private static string TexteDuRang(int rang, Func<string, string> T)
    {
        var cle = "multiplayer.rank." + rang.ToString(CultureInfo.InvariantCulture);
        var texte = T(cle);
        return texte == cle ? "#" + rang.ToString(CultureInfo.InvariantCulture) : texte;
    }

    private string? _cheminDuFanart;
    private string? _fanart;

    /// <summary>
    /// Le fanart du jeu sur cette borne, sinon sa capture : lu une fois par jeu dans son gamelist. Un fanart ecarte au
    /// catalogue (suggestif, Final Fight) laisse la capture seule.
    /// </summary>
    private string? FanartDuJeu()
    {
        var chemin = _jeu?.Path;
        if (chemin != _cheminDuFanart)
        {
            _cheminDuFanart = chemin;
            _fanart = _jeu?.Game.Ecran?.Fanart == false
                ? RetroBat.Api.Media.MediaDuGamelist.Resoudre(chemin, "image")
                : RetroBat.Api.Media.MediaDuGamelist.Resoudre(chemin, "fanart", "image");
        }

        return _fanart;
    }

    /// <summary>Le profil des statistiques et les valeurs de chaque place qui en a.</summary>
    private (MultiplayerStatsProfil? Profil, Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>> Valeurs) ValeursDeLaRencontre(
        MultiplayerRoom? room, IEnumerable<int> places)
    {
        var valeurs = new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>();
        var profil = _mesuresVues?.Profil ?? (_jeu?.Game is { } jeu ? MultiplayerStatsProfil.Pour(jeu, out _) : null);
        if (profil is null)
        {
            return (null, valeurs);
        }

        foreach (var place in places)
        {
            if (ValeursDe(room, profil, place) is { } lues)
            {
                valeurs[place] = lues.Select(v => new MultiplayerEcranDeFin.ValeurDeFin(v.Mesure, v.Valeur, v.Etat)).ToList();
            }
        }

        return (profil, valeurs);
    }

    /// <summary>La part des degats infliges par chaque place d'un duel, a leurs couleurs (P1 a gauche).</summary>
    private LeaderboardOverlayService.Partage? LePartage(MultiplayerRoom? room, Func<string, string> T)
    {
        if (StatsDeLaRencontre(room) is not { } stats || !stats.TryGetValue("1", out var p1) || !stats.TryGetValue("2", out var p2)
            || p1.Damage + p2.Damage <= 0)
        {
            return null;
        }

        var couleur = (int place) => room.Players.FirstOrDefault(j => j.Seat == place) is { } j ? CouleurDe(j.Handle) : "8A93A6";
        return new LeaderboardOverlayService.Partage(p1.Damage / (double)(p1.Damage + p2.Damage), couleur(1), couleur(2), T("multiplayer.result.damage"));
    }

    private LeaderboardOverlayService.JoueurMultiplayer Joueur(MultiplayerJoueur joueur, string etat, Func<string, string> T)
        => new(
            joueur.Pseudo.Length > 0 ? joueur.Pseudo : joueur.Handle,
            joueur.Country,
            CouleurDe(joueur.Handle),
            joueur.Host,
            joueur.You,
            joueur.Present,
            Barres(joueur.RttMs),
            etat,
            T("multiplayer.host"),
            PersonnageDe(joueur),
            Avatar: PlancheDe(joueur.Avatar),
            Portrait: PortraitDeJoueur(joueur),
            Portraits: PortraitsDuJoueur(joueur),
            RangCourant: joueur.You && _jeu is { Game: var jeuDuJoueur } && AuChoixDuPersonnage() is not null ? RangDuChoix(jeuDuJoueur) - 1 : -1,
            // Versus classe : la cote du joueur sur ce jeu, a la suite de son pseudo ; « provisoire » pendant ses 10
            // premieres rencontres et tant qu'il n'a pas rencontre 3 adversaires differents (RANK-03).
            // Coop qui concourt (31/10) : son meilleur 1CC MULTI et sa place, a la place de la cote.
            Cote: joueur.Rating is { } cote ? cote.Lettre + " · ELO " + Math.Round(cote.Value).ToString(CultureInfo.InvariantCulture)
                : joueur.UnCreditMulti is { } score ? MultiplayerScoring.Etiquette + " " + MultiplayerScoring.ScoreEtRang(score)
                : string.Empty,
            Provisoire: joueur.Rating is { Provisional: true } ? T("multiplayer.rating.provisional") : string.Empty);

    /// <summary>NIVEAU NELFEPLAY : le portrait du personnage de ce joueur.</summary>
    private string? PortraitDeJoueur(MultiplayerJoueur joueur)
    {
        if (_jeu is not { ChoixOuverts: true, Game: { Choices: { } choix } jeu })
        {
            return null;
        }

        var premier = choix[0];
        var valeur = ValeurJouee(joueur, jeu, premier);
        return PortraitDu(premier.ValeursDe(joueur.Seat).FirstOrDefault(x => x.Value == valeur));
    }

    /// <summary>Plusieurs choix par place (v1.0 §8.2) : le portrait de chaque personnage de l'equipe, dans l'ordre des choix.</summary>
    private IReadOnlyList<string?>? PortraitsDuJoueur(MultiplayerJoueur joueur)
    {
        if (_jeu is not { ChoixOuverts: true, Game: { Choices: [{ Picks: > 1 } premier, ..] } jeu } || ChoixDe(joueur, jeu, premier) < 2)
        {
            return null;
        }

        return EquipeDe(joueur, jeu, premier).Select(valeur => PortraitDu(premier.ValeursDe(joueur.Seat).FirstOrDefault(x => x.Value == valeur))).ToList();
    }

    /// <summary>Les suivants de l'equipe de ce joueur (ecrans de lancement et de fin), ou null : un seul personnage.</summary>
    private IReadOnlyList<string?>? CoequipiersDe(MultiplayerJoueur joueur)
        => PortraitsDuJoueur(joueur) is { Count: > 1 } equipe ? equipe.Skip(1).ToList() : null;

    /// <summary>L'equipe de ce joueur : la sienne tout de suite (▲▼), celle de la gameroom pour les autres.</summary>
    private IReadOnlyList<int> EquipeDe(MultiplayerJoueur joueur, MultiplayerGame jeu, MultiplayerChoice premier)
    {
        var siens = joueur.You ? _session.MesChoix(jeu) : joueur.Choices;
        return premier.Equipe(rang => siens is not null && siens.TryGetValue(premier.CleDu(rang), out var v) ? v : null, ChoixDe(joueur, jeu, premier));
    }

    /// <summary>La valeur voulue par ce joueur : la sienne tout de suite (▲▼), celle de la gameroom pour les autres.</summary>
    private int ValeurVoulue(MultiplayerJoueur joueur, MultiplayerGame jeu, MultiplayerChoice premier)
        => joueur.You ? _session.MesChoix(jeu).GetValueOrDefault(premier.Key, premier.Default)
            : joueur.Choices is not null && joueur.Choices.TryGetValue(premier.Key, out var v) ? v
            : premier.Default;

    /// <summary>
    /// La valeur que l'hote ecrira pour ce joueur : un choix distinct deja pris par une place d'avant passe a la suivante
    /// libre (le meme calcul que <see cref="MultiplayerDepartPret.Ecritures"/>), le panneau montre donc le vrai soldat.
    /// </summary>
    private int ValeurJouee(MultiplayerJoueur joueur, MultiplayerGame jeu, MultiplayerChoice premier)
    {
        if (!premier.Distinct || joueur.Seat is not { } place || RoomDuJeu() is not { } salle)
        {
            return ValeurVoulue(joueur, jeu, premier);
        }

        var retenues = premier.Departager(salle.Players
            .Where(j => j.Seat is not null)
            .GroupBy(j => j.Seat!.Value)
            .Select(g => (g.Key, ValeurVoulue(g.First(), jeu, premier))));
        return retenues.TryGetValue(place, out var valeur) ? valeur : ValeurVoulue(joueur, jeu, premier);
    }

    private async Task ChangerDePersonnageAsync(MultiplayerRoom salle, MultiplayerGame jeu, int pas)
    {
        // Le choix distinct (Metal Slug 3) saute ce que les autres joueurs ont deja pris, comme le jeu.
        var premier = jeu.Choices![0];
        var prises = salle.Players
            .Where(joueur => !joueur.You)
            .Select(joueur => joueur.Choices is not null && joueur.Choices.TryGetValue(premier.Key, out var v) ? v : premier.Default)
            .ToHashSet();
        // En manette d'equipe : ce que ses coequipiers ont pris, tous leurs choix.
        if (jeu.TeamRelay is { } equipe)
        {
            var monCamp = salle.Players.FirstOrDefault(joueur => joueur.You)?.Seat is { } maPlace ? equipe.CampDe(maPlace) : null;
            prises = salle.Players.Where(joueur => !joueur.You && joueur.Seat is { } place && monCamp?.Seats.Contains(place) == true)
                .SelectMany(joueur => EquipeDe(joueur, jeu, premier)).ToHashSet();
        }

        // Le nom change tout de suite a l'ecran ; la gameroom le recoit dans la foulee.
        var envoi = _session.ChangerDeChoixAsync(salle.Room, jeu, pas, prises: prises, rang: RangDuChoix(jeu), nombre: MesChoixDansLaSalle(jeu));
        Rafraichir();
        var refus = await envoi.ConfigureAwait(false);
        if (refus is not null)
        {
            _journal?.LogInformation("MULTIPLAYER : choix de personnage refuse ({Raison})", refus);
        }

        Rafraichir();
    }

    /// <summary>
    /// Les statistiques d'une place au resultat (ecran 10) : les trois du profil du jeu (Execution, Efficacite, Maitrise),
    /// celles de la plateforme (l'hote) d'abord, sinon celles vues ici ; a defaut, les quatre compteurs d'avant les profils.
    /// </summary>
    private IReadOnlyList<LeaderboardOverlayService.StatDuJoueur>? StatsDe(MultiplayerRoom? room, int? place, Func<string, string> T)
    {
        if (place is not null && MesuresDe(room, place.Value, T) is { } mesures)
        {
            return mesures;
        }

        if (place is null || StatsDeLaRencontre(room) is not { } stats
            || !stats.TryGetValue(place.Value.ToString(CultureInfo.InvariantCulture), out var s))
        {
            return null;
        }

        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        return
        [
            new(T("multiplayer.stat.hits"), N(s.Hits)), new(T("multiplayer.stat.kos"), N(s.Kos)),
            new(T("multiplayer.stat.perfects"), N(s.Perfects)), new(T("multiplayer.stat.damage"), N(s.Damage)),
        ];
    }

    /// <summary>
    /// Les trois statistiques du profil pour une place, ou null sans profil ni mesure. Celles du site ne valent que pour le
    /// profil que cette borne connait (meme identifiant) : un autre profil se lirait de travers.
    /// </summary>
    private IReadOnlyList<LeaderboardOverlayService.StatDuJoueur>? MesuresDe(MultiplayerRoom? room, int place, Func<string, string> T)
    {
        var profil = _mesuresVues?.Profil ?? (_jeu?.Game is { } jeu ? MultiplayerStatsProfil.Pour(jeu, out _) : null);
        if (profil is null)
        {
            return null;
        }

        return ValeursDe(room, profil, place)?.Select(v => new LeaderboardOverlayService.StatDuJoueur(
                T("multiplayer.stat.category." + v.Mesure.Category),
                v.Etat == MultiplayerStatsValeur.Indisponible || v.Valeur is null
                    ? T("multiplayer.stat.unmeasured")
                    : v.Valeur.Value.ToString(CultureInfo.InvariantCulture) + " " + T(v.Mesure.Label).ToLowerInvariant()))
            .ToList();
    }

    /// <summary>
    /// Les valeurs du profil pour une place : celles de la plateforme (l'hote) si elles sont du meme profil, sinon celles vues
    /// ici ; null sans mesure. Une valeur que la plateforme n'a pas eue reste non mesuree.
    /// </summary>
    private IReadOnlyList<(MultiplayerStatsMesure Mesure, long? Valeur, string Etat)>? ValeursDe(MultiplayerRoom? room, MultiplayerStatsProfil profil, int place)
    {
        var cle = place.ToString(CultureInfo.InvariantCulture);
        if (room?.Ended?.Metrics is { Seats: { } sieges } envoyees && envoyees.Profile?.Id == profil.Id && sieges.TryGetValue(cle, out var parId))
        {
            return profil.Metrics.Select(m => parId.TryGetValue(m.Id, out var v) && v.State is MultiplayerStatsValeur.Mesuree or MultiplayerStatsValeur.Zero
                    ? (m, v.Value is { } d ? (long?) Math.Round(d) : null, v.State)
                    : (m, (long?) null, MultiplayerStatsValeur.Indisponible))
                .ToList();
        }

        if (_mesuresVues is { } vues && vues.Compteurs.ContainsKey(place))
        {
            return vues.MesuresDe(place).Select(v => (v.Mesure, v.Valeur, v.Etat)).ToList();
        }

        return null;
    }

    /// <summary>NIVEAU NELFEPLAY : le nom du personnage de ce joueur (le premier choix du catalogue), ou rien.</summary>
    private string PersonnageDe(MultiplayerJoueur joueur)
    {
        if (_jeu is not { ChoixOuverts: true, Game: { Choices: { } choix } jeu })
        {
            return string.Empty;
        }

        var premier = choix[0];
        // Plusieurs choix par place : l'equipe, dans l'ordre des choix.
        if (premier.Picks > 1)
        {
            return string.Join(" / ", EquipeDe(joueur, jeu, premier).Select(premier.NomDe));
        }

        // Le sien d'abord : ▲▼ le change ici avant que la gameroom le renvoie.
        return premier.NomDe(ValeurJouee(joueur, jeu, premier));
    }

    private LeaderboardOverlayService.JoueurMultiplayer Ami(MultiplayerAmi ami, Func<string, string> T)
        => new(ami.Pseudo.Length > 0 ? ami.Pseudo : ami.Handle, ami.Country, CouleurDe(ami.Handle), false, false,
            true, null, T("multiplayer.friend." + ami.State));

    private IReadOnlyList<LeaderboardOverlayService.Aide> AidesDuPanneau(Func<string, string> T)
    {
        if (_modele.Etat != MultiplayerPanelModel.Foyer.Panneau)
        {
            return [new("dpad_left", T("multiplayer.help.open")), new(_glypheAnnuler, T("multiplayer.help.close"))];
        }

        var aides = new List<LeaderboardOverlayService.Aide> { new("dpad_leftright", T("multiplayer.help.tab")) };
        if (_modele.OngletCourant == MultiplayerPanelModel.Onglet.InviteFriends && Invitables().Count > 0)
        {
            aides.Add(new("dpad_updown", T("multiplayer.help.choose")));
            aides.Add(new(_glypheValider, T("multiplayer.help.invite")));
        }
        else if (_modele.OngletCourant is MultiplayerPanelModel.Onglet.WaitingForPlayers or MultiplayerPanelModel.Onglet.WaitingForFriends
                 && _jeu is { ChoixOuverts: true })
        {
            aides.Add(new("dpad_updown", T("multiplayer.help.character")));
            if (_jeu.Game.Choices is [{ Picks: > 1 }, ..] && MesChoixDansLaSalle(_jeu.Game) > 1)
            {
                aides.Add(new(_glypheValider, Texte("multiplayer.help.next_pick", "next")));
            }
        }

        // X, chez l'ouvreur de la gameroom d'amis : le nombre de joueurs attendus (28/10).
        if (_modele.OngletCourant == MultiplayerPanelModel.Onglet.WaitingForFriends
            && RoomDuJeu() is { EntreAmis: true, Owner: true, GameMax: > 2, State: "waiting" or "countdown" })
        {
            aides.Add(new(_glypheX, Texte("multiplayer.help.players", "players")));
        }

        aides.Add(new(_glypheAnnuler, T("multiplayer.help.back")));
        aides.Add(new(_glypheAnnuler, T("multiplayer.help.leave")));
        return aides;
    }

    /// <summary>La latence vers le relais, en quatre barres.</summary>
    internal static int? Barres(int? rttMs) => rttMs switch
    {
        null or <= 0 => null,
        < 40 => 4,
        < 80 => 3,
        < 140 => 2,
        _ => 1,
    };

    internal static string CouleurDe(string poignee)
    {
        var somme = 0;
        foreach (var caractere in poignee)
        {
            somme = unchecked(somme * 31 + caractere);
        }

        return Couleurs[(int)((uint)somme % (uint)Couleurs.Length)];
    }

    private string Langue()
    {
        if (_langue.Length > 0)
        {
            return _langue;
        }

        try
        {
            _langue = _reglages.GetScrapingSettings().Language;
        }
        catch (Exception)
        {
            _langue = "en";
        }

        return _langue;
    }

    /// <summary>Un texte de l'interface dans la langue d'ES, ou `parDefaut` si le dictionnaire ne l'a pas encore.</summary>
    public string Texte(string cle, string parDefaut)
    {
        var texte = _textes.Text(cle, Langue());
        return texte.Length > 0 && texte != cle ? texte : parDefaut;
    }
}
