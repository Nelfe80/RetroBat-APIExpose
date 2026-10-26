using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Netplay;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE LANCEMENT D'UNE PARTIE MULTIPLAYER (CDC MULTIPLAYER v2.5, lot M4), sur l'ordre de la plateforme :
///
///   - la borne elue HOTE (la mieux connectee, V24-12) lance le jeu en hote, attend que sa session
///     paraisse au lobby, et la publie a la gameroom ;
///   - chaque INVITE attend cette session, puis rejoint a sa place ; la borne tape pour lui le mot de
///     passe que RetroArch demande a l'ecran.
///
/// Le coeur et ses options sont imposes par le catalogue, la place N est la manette N du netplay, et
/// RetroAchievements est coupe pour la seance. Tout ce qui est pose dans la configuration de RetroArch
/// est rendu a la fin de la partie, et au demarrage suivant si l'API s'est arretee entre-temps.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiplayerLaunchService : IHostedService
{
    /// <summary>La plateforme rend la gameroom a l'attente au bout de 90 s sans session : on publie avant.</summary>
    private static readonly TimeSpan PatienceLobby = TimeSpan.FromSeconds(85);

    private readonly MultiplayerSessionService _session;
    private readonly MultiplayerClient _client;
    private readonly MultiplayerPanelService _panneau;
    private readonly NetplayRelayPicker _relais;
    private readonly NetplayLobbyClient _lobby;
    private readonly ApiContext _contexte;
    private readonly IEventBus _bus;

    /// <summary>ES se tait pendant la partie : il ne sait pas qu'un jeu tourne (retour du user du 26/10).</summary>
    private readonly SonDEmulationStation? _sonDEs;

    /// <summary>Le bilan de fin de partie du wrapper : une rencontre classee s'annule sur triche (22/10).</summary>
    private readonly MultiplayerBilanDuWrapper _bilans;
    private readonly ILogger<MultiplayerLaunchService> _journal;
    private readonly object _verrou = new();
    private readonly HashSet<string> _lancees = new(StringComparer.Ordinal);
    private readonly MultiplayerDepartPret _depart;
    private readonly MultiplayerFinDeRencontre _fin;
    private readonly MultiplayerStatsMoteur _moteurDesStats;

    /// <summary>Le suivi des statistiques de la rencontre chez l'hote (profil du jeu) ; il rend ce qu'il a vu quand le guet s'arrete.</summary>
    private MultiplayerStatsSuivi? _suiviDesStats;

    /// <summary>Le meme suivi chez l'invite, pour son ecran de fin et son rapport.</summary>
    private MultiplayerStatsSuivi? _suiviDesStatsInvite;
    private string? _enCours;

    /// <summary>Le guet de la fin d'une rencontre NelfePlay, et la place gagnante qu'il a vue.</summary>
    private CancellationTokenSource? _guet;
    private int? _vainqueur;

    /// <summary>L'etat d'un depart pret pose dans l'emplacement reserve de RetroArch, retire a la fin de la partie.</summary>
    private string? _etatPose;

    /// <summary>La sortie du lanceur de la derniere partie : ses reglages se rendent alors (voir AttendreLaFinAsync).</summary>
    private Task? _sortie;

    public MultiplayerLaunchService(
        MultiplayerSessionService session,
        MultiplayerClient client,
        MultiplayerPanelService panneau,
        NetplayRelayPicker relais,
        NetplayLobbyClient lobby,
        ApiContext contexte,
        IEventBus bus,
        ILogger<MultiplayerLaunchService> journal,
        SonDEmulationStation? sonDEs = null)
    {
        _sonDEs = sonDEs;
        _session = session;
        _client = client;
        _panneau = panneau;
        _relais = relais;
        _lobby = lobby;
        _contexte = contexte;
        _bus = bus;
        _journal = journal;
        _bilans = new MultiplayerBilanDuWrapper(bus);
        _depart = new MultiplayerDepartPret(new CommandesRetroArchUdp());
        _fin = new MultiplayerFinDeRencontre(new CommandesRetroArchUdp());
        _moteurDesStats = new MultiplayerStatsMoteur(new CommandesRetroArchUdp());
    }

    private static string RacineRetroArch => Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch");

    private static string ConfigRetroArch => Path.Combine(RacineRetroArch, "retroarch.cfg");

    private static string OptionsDesCoeurs => Path.Combine(RacineRetroArch, "retroarch-core-options.cfg");

    private static string Journal => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "multiplayer-lancement.json");

    /// <summary>Une partie MULTIPLAYER tourne-t-elle sur cette borne ?</summary>
    public bool EnPartie
    {
        get { lock (_verrou) return _enCours is not null; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Une partie interrompue par l'arret de l'API a laisse ses reglages : on les rend.
            var rendus = RetroArchConfigEdit.RendreLeJournal(Journal);
            if (rendus > 0)
            {
                _journal.LogInformation("MULTIPLAYER : reglages d'une partie interrompue rendus ({Fichiers} fichiers)", rendus);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : reglages d'une partie interrompue non rendus");
        }

        _session.Change += SurChangement;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _session.Change -= SurChangement;
        return Task.CompletedTask;
    }

    private void SurChangement(MultiplayerEtat etat)
    {
        foreach (var room in etat.Rooms)
        {
            if (room.Launch is not { } lancement)
            {
                continue;
            }

            var hote = lancement.Role == "host" && room.State == "launching";
            var invite = lancement.Role == "guest" && room.State == "playing" && lancement.Netplay is not null;
            if (!hote && !invite)
            {
                continue;
            }

            lock (_verrou)
            {
                if (_enCours is not null || !_lancees.Add(room.Room))
                {
                    continue;
                }

                _enCours = room.Room;
            }

            _ = Task.Run(() => hote ? HebergerAsync(room) : RejoindreAsync(room));
            return;
        }
    }

    private async Task HebergerAsync(MultiplayerRoom room)
    {
        try
        {
            var jeu = _session.JeuDeLaRoom(room);
            if (jeu is null)
            {
                _journal.LogWarning("MULTIPLAYER : {Jeu} n'est plus sur cette borne, la gameroom {Room} est quittee", room.Game, room.Room);
                await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var relais = await _relais.ChoisirAsync().ConfigureAwait(false);
            var jeton = Hex(4);
            var motJoueur = Hex(4);
            var motSpectateur = Hex(4);
            var pseudo = MultiplayerLaunchPlan.Pseudo(MonPseudo(room), jeton);
            var partie = Lancer(room, jeu, MultiplayerLaunchPlan.ArgumentsHote(pseudo, motJoueur, motSpectateur, relais));
            if (partie is null)
            {
                await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // Une partie ouverte a d'autres joueurs n'entre pas au classement solo (charte de la partie certifiee).
            NetplayHostService.NoterOuverteAuxJoueurs();
            _ = Task.Run(() => PublierLaSessionAsync(room, jeu, jeton, motJoueur, motSpectateur));
            var duree = await AttendreLaFinAsync(partie).ConfigureAwait(false);
            RetirerLEtatPose();
            int? vainqueur;
            MultiplayerStatsSuivi? suivi;
            lock (_verrou)
            {
                _guet?.Cancel();
                _guet = null;
                vainqueur = _vainqueur;
                _vainqueur = null;
                suivi = _suiviDesStats;
                _suiviDesStats = null;
            }

            var mesures = suivi is null ? null : await suivi.Fin.ConfigureAwait(false);
            var stats = mesures?.AncienDesPlaces();
            // Le depart pret charge un etat chez l'hote : c'est le seul chargement permis.
            if (await JugerLeBilanAsync(room, jeu.Game, partie.Debut, DepartDeLaRoom(room, jeu) is null ? 0 : 1).ConfigureAwait(false) is { } motif)
            {
                await _client.TerminerAsync(room.Room, "voided", duree, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _client.TerminerAsync(room.Room, vainqueur is null ? "game_closed" : "match_over", duree, CancellationToken.None, vainqueur,
                    vainqueur is null ? null : stats, vainqueur is null ? null : mesures?.PourLeSite()).ConfigureAwait(false);
            }

            _panneau.MontrerLeResultat(room.Room, duree, stats, mesures);
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : hebergement de {Room} interrompu", room.Room);
        }
        finally
        {
            FinDuLancement(room.Room);
            await AttendreLaSortieAsync().ConfigureAwait(false);
            // Le decor des panneaux en jeu s'efface une fois la partie finie : il n'apparait pas parmi les decors d'ES.
            MultiplayerDecor.Effacer();
            lock (_verrou)
            {
                _enCours = null;
            }
        }
    }

    /// <summary>La session parait au lobby une quarantaine de secondes apres le lancement : on la publie a la gameroom.</summary>
    private async Task PublierLaSessionAsync(MultiplayerRoom room, MultiplayerInstalledGame jeu, string jeton, string motJoueur, string motSpectateur)
    {
        try
        {
            var session = await _lobby.AttendreAsync(jeton, PatienceLobby).ConfigureAwait(false);
            if (session is null)
            {
                // Sans session, personne ne rejoindra : l'hote ne joue pas seul en croyant qu'on va venir. La partie
                // se ferme, la gameroom est quittee, et les autres retrouvent l'attente.
                _journal.LogWarning("MULTIPLAYER : aucune session au lobby pour {Room} : la partie se ferme", room.Room);
                await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
                await RetroArchInvite.FermerAsync().ConfigureAwait(false);
                return;
            }

            // NIVEAU NELFEPLAY : le depart pret, AVANT que la session parte et que quiconque se connecte. Les invites
            // recevront l'etat de l'hote, choix compris ; un depart qui ne se verifie pas ne part pas du tout. Sans etat
            // pour ce contenu (Mario Kart Europe, l'etat est fait sur l'USA), la partie se joue en classique.
            var depart = DepartDeLaRoom(room, jeu);
            if (depart is null && jeu.Game.EstNelfePlay)
            {
                _journal.LogInformation("MULTIPLAYER : pas de depart pret de {Jeu} pour ce contenu a {Joueurs} joueurs : partie classique ({Room})",
                    jeu.Game.Id, JoueursDeLaRoom(room), room.Room);
            }

            if (depart is not null && !await PreparerLeDepartAsync(room, jeu, depart).ConfigureAwait(false))
            {
                await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
                await RetroArchInvite.FermerAsync().ConfigureAwait(false);
                return;
            }

            // Le mot de passe que RetroArch applique, relu dans sa configuration (voir NetplayHostService).
            var config = File.Exists(ConfigRetroArch) ? await File.ReadAllTextAsync(ConfigRetroArch).ConfigureAwait(false) : string.Empty;
            var (joueurReel, spectateurReel) = NetplayHostService.LireMotsDePasse(config);
            var publiee = new MultiplayerSessionNetplay
            {
                Relay = session.RelayHote,
                Port = session.RelayPort,
                Session = session.Id,
                PlayerPassword = joueurReel is { Length: > 0 } ? joueurReel : motJoueur,
                SpectatePassword = spectateurReel is { Length: > 0 } ? spectateurReel : motSpectateur,
                Core = jeu.Core?.Core ?? string.Empty,
                CoreVersion = session.VersionCoeur,
                Crc = NetplayContent.Empreinte(jeu.Path),
            };
            // La plateforme rend le meme 409 pour une salle quittee et pour une base occupee un instant : trois essais avant de
            // conclure au refus.
            var reponse = await _client.PublierNetplayAsync(room.Room, publiee, CancellationToken.None).ConfigureAwait(false);
            for (var essai = 2; essai <= 3 && reponse?.Ok != true; essai++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                reponse = await _client.PublierNetplayAsync(room.Room, publiee, CancellationToken.None).ConfigureAwait(false);
            }

            _journal.LogInformation("MULTIPLAYER : session {Session} publiee pour {Room} ({Statut})", session.Id, room.Room,
                reponse?.Ok == true ? "ok" : reponse is null ? "injoignable" : "refusee" + (reponse.Reason is { Length: > 0 } r ? " : " + r : string.Empty));
            if (reponse is { Ok: false })
            {
                // La plateforme refuse la session : la salle n'est plus en lancement (quittee, abandonnee). Personne ne pourra
                // rejoindre (25/10 : l'hote restait seul sur l'ecran des bolides, START sans effet). Comme sans session au
                // lobby, la partie se ferme.
                _journal.LogWarning("MULTIPLAYER : la plateforme refuse la session de {Room} : la partie se ferme", room.Room);
                await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
                await RetroArchInvite.FermerAsync().ConfigureAwait(false);
                return;
            }
            // Une rencontre classee (fin lue + type versus, 23/10), quel que soit le niveau : l'hote guette sa fin.
            if (reponse?.Ok == true && jeu.Game.EstClasse)
            {
                _ = Task.Run(() => GuetterLaFinAsync(room, jeu.Game));
            }
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : publication de la session de {Room} interrompue", room.Room);
        }
    }

    /// <summary>
    /// V24-16 : au niveau NelfePlay, la rencontre finie, la plateforme ferme le jeu (sans cela, le gagnant d'un SF2
    /// continuerait seul contre l'ordinateur). Le delai du catalogue laisse voir le vainqueur.
    /// </summary>
    private async Task GuetterLaFinAsync(MultiplayerRoom room, MultiplayerGame jeu)
    {
        CancellationToken jeton;
        lock (_verrou)
        {
            _guet?.Cancel();
            _guet = new CancellationTokenSource();
            jeton = _guet.Token;
        }

        var places = room.Players.Where(joueur => joueur.Seat is not null).Select(joueur => joueur.Seat!.Value).Distinct().ToList();
        if (ProfilDesStats(jeu) is { } profil)
        {
            lock (_verrou)
            {
                _suiviDesStats = _moteurDesStats.Suivre(profil, places, jeton);
            }
        }

        var vainqueur = await _fin.AttendreLaFinAsync(jeu, places, jeton).ConfigureAwait(false);
        if (vainqueur is null)
        {
            return;
        }

        lock (_verrou)
        {
            _vainqueur = vainqueur;
        }

        _journal.LogInformation("MULTIPLAYER : rencontre de {Jeu} finie, {Issue} ; le jeu se ferme dans {Delai} s",
            jeu.Id, vainqueur == MultiplayerResultats.MatchNul ? "match nul" : "la place " + vainqueur + " gagne", jeu.DelaiDeFermeture);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(jeu.DelaiDeFermeture), jeton).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // la partie s'est fermee avant
        }

        // Le noir monte sur le jeu, puis RetroArch se ferme : l'ecran de fin sortira du noir (retour du user du 23/10).
        await _panneau.FonduAuNoirAsync().ConfigureAwait(false);
        await RetroArchInvite.FermerAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Chez l'invite : le meme guet que l'hote, une fois sa partie lancee, puis le rapport de la place gagnante. Le guet
    /// attend d'avoir vu les compteurs au depart : avant de recevoir l'etat de l'hote, l'invite tourne sa propre demo.
    /// </summary>
    private async Task RapporterEnInviteAsync(MultiplayerRoom room, MultiplayerGame jeu, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var places = room.Players.Where(joueur => joueur.Seat is not null).Select(joueur => joueur.Seat!.Value).Distinct().ToList();
            // L'invite joue la meme partie : il compte les memes coups que l'hote, pour son propre ecran de fin (l'hote
            // d'une version plus ancienne n'en envoie pas, et celui qui part avant la fin n'envoie rien).
            MultiplayerStatsSuivi? suiviInvite = null;
            if (ProfilDesStats(jeu) is { } profil)
            {
                suiviInvite = _moteurDesStats.Suivre(profil, places, ct);
                lock (_verrou)
                {
                    _suiviDesStatsInvite = suiviInvite;
                }
            }

            var vainqueur = await _fin.AttendreLaFinAsync(jeu, places, ct).ConfigureAwait(false);
            if (vainqueur is null)
            {
                return;
            }

            // Ses statistiques partent avec son rapport : le site les compare a celles de l'hote (ecart journalise, sans suite en V1).
            var reponse = await _client.RapporterAsync(room.Room, vainqueur.Value, CancellationToken.None,
                suiviInvite?.Instantane().PourLeSite()).ConfigureAwait(false);
            _journal.LogInformation("MULTIPLAYER : rencontre de {Jeu} vue finie chez l'invite, place {Place} rapportee ({Statut})",
                jeu.Id, vainqueur, reponse?.Ok == true ? "ok" : reponse?.Reason ?? "injoignable");
            // Le noir monte en meme temps que chez l'hote, qui ferme le jeu au meme delai (retour du user du 23/10).
            await Task.Delay(TimeSpan.FromSeconds(jeu.DelaiDeFermeture), ct).ConfigureAwait(false);
            await _panneau.FonduAuNoirAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Le profil de statistiques du jeu (son fichier, sinon son bloc <c>stats.life</c>), ou null : rien a mesurer.</summary>
    private MultiplayerStatsProfil? ProfilDesStats(MultiplayerGame jeu)
    {
        var profil = MultiplayerStatsProfil.Pour(jeu, out var erreur);
        if (erreur is not null)
        {
            _journal.LogWarning("MULTIPLAYER : profil de statistiques de {Jeu} refuse ({Erreur}), repli sur son bloc de vie", jeu.Id, erreur);
        }
        return profil;
    }

    /// <summary>Le nombre de joueurs de la partie, celui des options du coeur (Lancer) : l'etat a autant de consoles que lui.</summary>
    private static int JoueursDeLaRoom(MultiplayerRoom room) => Math.Max(2, room.Launch?.Players ?? 0);

    /// <summary>Le depart pret de cette partie (niveau NelfePlay, nombre de joueurs, contenu), ou null : partie classique.</summary>
    private static MultiplayerStart? DepartDeLaRoom(MultiplayerRoom room, MultiplayerInstalledGame jeu) => jeu.DepartPour(JoueursDeLaRoom(room));

    /// <summary>Charge l'etat, ecrit le choix de chaque place, relit. Faux quand le depart ne se verifie pas.</summary>
    private async Task<bool> PreparerLeDepartAsync(MultiplayerRoom room, MultiplayerInstalledGame jeu, MultiplayerStart depart)
    {
        var etat = Path.Combine(RetroBatPaths.PluginRoot, "resources", "multiplayer", depart.State.Replace('/', Path.DirectorySeparatorChar));
        var choixParPlace = room.Players
            .Where(joueur => joueur.Seat is not null)
            .GroupBy(joueur => joueur.Seat!.Value)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.First().Choices);
        // Le pseudo de chaque place, ecrit dans le jeu quand le catalogue sait ou (F-Zero) ; le nom de compte a defaut.
        var pseudos = room.Players
            .Where(joueur => joueur.Seat is not null)
            .GroupBy(joueur => joueur.Seat!.Value)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.First() is var j && j.Pseudo.Length > 0 ? j.Pseudo : j.Handle);
        var resultat = await _depart.PreparerAsync(jeu.Game, etat, Path.GetFileNameWithoutExtension(jeu.Path), choixParPlace, CancellationToken.None, depart,
                pseudos)
            .ConfigureAwait(false);
        lock (_verrou)
        {
            _etatPose = resultat.Fichier;
        }

        if (!resultat.Ok)
        {
            _journal.LogWarning("MULTIPLAYER : depart pret de {Jeu} annule pour {Room} : {Raison}", jeu.Game.Id, room.Room, resultat.Raison);
            RetirerLEtatPose();
            return false;
        }

        _journal.LogInformation("MULTIPLAYER : depart pret de {Jeu} pose et verifie ({Places} places)", jeu.Game.Id, choixParPlace.Count);
        return true;
    }

    private void RetirerLEtatPose()
    {
        string? fichier;
        lock (_verrou)
        {
            fichier = _etatPose;
            _etatPose = null;
        }

        if (fichier is null)
        {
            return;
        }

        try
        {
            File.Delete(fichier);
            File.Delete(fichier + ".png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal.LogDebug(ex, "MULTIPLAYER : etat du depart pret non retire ({Fichier})", fichier);
        }
    }

    private async Task RejoindreAsync(MultiplayerRoom room)
    {
        try
        {
            var jeu = _session.JeuDeLaRoom(room);
            var session = room.Launch!.Netplay!;
            if (jeu is null)
            {
                _journal.LogWarning("MULTIPLAYER : {Jeu} n'est plus sur cette borne, la gameroom {Room} est quittee", room.Game, room.Room);
                await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // La borne invitee joue la partie de l'hote : le rapporteur ne soumettra pas le score du P1 sous son nom.
            NetplayGuestService.NoterInviteMultiplayer(room.Launch.Seat);
            var pseudo = MultiplayerLaunchPlan.Pseudo(MonPseudo(room), Hex(2));
            var partie = Lancer(room, jeu, MultiplayerLaunchPlan.ArgumentsInvite(pseudo, session));
            if (partie is null)
            {
                NetplayGuestService.OublierInviteMultiplayer();
                await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // NIVEAU NELFEPLAY : l'invite joue la meme partie que l'hote ; il guette les memes compteurs et rapporte la place
            // gagnante. Le resultat n'est valide, et la cote ne bouge, que si les deux concordent.
            using var guetInvite = new CancellationTokenSource();
            if (jeu.Game.EstClasse)
            {
                _ = Task.Run(() => RapporterEnInviteAsync(room, jeu.Game, guetInvite.Token));
            }

            // RetroArch demande le mot de passe a l'ecran : la borne le tape. Si l'hote part, la partie se ferme.
            var hoteParti = false;
            _ = Task.Run(() => RetroArchInvite.SuivreAsync(session.Relay, session.Port, session.PlayerPassword,
                async () =>
                {
                    hoteParti = true;
                    _journal.LogInformation("MULTIPLAYER : l'hote de {Room} est parti, la partie se ferme", room.Room);
                    await _panneau.FonduAuNoirAsync().ConfigureAwait(false);
                    await RetroArchInvite.FermerAsync().ConfigureAwait(false);
                }, _journal));
            var duree = await AttendreLaFinAsync(partie).ConfigureAwait(false);
            guetInvite.Cancel();
            NetplayGuestService.OublierInviteMultiplayer();
            // Un chargement d'etat fait ici se propage a l'hote sans y etre compte : l'invite juge son propre bilan.
            if (await JugerLeBilanAsync(room, jeu.Game, partie.Debut, 0).ConfigureAwait(false) is { } motif)
            {
                await _client.AnnulerAsync(room.Room, motif, CancellationToken.None).ConfigureAwait(false);
            }

            // En Classique, la partie finit quand tous les invites sont partis : la plateforme doit le savoir. Parti derriere
            // l'hote, cette borne le dit : elle parle parfois avant lui, et prenait alors l'abandon a sa place (26/10).
            await _client.QuitterAsync(room.Room, CancellationToken.None, hoteParti ? "host_gone" : null).ConfigureAwait(false);
            MultiplayerStatsSuivi? suiviInvite;
            lock (_verrou)
            {
                suiviInvite = _suiviDesStatsInvite;
                _suiviDesStatsInvite = null;
            }

            guetInvite.Cancel();
            var mesuresInvite = suiviInvite is null ? null : await suiviInvite.Fin.ConfigureAwait(false);
            _panneau.MontrerLeResultat(room.Room, duree, mesuresInvite?.AncienDesPlaces(), mesuresInvite);
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : arrivee dans {Room} interrompue", room.Room);
        }
        finally
        {
            FinDuLancement(room.Room);
            await AttendreLaSortieAsync().ConfigureAwait(false);
            // Le decor des panneaux en jeu s'efface une fois la partie finie : il n'apparait pas parmi les decors d'ES.
            MultiplayerDecor.Effacer();
            lock (_verrou)
            {
                _enCours = null;
            }
        }
    }

    private sealed record Partie(Process Lanceur, Dictionary<string, Dictionary<string, string?>> ARendre, DateTime Debut, GameReference Jeu);

    /// <summary>
    /// Dessine les panneaux des joueurs (pseudo, personnage choisi, lettre et cote, ping) et les ecrit comme un decor de
    /// RetroBat ; rend le nom du decor a passer au lanceur, ou null (jeu non concerne, ecran sans bandes, echec). Jamais
    /// bloquant : une partie sans panneaux reste une partie.
    /// </summary>
    private string? PoserLesPanneaux(MultiplayerRoom room, MultiplayerInstalledGame jeu, string systeme, int joueurs)
    {
        try
        {
            var place = room.Launch!.Seat;
            if (!MultiplayerDecor.Concerne(jeu.Game) || jeu.Game.VueDeLaPlace(place, joueurs) is not null
                || MultiplayerLaunchPlan.ShaderDeLaPlace(jeu.Game, place, joueurs) is not null)
            {
                return null;
            }

            var ecran = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            if (RetroBat.Api.Leaderboard.PanneauxEnJeu.Calculer(ecran.Width, ecran.Height, MultiplayerDecor.FormatArcade) is not { } bandes)
            {
                _journal.LogInformation("MULTIPLAYER : pas de panneaux en jeu, l'ecran {Largeur} x {Hauteur} n'a pas de bandes", ecran.Width, ecran.Height);
                return null;
            }

            var assis = room.Players.Where(j => j.Seat is not null).ToList();
            var personnages = PersonnagesDesPlaces(jeu, assis);
            var fiches = assis.Select(j =>
            {
                var siege = j.Seat!.Value;
                var (nom, image) = personnages.TryGetValue(siege, out var p) ? p : (string.Empty, null);
                var cote = j.Rating;
                return new RetroBat.Api.Leaderboard.PanneauxEnJeu.Joueur(
                    siege,
                    j.Pseudo.Length > 0 ? j.Pseudo : j.Handle,
                    nom,
                    image,
                    cote is null ? string.Empty : cote.Lettre,
                    cote is null ? string.Empty : MultiplayerDecor.Cote(cote.Value),
                    cote is null || cote.Provisional ? null : MultiplayerDecor.Palier(cote.Value),
                    j.RttMs,
                    cote?.Wins,
                    cote?.Streak);
            }).ToList();
            var type = RetroBat.Api.Leaderboard.StylesDesEcrans.TypeDe(jeu.Game.EloType, jeu.Game.Family);
            var fanart = RetroBat.Api.Media.MediaDuGamelist.Resoudre(jeu.Path, "fanart", "image");
            var mots = new RetroBat.Api.Leaderboard.PanneauxEnJeu.MotsDesPanneaux(
                _panneau.Texte("multiplayer.panel.wins", "WINS"), _panneau.Texte("multiplayer.panel.streak", "STREAK"));
            using var image = RetroBat.Api.Leaderboard.PanneauxEnJeu.Dessiner(ecran.Width, ecran.Height, bandes, type, fanart, fiches, mots);
            if (!MultiplayerDecor.Ecrire(systeme, jeu.Path, image, bandes))
            {
                return null;
            }

            _journal.LogInformation("MULTIPLAYER : panneaux en jeu poses (decor {Decor}, bandes {Gauche} et {Droite} pixels, {Joueurs} joueurs, fanart {Fanart})",
                MultiplayerDecor.Nom, bandes.Gauche, bandes.Droite, fiches.Count, fanart is null ? "absent" : "present");
            return MultiplayerDecor.Nom;
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : panneaux en jeu non poses");
            return null;
        }
    }

    /// <summary>
    /// NIVEAU NELFEPLAY : le personnage de chaque place (le premier choix du catalogue) et son image du Data Pack ; un choix
    /// distinct deja pris par une place d'avant passe au suivant libre, comme le depart pret l'ecrit.
    /// </summary>
    private static IReadOnlyDictionary<int, (string Nom, string? Image)> PersonnagesDesPlaces(MultiplayerInstalledGame jeu, IReadOnlyList<MultiplayerJoueur> assis)
    {
        var resultat = new Dictionary<int, (string, string?)>();
        if (!jeu.ChoixOuverts || jeu.Game.Choices is not { Count: > 0 } choix)
        {
            return resultat;
        }

        var premier = choix[0];
        var voulues = assis.GroupBy(j => j.Seat!.Value)
            .Select(g => (Place: g.Key, Valeur: g.First().Choices is { } c && c.TryGetValue(premier.Key, out var v) ? v : premier.Default))
            .ToList();
        IReadOnlyDictionary<int, int> retenues = premier.Distinct ? premier.Departager(voulues) : voulues.ToDictionary(v => v.Place, v => v.Valeur);
        foreach (var (place, valeur) in retenues)
        {
            var valeurDuChoix = premier.Values.FirstOrDefault(x => x.Value == valeur);
            var image = valeurDuChoix?.CheminDeLImage(MultiplayerStatsProfil.DossierDuCatalogue);
            resultat[place] = (valeurDuChoix?.Name ?? string.Empty, image is not null && File.Exists(image) ? image : null);
        }

        return resultat;
    }

    /// <summary>Pose les reglages, lance, et annonce la partie quand l'emulateur est la.</summary>
    private Partie? Lancer(MultiplayerRoom room, MultiplayerInstalledGame jeu, IReadOnlyList<string> netplay)
    {
        var lancement = room.Launch!;
        var systeme = NetplayHostService.SystemeDuChemin(jeu.Path, RetroBatPaths.RomsRoot);
        var coeur = jeu.Core?.Core ?? string.Empty;
        // Sans ES, les manettes se calculent comme ES le ferait : le lanceur n'en cherche aucune lui-meme (EsManettes).
        var choix = EsManettes.PourLancer(_journal);
        var manettes = choix.Arguments;
        // En netplay, chaque borne joue avec SON joueur 1 de RetroArch, quelle que soit sa place : si le panel n'y est pas,
        // ses commandes ne repondent pas (vu le 22/10 sur la seconde borne). Le journal dit donc qui est le joueur 1.
        _journal.LogInformation("MULTIPLAYER : manettes transmises au lanceur ({Source}) : {Manettes}", choix.Source, MultiplayerLaunchPlan.NomsDesManettes(manettes));
        var joueurs = Math.Max(2, lancement.Players);
        // Les panneaux en jeu (CDC Ecrans v1.0, lot E3) : un decor que RetroArch affiche lui-meme, dans les bandes.
        var decor = PoserLesPanneaux(room, jeu, systeme, joueurs);
        var arguments = MultiplayerLaunchPlan.Arguments(manettes, systeme, coeur, jeu.Path, jeu.Game, lancement.Seat, netplay, joueurs, decor);
        if (arguments is null)
        {
            _journal.LogWarning("MULTIPLAYER : lancement de {Jeu} refuse (systeme « {Systeme} », coeur « {Coeur} »)", jeu.Game.Id, systeme, coeur);
            return null;
        }

        _panneau.AnnoncerLeLancement(room, jeu);

        var imposees = MultiplayerLaunchPlan.OptionsDuCoeur(jeu.Core, Math.Max(2, lancement.Players), lancement.Seat);
        var optionsAvant = RetroArchConfigEdit.Poser(OptionsDesCoeurs, imposees);
        // Les DIP du jeu que le catalogue ne fixe pas reprennent la valeur par defaut du coeur, la meme sur toutes les
        // bornes : le netplay ne synchronise pas les options, et une difficulte ou un nombre de vies changes sur une
        // seule borne desynchroniseraient la partie. Les reglages du joueur sont rendus a la fin.
        if (MultiplayerLaunchPlan.PrefixeDesDip(jeu.Game, coeur) is { } prefixe)
        {
            foreach (var (cle, valeur) in RetroArchConfigEdit.Retirer(OptionsDesCoeurs,
                         cle => cle.StartsWith(prefixe, StringComparison.Ordinal) && !imposees.ContainsKey(cle)))
            {
                optionsAvant.TryAdd(cle, valeur);
            }
        }

        // L'ecran partage : la vue de cette place, a son format (« -ratio config » sur la ligne du lanceur).
        var vue = jeu.Game.VueDeLaPlace(lancement.Seat, joueurs);
        var reglagesDeLaPlace = new Dictionary<string, string>(MultiplayerLaunchPlan.Place(lancement.Seat), StringComparer.Ordinal);
        if (vue is not null)
        {
            reglagesDeLaPlace["video_aspect_ratio"] = MultiplayerLaunchPlan.FormatPourRetroArch(jeu.Game, vue);
        }

        var aRendre = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            [OptionsDesCoeurs] = optionsAvant,
            [ConfigRetroArch] = RetroArchConfigEdit.Poser(ConfigRetroArch, reglagesDeLaPlace),
        };
        RetroArchConfigEdit.Noter(Journal, aRendre);
        if (vue is not null)
        {
            PoserLesFichiersDeShader(MultiplayerLaunchPlan.FichiersDeLaVue(vue));
        }
        else if (jeu.Game.Screens > 1)
        {
            PoserLeShader(jeu.Game.EcranDeLaPlace(lancement.Seat), jeu.Game.Screens);
        }

        _journal.LogInformation("MULTIPLAYER : {Jeu} lance en {Role}, place {Place} sur {Joueurs}, coeur {Coeur}",
            jeu.Game.Id, lancement.Role, lancement.Seat, lancement.Players, coeur);
        // Jusqu'a l'arrivee de l'emulateur, la salle ne se quitte pas et le panneau ne se rouvre pas (voir LancementEnCours).
        var lanceA = DateTime.Now;
        _session.LancementEnCours = room.Room;
        _sonDEs?.Couper();
        var lanceur = NetplayLaunch.DemarrerDirectement(arguments, _journal);
        if (lanceur is null)
        {
            _sonDEs?.Rendre();
            FinDuLancement(room.Room);
            Rendre(aRendre);
            _panneau.Fermer();
            return null;
        }

        var reference = new GameReference { SystemId = systeme, GamePath = jeu.Path, GameName = jeu.Game.Name };
        _ = Task.Run(() => AnnoncerQuandLEmulateurEstLaAsync(reference, room.Room, lanceA, sansManette: manettes.Length == 0));
        return new Partie(lanceur, aRendre, DateTime.UtcNow, reference);
    }

    /// <summary>Le lancement de cette salle est fini (l'emulateur est la, ou la partie n'a pas pu partir).</summary>
    private void FinDuLancement(string room)
    {
        if (_session.LancementEnCours == room)
        {
            _session.LancementEnCours = null;
        }
    }

    /// <summary>
    /// SANS EMULATIONSTATION, PERSONNE NE DIT QUEL JEU TOURNE (voir le lancement a moteur impose). On pose le jeu
    /// nous-memes et on annonce son debut quand l'emulateur parait : avant, l'annonce fermerait la presentation
    /// « Les joueurs se synchronisent… » pendant que RetroArch charge encore. Seul compte un emulateur demarre apres ce
    /// lancement : un RetroArch deja ouvert (un banc, le 25/10) effacait la presentation 48 s avant le vrai jeu.
    /// </summary>
    private async Task AnnoncerQuandLEmulateurEstLaAsync(GameReference jeu, string room, DateTime lanceA, bool sansManette)
    {
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite && !EmulatorForeground.EmulateurDeJeuTourne(lanceA.AddSeconds(-1)))
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        FinDuLancement(room);
        _contexte.Ui.Running = jeu;
        _contexte.Ui.State = "playing";
        _panneau.EffacerPourLeJeu();
        await _bus.PublishAsync(new EventEnvelope
        {
            Type = "ui.game.started",
            Payload = new { Source = "multiplayer", jeu.SystemId, jeu.GamePath, jeu.GameName, Direct = true },
        }).ConfigureAwait(false);
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();
        if (sansManette)
        {
            await PrevenirSansManetteAsync().ConfigureAwait(false);
        }
    }

    private const string MessageSansManetteParDefaut =
        "No controller found for this game: only the keyboard responds. Plug in a controller, set it up in EmulationStation, then start the game again.";

    /// <summary>
    /// AUCUNE MANETTE POUR CETTE PARTIE (decision du user du 26/10 : « utilise les messages retroarch ») : ni ES, ni le
    /// calcul, ni les journaux n'en donnent une, et le lanceur n'en cherche pas. Le joueur l'apprend dans RetroArch, le
    /// jeu charge, trois fois : RetroArch ne tient un message que trois secondes.
    /// </summary>
    private async Task PrevenirSansManetteAsync()
    {
        var commandes = new CommandesRetroArchUdp();
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite)
        {
            var statut = await commandes.DemanderAsync("GET_STATUS", CancellationToken.None).ConfigureAwait(false);
            if (statut is not null && (statut.Contains("PLAYING", StringComparison.Ordinal) || statut.Contains("PAUSED", StringComparison.Ordinal)))
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        var texte = _panneau.Texte("multiplayer.no_controller", MessageSansManetteParDefaut);
        _journal.LogWarning("MULTIPLAYER : aucune manette pour cette partie, le joueur est prevenu dans RetroArch");
        for (var i = 0; i < 3; i++)
        {
            await commandes.EnvoyerAsync("SHOW_MSG " + texte.ReplaceLineEndings(" ").Trim(), CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(3200).ConfigureAwait(false);
        }
    }

    /// <summary>Attend que le lanceur rende la main, rend les reglages et annonce la fin. Rend la duree, en secondes.</summary>
    /// <summary>
    /// Le bilan du wrapper de cette partie, attendu 4 s apres sa fin et journalise. Rend le motif d'annulation d'une
    /// rencontre classee (triche, retour arriere, avance rapide, execution anticipee, chargement d'etat en trop), ou null.
    /// </summary>
    private async Task<string?> JugerLeBilanAsync(MultiplayerRoom room, MultiplayerGame jeu, DateTime debut, int chargementsPermis)
    {
        var bilan = await _bilans.AttendreAsync(debut, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        if (bilan is not { } b)
        {
            _journal.LogInformation("MULTIPLAYER : pas de bilan du wrapper pour {Room} ({Jeu})", room.Room, jeu.Id);
            return null;
        }

        var (annulee, motif) = MultiplayerBilanDuWrapper.Juger(b, chargementsPermis);
        _journal.LogInformation("MULTIPLAYER : bilan du wrapper pour {Jeu} : {Bilan}{Verdict}", jeu.Id, MultiplayerBilanDuWrapper.Resume(b),
            annulee ? " ; rencontre annulee (" + motif + ")" : string.Empty);
        return annulee && jeu.EstClasse ? motif : null;
    }

    /// <summary>
    /// LA PARTIE EST FINIE QUAND LE COEUR EST DECHARGE (retour du user du 23/10 : « j'etais devant ES pendant 10 secondes
    /// avec netplay disconnected »). RetroArch rend alors son bilan au wrapper, puis plante en sortant (0xC0000005) et ne
    /// rend la main au lanceur qu'une dizaine de secondes plus tard. Le resultat n'attend plus cette sortie ; les reglages
    /// se rendent, et la fin du jeu s'annonce, quand le lanceur est vraiment sorti (RetroArch ecrit encore en partant).
    /// </summary>
    private async Task<int> AttendreLaFinAsync(Partie partie)
    {
        var sortie = SortirAsync(partie);
        lock (_verrou)
        {
            _sortie = sortie;
        }

        using var arret = new CancellationTokenSource();
        var coeurDecharge = _bilans.AttendreAsync(partie.Debut, Timeout.InfiniteTimeSpan, arret.Token);
        await Task.WhenAny(sortie, coeurDecharge).ConfigureAwait(false);
        arret.Cancel();
        if (!sortie.IsCompleted && EmulatorForeground.RenduEnCours)
        {
            // Le RetroArch cache d'un rendu video a peut-etre rendu ce bilan : seule la sortie du lanceur fait foi.
            await sortie.ConfigureAwait(false);
        }

        var duree = (int)Math.Max(0, (DateTime.UtcNow - partie.Debut).TotalSeconds);
        _journal.LogInformation("MULTIPLAYER : partie de {Jeu} finie apres {Duree} s{Detail}", partie.Jeu.GameName, duree,
            sortie.IsCompleted ? string.Empty : " (coeur decharge, RetroArch pas encore sorti)");
        return duree;
    }

    private async Task SortirAsync(Partie partie)
    {
        try
        {
            await partie.Lanceur.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Le lanceur a deja disparu.
        }

        // Le son d'ES revient au premier appui : cet appui le sort aussi de son ecran de veille.
        _sonDEs?.RendreAuProchainAppui();
        Rendre(partie.ARendre);
        if (ReferenceEquals(_contexte.Ui.Running, partie.Jeu))
        {
            _contexte.Ui.Running = null;
            _contexte.Ui.State = "browsing";
        }

        await _bus.PublishAsync(new EventEnvelope
        {
            Type = "ui.game.ended",
            Payload = new { Source = "multiplayer", partie.Jeu.SystemId, partie.Jeu.GamePath, partie.Jeu.GameName, Direct = true },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// La sortie du lanceur de la derniere partie, attendue avant de liberer la borne : une partie suivante poserait ses
    /// reglages, et la sortie en retard y remettrait ceux d'avant.
    /// </summary>
    private async Task AttendreLaSortieAsync()
    {
        Task? sortie;
        lock (_verrou)
        {
            sortie = _sortie;
        }

        if (sortie is null)
        {
            return;
        }

        try
        {
            await sortie.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _journal.LogWarning("MULTIPLAYER : le lanceur n'est pas sorti 60 s apres la partie, la borne se libere quand meme");
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : sortie du lanceur en echec");
        }
    }

    private void Rendre(Dictionary<string, Dictionary<string, string?>> aRendre)
    {
        foreach (var (fichier, avant) in aRendre)
        {
            try
            {
                RetroArchConfigEdit.Rendre(fichier, avant);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _journal.LogWarning(ex, "MULTIPLAYER : reglages de {Fichier} non rendus", Path.GetFileName(fichier));
                return;   // le journal reste : le prochain demarrage reessaiera
            }
        }

        try
        {
            File.Delete(Journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Le shader de recadrage, ecrit une fois sous les shaders de RetroBat (dossier nelfeplay).</summary>
    private void PoserLeShader(int ecran, int ecrans) => PoserLesFichiersDeShader(MultiplayerLaunchPlan.FichiersDeShader(ecran, ecrans));

    private void PoserLesFichiersDeShader(IReadOnlyDictionary<string, string> fichiers)
    {
        var dossier = Path.Combine(RetroBatPaths.RetroBatRoot, "system", "shaders", MultiplayerLaunchPlan.DossierShaders);
        try
        {
            Directory.CreateDirectory(dossier);
            foreach (var (nom, contenu) in fichiers)
            {
                var chemin = Path.Combine(dossier, nom);
                if (!File.Exists(chemin) || File.ReadAllText(chemin) != contenu)
                {
                    File.WriteAllText(chemin, contenu);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : shader de recadrage non ecrit, la borne affichera les deux ecrans");
        }
    }

    private static string MonPseudo(MultiplayerRoom room)
        => room.Players.FirstOrDefault(joueur => joueur.You)?.Pseudo ?? string.Empty;

    private static string Hex(int octets) => Convert.ToHexString(RandomNumberGenerator.GetBytes(octets)).ToLowerInvariant();
}
