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
    private readonly ILogger<MultiplayerLaunchService> _journal;
    private readonly object _verrou = new();
    private readonly HashSet<string> _lancees = new(StringComparer.Ordinal);
    private readonly MultiplayerDepartPret _depart;
    private readonly MultiplayerFinDeRencontre _fin;
    private readonly MultiplayerStatsDuDuel _statsDuDuel;

    /// <summary>Le suivi des statistiques du duel en cours chez l'hote ; il rend ce qu'il a vu quand le guet s'arrete.</summary>
    private Task<IReadOnlyDictionary<int, StatsDeLaPlace>>? _suiviDesStats;
    private string? _enCours;

    /// <summary>Le guet de la fin d'une rencontre NelfePlay, et la place gagnante qu'il a vue.</summary>
    private CancellationTokenSource? _guet;
    private int? _vainqueur;

    /// <summary>L'etat d'un depart pret pose dans l'emplacement reserve de RetroArch, retire a la fin de la partie.</summary>
    private string? _etatPose;

    public MultiplayerLaunchService(
        MultiplayerSessionService session,
        MultiplayerClient client,
        MultiplayerPanelService panneau,
        NetplayRelayPicker relais,
        NetplayLobbyClient lobby,
        ApiContext contexte,
        IEventBus bus,
        ILogger<MultiplayerLaunchService> journal)
    {
        _session = session;
        _client = client;
        _panneau = panneau;
        _relais = relais;
        _lobby = lobby;
        _contexte = contexte;
        _bus = bus;
        _journal = journal;
        _depart = new MultiplayerDepartPret(new CommandesRetroArchUdp());
        _fin = new MultiplayerFinDeRencontre(new CommandesRetroArchUdp());
        _statsDuDuel = new MultiplayerStatsDuDuel(new CommandesRetroArchUdp());
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
            Task<IReadOnlyDictionary<int, StatsDeLaPlace>>? suivi;
            lock (_verrou)
            {
                _guet?.Cancel();
                _guet = null;
                vainqueur = _vainqueur;
                _vainqueur = null;
                suivi = _suiviDesStats;
                _suiviDesStats = null;
            }

            var stats = suivi is null ? null : await suivi.ConfigureAwait(false);
            await _client.TerminerAsync(room.Room, vainqueur is null ? "game_closed" : "match_over", duree, CancellationToken.None, vainqueur,
                vainqueur is null ? null : stats).ConfigureAwait(false);
            _panneau.MontrerLeResultat(room.Room, duree);
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : hebergement de {Room} interrompu", room.Room);
        }
        finally
        {
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
            // recevront l'etat de l'hote, choix compris ; un depart qui ne se verifie pas ne part pas du tout.
            if (jeu.Game.EstNelfePlay && !await PreparerLeDepartAsync(room, jeu).ConfigureAwait(false))
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
            var reponse = await _client.PublierNetplayAsync(room.Room, publiee, CancellationToken.None).ConfigureAwait(false);
            _journal.LogInformation("MULTIPLAYER : session {Session} publiee pour {Room} ({Statut})", session.Id, room.Room, reponse?.Ok == true ? "ok" : reponse?.Reason ?? "injoignable");
            if (reponse?.Ok == true && jeu.Game is { EstNelfePlay: true, End: not null })
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
        if (jeu.Stats?.Life is not null)
        {
            lock (_verrou)
            {
                _suiviDesStats = _statsDuDuel.SuivreAsync(jeu, places, jeton);
            }
        }

        var vainqueur = await _fin.AttendreLeVainqueurAsync(jeu.End!, places, jeton).ConfigureAwait(false);
        if (vainqueur is null)
        {
            return;
        }

        lock (_verrou)
        {
            _vainqueur = vainqueur;
        }

        _journal.LogInformation("MULTIPLAYER : rencontre de {Jeu} finie, la place {Place} gagne ; le jeu se ferme dans {Delai} s",
            jeu.Id, vainqueur, jeu.End!.CloseAfterS);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(jeu.End.CloseAfterS), jeton).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // la partie s'est fermee avant
        }

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
            var vainqueur = await _fin.AttendreLeVainqueurAsync(jeu.End!, places, ct).ConfigureAwait(false);
            if (vainqueur is null)
            {
                return;
            }

            var reponse = await _client.RapporterAsync(room.Room, vainqueur.Value, CancellationToken.None).ConfigureAwait(false);
            _journal.LogInformation("MULTIPLAYER : rencontre de {Jeu} vue finie chez l'invite, place {Place} rapportee ({Statut})",
                jeu.Id, vainqueur, reponse?.Ok == true ? "ok" : reponse?.Reason ?? "injoignable");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Charge l'etat, ecrit le choix de chaque place, relit. Faux quand le depart ne se verifie pas.</summary>
    private async Task<bool> PreparerLeDepartAsync(MultiplayerRoom room, MultiplayerInstalledGame jeu)
    {
        var etat = Path.Combine(RetroBatPaths.PluginRoot, "resources", "multiplayer", jeu.Game.Start!.State.Replace('/', Path.DirectorySeparatorChar));
        var choixParPlace = room.Players
            .Where(joueur => joueur.Seat is not null)
            .GroupBy(joueur => joueur.Seat!.Value)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.First().Choices);
        var resultat = await _depart.PreparerAsync(jeu.Game, etat, Path.GetFileNameWithoutExtension(jeu.Path), choixParPlace, CancellationToken.None)
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
            if (jeu.Game is { EstNelfePlay: true, End: not null })
            {
                _ = Task.Run(() => RapporterEnInviteAsync(room, jeu.Game, guetInvite.Token));
            }

            // RetroArch demande le mot de passe a l'ecran : la borne le tape. Si l'hote part, la partie se ferme.
            _ = Task.Run(() => RetroArchInvite.SuivreAsync(session.Relay, session.Port, session.PlayerPassword,
                async () =>
                {
                    _journal.LogInformation("MULTIPLAYER : l'hote de {Room} est parti, la partie se ferme", room.Room);
                    await RetroArchInvite.FermerAsync().ConfigureAwait(false);
                }, _journal));
            var duree = await AttendreLaFinAsync(partie).ConfigureAwait(false);
            guetInvite.Cancel();
            NetplayGuestService.OublierInviteMultiplayer();
            // En Classique, la partie finit quand tous les invites sont partis : la plateforme doit le savoir.
            await _client.QuitterAsync(room.Room, CancellationToken.None).ConfigureAwait(false);
            _panneau.MontrerLeResultat(room.Room, duree);
        }
        catch (Exception ex)
        {
            _journal.LogWarning(ex, "MULTIPLAYER : arrivee dans {Room} interrompue", room.Room);
        }
        finally
        {
            lock (_verrou)
            {
                _enCours = null;
            }
        }
    }

    private sealed record Partie(Process Lanceur, Dictionary<string, Dictionary<string, string?>> ARendre, DateTime Debut, GameReference Jeu);

    /// <summary>Pose les reglages, lance, et annonce la partie quand l'emulateur est la.</summary>
    private Partie? Lancer(MultiplayerRoom room, MultiplayerInstalledGame jeu, IReadOnlyList<string> netplay)
    {
        var lancement = room.Launch!;
        var systeme = NetplayHostService.SystemeDuChemin(jeu.Path, RetroBatPaths.RomsRoot);
        var coeur = jeu.Core?.Core ?? string.Empty;
        var arguments = MultiplayerLaunchPlan.Arguments(EsLaunchArguments.ManettesDuDernierLancement(), systeme, coeur, jeu.Path, jeu.Game, lancement.Seat, netplay);
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

        var aRendre = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            [OptionsDesCoeurs] = optionsAvant,
            [ConfigRetroArch] = RetroArchConfigEdit.Poser(ConfigRetroArch, MultiplayerLaunchPlan.Place(lancement.Seat)),
        };
        RetroArchConfigEdit.Noter(Journal, aRendre);
        if (jeu.Game.Screens > 1)
        {
            PoserLeShader(jeu.Game.EcranDeLaPlace(lancement.Seat), jeu.Game.Screens);
        }

        _journal.LogInformation("MULTIPLAYER : {Jeu} lance en {Role}, place {Place} sur {Joueurs}, coeur {Coeur}",
            jeu.Game.Id, lancement.Role, lancement.Seat, lancement.Players, coeur);
        var lanceur = NetplayLaunch.DemarrerDirectement(arguments, _journal);
        if (lanceur is null)
        {
            Rendre(aRendre);
            _panneau.Fermer();
            return null;
        }

        var reference = new GameReference { SystemId = systeme, GamePath = jeu.Path, GameName = jeu.Game.Name };
        _ = Task.Run(() => AnnoncerQuandLEmulateurEstLaAsync(reference));
        return new Partie(lanceur, aRendre, DateTime.UtcNow, reference);
    }

    /// <summary>
    /// SANS EMULATIONSTATION, PERSONNE NE DIT QUEL JEU TOURNE (voir le lancement a moteur impose). On pose le jeu
    /// nous-memes et on annonce son debut quand l'emulateur parait : avant, l'annonce fermerait la presentation
    /// « Les joueurs se synchronisent… » pendant que RetroArch charge encore.
    /// </summary>
    private async Task AnnoncerQuandLEmulateurEstLaAsync(GameReference jeu)
    {
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite && !EmulatorForeground.EmulateurDeJeuTourne())
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        _contexte.Ui.Running = jeu;
        _contexte.Ui.State = "playing";
        _panneau.EffacerPourLeJeu();
        await _bus.PublishAsync(new EventEnvelope
        {
            Type = "ui.game.started",
            Payload = new { Source = "multiplayer", jeu.SystemId, jeu.GamePath, jeu.GameName, Direct = true },
        }).ConfigureAwait(false);
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();
    }

    /// <summary>Attend que le lanceur rende la main, rend les reglages et annonce la fin. Rend la duree, en secondes.</summary>
    private async Task<int> AttendreLaFinAsync(Partie partie)
    {
        try
        {
            await partie.Lanceur.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Le lanceur a deja disparu.
        }

        var duree = (int)Math.Max(0, (DateTime.UtcNow - partie.Debut).TotalSeconds);
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
        _journal.LogInformation("MULTIPLAYER : partie de {Jeu} finie apres {Duree} s", partie.Jeu.GameName, duree);
        return duree;
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
    private void PoserLeShader(int ecran, int ecrans)
    {
        var dossier = Path.Combine(RetroBatPaths.RetroBatRoot, "system", "shaders", MultiplayerLaunchPlan.DossierShaders);
        try
        {
            Directory.CreateDirectory(dossier);
            foreach (var (nom, contenu) in MultiplayerLaunchPlan.FichiersDeShader(ecran, ecrans))
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
