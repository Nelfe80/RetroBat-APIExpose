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
/// LE COEUR GBA DE MULTIPLAYER SUR CHAQUE BORNE (CDC MULTIPLAYER 00quater.9, point 2 ; 23/10). Les jeux GBA se jouent a
/// plusieurs dans un seul coeur, mGBA Splitscreen, que NelfePlay compile et corrige (menu de demarrage coupe pour le netplay,
/// memoire de chaque console pour le depart pret). Ni RetroBat ni le serveur de libretro ne le distribuent : il arrive par
/// outils.json dans <c>tools/cores/</c>, et ce service le pose chez RetroArch.
///
/// Trois fichiers, chacun repris s'il manque ou differe : le coeur (dans <c>cores/</c>, ou <c>cores_real/</c> si le wrapper
/// y range deja le vrai coeur), sa fiche <c>.info</c> (son <c>systemid</c> evite le plantage de GET_STATUS sous RetroArch
/// 1.22.2) et <c>es_systems_nelfeplay.cfg</c>, qui ajoute le coeur au systeme gba d'EmulationStation sans toucher au fichier
/// de RetroBat. Seulement quand la collection MULTIPLAYER est affichee, et jamais pendant une partie : un coeur charge ne se
/// remplace pas.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiplayerCoeurGbaService : BackgroundService
{
    public const string Coeur = "mgba_splitscreen_libretro.dll";
    public const string Fiche = "mgba_splitscreen_libretro.info";
    public const string NomDuCoeur = "mgba_splitscreen";
    public const string FichierEs = "es_systems_nelfeplay.cfg";

    /// <summary>Ou outils.json depose le coeur et sa fiche, relatif au dossier d'APIExpose.</summary>
    public const string CheminDuCoeur = "tools/cores/" + Coeur;
    public const string CheminDeLaFiche = "tools/cores/" + Fiche;

    private static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Periode = TimeSpan.FromMinutes(30);

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly RetroArchWrapperDeploymentService _wrapper;
    private readonly IEventBus? _bus;
    private readonly ILogger<MultiplayerCoeurGbaService>? _logger;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private IDisposable? _abonnement;

    public MultiplayerCoeurGbaService(IOptionsMonitor<ApiExposeOptions> options, RetroArchWrapperDeploymentService wrapper,
        IEventBus? bus = null, ILogger<MultiplayerCoeurGbaService>? logger = null)
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
                await Task.Delay(Periode, stoppingToken).ConfigureAwait(false);
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

    /// <summary>Une passe : rien a faire le plus souvent (trois empreintes comparees).</summary>
    public async Task VerifierAsync(CancellationToken ct)
    {
        if (!Visible || !await _porte.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var source = Path.Combine(RetroBatPaths.PluginRoot, CheminDuCoeur.Replace('/', Path.DirectorySeparatorChar));
            var sourceFiche = Path.Combine(RetroBatPaths.PluginRoot, CheminDeLaFiche.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source) || !File.Exists(sourceFiche) || UnePartieTourne())
            {
                return;
            }

            var deploiement = _options.CurrentValue.RetroArchWrapperDeployment;
            var cores = CheminDeLaBorne(deploiement.CoresPath);
            var coresReal = CheminDeLaBorne(deploiement.RealCoresPath);
            var fiche = Path.Combine(Path.GetDirectoryName(cores) ?? cores, "info", Fiche);
            if (Poser(sourceFiche, fiche))
            {
                _logger?.LogInformation("MULTIPLAYER : fiche du coeur GBA posee ({Chemin})", fiche);
            }

            var cible = OuEstLeVraiCoeur(cores, coresReal);
            if (Poser(source, cible))
            {
                _logger?.LogInformation("MULTIPLAYER : coeur GBA {Coeur} pose ({Chemin})", NomDuCoeur, cible);
                // Le wrapper le relie (core_proxy) ou le range (cores_real) : la partie suivante passe par lui, comme les autres.
                await _wrapper.DeployAsync(dryRun: false, ct).ConfigureAwait(false);
            }

            var esSystems = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_systems.cfg");
            var esNelfePlay = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, FichierEs);
            if (File.Exists(esSystems) && EsSystemsNelfePlay(await File.ReadAllTextAsync(esSystems, ct).ConfigureAwait(false)) is { } contenu
                && (!File.Exists(esNelfePlay) || await File.ReadAllTextAsync(esNelfePlay, ct).ConfigureAwait(false) != contenu))
            {
                EcrireAtomique(esNelfePlay, System.Text.Encoding.UTF8.GetBytes(contenu));
                _logger?.LogInformation("MULTIPLAYER : {Fichier} ecrit, le systeme gba d'EmulationStation connait {Coeur} au prochain demarrage d'ES",
                    FichierEs, NomDuCoeur);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Un coeur ouvert, un dossier en lecture seule : la passe suivante reessaie.
            _logger?.LogDebug(ex, "MULTIPLAYER : coeur GBA pas pose cette fois");
        }
        finally
        {
            _porte.Release();
        }
    }

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

    private static string CheminDeLaBorne(string configure)
        => Path.IsPathRooted(configure)
            ? Path.GetFullPath(configure)
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, configure));

    /// <summary>
    /// Le fichier du vrai coeur : dans <c>cores_real/</c> si l'ancien montage du wrapper l'y a deja range (<c>cores/</c> porte
    /// alors le wrapper, qu'on ne remplace pas), sinon dans <c>cores/</c>, d'ou le deploiement du wrapper le prend.
    /// </summary>
    internal static string OuEstLeVraiCoeur(string cores, string coresReal)
        => File.Exists(Path.Combine(coresReal, Coeur)) ? Path.Combine(coresReal, Coeur) : Path.Combine(cores, Coeur);

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
    /// Le <c>es_systems_nelfeplay.cfg</c> tire du systeme gba de RetroBat : ses emulateurs tels quels, et
    /// <c>mgba_splitscreen</c> en dernier parmi les coeurs libretro (mgba reste celui des parties solo). EmulationStation
    /// fusionne ce fichier, sa balise <c>emulators</c> remplace celle du systeme ; le reprendre de RetroBat a chaque passe
    /// evite de masquer un emulateur qu'une mise a jour de RetroBat ajouterait. Null sans systeme gba ou sans libretro.
    /// </summary>
    internal static string? EsSystemsNelfePlay(string esSystemsDeRetroBat)
    {
        var doc = XDocument.Parse(esSystemsDeRetroBat);
        var gba = doc.Root?.Elements("system").FirstOrDefault(s => string.Equals((string?) s.Element("name"), "gba", StringComparison.OrdinalIgnoreCase));
        if (gba?.Element("emulators") is not { } emulateurs)
        {
            return null;
        }

        var copie = new XElement(emulateurs);
        var libretro = copie.Elements("emulator").FirstOrDefault(e => string.Equals((string?) e.Attribute("name"), "libretro", StringComparison.OrdinalIgnoreCase));
        if (libretro is null)
        {
            return null;
        }

        var coeurs = libretro.Element("cores");
        if (coeurs is null)
        {
            coeurs = new XElement("cores");
            libretro.Add(coeurs);
        }

        if (!coeurs.Elements("core").Any(c => string.Equals(c.Value.Trim(), NomDuCoeur, StringComparison.OrdinalIgnoreCase)))
        {
            coeurs.Add(new XElement("core", NomDuCoeur));
        }

        var sortie = new XDocument(
            new XComment(" NelfePlay : complete le systeme gba de RetroBat sans modifier es_systems.cfg (ecrit par APIExpose, repris "
                + "de RetroBat a chaque passe). EmulationStation fusionne ce fichier : la balise <emulators> ci-dessous remplace celle "
                + "du systeme gba. Ajout : le coeur libretro mgba_splitscreen (2 a 4 GBA reliees dans un seul coeur, pour le netplay "
                + "de MULTIPLAYER), en dernier : mgba reste le coeur des parties solo. "),
            new XElement("systemList", new XElement("system", new XElement("name", "gba"), copie)));
        return "<?xml version=\"1.0\"?>\n" + sortie.ToString().Replace("\r\n", "\n") + "\n";
    }
}
