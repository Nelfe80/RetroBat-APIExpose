using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Heberger une partie en netplay, et savoir sous quelle session.
///
/// Chaque piece a ete prouvee separement ; celle-ci les enchaine :
///
///   1. ce qu'ES a RESOLU pour ce jeu (emulateur, coeur, manettes) — relu dans son journal,
///      jamais recalcule ;
///   2. le relais le plus proche — MESURE, pas deduit de la geographie ;
///   3. les reglages d'hebergement — poses dans es_settings.cfg, avec copie de cote ;
///   4. le lancement — la commande d'ES, plus `-netplaymode host` ;
///   5. l'identifiant de session — relu AU LOBBY, seul endroit ou RetroArch le transmet.
///
/// Chacune peut echouer d'une facon qui se DIT. Un hebergement qui ne demarre pas doit
/// l'annoncer, jamais laisser croire qu'une partie attend des spectateurs qui n'arriveront pas.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetplayHostService
{
    /// <summary>
    /// Le temps qu'on laisse a la chaine : demarrage de RetroArch, montage du tunnel, annonce.
    /// Mesure sur borne : l'entree apparait vers 40 s. On prend de la marge sans etre infini.
    /// </summary>
    private static readonly TimeSpan PatienceLobby = TimeSpan.FromSeconds(90);

    private const int PortNetplay = 55435;

    private readonly NetplayRelayPicker _relais;
    private readonly NetplayLobbyClient _lobby;
    private readonly NelfePlayDeviceStore _machine;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NetplayHostService> _logger;

    public NetplayHostService(
        NetplayRelayPicker relais,
        NetplayLobbyClient lobby,
        NelfePlayDeviceStore machine,
        IHttpClientFactory httpFactory,
        ILogger<NetplayHostService> logger)
    {
        _relais = relais;
        _lobby = lobby;
        _machine = machine;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Pourquoi un hebergement n'a pas abouti. Chaque cas se dit differemment.</summary>
    public enum Echec
    {
        Aucun,
        /// <summary>Le jeu n'a jamais ete lance sur cette borne : ES n'a donc rien resolu pour lui.</summary>
        JamaisLance,
        /// <summary>es_settings.cfg illisible : on n'ecrit pas par-dessus ce qu'on n'a pas compris.</summary>
        ReglagesRefuses,
        /// <summary>Le lanceur n'a pas demarre.</summary>
        LancementRefuse,
        /// <summary>La partie tourne, mais aucune entree n'est apparue au lobby : pas de session a partager.</summary>
        PasDeSession,
    }

    /// <summary>
    /// Lance ce jeu en hote.
    ///
    /// EN DEUX TEMPS, et c'est deliberé. Ce qui se sait TOUT DE SUITE — le jeu est-il connu
    /// d'ES, les reglages ont-ils pu etre poses, le lanceur a-t-il demarre — est rendu a
    /// l'appelant, parce qu'une navigation de navigateur attend une reponse. L'identifiant de
    /// session, lui, n'existe qu'apres le demarrage de RetroArch et le montage du tunnel : il
    /// arrive une minute plus tard, et c'est la PLATEFORME qui l'apprend, pas l'appelant.
    ///
    /// Faire attendre la fenetre quatre-vingt-dix secondes pour lui rendre une valeur dont elle
    /// n'a pas l'usage serait la bloquer pour rien.
    ///
    /// <paramref name="autoriserAJouer"/> decide si le mot de passe JOUEUR est rapporte. Les
    /// deux sont poses sur la borne dans tous les cas : c'est RetroArch qui applique la
    /// difference, pas notre interface. Ne pas le publier suffit donc a rendre la partie
    /// regardable sans etre jouable.
    /// </summary>
    public async Task<Echec> HebergerAsync(
        string cheminRom,
        string pseudoJoueur,
        bool autoriserAJouer,
        CancellationToken ct = default)
    {
        var resolution = EsLaunchArguments.PourRom(cheminRom);
        if (resolution is null)
        {
            _logger.LogInformation("Netplay : {Rom} n'a jamais ete lance ici, rien a reprendre.",
                Path.GetFileName(cheminRom));
            return Echec.JamaisLance;
        }

        var relais = await _relais.ChoisirAsync(ct).ConfigureAwait(false);
        var jeton = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var mdpJoueur = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var mdpSpectateur = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        var reglages = new NetplaySettings.Hebergement(
            NetplaySettings.ComposerPseudo(pseudoJoueur, jeton),
            mdpJoueur,
            mdpSpectateur,
            relais,
            PortNetplay);

        if (!NetplaySettings.Poser(reglages, _logger) || !NetplaySettings.PoserHebergement(_logger))
        {
            return Echec.ReglagesRefuses;
        }

        // Par ES de preference : lui seul cesse de dessiner pendant la partie (voir NetplayLaunch).
        var lancement = await NetplayLaunch.LancerAsync(
            cheminRom, Arguments(resolution, cheminRom), _httpFactory, _logger, ct).ConfigureAwait(false);
        if (!lancement.Ok)
        {
            return Echec.LancementRefuse;
        }

        // L'emulateur devant, sans attendre : la reponse HTTP n'a pas a patienter le temps
        // qu'un coeur charge sa ROM.
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();

        // SECOND TEMPS, detache. On ne passe PAS le jeton d'annulation de la requete HTTP :
        // elle sera terminee bien avant, et annuler ce travail avec elle laisserait une partie
        // hebergee que personne ne pourrait rejoindre.
        // On emporte le nom COURT du coeur et l'empreinte du contenu, calcules ICI. Le lobby
        // publie, lui, le nom d'AFFICHAGE du coeur et son propre CRC : rapporter ces valeurs
        // faisait comparer au client deux choses fabriquees differemment.
        var coeurLocal = resolution.Coeur;
        var empreinte = NetplayContent.Empreinte(cheminRom);

        _ = Task.Run(
            () => AttendreEtRapporterAsync(
                jeton, mdpSpectateur, autoriserAJouer ? mdpJoueur : "", coeurLocal, empreinte),
            CancellationToken.None);

        // TROISIEME TEMPS : le direct doit CESSER quand la partie cesse. Sans lui, l'annonce
        // vit jusqu'a sa peremption et la fiche du joueur continue d'inviter a rejoindre une
        // partie fermee. Constate en test, et c'est pire qu'une absence d'annonce : ca envoie
        // quelqu'un se brancher sur rien.
        _ = Task.Run(RetirerQuandLaPartieFinitAsync, CancellationToken.None);

        return Echec.Aucun;
    }

    /// <summary>
    /// Attend que la session paraisse au lobby, puis la rapporte a la plateforme.
    ///
    /// Si elle ne parait jamais, la partie TOURNE quand meme — on ne la coupe pas. Elle n'est
    /// simplement pas rejoignable, et le journal le dit : laisser croire que des spectateurs
    /// peuvent venir serait pire que de jouer seul.
    /// </summary>
    private async Task AttendreEtRapporterAsync(
        string jeton,
        string motDePasseSpectateur,
        string motDePasseJoueur,
        string coeurLocal,
        string empreinte)
    {
        try
        {
            var session = await _lobby.AttendreAsync(jeton, PatienceLobby).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogWarning(
                    "Netplay : partie lancee, mais aucune session au lobby (jeton {Jeton}).", jeton);
                return;
            }

            _logger.LogInformation("Netplay : session {Session} sur {Relais}.", session.Id, session.RelayHote);
            await RapporterAsync(
                    session,
                    motDePasseSpectateur,
                    motDePasseJoueur,
                    coeurLocal,
                    empreinte,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netplay : attente de la session interrompue.");
        }
    }

    /// <summary>
    /// Attend la fin de la partie, puis retire l'annonce.
    ///
    /// Par SONDAGE et non par evenement : c'est ainsi que le module de replay constate deja
    /// la fin d'une partie, et un second mecanisme pour la meme question ferait deux verites a
    /// maintenir.
    ///
    /// On attend d'abord que l'emulateur PARAISSE. Il vient d'etre lance et peut ne pas encore
    /// avoir de processus : conclure « deja fini » retirerait l'annonce dans la seconde.
    /// </summary>
    private async Task RetirerQuandLaPartieFinitAsync()
    {
        try
        {
            // Une minute pour paraitre. Au-dela, le lancement a echoue autrement, et il n'y a
            // rien a retirer : c'est le navigateur qui annonce, et seulement sur confirmation.
            var apparu = false;
            var apparition = DateTime.UtcNow.AddSeconds(60);
            while (!apparu && DateTime.UtcNow < apparition)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                apparu = EmulatorForeground.EmulateurTourne();
            }
            if (!apparu)
            {
                _logger.LogInformation("Netplay : aucun emulateur apparu, pas d'annonce a retirer.");
                return;
            }

            // La borne d'un joueur reste allumee : on ne guette pas indefiniment. Deux heures,
            // c'est exactement la peremption de l'annonce, donc au-dela il n'y a plus rien a
            // retirer de toute facon.
            var limite = DateTime.UtcNow.AddHours(2);
            while (DateTime.UtcNow < limite && EmulatorForeground.EmulateurTourne())
            {
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }

            await RetirerAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netplay : guet de la fin de partie interrompu.");
        }
    }

    /// <summary>
    /// Retire l'annonce, authentifie comme MACHINE.
    ///
    /// La borne ne connait pas l'identifiant de session cote plateforme : le lien
    /// machine -> compte suffit, comme pour le rapport de la session netplay.
    /// </summary>
    private async Task RetirerAsync(CancellationToken ct)
    {
        var credential = _machine.GetCredential();
        if (string.IsNullOrEmpty(credential))
        {
            return;
        }
        try
        {
            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

            using var contenu = new StringContent("{}", Encoding.UTF8, "application/json");
            using var reponse = await client
                .PostAsync("/api/v1/agent/live/withdraw", contenu, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Netplay : partie finie, annonce retiree (HTTP {Code}).", (int) reponse.StatusCode);
        }
        catch (Exception ex)
        {
            // Un retrait manque n'est pas grave au point de meriter une alerte : la peremption
            // de deux heures reste le filet. Mais il doit se LIRE, sinon on cherchera ailleurs.
            _logger.LogWarning(ex, "Netplay : retrait de l'annonce en echec.");
        }
    }

    /// <summary>
    /// Rapporte la session a NelfePlay, authentifie comme MACHINE.
    ///
    /// La borne ne connait pas l'identifiant de session cote plateforme : le lien
    /// machine -> compte suffit a retrouver l'annonce en cours. Un couplage de moins.
    ///
    /// Le mot de passe JOUEUR n'est envoye que si l'hote a autorise a jouer. Ce qui n'est pas
    /// envoye ne peut pas fuiter d'une base ou d'un journal.
    /// </summary>
    private async Task RapporterAsync(
        NetplayLobbyClient.Session session,
        string motDePasseSpectateur,
        string motDePasseJoueur,
        string coeurLocal,
        string empreinte,
        CancellationToken ct)
    {
        var credential = _machine.GetCredential();
        if (string.IsNullOrEmpty(credential))
        {
            _logger.LogInformation("Netplay : machine non appairee, session non rapportee.");
            return;
        }

        try
        {
            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

            var corps = new JsonObject
            {
                ["relay"] = session.RelayHote,
                ["port"] = session.RelayPort,
                ["session"] = session.Id,
                ["spectate_password"] = motDePasseSpectateur,
                ["player_password"] = motDePasseJoueur,
                // Le nom COURT du coeur (« fbneo »), celui de la ligne de lancement d'ES, et
                // non celui que publie le lobby (« FinalBurn Neo »). Le client ne connait que
                // le nom court, tire de sa propre ligne de lancement : comparer les deux
                // conventions faisait refuser le MEME coeur, ce qui est arrive en test.
                ["core"] = coeurLocal.Length > 0 ? coeurLocal : session.Coeur,
                // La version reste celle du lobby : elle n'entre dans aucune comparaison, elle
                // ne sert qu'a expliquer un refus.
                ["core_version"] = session.VersionCoeur,
                // L'empreinte du contenu calculee par NOTRE code, pour que le client compare
                // deux valeurs produites de la meme facon. Le CRC du lobby est celui de
                // RetroArch, obtenu autrement, donc incomparable au notre.
                ["crc"] = empreinte.Length > 0 ? empreinte : session.Crc,
            };
            using var contenu = new StringContent(corps.ToJsonString(), Encoding.UTF8, "application/json");
            using var reponse = await client
                .PostAsync("/api/v1/agent/live/netplay", contenu, ct)
                .ConfigureAwait(false);

            if (!reponse.IsSuccessStatusCode)
            {
                // 409 = aucune annonce en cours : la partie a ete lancee sans « Diffuser en
                // live ». Ce n'est pas une panne, c'est le cas normal quand on joue seul.
                _logger.LogInformation("Netplay : session non rapportee (HTTP {Code}).", (int)reponse.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netplay : remontee de la session impossible.");
        }
    }

    /// <summary>
    /// La commande d'ES, avec le netplay en plus : le REPLI quand ES ne repond pas.
    ///
    /// Les arguments de manette sont repris MOT POUR MOT : les recalculer perdrait le reglage du
    /// joueur. `-gameinfo` n'est pas repris — il pointe un temporaire qu'ES reecrit et supprime.
    /// </summary>
    private static string Arguments(EsLaunchArguments.Resolution r, string cheminRom)
        => string.Join(' ', new[]
        {
            r.Manettes,
            "-system", r.Systeme,
            "-emulator", r.Emulateur,
            "-core", r.Coeur,
            "-rom", Guillemets(cheminRom),
            "-netplaymode", "host",
        }.Where(x => x.Length > 0));

    /// <summary>Un chemin contient des espaces : sans guillemets, le lanceur ne recoit qu'un morceau.</summary>
    private static string Guillemets(string valeur)
        => valeur.StartsWith('"') ? valeur : '"' + valeur.Replace("\"", "") + '"';
}
