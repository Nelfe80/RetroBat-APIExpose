using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Netplay;

/// <summary>
/// Rejoindre la partie de quelqu'un, en spectateur ou en joueur.
///
/// La borne demande elle-meme a NelfePlay de quoi rejoindre, authentifiee comme machine. Elle
/// ne le recoit PAS du navigateur : la reponse contient des mots de passe, et dans une URL ils
/// finiraient dans l'historique, dans les journaux du serveur et dans le referer.
///
/// C'est aussi la plateforme qui decide si le mot de passe JOUEUR sort, selon ce que l'hote a
/// autorise. Cette borne n'a donc rien a arbitrer : elle joue avec ce qu'on lui donne, et
/// RetroArch refusera la manette si elle n'a que le mot de passe des spectateurs.
///
/// AVANT de lancer, elle verifie le triplet que la poignee de main netplay exige identique des
/// deux cotes — coeur, version, empreinte du contenu. Une connexion qui echoue apres coup ne
/// dit rien au joueur ; un refus explique lui dit quoi faire.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetplayGuestService
{
    private readonly NelfePlayDeviceStore _machine;
    private readonly IHttpClientFactory _httpFactory;
    private readonly RomCanonicalResolver _canonical;
    private readonly ILogger<NetplayGuestService> _logger;

    public NetplayGuestService(
        NelfePlayDeviceStore machine,
        IHttpClientFactory httpFactory,
        RomCanonicalResolver canonical,
        ILogger<NetplayGuestService> logger)
    {
        _machine = machine;
        _httpFactory = httpFactory;
        _canonical = canonical;
        _logger = logger;
    }

    /// <summary>Pourquoi on n'a pas pu rejoindre. Chaque cas se dit differemment au joueur.</summary>
    public enum Echec
    {
        Aucun,
        /// <summary>Machine non appairee : on ne peut pas demander a qui l'on a droit.</summary>
        NonAppairee,
        /// <summary>La plateforme ne connait pas cette session, ou elle est finie.</summary>
        SessionInconnue,
        /// <summary>Ce jeu n'est pas sur cette borne.</summary>
        JeuAbsent,
        /// <summary>Le jeu est la, mais ES ne l'a jamais lance : on ne sait pas avec quoi.</summary>
        JamaisLance,
        /// <summary>Meme jeu, mais pas le meme DUMP : la poignee de main echouerait.</summary>
        DumpDifferent,
        /// <summary>Meme jeu, mais pas le meme coeur : la poignee de main echouerait.</summary>
        CoeurDifferent,
        LancementRefuse,
    }

    /// <summary>
    /// Rejoint la session. Spectateur par defaut ; joueur seulement si la plateforme a donne le
    /// mot de passe correspondant, ce qu'elle ne fait que si l'hote l'a autorise.
    /// </summary>
    public async Task<Echec> RejoindreAsync(string sessionId, CancellationToken ct = default)
    {
        var credential = _machine.GetCredential();
        if (string.IsNullOrEmpty(credential))
        {
            return Echec.NonAppairee;
        }

        var infos = await DemanderAsync(sessionId, credential, ct).ConfigureAwait(false);
        if (infos is null)
        {
            return Echec.SessionInconnue;
        }

        var rom = TrouverRom(infos.Value.Systeme, infos.Value.Jeu);
        if (rom is null)
        {
            return Echec.JeuAbsent;
        }

        var resolution = EsLaunchArguments.PourRom(rom);
        if (resolution is null)
        {
            return Echec.JamaisLance;
        }

        // ── La verification qui evite un echec muet ──────────────────────────
        //
        // Le netplay exige le MEME coeur et le MEME contenu des deux cotes. NelfePlay, lui,
        // identifie les jeux par leur identite de SCORING, qui accepte volontiers deux dumps
        // differents du meme jeu. Deux personnes qui possedent « Sonic » peuvent donc tres bien
        // ne pas pouvoir jouer ensemble — et il vaut mieux le dire ici que laisser la connexion
        // tomber sans explication.
        // On compare des noms COURTS de coeur (« fbneo »), ceux des lignes de lancement d'ES.
        // Une borne restee sur une version anterieure rapporte encore le nom d'AFFICHAGE que
        // publie le lobby (« FinalBurn Neo ») : les deux conventions ne se comparent pas, et le
        // faire refusait le meme coeur. Dans ce cas on ne compare PAS, et le journal le dit :
        // l'empreinte du contenu reste la garantie, et un coeur reellement different,
        // RetroArch le refusera de lui-meme.
        var coeurHote = infos.Value.Coeur;
        if (coeurHote.Length > 0 && coeurHote.Contains(' '))
        {
            _logger.LogInformation(
                "Netplay : nom de coeur incomparable (hote {Hote}, ici {Ici}), on s'en remet a l'empreinte.",
                coeurHote, resolution.Coeur);
        }
        else if (coeurHote.Length > 0
            && !string.Equals(resolution.Coeur, coeurHote, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Netplay : coeur different (hote {Hote}, ici {Ici}).", coeurHote, resolution.Coeur);
            return Echec.CoeurDifferent;
        }

        var crcLocal = NetplayContent.Empreinte(rom);
        if (infos.Value.Crc.Length > 0 && crcLocal.Length > 0
            && !string.Equals(crcLocal, infos.Value.Crc, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Netplay : dump different (hote {Hote}, ici {Ici}).", infos.Value.Crc, crcLocal);
            return Echec.DumpDifferent;
        }

        return Lancer(resolution, rom, infos.Value) ? Echec.Aucun : Echec.LancementRefuse;
    }

    /// <summary>Ce que la plateforme nous accorde pour cette session.</summary>
    private readonly record struct Infos(
        string Jeu,
        string Systeme,
        string Relais,
        int Port,
        string Session,
        string MotDePasse,
        bool PeutJouer,
        string Coeur,
        string Crc);

    private async Task<Infos?> DemanderAsync(string sessionId, string credential, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(NelfePlayAgentService.BaseUrl.TrimEnd('/'));
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("X-NelfePlay-Device", credential);

            using var reponse = await client
                .GetAsync($"/api/v1/agent/live/{Uri.EscapeDataString(sessionId)}/join", ct)
                .ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(
                await reponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var r = doc.RootElement;

            var motJoueur = Texte(r, "player_password");
            var peutJouer = r.TryGetProperty("can_play", out var cp)
                && cp.ValueKind == JsonValueKind.True
                && motJoueur.Length > 0;

            return new Infos(
                Texte(r, "game"),
                Texte(r, "system"),
                Texte(r, "relay"),
                r.TryGetProperty("port", out var p) && p.TryGetInt32(out var port) ? port : 55435,
                Texte(r, "session"),
                // Le mot de passe JOUEUR s'il nous a ete donne, celui des spectateurs sinon.
                // RetroArch refusera la manette avec le second : c'est LUI qui applique, et
                // c'est ce qui rend la regle infranchissable.
                peutJouer ? motJoueur : Texte(r, "spectate_password"),
                peutJouer,
                Texte(r, "core"),
                Texte(r, "crc"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netplay : de quoi rejoindre, indisponible.");
            return null;
        }
    }

    private static string Texte(JsonElement racine, string nom)
        => racine.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>
    /// La ROM locale de ce jeu, par son identite de SCORING — la meme resolution que le
    /// lancement ordinaire, pour que « le meme jeu » veuille dire la meme chose partout.
    /// </summary>
    private string? TrouverRom(string systeme, string romGroup)
    {
        var cible = romGroup.Trim().ToLowerInvariant();
        var sys = systeme.Trim().ToLowerInvariant();
        // L'arcade est UN systeme cote scoring et PLUSIEURS dossiers cote RetroBat.
        string[] dossiers = sys is "arcade"
            ? ["fbneo", "mame", "mame64", "fba", "arcade"]
            : [sys];

        foreach (var dossier in dossiers)
        {
            if (dossier.Length == 0 || !string.Equals(Path.GetFileName(dossier), dossier, StringComparison.Ordinal))
            {
                continue;
            }
            var racine = Path.Combine(RetroBatPaths.RomsRoot, dossier);
            if (!Directory.Exists(racine))
            {
                continue;
            }
            foreach (var fichier in Directory.EnumerateFiles(racine))
            {
                var nom = Path.GetFileName(fichier);
                var slug = _canonical.ResolveScoreSlug(dossier, nom, null, null);
                if (slug is not null && string.Equals(slug, cible, StringComparison.OrdinalIgnoreCase))
                {
                    return fichier;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// La commande d'ES, en mode client. Les arguments de manette sont repris mot pour mot :
    /// les recalculer perdrait le reglage du joueur.
    /// </summary>
    private bool Lancer(EsLaunchArguments.Resolution r, string rom, Infos infos)
    {
        var exe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", "emulatorLauncher.exe");
        if (!File.Exists(exe))
        {
            return false;
        }

        // « spectator » et non « client » quand on n'a pas le droit de jouer : le mode dit
        // l'intention, et le mot de passe la fait respecter. Les deux vont ensemble.
        var mode = infos.PeutJouer ? "client" : "spectator";

        var arguments = string.Join(' ', new[]
        {
            r.Manettes,
            "-system", r.Systeme,
            "-emulator", r.Emulateur,
            "-core", r.Coeur,
            "-rom", '"' + rom + '"',
            "-netplaymode", mode,
            "-netplayip", infos.Relais,
            "-netplayport", infos.Port.ToString(),
            "-netplaysession", '"' + infos.Session + '"',
            "-netplaypass", '"' + infos.MotDePasse + '"',
        }.Where(x => x.Length > 0));

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            });
            // Un spectateur regarde : son ecran ne score rien, donc l'emulateur peut passer
            // devant sans precaution particuliere.
            _ = EmulatorForeground.FocusEmulatorWhenUpAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netplay : impossible de rejoindre.");
            return false;
        }
    }
}
