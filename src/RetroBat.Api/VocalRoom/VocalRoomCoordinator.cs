using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NelfePlay.VocalRoom.Protocol;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;

namespace RetroBat.Api.VocalRoom;

/// <summary>
/// LE VOCAL DANS LE CYCLE MULTIPLAYER (CDC NelfeVoice v1.3 §9, §9 bis ; contrat site lots B/C ; lot B du 12/11/2026).
///
/// <list type="number">
/// <item>Une partie a plusieurs demarre sur cette borne (room <c>launching</c> ou <c>playing</c>, jamais pendant l'attente
/// ni la navigation) : si le compte est lie a Discord, consentant, et que le site le permet, l'etat vocal de la room est lu
/// (<c>rooms[].voice</c> du sondage, sinon la route <c>session</c>).</item>
/// <item>Lobby <c>ready</c> et <c>join</c> = <c>auto</c> (ou <c>offer</c> accepte par le joueur, touche Y) : l'exe est lance,
/// le jeton court du joueur lui est passe (<c>auth.apply_token</c>), puis <c>session.attach</c>. La partie ne l'attend
/// jamais.</item>
/// <item>Fin de partie : le vocal reste pendant l'ecran de fin ; <c>stats shown</c>, <c>heartbeat</c> (15 s), <c>closed</c> ;
/// le site dit quand detacher (moins de deux joueurs encore sur l'ecran).</item>
/// <item>Ensuite <c>session.detach</c>, puis l'exe s'arrete (plus de session vocale).</item>
/// </list>
///
/// Le site decide (politique, membres, lobby) ; la borne n'est jamais plus permissive que lui (§9.3). Le micro part coupe.
/// </summary>
public sealed class VocalRoomCoordinator : BackgroundService
{
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeriodeDuProfil = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PeriodeDuProfilRefuse = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan Battement = TimeSpan.FromSeconds(15);

    /// <summary>Une room finie dont l'ecran de fin ne parait pas : le vocal ne reste pas plus (garde-fou local, §9.2).</summary>
    internal static readonly TimeSpan FinSansEcran = TimeSpan.FromMinutes(2);

    private readonly Func<IReadOnlyList<MultiplayerRoom>> _rooms;
    private readonly VocalRoomSiteClient _site;
    private readonly VocalRoomProcessService _processus;
    private readonly IOptionsMonitor<ApiExposeOptions>? _options;
    private readonly ILogger<VocalRoomCoordinator>? _journal;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private readonly object _verrou = new();
    private readonly HashSet<string> _ecartees = new(StringComparer.Ordinal);
    private SessionVocale? _session;
    private VocalProfil? _profil;
    private DateTime _profilLuUtc = DateTime.MinValue;
    private bool _microCoupe = true;

    /// <summary>
    /// Le dernier choix explicite du joueur pendant cette session (Y, reglage local) a ete de couper le micro : il passe
    /// avant le micro ouvert du site a chaque salon de la session, jusqu'a sa fin.
    /// </summary>
    private bool _coupeParLeJoueur;

    private bool _casqueCoupe;

    public VocalRoomCoordinator(
        MultiplayerSessionService multiplayer,
        VocalRoomSiteClient site,
        VocalRoomProcessService processus,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<VocalRoomCoordinator>? journal = null)
        : this(() => multiplayer.Etat.Rooms, site, processus, options, journal)
    {
    }

    internal VocalRoomCoordinator(
        Func<IReadOnlyList<MultiplayerRoom>> rooms,
        VocalRoomSiteClient site,
        VocalRoomProcessService processus,
        IOptionsMonitor<ApiExposeOptions>? options = null,
        ILogger<VocalRoomCoordinator>? journal = null)
    {
        _rooms = rooms;
        _site = site;
        _processus = processus;
        _options = options;
        _journal = journal;
        _processus.Relance += () => _ = Task.Run(ReprendreApresRelanceAsync);
        _processus.Change += _ => Signaler();
    }

    /// <summary>L'horloge (tests).</summary>
    internal Func<DateTime> Maintenant { get; set; } = () => DateTime.UtcNow;

    /// <summary>Ce que le panneau montre a change.</summary>
    public event Action? AffichageChange;

    private bool Actif => _options?.CurrentValue.NelfePlay is not { } np || (np.Enabled && np.Voice.Enabled);

    // ── la boucle ──────────────────────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TourAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Le vocal ne fait jamais tomber l'API ni la partie.
                _journal?.LogWarning(ex, "VOCAL : tour du coordinateur en erreur");
            }

            try
            {
                await Task.Delay(Periode, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>L'API s'arrete : la borne dit au site qu'elle quitte le vocal, l'exe s'arrete avec le service du processus.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        SessionVocale? s;
        lock (_verrou) s = _session;
        if (s is { Phase: Phase.Attachee })
        {
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _site.QuitterAsync(s.Room, s.Generation, "shutdown", limite.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Un tour : lire les rooms, ouvrir, suivre ou fermer la session vocale. Sous la porte (une action a la fois).</summary>
    internal async Task TourAsync(CancellationToken ct)
    {
        await _porte.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TourSansPorteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _porte.Release();
        }
    }

    private async Task TourSansPorteAsync(CancellationToken ct)
    {
        var rooms = _rooms();
        var s = Session;
        if (!Actif)
        {
            if (s is not null) await TerminerAsync("disabled", quitter: "user", ct).ConfigureAwait(false);
            return;
        }

        if (s is null)
        {
            var candidate = rooms.FirstOrDefault(r => r.State is "launching" or "playing" && !_ecartees.Contains(r.Room));
            if (candidate is null)
            {
                return;
            }

            var profil = await ProfilAsync(ct).ConfigureAwait(false);
            if (!profil.Permis)
            {
                // Pas de liaison, pas de consentement, preference « off », site sans vocal : rien, et on ne redemande pas
                // pour cette room.
                Ecarter(candidate.Room);
                _journal?.LogInformation("VOCAL : pas de vocal pour la room {Room} ({Raison})", candidate.Room, RaisonDuProfil(profil));
                return;
            }

            s = new SessionVocale(candidate.Room);
            lock (_verrou)
            {
                _session = s;
                _microCoupe = true;
                _coupeParLeJoueur = false;
            }

            _processus.NouvelleSession();
            await SuivreLaVoixAsync(s, candidate, ct).ConfigureAwait(false);
            Signaler();
            return;
        }

        var room = rooms.FirstOrDefault(r => r.Room == s.Room);
        if (room is null)
        {
            // La room n'est plus la (fermee, quittee) : on quitte le vocal.
            await TerminerAsync("room_gone", quitter: s.EcranMontre ? null : "user", ct, statsFermees: s.EcranMontre ? "lost" : null)
                .ConfigureAwait(false);
            return;
        }

        if (room.State is "waiting" or "countdown")
        {
            // L'hote s'est tu et la room relance : ce lancement-la est fini, le suivant prendra une nouvelle generation.
            await TerminerAsync("room_restarting", quitter: null, ct).ConfigureAwait(false);
            _ecartees.Remove(room.Room);
            return;
        }

        if (room.State == "finished")
        {
            s.FinieDepuisUtc ??= Maintenant();
            if (!s.EcranMontre && Maintenant() - s.FinieDepuisUtc > FinSansEcran)
            {
                await TerminerAsync("no_end_screen", quitter: null, ct, statsFermees: "lost").ConfigureAwait(false);
                return;
            }
        }

        // Un exe tombe et non relance : la session vocale s'arrete la (§9.3, jamais de reactivation secrete).
        if (s.Phase == Phase.Attachee && _processus.Etat.Processus == VocalRoomEtat.Indisponible)
        {
            await PerdreLeVocalAsync(s, _processus.Etat.Raison ?? "crashed", ct).ConfigureAwait(false);
        }

        // Le compte connecte n'est pas celui que le site a lie (lot D) : jamais d'appel, pas de second essai.
        if (s.Phase is Phase.Attente or Phase.Attachee && s.JetonDonne
            && _processus.Etat.DerniereErreur is "user_mismatch" or "user_unverified" && JetonValable(s))
        {
            await PerdreLeVocalAsync(s, _processus.Etat.DerniereErreur!, ct).ConfigureAwait(false);
            return;
        }

        // Le jeton refuse par Discord : un second essai par generation, pas plus.
        if (s.Phase is Phase.Attachee or Phase.Attente && JetonValable(s) && _processus.Etat.Voix == "AUTH_REQUIRED"
            && _processus.Etat.DerniereErreur == "token_rejected")
        {
            if (s.JetonsRedonnes < 1)
            {
                s.JetonsRedonnes++;
                await DonnerLeJetonAsync(s, ct).ConfigureAwait(false);
            }
            else
            {
                await PerdreLeVocalAsync(s, "token_rejected", ct).ConfigureAwait(false);
            }
        }

        if (Maintenant() >= s.ProchainAppelUtc && s.Phase is not (Phase.Indisponible or Phase.Terminee))
        {
            await SuivreLaVoixAsync(s, room, ct).ConfigureAwait(false);
        }

        if (s.EcranMontre && !s.StatsFermees && Maintenant() - s.DernierBattementUtc >= Battement && Session == s)
        {
            s.DernierBattementUtc = Maintenant();
            var battement = await _site.StatsAsync(s.Room, s.Generation, "heartbeat", null, ct).ConfigureAwait(false);
            if (battement.Ok && battement.ADetacher)
            {
                await TerminerAsync("stats_" + (battement.Lobby ?? "detach"), quitter: null, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lit l'etat vocal de la room (sondage, sinon route session) et agit.</summary>
    private async Task SuivreLaVoixAsync(SessionVocale s, MultiplayerRoom room, CancellationToken ct)
    {
        var voix = room.Voice;
        string? refus = null;
        if (voix is null)
        {
            var reponse = await _site.SessionAsync(s.Room, ct).ConfigureAwait(false);
            voix = reponse.Ok ? reponse.Voice : null;
            refus = reponse.Ok ? null : reponse.Reason;
        }

        s.ProchainAppelUtc = Maintenant() + (voix?.RetryMs is > 0 and < 60_000 ? TimeSpan.FromMilliseconds(voix.RetryMs.Value) : Periode);
        if (voix is null)
        {
            if (refus is "not_in_room" or "room_finished" or "voice_unsupported" or "unauthorized")
            {
                await PerdreLeVocalAsync(s, refus, ct).ConfigureAwait(false);
            }

            // offline et autres : on reessaie au prochain tour, sans rien couper.
            return;
        }

        await AppliquerAsync(s, voix, ct).ConfigureAwait(false);
    }

    /// <summary>Ce que la borne fait d'un etat vocal du site. Une generation plus ancienne est ignoree.</summary>
    internal async Task AppliquerAsync(SessionVocale s, MultiplayerVoix voix, CancellationToken ct)
    {
        if (voix.Generation < 1 || voix.Generation < s.Generation)
        {
            return;
        }

        if (voix.Generation > s.Generation)
        {
            if (s.Phase == Phase.Attachee)
            {
                // Une nouvelle generation de la meme room : jamais de reemploi aveugle de l'ancien lobby.
                await _processus.CommanderAsync(Commands.SessionDetach, new JsonObject { ["room_id"] = s.Room, ["generation"] = s.Generation })
                    .ConfigureAwait(false);
            }

            s.Generation = voix.Generation;
            s.Phase = Phase.Attente;
            s.JetonDonne = false;
            s.JetonsRedonnes = 0;
            s.Accepte = false;
            s.AEteAttachee = false;
        }

        s.Join = voix.Join;
        switch (voix.Status)
        {
            case "pending":
                // LE JETON DES PENDING (contrat §3) : le SDK se connecte pendant que le site cree le lobby ; l'appel partira
                // des ready. Seulement si le joueur est deja d'accord (auto, ou offre acceptee).
                if (s.Phase == Phase.Attente && (voix.Join == "auto" || s.Accepte) && !JetonValable(s))
                {
                    await PreconnecterAsync(s, ct).ConfigureAwait(false);
                }

                break;
            case "ready":
                s.Lobby = voix.LobbyId;
                if (s.Phase == Phase.Attente)
                {
                    if (voix.Join == "auto")
                    {
                        await AttacherAsync(s, voix.MicMuted, ct).ConfigureAwait(false);
                    }
                    else if (voix.Join == "offer")
                    {
                        s.Phase = s.Accepte ? s.Phase : Phase.Proposee;
                        if (s.Accepte) await AttacherAsync(s, voix.MicMuted, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await PerdreLeVocalAsync(s, voix.Reason ?? "policy_off", ct).ConfigureAwait(false);
                    }
                }
                else if (s.Phase == Phase.Proposee && s.Accepte)
                {
                    await AttacherAsync(s, voix.MicMuted, ct).ConfigureAwait(false);
                }

                break;
            case "closing":
            case "closed":
                await TerminerAsync("lobby_" + voix.Status, quitter: null, ct).ConfigureAwait(false);
                break;
            default:
                await PerdreLeVocalAsync(s, voix.Reason ?? voix.Status, ct).ConfigureAwait(false);
                break;
        }

        Signaler();
    }

    /// <summary>Lance l'exe si besoin, donne le jeton du joueur puis le salon. Rien ici ne retarde la partie.</summary>
    private async Task AttacherAsync(SessionVocale s, bool microCoupeDuSite, CancellationToken ct)
    {
        if (!Messages.TryParseLobbyId(s.Lobby, out _))
        {
            return;
        }

        // A chaque salon, le micro prend la valeur du site (micro ouvert des l'arrivee si le joueur l'a choisi sur /account),
        // sauf si le joueur l'a coupe pendant cette session ; jamais plus permissif que le site.
        lock (_verrou) _microCoupe = MicroAuSalon(microCoupeDuSite, _coupeParLeJoueur);
        if (!await _processus.DemarrerAsync(ct).ConfigureAwait(false))
        {
            await PerdreLeVocalAsync(s, _processus.Etat.Raison ?? "exe_unavailable", ct).ConfigureAwait(false);
            return;
        }

        if (!JetonValable(s) && !await DonnerLeJetonAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        bool micro, casque;
        lock (_verrou)
        {
            micro = _microCoupe;
            casque = _casqueCoupe;
        }

        var attache = await _processus.CommanderAsync(Commands.SessionAttach, new JsonObject
        {
            ["room_id"] = s.Room,
            ["generation"] = s.Generation,
            ["lobby_id"] = s.Lobby,
            ["auto_join"] = true,
            ["mic_muted"] = micro,
        }).ConfigureAwait(false);
        if (Messages.Bool(attache, "ok") != true)
        {
            await PerdreLeVocalAsync(s, Messages.Str(Messages.Obj(attache, "error"), "code") ?? "attach_failed", ct).ConfigureAwait(false);
            return;
        }

        // Le micro dit de nouveau, explicitement, au salon qui vient d'etre attache (l'exe l'a deja pris de session.attach ;
        // voice.mute garantit l'etat meme si l'exe en avait un autre).
        var muet = await _processus.CommanderAsync(Commands.VoiceMute, new JsonObject
        {
            ["muted"] = micro,
            ["room_id"] = s.Room,
            ["generation"] = s.Generation,
        }).ConfigureAwait(false);
        if (Messages.Bool(muet, "ok") != true)
        {
            _journal?.LogWarning("VOCAL : room {Room} generation {Generation}, voice.mute refuse a l'attachement ({Code})", s.Room,
                s.Generation, Messages.Str(Messages.Obj(muet, "error"), "code") ?? "sans reponse");
        }

        if (casque)
        {
            await _processus.CommanderAsync(Commands.VoiceDeafen, new JsonObject { ["deafened"] = true }).ConfigureAwait(false);
        }

        s.Phase = Phase.Attachee;
        s.AEteAttachee = true;
        _journal?.LogInformation("VOCAL : room {Room} generation {Generation} attachee (micro {Micro})", s.Room, s.Generation,
            micro ? "coupe" : "ouvert");
    }

    /// <summary>Le jeton a-t-il ete donne a l'exe qui tourne maintenant ? (un exe relance a perdu son compte)</summary>
    private bool JetonValable(SessionVocale s) => s.JetonDonne && s.JetonLancement == _processus.Lancement && _processus.Tourne;

    /// <summary>Lobby encore en creation : l'exe se lance et se connecte au compte, sans salon.</summary>
    private async Task PreconnecterAsync(SessionVocale s, CancellationToken ct)
    {
        if (!await _processus.DemarrerAsync(ct).ConfigureAwait(false))
        {
            await PerdreLeVocalAsync(s, _processus.Etat.Raison ?? "exe_unavailable", ct).ConfigureAwait(false);
            return;
        }

        if (await DonnerLeJetonAsync(s, ct).ConfigureAwait(false))
        {
            _journal?.LogInformation("VOCAL : room {Room} generation {Generation}, compte connecte pendant la creation du lobby",
                s.Room, s.Generation);
        }
    }

    /// <summary>Demande le jeton au site et le passe a l'exe ; il n'est garde nulle part ensuite.</summary>
    private async Task<bool> DonnerLeJetonAsync(SessionVocale s, CancellationToken ct)
    {
        var jeton = await _site.JetonAsync(s.Room, s.Generation, ct).ConfigureAwait(false);
        if (!jeton.Ok || string.IsNullOrEmpty(jeton.AccessToken))
        {
            await PerdreLeVocalAsync(s, jeton.Reason ?? "token_unavailable", ct).ConfigureAwait(false);
            return false;
        }

        var charge = new JsonObject
        {
            ["access_token"] = jeton.AccessToken,
            ["token_type"] = jeton.TokenType,
        };
        // L'identite que le site a verifiee a la liaison (contrat §3) : l'exe refuse tout appel si le compte connecte en
        // est un autre (voice.error user_mismatch).
        if (Messages.TryParseLobbyId(jeton.DiscordUserId, out _))
        {
            charge["expected_user_id"] = jeton.DiscordUserId;
        }

        var reponse = await _processus.CommanderAsync(Commands.AuthApplyToken, charge).ConfigureAwait(false);
        if (Messages.Bool(reponse, "ok") != true)
        {
            await PerdreLeVocalAsync(s, Messages.Str(Messages.Obj(reponse, "error"), "code") ?? "token_not_applied", ct).ConfigureAwait(false);
            return false;
        }

        s.JetonDonne = true;
        s.JetonLancement = _processus.Lancement;
        return true;
    }

    /// <summary>L'exe a ete relance apres un plantage : jeton et salon de nouveau, s'il y a une session attachee.</summary>
    private async Task ReprendreApresRelanceAsync()
    {
        await _porte.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Session is { Phase: Phase.Attachee } s)
            {
                s.Phase = Phase.Attente;
                await AttacherAsync(s, true, CancellationToken.None).ConfigureAwait(false);
                Signaler();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _journal?.LogWarning(ex, "VOCAL : reprise apres relance impossible");
        }
        finally
        {
            _porte.Release();
        }
    }

    /// <summary>Le vocal n'est pas (ou plus) possible pour cette generation : indicateur « indisponible », l'exe s'arrete.</summary>
    private async Task PerdreLeVocalAsync(SessionVocale s, string raison, CancellationToken ct)
    {
        var etaitAttachee = s.Phase == Phase.Attachee;
        s.Phase = Phase.Indisponible;
        s.JetonDonne = false;
        s.Raison = raison;
        _journal?.LogInformation("VOCAL : vocal indisponible pour la room {Room} ({Raison}) ; la partie continue", s.Room, raison);
        if (etaitAttachee)
        {
            await _processus.CommanderAsync(Commands.SessionDetach, new JsonObject { ["room_id"] = s.Room, ["generation"] = s.Generation })
                .ConfigureAwait(false);
        }

        if (etaitAttachee || s.AEteAttachee)
        {
            // Le site garde le membre (il peut revenir) mais sait qu'il est parti (contrat §5).
            var plantage = raison == "crashed" || _processus.Etat.Raison == "crashed";
            await _site.QuitterAsync(s.Room, s.Generation, plantage ? "crash" : "user", ct).ConfigureAwait(false);
            s.AEteAttachee = false;
        }

        await _processus.ArreterAsync("unavailable").ConfigureAwait(false);
        Signaler();
    }

    /// <summary>Fin de la session vocale : detach, l'exe s'arrete, la room n'est plus suivie.</summary>
    private async Task TerminerAsync(string raison, string? quitter, CancellationToken ct, string? statsFermees = null)
    {
        var s = Session;
        if (s is null)
        {
            return;
        }

        if (statsFermees is not null && s.EcranMontre && !s.StatsFermees && s.Generation > 0)
        {
            s.StatsFermees = true;
            await _site.StatsAsync(s.Room, s.Generation, "closed", statsFermees, ct).ConfigureAwait(false);
        }

        if (s.Phase == Phase.Attachee)
        {
            await _processus.CommanderAsync(Commands.SessionDetach, new JsonObject { ["room_id"] = s.Room, ["generation"] = s.Generation })
                .ConfigureAwait(false);
            if (quitter is not null)
            {
                await _site.QuitterAsync(s.Room, s.Generation, quitter, ct).ConfigureAwait(false);
            }
        }

        s.Phase = Phase.Terminee;
        lock (_verrou) _session = null;
        Ecarter(s.Room);
        _journal?.LogInformation("VOCAL : session vocale de la room {Room} terminee ({Raison})", s.Room, raison);
        await _processus.ArreterAsync("session_end").ConfigureAwait(false);
        Signaler();
    }

    // ── l'ecran de fin (§9.2) ──────────────────────────────────────────────────────────────────────

    /// <summary>L'ecran de fin de cette room s'affiche : le vocal reste, le site le sait.</summary>
    public Task EcranDeFinMontreAsync(string room) => SousLaPorteAsync(async ct =>
    {
        if (Session is not { } s || s.Room != room || s.Generation < 1 || s.EcranMontre)
        {
            return;
        }

        s.EcranMontre = true;
        s.DernierBattementUtc = Maintenant();
        var r = await _site.StatsAsync(s.Room, s.Generation, "shown", null, ct).ConfigureAwait(false);
        if (r.Ok && r.ADetacher)
        {
            await TerminerAsync("stats_" + (r.Lobby ?? "detach"), quitter: null, ct).ConfigureAwait(false);
        }
    });

    /// <summary>
    /// L'ecran de fin de cette room se ferme (<c>ok</c>, <c>cancel</c>, <c>replay</c>, <c>lost</c>, <c>process_exit</c>,
    /// <c>shutdown</c>) : <c>stats closed</c>, puis detach.
    /// </summary>
    public Task EcranDeFinFermeAsync(string room, string raison) => SousLaPorteAsync(async ct =>
    {
        if (Session is not { } s || s.Room != room)
        {
            return;
        }

        s.EcranMontre = true;
        await TerminerAsync("stats_closed_" + raison, quitter: null, ct, statsFermees: raison).ConfigureAwait(false);
    });

    // ── le joueur (touche Y du panneau) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Y court : rejoindre le vocal propose (<c>offer</c>), sinon couper ou rouvrir le micro. Y maintenu : couper ou rendre le
    /// son du vocal (casque). Rend vrai si le geste a servi.
    /// </summary>
    public Task<bool> GesteAsync(bool maintenu) => SousLaPorteAsync(async ct =>
    {
        if (Session is not { } s)
        {
            return false;
        }

        if (s.Phase == Phase.Proposee && !maintenu)
        {
            s.Accepte = true;
            s.Phase = Phase.Attente;
            _journal?.LogInformation("VOCAL : le joueur rejoint le vocal propose (room {Room})", s.Room);
            s.ProchainAppelUtc = DateTime.MinValue;
            if (_rooms().FirstOrDefault(r => r.Room == s.Room) is { } room)
            {
                await SuivreLaVoixAsync(s, room, ct).ConfigureAwait(false);
            }

            Signaler();
            return true;
        }

        if (s.Phase != Phase.Attachee)
        {
            return false;
        }

        if (maintenu)
        {
            bool casque;
            lock (_verrou) casque = _casqueCoupe = !_casqueCoupe;
            await _processus.CommanderAsync(Commands.VoiceDeafen, new JsonObject { ["deafened"] = casque }).ConfigureAwait(false);
        }
        else
        {
            bool micro;
            lock (_verrou) micro = _coupeParLeJoueur = _microCoupe = !_microCoupe;
            await _processus.CommanderAsync(Commands.VoiceMute, new JsonObject { ["muted"] = micro }).ConfigureAwait(false);
        }

        Signaler();
        return true;
    });

    /// <summary>Micro et casque reglables aussi par l'API locale (diagnostic, panneau web).</summary>
    public Task<bool> ReglerAsync(bool? microCoupe, bool? casqueCoupe) => SousLaPorteAsync(async _ =>
    {
        if (microCoupe is { } m)
        {
            lock (_verrou) _coupeParLeJoueur = _microCoupe = m;
            await _processus.CommanderAsync(Commands.VoiceMute, new JsonObject { ["muted"] = m }).ConfigureAwait(false);
        }

        if (casqueCoupe is { } c)
        {
            lock (_verrou) _casqueCoupe = c;
            await _processus.CommanderAsync(Commands.VoiceDeafen, new JsonObject { ["deafened"] = c }).ConfigureAwait(false);
        }

        Signaler();
        return true;
    });

    // ── ce que montre le panneau ───────────────────────────────────────────────────────────────────

    /// <summary>L'indicateur du panneau, ou null quand il n'y a rien a montrer (pas de session vocale).</summary>
    public VoixAffichee? Affichage
    {
        get
        {
            SessionVocale? s;
            bool micro, casque;
            lock (_verrou)
            {
                s = _session;
                micro = _microCoupe;
                casque = _casqueCoupe;
            }

            return s is null ? null : Afficher(s.Phase, s.Raison, _processus.Etat, micro, casque);
        }
    }

    /// <summary>
    /// Le micro a l'attachement d'un salon : coupe si le site le dit coupe, ou si le joueur l'a coupe pendant la session ;
    /// ouvert seulement si le site l'ouvre et que le joueur ne l'a pas coupe. Pur.
    /// </summary>
    internal static bool MicroAuSalon(bool microCoupeDuSite, bool coupeParLeJoueur) => microCoupeDuSite || coupeParLeJoueur;

    /// <summary>L'indicateur pour une phase de session et un etat de l'exe. Pur.</summary>
    internal static VoixAffichee? Afficher(Phase phase, string? raison, VocalRoomEtat exe, bool microCoupe, bool casqueCoupe) => phase switch
    {
        Phase.Terminee => null,
        Phase.Proposee => new VoixAffichee(EtatDeLaVoix.Propose, 0, microCoupe, casqueCoupe, null),
        Phase.Indisponible => new VoixAffichee(EtatDeLaVoix.Indisponible, 0, microCoupe, casqueCoupe, raison),
        Phase.Attente => new VoixAffichee(EtatDeLaVoix.Connexion, 0, microCoupe, casqueCoupe, null),
        _ => exe.Processus == VocalRoomEtat.Indisponible || exe.Voix == "ERROR"
                ? new VoixAffichee(EtatDeLaVoix.Indisponible, 0, microCoupe, casqueCoupe, exe.Raison)
            : exe.Voix == "IN_CALL"
                ? new VoixAffichee(EtatDeLaVoix.Connecte, exe.Participants, exe.MicroCoupe, exe.CasqueCoupe, null)
            : exe.Voix == "READY"
                ? new VoixAffichee(EtatDeLaVoix.Pret, 0, microCoupe, casqueCoupe, null)
                : new VoixAffichee(EtatDeLaVoix.Connexion, 0, microCoupe, casqueCoupe, null),
    };

    // ── outils ─────────────────────────────────────────────────────────────────────────────────────

    internal SessionVocale? Session
    {
        get { lock (_verrou) return _session; }
    }

    internal bool MicroCoupe
    {
        get { lock (_verrou) return _microCoupe; }
    }

    internal bool CasqueCoupe
    {
        get { lock (_verrou) return _casqueCoupe; }
    }

    private async Task<VocalProfil> ProfilAsync(CancellationToken ct)
    {
        var age = Maintenant() - _profilLuUtc;
        if (_profil is { } connu && age < (connu.Permis ? PeriodeDuProfil : PeriodeDuProfilRefuse))
        {
            return connu;
        }

        var lu = await _site.ProfilAsync(ct).ConfigureAwait(false);
        // Hors ligne : on garde ce qu'on savait (jamais plus permissif : un profil refuse reste refuse).
        if (!lu.Ok && lu.Reason == "offline" && _profil is not null)
        {
            return _profil;
        }

        _profil = lu;
        _profilLuUtc = Maintenant();
        return lu;
    }

    /// <summary>Une room qu'on ne suit plus (finie, refusee) ; la liste ne grandit pas sans fin sur une borne allumee des jours.</summary>
    private void Ecarter(string room)
    {
        if (_ecartees.Count >= 256) _ecartees.Clear();
        _ecartees.Add(room);
    }

    private static string RaisonDuProfil(VocalProfil p)
        => !p.Ok ? p.Reason ?? "profile_unavailable"
            : !p.VoiceSupported ? "voice_unsupported"
            : !p.DiscordLinked ? "not_linked"
            : !p.VoiceConsent ? "no_consent"
            : "policy_off";

    private async Task<T> SousLaPorteAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        await _porte.WaitAsync().ConfigureAwait(false);
        try
        {
            return await action(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _journal?.LogWarning(ex, "VOCAL : action en erreur");
            return default!;
        }
        finally
        {
            _porte.Release();
        }
    }

    private Task SousLaPorteAsync(Func<CancellationToken, Task> action)
        => SousLaPorteAsync<bool>(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        });

    private void Signaler()
    {
        try { AffichageChange?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _journal?.LogDebug(ex, "VOCAL : abonne de l'affichage"); }
    }

    internal enum Phase
    {
        /// <summary>Lobby en creation, ou decision attendue.</summary>
        Attente,
        /// <summary>Le site propose (<c>offer</c>) : le joueur accepte par Y.</summary>
        Proposee,
        /// <summary>Jeton donne et salon attache.</summary>
        Attachee,
        /// <summary>Pas de vocal pour cette generation (raison dans <see cref="SessionVocale.Raison"/>).</summary>
        Indisponible,
        Terminee,
    }

    internal sealed class SessionVocale(string room)
    {
        public string Room { get; } = room;
        public long Generation { get; set; }
        public string? Lobby { get; set; }
        public string Join { get; set; } = "none";
        public Phase Phase { get; set; } = Phase.Attente;
        public string? Raison { get; set; }
        public bool Accepte { get; set; }
        public bool JetonDonne { get; set; }

        /// <summary>Le lancement de l'exe qui a recu le jeton (voir <see cref="VocalRoomProcessService.Lancement"/>).</summary>
        public int JetonLancement { get; set; }

        /// <summary>Le salon a ete attache au moins une fois dans cette generation (le site le compte comme membre venu).</summary>
        public bool AEteAttachee { get; set; }
        public int JetonsRedonnes { get; set; }
        public DateTime? FinieDepuisUtc { get; set; }
        public bool EcranMontre { get; set; }
        public bool StatsFermees { get; set; }
        public DateTime DernierBattementUtc { get; set; }
        public DateTime ProchainAppelUtc { get; set; }
    }
}

/// <summary>Les etats de l'indicateur (§10) : pret, connexion, connecte, indisponible, et le vocal propose.</summary>
public enum EtatDeLaVoix
{
    Propose,
    Connexion,
    Pret,
    Connecte,
    Indisponible,
}

/// <summary>L'indicateur vocal du panneau : son etat, le nombre de voix, micro et casque. Jamais de nom de joueur.</summary>
public sealed record VoixAffichee(EtatDeLaVoix Etat, int Participants, bool MicroCoupe, bool CasqueCoupe, string? Raison);
