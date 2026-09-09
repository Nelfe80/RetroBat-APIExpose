using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
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
    private readonly ILogger<NetplayHostService> _logger;

    public NetplayHostService(
        NetplayRelayPicker relais,
        NetplayLobbyClient lobby,
        ILogger<NetplayHostService> logger)
    {
        _relais = relais;
        _lobby = lobby;
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
    /// Ce qu'il faut publier pour qu'on puisse rejoindre. Le mot de passe JOUEUR n'y figure que
    /// si l'hote a autorise a jouer ; sinon on ne partage que celui des spectateurs.
    /// </summary>
    public sealed record Resultat(
        Echec Echec,
        string Relais,
        string RelaisAdresse,
        int RelaisPort,
        string SessionRelais,
        string MotDePasseSpectateur,
        string? MotDePasseJoueur,
        string Coeur,
        string VersionCoeur,
        string Crc);

    /// <summary>
    /// Lance ce jeu en hote et rend de quoi le rejoindre.
    ///
    /// <paramref name="autoriserAJouer"/> decide si le mot de passe JOUEUR sort. Les deux mots
    /// de passe sont poses dans tous les cas : c'est RetroArch qui applique la difference, pas
    /// notre interface. Ne pas publier celui des joueurs suffit donc a rendre la partie
    /// regardable sans etre jouable.
    /// </summary>
    public async Task<Resultat> HebergerAsync(
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
            return Rate(Echec.JamaisLance);
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
            return Rate(Echec.ReglagesRefuses);
        }

        if (!Lancer(resolution, cheminRom))
        {
            return Rate(Echec.LancementRefuse);
        }

        // L'emulateur devant, sans attendre : la reponse HTTP n'a pas a patienter le temps
        // qu'un coeur charge sa ROM.
        _ = EmulatorForeground.FocusEmulatorWhenUpAsync();

        var session = await _lobby.AttendreAsync(jeton, PatienceLobby, ct).ConfigureAwait(false);
        if (session is null)
        {
            // La partie TOURNE — on ne la coupe pas — mais elle n'est pas partageable. C'est
            // une information, pas une panne a masquer.
            _logger.LogWarning("Netplay : partie lancee, mais aucune session au lobby (jeton {Jeton}).", jeton);
            return Rate(Echec.PasDeSession);
        }

        _logger.LogInformation("Netplay : session {Session} sur {Relais}.", session.Id, session.RelayHote);

        return new Resultat(
            Echec.Aucun,
            relais,
            session.RelayHote,
            session.RelayPort,
            session.Id,
            mdpSpectateur,
            autoriserAJouer ? mdpJoueur : null,
            // Le triplet que la poignee de main exige identique des deux cotes : l'invite peut
            // ainsi verifier AVANT de lancer, au lieu d'echouer sans explication.
            session.Coeur,
            session.VersionCoeur,
            session.Crc);
    }

    private static Resultat Rate(Echec echec)
        => new(echec, "", "", 0, "", "", null, "", "", "");

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
