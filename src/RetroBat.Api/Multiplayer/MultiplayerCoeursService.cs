using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Un coeur que NelfePlay compile et livre lui-meme : son systeme d'EmulationStation, son nom de coeur, ses bibliotheques
/// (la premiere est le coeur, les suivantes vont a cote d'elle) et sa fiche <c>.info</c>.
/// </summary>
public sealed record MultiplayerCoeurLivre(string Systeme, string Nom, IReadOnlyList<string> Bibliotheques, string Fiche, string Ajout)
{
    /// <summary>Ou outils.json depose chaque fichier, relatif au dossier d'APIExpose.</summary>
    public IEnumerable<string> CheminsDesOutils => Bibliotheques.Append(Fiche).Select(fichier => "tools/cores/" + fichier);
}

/// <summary>
/// LES COEURS DE MULTIPLAYER SUR CHAQUE BORNE (CDC MULTIPLAYER 00quater.9, point 2 ; 23/10, NGPC le 25/10). Certains jeux se
/// jouent a plusieurs dans un seul coeur que NelfePlay compile : mGBA Splitscreen (2 a 4 GBA reliees) et NGP Link (deux Neo
/// Geo Pocket reliees par le cable, Card Fighters' Clash). D'autres demandent un coeur corrige : FBNeo Multi 32 (27/10).
/// Ni RetroBat ni le serveur de libretro ne les distribuent : ils arrivent par outils.json dans <c>tools/cores/</c>, et ce
/// service les pose chez RetroArch.
///
/// Pour chaque coeur, chaque fichier est repris s'il manque ou differe : ses bibliotheques (dans <c>cores/</c>, ou
/// <c>cores_real/</c> si le wrapper y range deja le vrai coeur ; les suivantes a cote de la premiere), sa fiche <c>.info</c> (son <c>systemid</c> evite le plantage de GET_STATUS sous RetroArch 1.22.2), et
/// <c>es_systems_nelfeplay.cfg</c>, qui ajoute chaque coeur a son systeme d'EmulationStation sans toucher au fichier de
/// RetroBat. Seulement quand la collection MULTIPLAYER est affichee, et jamais pendant une partie : un coeur charge ne se
/// remplace pas.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiplayerCoeursService : BackgroundService
{
    public static readonly IReadOnlyList<MultiplayerCoeurLivre> Coeurs =
    [
        new("gba", "mgba_splitscreen", ["mgba_splitscreen_libretro.dll"], "mgba_splitscreen_libretro.info",
            "le coeur libretro mgba_splitscreen (2 a 4 GBA reliees dans un seul coeur, pour le netplay de MULTIPLAYER), en "
            + "dernier : mgba reste le coeur des parties solo"),
        // NGP LINK (25/10) : deux copies du moteur Beetle NGP liees dans le meme fichier, chacune avec ses variables.
        new("ngpc", "mednafen_ngp_link", ["mednafen_ngp_link_libretro.dll"], "mednafen_ngp_link_libretro.info",
            "le coeur libretro mednafen_ngp_link (deux Neo Geo Pocket reliees par le cable dans un seul coeur, pour le netplay "
            + "de MULTIPLAYER), en dernier : mednafen_ngp reste le coeur des parties solo"),
        // FBNEO MULTI 32 (27/10) : FBNeo mettait dans ses etats les adresses de la puce son MultiPCM, propres au processus ;
        // l'invite d'un netplay plantait des le premier son (Title Fight, Hard Dunk, OutRunners, Stadium Cross). Cette
        // compilation les reconstruit au chargement (finalburnneo/FBNeo#2838). Un coeur a part : fbneo_libretro.dll garde
        // l'empreinte que le scoring certifie. Sa licence (non commerciale) le suit, a cote de lui.
        new("fbneo", "fbneo_multi32", ["fbneo_multi32_libretro.dll", "fbneo_multi32_license.txt"], "fbneo_multi32_libretro.info",
            "le coeur libretro fbneo_multi32 (FBNeo aux etats portables, pour le netplay des jeux Sega Multi 32 de "
            + "MULTIPLAYER), en dernier : fbneo reste le coeur des parties solo"),
        // PPSSPP NETPLAY (29/10) : le multijoueur ad hoc de la PSP par le netplay de RetroArch (Netpacket, PR
        // hrydgard/ppsspp #22832), avec le GameMode qui se renvoie et le depart par place. Un coeur a part : le PPSSPP de
        // RetroBat reste celui des parties solo. Sa licence (GPL, avec l'adresse des sources) le suit, a cote de lui.
        new("psp", "ppsspp_netplay", ["ppsspp_netplay_libretro.dll", "ppsspp_netplay_license.txt"], "ppsspp_netplay_libretro.info",
            "le coeur libretro ppsspp_netplay (PPSSPP au multijoueur ad hoc par le netplay de RetroArch, pour MULTIPLAYER), "
            + "en dernier : ppsspp reste le coeur des parties solo"),
    ];

    public const string FichierEs = "es_systems_nelfeplay.cfg";

    private static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Tant qu'un coeur livre n'est pas pose, la passe suivante vient vite (27/10 : fbneo_multi32, 66 Mo, arrivait apres la
    /// premiere passe, et ses jeux restaient caches une demi-heure).
    /// </summary>
    private static readonly TimeSpan PeriodeEnAttente = TimeSpan.FromMinutes(2);

    internal static TimeSpan ProchainePasse(bool toutEstPose) => toutEstPose ? Periode : PeriodeEnAttente;

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly RetroArchWrapperDeploymentService _wrapper;
    private readonly IEventBus? _bus;
    private readonly ILogger<MultiplayerCoeursService>? _logger;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private IDisposable? _abonnement;

    public MultiplayerCoeursService(IOptionsMonitor<ApiExposeOptions> options, RetroArchWrapperDeploymentService wrapper,
        IEventBus? bus = null, ILogger<MultiplayerCoeursService>? logger = null)
    {
        _options = options;
        _wrapper = wrapper;
        _bus = bus;
        _logger = logger;
    }

    private bool Visible => _options.CurrentValue.NelfePlay is { Enabled: true, ShowMultiplayerCollection: true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Un coeur remis pendant une partie se pose apres elle.
        _abonnement = _bus?.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "ui.game.ended", StringComparison.Ordinal))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
                    await VerifierAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }, stoppingToken);
        });

        try
        {
            await Task.Delay(PremierDelai, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await VerifierAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(ProchainePasse(Coeurs.All(c => EstPose(c.Nom, _options.CurrentValue))), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _abonnement?.Dispose();
        }
    }

    /// <summary>Une passe : rien a faire le plus souvent (quelques empreintes comparees).</summary>
    public async Task VerifierAsync(CancellationToken ct)
    {
        if (!Visible || !await _porte.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (UnePartieTourne())
            {
                return;
            }

            var deploiement = _options.CurrentValue.RetroArchWrapperDeployment;
            var cores = CheminDeLaBorne(deploiement.CoresPath);
            var coresReal = CheminDeLaBorne(deploiement.RealCoresPath);
            var livres = new List<MultiplayerCoeurLivre>();
            var unCoeurAChange = false;
            foreach (var coeur in Coeurs)
            {
                var sources = coeur.CheminsDesOutils.Select(SourceDansLesOutils).ToList();
                if (!sources.All(File.Exists))
                {
                    continue;
                }

                livres.Add(coeur);
                var fiche = Path.Combine(Path.GetDirectoryName(cores) ?? cores, "info", coeur.Fiche);
                if (Poser(SourceDansLesOutils("tools/cores/" + coeur.Fiche), fiche))
                {
                    _logger?.LogInformation("MULTIPLAYER : fiche du coeur {Coeur} posee ({Chemin})", coeur.Nom, fiche);
                }

                var dossier = Path.GetDirectoryName(OuEstLeVraiCoeur(cores, coresReal, coeur.Bibliotheques[0]))!;
                foreach (var bibliotheque in coeur.Bibliotheques)
                {
                    var cible = Path.Combine(dossier, bibliotheque);
                    if (Poser(SourceDansLesOutils("tools/cores/" + bibliotheque), cible))
                    {
                        _logger?.LogInformation("MULTIPLAYER : {Bibliotheque} du coeur {Coeur} posee ({Chemin})", bibliotheque, coeur.Nom, cible);
                        unCoeurAChange = true;
                    }
                }
            }

            if (unCoeurAChange)
            {
                // Le wrapper le relie (core_proxy) ou le range (cores_real) : la partie suivante passe par lui, comme les autres.
                await _wrapper.DeployAsync(dryRun: false, ct).ConfigureAwait(false);
            }

            var esSystems = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_systems.cfg");
            var esNelfePlay = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, FichierEs);
            if (livres.Count > 0 && File.Exists(esSystems)
                && EsSystemsNelfePlay(await File.ReadAllTextAsync(esSystems, ct).ConfigureAwait(false), livres) is { } contenu
                && (!File.Exists(esNelfePlay) || await File.ReadAllTextAsync(esNelfePlay, ct).ConfigureAwait(false) != contenu))
            {
                EcrireAtomique(esNelfePlay, System.Text.Encoding.UTF8.GetBytes(contenu));
                _logger?.LogInformation("MULTIPLAYER : {Fichier} ecrit, EmulationStation connait {Coeurs} au prochain demarrage",
                    FichierEs, string.Join(", ", livres.Select(c => c.Systeme + "/" + c.Nom)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Un coeur ouvert, un dossier en lecture seule : la passe suivante reessaie.
            _logger?.LogDebug(ex, "MULTIPLAYER : coeurs pas poses cette fois");
        }
        finally
        {
            _porte.Release();
        }
    }

    private static string SourceDansLesOutils(string chemin)
        => Path.Combine(RetroBatPaths.PluginRoot, chemin.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>RetroArch, ou le lanceur de RetroBat, qui vit toute la partie et la precede.</summary>
    private static bool UnePartieTourne()
    {
        if (EmulatorForeground.EmulateurTourne())
        {
            return true;
        }

        var lanceurs = System.Diagnostics.Process.GetProcessesByName("emulatorLauncher");
        foreach (var p in lanceurs)
        {
            p.Dispose();
        }

        return lanceurs.Length > 0;
    }

    /// <summary>
    /// Un coeur que NelfePlay livre est-il pose chez RetroArch (dans <c>cores/</c> ou <c>cores_real/</c>) ? Un autre coeur
    /// n'est pas juge ici : vrai. La collection MULTIPLAYER cache le jeu d'un coeur livre pas encore pose (25/10).
    /// </summary>
    public static bool EstPose(string? coeur, ApiExposeOptions options)
    {
        var livre = Coeurs.FirstOrDefault(c => string.Equals(c.Nom, coeur, StringComparison.OrdinalIgnoreCase));
        if (livre is null)
        {
            return true;
        }

        var deploiement = options.RetroArchWrapperDeployment;
        return File.Exists(OuEstLeVraiCoeur(CheminDeLaBorne(deploiement.CoresPath), CheminDeLaBorne(deploiement.RealCoresPath), livre.Bibliotheques[0]));
    }

    private static string CheminDeLaBorne(string configure)
        => Path.IsPathRooted(configure)
            ? Path.GetFullPath(configure)
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, configure));

    /// <summary>
    /// Le fichier du vrai coeur : dans <c>cores_real/</c> si l'ancien montage du wrapper l'y a deja range (<c>cores/</c> porte
    /// alors le wrapper, qu'on ne remplace pas), sinon dans <c>cores/</c>, d'ou le deploiement du wrapper le prend.
    /// </summary>
    internal static string OuEstLeVraiCoeur(string cores, string coresReal, string coeur)
        => File.Exists(Path.Combine(coresReal, coeur)) ? Path.Combine(coresReal, coeur) : Path.Combine(cores, coeur);

    /// <summary>Copie la source sur la cible si elle manque ou differe. Vrai quand la cible a change.</summary>
    internal static bool Poser(string source, string cible)
    {
        if (File.Exists(cible) && string.Equals(Empreinte(source), Empreinte(cible), StringComparison.Ordinal))
        {
            return false;
        }

        EcrireAtomique(cible, File.ReadAllBytes(source));
        return true;
    }

    private static string Empreinte(string chemin)
    {
        using var flux = File.OpenRead(chemin);
        return Convert.ToHexString(SHA256.HashData(flux));
    }

    private static void EcrireAtomique(string cible, byte[] octets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
        var temporaire = cible + ".nelfeplay-tmp";
        File.WriteAllBytes(temporaire, octets);
        File.Move(temporaire, cible, overwrite: true);
    }

    /// <summary>
    /// Le <c>es_systems_nelfeplay.cfg</c> tire des systemes de RetroBat : pour chaque coeur livre, les emulateurs de son
    /// systeme tels quels, et le coeur en dernier parmi les coeurs libretro (celui de RetroBat reste celui des parties solo).
    /// EmulationStation fusionne ce fichier, la balise <c>emulators</c> d'un systeme remplace la sienne ; la reprendre de
    /// RetroBat a chaque passe evite de masquer un emulateur qu'une mise a jour de RetroBat ajouterait. Un systeme absent ou
    /// sans libretro est saute ; null si aucun ne reste.
    /// </summary>
    internal static string? EsSystemsNelfePlay(string esSystemsDeRetroBat, IReadOnlyList<MultiplayerCoeurLivre> coeurs)
    {
        var doc = XDocument.Parse(esSystemsDeRetroBat);
        var systemes = new List<XElement>();
        var ajouts = new List<string>();
        foreach (var coeur in coeurs)
        {
            var systeme = doc.Root?.Elements("system").FirstOrDefault(s => string.Equals((string?) s.Element("name"), coeur.Systeme, StringComparison.OrdinalIgnoreCase));
            if (systeme?.Element("emulators") is not { } emulateurs)
            {
                continue;
            }

            var copie = new XElement(emulateurs);
            var libretro = copie.Elements("emulator").FirstOrDefault(e => string.Equals((string?) e.Attribute("name"), "libretro", StringComparison.OrdinalIgnoreCase));
            if (libretro is null)
            {
                continue;
            }

            var liste = libretro.Element("cores");
            if (liste is null)
            {
                liste = new XElement("cores");
                libretro.Add(liste);
            }

            if (!liste.Elements("core").Any(c => string.Equals(c.Value.Trim(), coeur.Nom, StringComparison.OrdinalIgnoreCase)))
            {
                liste.Add(new XElement("core", coeur.Nom));
            }

            systemes.Add(new XElement("system", new XElement("name", coeur.Systeme), copie));
            ajouts.Add($"systeme {coeur.Systeme} : {coeur.Ajout}");
        }

        if (systemes.Count == 0)
        {
            return null;
        }

        var sortie = new XDocument(
            new XComment(" NelfePlay : complete des systemes de RetroBat sans modifier es_systems.cfg (ecrit par APIExpose, repris "
                + "de RetroBat a chaque passe). EmulationStation fusionne ce fichier : la balise <emulators> de chaque systeme "
                + "ci-dessous remplace la sienne. Ajouts : " + string.Join(" ; ", ajouts) + ". "),
            new XElement("systemList", systemes));
        return "<?xml version=\"1.0\"?>\n" + sortie.ToString().Replace("\r\n", "\n") + "\n";
    }
}
