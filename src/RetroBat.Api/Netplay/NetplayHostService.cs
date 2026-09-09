using System.Diagnostics;
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

        if (!NetplaySettings.Poser(reglages, _logger))
        {
            return Echec.ReglagesRefuses;
        }

        if (!Lancer(resolution, cheminRom))
        {
            return Echec.LancementRefuse;
        }

        // L'emulateur devant, sans attendre : la reponse HTTP n'a pas a patienter le temps
        // qu'un coeur charge sa ROM.
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();

        // SECOND TEMPS, detache. On ne passe PAS le jeton d'annulation de la requete HTTP :
        // elle sera terminee bien avant, et annuler ce travail avec elle laisserait une partie
        // hebergee que personne ne pourrait rejoindre.
        _ = Task.Run(
            () => AttendreEtRapporterAsync(jeton, mdpSpectateur, autoriserAJouer ? mdpJoueur : ""),
            CancellationToken.None);

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
        string jeton, string motDePasseSpectateur, string motDePasseJoueur)
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
            await RapporterAsync(session, motDePasseSpectateur, motDePasseJoueur, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netplay : attente de la session interrompue.");
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
                ["core"] = session.Coeur,
                ["core_version"] = session.VersionCoeur,
                ["crc"] = session.Crc,
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
    /// La commande d'ES, avec le netplay en plus.
    ///
    /// Les arguments de manette sont repris MOT POUR MOT : les recalculer perdrait le reglage du
    /// joueur. `-gameinfo` n'est pas repris — il pointe un temporaire qu'ES reecrit et supprime.
    /// </summary>
    private bool Lancer(EsLaunchArguments.Resolution r, string cheminRom)
    {
        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", "emulatorLauncher.exe");
        if (!File.Exists(exe))
        {
            _logger.LogWarning("Netplay : emulatorLauncher introuvable ({Chemin}).", exe);
            return false;
        }

        var arguments = string.Join(' ', new[]
        {
            r.Manettes,
            "-system", r.Systeme,
            "-emulator", r.Emulateur,
            "-core", r.Coeur,
            "-rom", Guillemets(cheminRom),
            "-netplaymode", "host",
        }.Where(x => x.Length > 0));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netplay : lancement refuse.");
            return false;
        }
    }

    /// <summary>Un chemin contient des espaces : sans guillemets, le lanceur ne recoit qu'un morceau.</summary>
    private static string Guillemets(string valeur)
        => valeur.StartsWith('"') ? valeur : '"' + valeur.Replace("\"", "") + '"';
}
