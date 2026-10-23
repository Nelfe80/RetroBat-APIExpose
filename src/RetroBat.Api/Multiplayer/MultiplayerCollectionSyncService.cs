using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Tient a jour la collection EmulationStation « NELFEPLAY MULTIPLAYER » : les jeux du catalogue
/// MULTIPLAYER que cette borne possede.
///
/// Deux autorites : le Data Pack dit quels jeux passent le netplay et avec quel coeur, la borne
/// dit ce qu'elle possede. Rien ne remonte, rien n'est telecharge ici : le catalogue arrive par
/// la synchronisation du Data Pack.
///
/// Un jeu d'arcade entre par le nom de son set, dans le premier dossier d'arcade qui l'a (fbneo
/// d'abord : MULTIPLAYER joue l'arcade sous FBNeo). Un jeu console entre par son contenu, jamais
/// par son nom de fichier : deux dumps differents se desynchronisent en netplay, et RetroArch ne
/// fait qu'avertir quand les CRC different.
/// </summary>
public sealed class MultiplayerCollectionSyncService : BackgroundService
{
    public const string CollectionName = "nelfeplay-multiplayer";

    /// <summary>Le logo livre par le Data Pack (<c>theme/images</c>), dessine le 21/10.</summary>
    public const string LogoFile = "nelfeplay-multiplayer.png";

    /// <summary>Le fond de World Scoring : meme univers visuel (globe, hexagones, orbite).</summary>
    public const string FondFile = "nelfeplay-worldscoring-bg.jpg";

    /// <summary>
    /// L'ordre de recherche d'un set. FBNeo d'abord ; puis les dossiers d'arcade generiques ; puis
    /// ceux d'une seule carte, que RetroBat lance deja sous FBNeo.
    /// </summary>
    internal static readonly string[] DossiersArcade = ["fbneo", "arcade", "fba", "mame", "mame64", "neogeo", "cps1", "cps2", "cps3"];

    private static readonly string[] ExtensionsArcade = [".zip", ".7z"];
    private static readonly TimeSpan PremierDelai = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(300);
    private const long PlafondContenu = 64L * 1024 * 1024;

    /// <summary>Le prefiltre d'un fichier, garde tant qu'il ne change pas : CRC de l'entree unique d'un zip, ou taille.</summary>
    private static readonly ConcurrentDictionary<string, (string? Crc, long Taille)> Prefiltres = new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly MultiplayerCatalogueStore _store;
    private readonly InstalledGameCatalog _catalog;
    private readonly EsCustomCollectionWriter _writer;
    private readonly EsCollectionThemeAssets _assets;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEsSettingsChangeBus? _settingsChangeBus;
    private readonly IEventBus? _bus;
    private readonly ILogger<MultiplayerCollectionSyncService>? _logger;
    private readonly string _romsRoot;
    private readonly Func<bool> _emulateurTourne;
    private readonly Func<string, string?> _md5DuContenu;
    private bool _inscriptionsFaites;
    private readonly SemaphoreSlim _porte = new(1, 1);
    private IDisposable? _abonnementReglages;
    private IDisposable? _abonnementBus;
    private bool _derniereVisibilite;
    private MultiplayerCollectionStatus _statut = MultiplayerCollectionStatus.Initial;

    public MultiplayerCollectionSyncService(
        IOptionsMonitor<ApiExposeOptions> options,
        MultiplayerCatalogueStore store,
        InstalledGameCatalog catalog,
        EsCustomCollectionWriter writer,
        EsCollectionThemeAssets assets,
        MediaRuntimeState runtimeState,
        IEsSettingsChangeBus? settingsChangeBus = null,
        IEventBus? bus = null,
        ILogger<MultiplayerCollectionSyncService>? logger = null,
        string? romsRoot = null,
        Func<bool>? emulateurTourne = null,
        Func<string, string?>? md5DuContenu = null,
        GamelistUpdateService? gamelists = null)
    {
        _options = options;
        _store = store;
        _catalog = catalog;
        _writer = writer;
        _assets = assets;
        _runtimeState = runtimeState;
        _settingsChangeBus = settingsChangeBus;
        _bus = bus;
        _logger = logger;
        _romsRoot = romsRoot ?? RetroBatPaths.RomsRoot;
        _emulateurTourne = emulateurTourne ?? EmulatorForeground.EmulateurTourne;
        _md5DuContenu = md5DuContenu ?? NelfePlayScoringCollectionSyncService.Md5DuContenu;
        Declarer = gamelists is null ? null : (systeme, jeux) => gamelists.DeclarerJeuxAbsents(systeme, jeux);
    }

    /// <summary>Inscrit dans la gamelist d'un systeme les jeux qu'elle ne nomme pas (voir <see cref="InscrireLesJeuxInconnusDEs"/>).</summary>
    internal Func<string, IReadOnlyList<(string CheminAbsolu, string Nom)>, int>? Declarer { get; set; }

    /// <summary>Le dernier etat connu.</summary>
    public MultiplayerCollectionStatus Status => _statut;

    /// <summary>
    /// Les jeux MULTIPLAYER de la derniere passe, par chemin : c'est ce que le panneau et le
    /// lancement liront pour savoir qu'un jeu est MULTIPLAYER, a quel niveau et avec quel coeur.
    /// </summary>
    public IReadOnlyList<MultiplayerInstalledGame> Games { get; private set; } = [];

    private bool Visible => _options.CurrentValue.NelfePlay is { Enabled: true, ShowMultiplayerCollection: true };

    /// <summary>
    /// Les jeux MULTIPLAYER de la borne, calcules tout de suite s'ils ne le sont pas encore : juste apres le
    /// demarrage de l'API, la premiere passe n'arrive qu'au bout d'une minute, et l'appui long repondait « pas un
    /// jeu de la collection » (vu le 22/10). Le calcul ne touche ni a la collection d'ES ni aux themes.
    /// </summary>
    public IReadOnlyList<MultiplayerInstalledGame> JeuxConnus()
    {
        if (Games.Count > 0 || !Visible)
        {
            return Games;
        }

        var catalogue = _store.Lire(out _);
        if (catalogue is null)
        {
            return Games;
        }

        Games = Intersecter(catalogue);
        return Games;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _derniereVisibilite = Visible;
        // Couper l'option fait disparaitre la collection tout de suite, pas au tic suivant.
        _abonnementReglages = _settingsChangeBus?.Subscribe(async (_, token) =>
        {
            if (Visible == _derniereVisibilite)
            {
                return;
            }

            _derniereVisibilite = Visible;
            await SynchroniserAsync("option", token);
        });

        // Comme World Scoring : rien ne bouge pendant une partie, la passe suit donc la fin de partie.
        _abonnementBus = _bus?.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "ui.game.ended", StringComparison.Ordinal))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    await SynchroniserAsync("fin de partie", stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Collection MULTIPLAYER : passe de fin de partie en echec");
                }
            }, stoppingToken);
        });

        try
        {
            await Task.Delay(PremierDelai, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SynchroniserAsync("periodique", stoppingToken);
                await Task.Delay(Periode, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _abonnementReglages?.Dispose();
            _abonnementBus?.Dispose();
        }
    }

    /// <summary>Une passe : catalogue, inventaire, intersection, ecriture.</summary>
    public async Task<MultiplayerCollectionStatus> SynchroniserAsync(string declencheur, CancellationToken cancellationToken)
    {
        if (!await _porte.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
        {
            return _statut;
        }

        try
        {
            if (!Visible)
            {
                return _statut = Desactiver();
            }

            // Pendant une partie, rien ne doit bouger devant le joueur.
            if (_emulateurTourne())
            {
                return _statut;
            }

            var catalogue = _store.Lire(out var erreur);
            if (catalogue == null)
            {
                // Un catalogue absent ou refuse n'est pas une liste vide : la collection reste telle quelle.
                _logger?.LogInformation("Collection MULTIPLAYER inchangee : {Erreur} (declencheur {Declencheur})", erreur, declencheur);
                return _statut = _statut with { State = "stale", LastError = erreur, LastAttemptUtc = DateTime.UtcNow };
            }

            var jeux = Intersecter(catalogue);
            var chemins = jeux.Select(jeu => jeu.Path).ToList();
            var resultat = _writer.Apply(CollectionName, chemins);
            var visuel = chemins.Count > 0 ? _assets.Install(CollectionName, LogoFile, FondFile) : _assets.Remove(CollectionName);
            if (visuel.Changed)
            {
                PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
            }

            Games = jeux;
            var inscrits = resultat.Changed || !_inscriptionsFaites ? InscrireLesJeuxInconnusDEs(jeux) : 0;
            if (resultat.Changed || visuel.Changed || inscrits > 0)
            {
                _logger?.LogInformation(
                    "Collection MULTIPLAYER : {Entrees} jeux sur {Catalogue} au catalogue, revision {Revision}",
                    chemins.Count, catalogue.Games.Count, catalogue.Revision);
                // Une collection qui apparait, change ou disparait est un nouveau systeme pour ES.
                _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), silencieux: true);
            }

            var maintenant = DateTime.UtcNow;
            return _statut = new MultiplayerCollectionStatus(
                Enabled: true,
                State: "ok",
                Revision: catalogue.Revision,
                CatalogueGames: catalogue.Games.Count,
                LocalGames: chemins.Count,
                Missing: catalogue.Games.Where(jeu => jeux.All(local => local.Game.Id != jeu.Id)).Select(jeu => jeu.Id).ToList(),
                LastSuccessUtc: maintenant,
                LastAttemptUtc: maintenant,
                LastError: null);
        }
        catch (OperationCanceledException)
        {
            return _statut;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Collection MULTIPLAYER : passe interrompue");
            return _statut = _statut with { State = "stale", LastError = ex.GetType().Name, LastAttemptUtc = DateTime.UtcNow };
        }
        finally
        {
            _porte.Release();
        }
    }

    /// <summary>
    /// UN JEU DE LA COLLECTION DOIT EXISTER POUR EMULATIONSTATION (comme World Scoring, 26/09). Avec ParseGamelistOnly,
    /// ES ne charge que les jeux que leur gamelist nomme, et n'affiche dans une collection que les jeux qu'il a charges :
    /// Mario Kart: Super Circuit manquait a la collection (23/10), le catalogue preferant la version USA quand la gamelist
    /// ne nommait que l'europeenne. Chaque jeu retenu y est inscrit s'il n'y est pas ; a la premiere passe et quand la
    /// collection change, pas a chaque tic : une gamelist d'arcade se relit cher.
    /// </summary>
    private int InscrireLesJeuxInconnusDEs(IReadOnlyList<MultiplayerInstalledGame> jeux)
    {
        _inscriptionsFaites = true;
        if (Declarer is null || jeux.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var systeme in jeux.GroupBy(jeu => Path.GetFileName(Path.GetDirectoryName(jeu.Path)) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (systeme.Key.Length == 0)
            {
                continue;
            }

            var entrees = systeme
                .Select(jeu => (jeu.Path, GamelistIdentity.DeclaredName(systeme.Key, jeu.Game.Name, Path.GetFileNameWithoutExtension(jeu.Path)) ?? jeu.Game.Name))
                .ToList();
            try
            {
                total += Declarer(systeme.Key, entrees);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                _logger?.LogWarning(ex, "Collection MULTIPLAYER : gamelist de {Systeme} non completee", systeme.Key);
            }
        }

        if (total > 0)
        {
            _logger?.LogInformation("Collection MULTIPLAYER : {Nombre} jeu(x) inscrit(s) dans leur gamelist pour qu'EmulationStation les charge", total);
        }

        return total;
    }

    private MultiplayerCollectionStatus Desactiver()
    {
        Games = [];
        var resultat = _writer.Remove(CollectionName);
        if (_assets.Remove(CollectionName).Changed)
        {
            PhysicalMediaWebSocketProjectionService.InvalidateThemeArt();
        }

        if (resultat.Changed)
        {
            _logger?.LogInformation("Collection MULTIPLAYER retiree (option coupee)");
            _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), silencieux: true);
        }

        return MultiplayerCollectionStatus.Initial with { Enabled = false, State = "disabled" };
    }

    /// <summary>Un fichier par jeu du catalogue que la borne possede, dans l'ordre du catalogue.</summary>
    internal IReadOnlyList<MultiplayerInstalledGame> Intersecter(MultiplayerCatalogue catalogue)
    {
        var inventaires = new Dictionary<string, IReadOnlyList<InstalledGame>>(StringComparer.OrdinalIgnoreCase);
        var retenus = new List<MultiplayerInstalledGame>();
        foreach (var jeu in catalogue.Games)
        {
            string? chemin;
            string? cle;
            if (jeu.EstArcade)
            {
                chemin = TrouverSet(jeu.Set!);
                cle = jeu.Set!;
            }
            else
            {
                if (!inventaires.TryGetValue(jeu.System, out var inventaire))
                {
                    inventaires[jeu.System] = inventaire = _catalog.Enumerate([jeu.System]);
                }

                (chemin, cle) = TrouverContenu(jeu.Content!, inventaire);
            }

            if (chemin != null && cle != null)
            {
                retenus.Add(new MultiplayerInstalledGame(jeu, chemin, catalogue.CoreOf(jeu), cle));
            }
        }

        return retenus;
    }

    private string? TrouverSet(string set)
    {
        foreach (var dossier in DossiersArcade)
        {
            foreach (var extension in ExtensionsArcade)
            {
                var chemin = Path.Combine(_romsRoot, dossier, set + extension);
                if (File.Exists(chemin))
                {
                    return chemin;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Le dump installe dont le CONTENU est accepte, dans l'ordre de preference du catalogue (la
    /// version USA avant celle qui demande sa langue au demarrage). Le md5 de la gamelist suffit
    /// quand il correspond ; sinon le contenu est mesure, mais seulement pour un fichier qui passe
    /// le prefiltre : on ne hache pas un dossier de deux mille cartouches a chaque passe.
    /// </summary>
    private (string? Chemin, string? Md5) TrouverContenu(MultiplayerContent contenu, IReadOnlyList<InstalledGame> inventaire)
    {
        var md5 = contenu.Md5.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trouves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installe in inventaire)
        {
            if (installe.Md5 is { Length: > 0 } declare && md5.Contains(declare))
            {
                trouves.TryAdd(declare, installe.AbsolutePath);
            }
        }

        foreach (var installe in inventaire)
        {
            if (trouves.Count == md5.Count)
            {
                break;
            }

            if (PassePrefiltre(installe.AbsolutePath, contenu) && _md5DuContenu(installe.AbsolutePath) is { } mesure && md5.Contains(mesure))
            {
                trouves.TryAdd(mesure, installe.AbsolutePath);
            }
        }

        foreach (var prefere in contenu.Md5)
        {
            if (trouves.TryGetValue(prefere, out var chemin))
            {
                return (chemin, prefere);
            }
        }

        return (null, null);
    }

    internal static bool PassePrefiltre(string chemin, MultiplayerContent contenu)
    {
        var (crc, taille) = Prefiltre(chemin);
        if (taille <= 0 || taille > PlafondContenu)
        {
            return false;
        }

        if (crc != null)
        {
            return contenu.Crc.Count == 0 || contenu.Crc.Contains(crc, StringComparer.OrdinalIgnoreCase);
        }

        return contenu.Size.Count == 0 || contenu.Size.Contains(taille);
    }

    /// <summary>
    /// Ce qu'on sait d'un fichier sans le lire en entier : pour un zip, le CRC et la taille de son
    /// entree unique, ecrits dans son repertoire central ; pour un fichier nu, sa taille.
    /// </summary>
    private static (string? Crc, long Taille) Prefiltre(string chemin)
    {
        try
        {
            var info = new FileInfo(chemin);
            if (!info.Exists)
            {
                return (null, 0);
            }

            var cle = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (Prefiltres.TryGetValue(cle, out var connu))
            {
                return connu;
            }

            (string? Crc, long Taille) resultat = (null, info.Length);
            if (string.Equals(info.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(info.FullName);
                var fichiers = zip.Entries.Where(entree => entree.Length > 0).ToList();
                // Plusieurs fichiers : ce n'est pas une cartouche zippee, le md5 du contenu n'existe pas.
                resultat = fichiers.Count == 1 ? (fichiers[0].Crc32.ToString("x8"), fichiers[0].Length) : (null, 0);
            }

            Prefiltres[cle] = resultat;
            return resultat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, 0);
        }
    }
}

/// <summary>
/// Un jeu du catalogue, le fichier qui le porte sur cette borne, son coeur, et la cle de contenu
/// que la plateforme compare (le set en arcade, le md5 en console) : une gameroom ne reunit que la meme.
/// </summary>
public sealed record MultiplayerInstalledGame(MultiplayerGame Game, string Path, MultiplayerCore? Core, string ContentKey)
{
    /// <summary>Le panneau propose les choix du niveau NelfePlay quand un depart pret existe pour ce contenu.</summary>
    public bool ChoixOuverts => Game.EstNelfePlay && Game.Choices is { Count: > 0 } && Game.ADesDepartsPour(ContentKey);

    /// <summary>Le depart pret d'une partie a <paramref name="joueurs"/> sur ce contenu ; null : elle se joue en classique.</summary>
    public MultiplayerStart? DepartPour(int joueurs) => Game.EstNelfePlay ? Game.DepartPour(joueurs, ContentKey) : null;
}

public sealed record MultiplayerCollectionStatus(
    bool Enabled,
    string State,
    string? Revision,
    int CatalogueGames,
    int LocalGames,
    IReadOnlyList<string> Missing,
    DateTime? LastSuccessUtc,
    DateTime? LastAttemptUtc,
    string? LastError)
{
    public static MultiplayerCollectionStatus Initial => new(true, "pending", null, 0, 0, [], null, null, null);
}
