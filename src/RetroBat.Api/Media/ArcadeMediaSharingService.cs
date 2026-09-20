using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Un jeu d'arcade est un jeu d'arcade : le meme dump se retrouve sous plusieurs dossiers
/// roms, et ses medias sont les memes. La chaine ne le savait que pour mame, fbneo, fba et
/// hbmame, parce que ScreenScraper leur donne le meme identifiant de systeme (75) et que le
/// store canonique suit cet identifiant. neogeo (142), cps1 (6), cps2 (7), cps3 (8),
/// naomi (56) et les autres ont donc chacun leur store, et le meme jeu y est retelecharge.
///
/// Mesure du 2026-09-19 sur les index de reference (resources/gamelist/systems), pas sur une
/// borne : mslug porte le groupe « metal_slug_-_super_vehicle-001 » dans arcade_lt.json comme
/// dans neogeo_lt.json, et 100 % des groupes de cps1/cps2/cps3, 94,8 % de ceux de neogeo,
/// 99,8 % de ceux de fbneo sont deja dans arcade_lt.json.
///
/// PIEGE de la donnee, verifie : les EMPREINTES ne peuvent pas servir de cle. arcade_lt.json
/// n'a aucun md5, 76,9 % de ses entrees n'ont aucune empreinte, et ses crc sont degeneres
/// (5a86cff2 est porte par 286 jeux, c'est le BIOS neogeo.zip). Le hash reste la cle du
/// scoring, ou le dump fait foi ; ici la cle est le SLUG du jeu, et a defaut les slugs du
/// meme groupe MAME.
///
/// Deux usages, une seule notion :
/// - en lecture, <see cref="ResolveSharedSourcePath"/> trouve le media deja present dans un
///   store arcade frere, ce qui evite un scrap pour un media qui est deja sur le disque ;
/// - en ecriture, <see cref="PropagateAsync"/> PEUPLE l'addgames a venir des autres dossiers
///   roms ou le meme jeu est installe. Il n'ecrit aucune gamelist et ne pousse rien : le
///   fragment part quand ce systeme a de son cote un rafraichissement autorise.
/// </summary>
public sealed class ArcadeMediaSharingService
{
    /// <summary>Le store canonique de mame, fbneo, fba et hbmame : celui qui porte le plus de jeux.</summary>
    private const string StoreArcade = "arcade";

    /// <summary>Au-dela, la memoire des propagations deja faites est remise a zero.</summary>
    private const int MaxPropagationsMemorisees = 5000;

    private readonly EsProjectionService _projectionService;
    private readonly MediaSystemRules _systemRules;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly MameGamelistGroupIndex _groupIndex;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<ArcadeMediaSharingService>? _logger;

    /// <summary>Ce qui a deja ete peuple, pour ne pas le refaire a l'identique a chaque scrap.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _dejaPropage =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lazy<IReadOnlyList<string>> _storesArcade;

    public ArcadeMediaSharingService(
        EsProjectionService projectionService,
        MediaSystemRules systemRules,
        SystemIdNormalizer systemIdNormalizer,
        MameGamelistGroupIndex groupIndex,
        GamelistUpdateService gamelistUpdateService,
        MediaRuntimeState runtimeState,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<ArcadeMediaSharingService>? logger = null)
    {
        _projectionService = projectionService;
        _systemRules = systemRules;
        _systemIdNormalizer = systemIdNormalizer;
        _groupIndex = groupIndex;
        _gamelistUpdateService = gamelistUpdateService;
        _runtimeState = runtimeState;
        _options = options;
        _logger = logger;
        _storesArcade = new Lazy<IReadOnlyList<string>>(DecouvrirStoresArcade);
    }

    // ── lecture : le media est peut-etre deja la, sous un autre systeme ──────────────────

    /// <summary>Un media trouve chez un autre systeme d'arcade, et le store qui le porte.</summary>
    public sealed record SharedSource(string SystemId, string Path, string Slug);

    /// <summary>
    /// Le media de ce type pour ce jeu, cherche dans les stores arcade FRERES. Appele en
    /// dernier recours, quand le store du systeme et l'heritage de groupe n'ont rien donne.
    /// Retourne null hors arcade, option coupee, ou si rien n'est trouve.
    /// </summary>
    public SharedSource? ResolveSharedSource(
        string frontendSystemId,
        string systemId,
        string gamePath,
        string gameSlug,
        string kind)
    {
        if (!_options.CurrentValue.Scraping.ShareArcadeMediaAcrossSystems ||
            string.IsNullOrWhiteSpace(gameSlug) ||
            !EstArcade(frontendSystemId, systemId))
        {
            return null;
        }

        var storeCourant = string.IsNullOrWhiteSpace(systemId) ? string.Empty : systemId.Trim().ToLowerInvariant();
        foreach (var store in _storesArcade.Value)
        {
            if (string.Equals(store, storeCourant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var slug in SlugsAChercher(frontendSystemId, gamePath, gameSlug))
            {
                var source = _projectionService.ResolveCanonicalSourcePath(store, slug, kind);
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                {
                    continue;
                }

                _logger?.LogInformation(
                    "Media arcade partage depuis un autre systeme ; scrap distant evite pour system={SystemId}, game={GameSlug}, kind={Kind}, store={Store}, slug={Slug}, source={SourcePath}",
                    frontendSystemId,
                    gameSlug,
                    kind,
                    store,
                    slug,
                    source);
                return new SharedSource(store, source, slug);
            }
        }

        return null;
    }

    /// <summary>
    /// Le slug du jeu d'abord : un set d'arcade porte le meme nom court d'un dossier roms a
    /// l'autre (mslug reste mslug). Puis les slugs du meme groupe MAME, pour le cas ou le
    /// systeme frere n'a que le parent ou qu'un autre clone.
    /// </summary>
    private IEnumerable<string> SlugsAChercher(string frontendSystemId, string gamePath, string gameSlug)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gameSlug };
        yield return gameSlug;

        IReadOnlyCollection<string> lies;
        try
        {
            lies = _groupIndex.GetRelatedRoms(frontendSystemId, gamePath ?? string.Empty, gameSlug).ToList();
        }
        catch (Exception ex)
        {
            // L'index de groupes est un confort : son absence ne doit pas couter le slug direct.
            _logger?.LogDebug(ex, "Groupe MAME illisible pour system={SystemId}, game={GameSlug}.", frontendSystemId, gameSlug);
            yield break;
        }

        foreach (var lie in lies)
        {
            var candidat = (lie ?? string.Empty).Trim();
            if (candidat.Length > 0 && vus.Add(candidat))
            {
                yield return candidat;
            }
        }
    }

    // ── ecriture : peupler l'addgames des autres dossiers roms ───────────────────────────

    public sealed record PropagationResult(int Systemes, int Entrees)
    {
        public static readonly PropagationResult Aucune = new(0, 0);
    }

    /// <summary>
    /// Le jeu vient d'etre scrape sous un dossier roms. S'il est aussi installe sous d'autres
    /// dossiers d'arcade, leur fiche porte les memes medias sans le savoir : on met l'entree
    /// en attente pour eux.
    ///
    /// Ce que ce code ne fait PAS, volontairement : aucune ecriture de gamelist, aucun POST
    /// /addgames, aucun F5, aucun reloadgames. Le lot dirty n'entre jamais dans la decision de
    /// delta (voir HasLiveGamelistRefreshDelta) : une entree peuplee ici voyage avec un
    /// rafraichissement deja autorise, elle n'en declenche jamais un.
    /// </summary>
    public async Task<PropagationResult> PropagateAsync(MediaProjectionPlan plan, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Scraping.PropagateArcadeMediaAcrossSystems ||
            plan == null ||
            string.IsNullOrWhiteSpace(plan.FrontendSystemId) ||
            string.IsNullOrWhiteSpace(plan.GamePath) ||
            string.IsNullOrWhiteSpace(plan.GameSlug) ||
            !EstArcade(plan.FrontendSystemId, plan.SystemId))
        {
            return PropagationResult.Aucune;
        }

        // Les systemes dont un jeu est un DOSSIER (daphne, mugen, openbor, teknoparrot) n'ont
        // pas de nom de fichier a rapprocher : leur identite ne se partage pas ainsi.
        if (plan.IsFolderBasedSystem || _systemRules.IsFolderBasedSystem(plan.FrontendSystemId))
        {
            return PropagationResult.Aucune;
        }

        // Pendant une partie, rien ne touche au disque pour le confort de l'interface : le
        // prochain scrap repropagera.
        if (_runtimeState.ShouldBlockLiveAddGames(out var raison, out _))
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "arcade-propagation",
                "gamelist",
                "skipped-game-session",
                new { reason = raison },
                cancellationToken);
            return PropagationResult.Aucune;
        }

        var cibles = ResoudreCibles(plan);
        if (cibles.Count == 0)
        {
            return PropagationResult.Aucune;
        }

        var empreinte = EmpreinteDesMedias(plan);
        var systemes = 0;
        var entrees = 0;
        foreach (var cible in cibles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cle = $"{cible.FrontendSystemId}|{cible.GamePath}".ToLowerInvariant();
            if (_dejaPropage.TryGetValue(cle, out var connue) &&
                string.Equals(connue, empreinte, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                // La memoire ne sert qu'a eviter le travail repete : bornee, elle repart de
                // zero plutot que de grandir sans fin sur une bibliotheque entiere.
                if (_dejaPropage.Count > MaxPropagationsMemorisees)
                {
                    _dejaPropage.Clear();
                }

                var clone = ClonerVers(plan, cible);
                _gamelistUpdateService.MarkLiveGamelistDirty(clone);
                var attente = await _gamelistUpdateService.StageExtendedEntriesAsync(clone, cancellationToken);
                _dejaPropage[cle] = empreinte;
                systemes++;
                if (attente.Changed)
                {
                    entrees++;
                }

                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "arcade-propagation",
                    "gamelist",
                    attente.Changed ? "staged-extended" : "staged-extended-unchanged",
                    new
                    {
                        targetSystem = cible.FrontendSystemId,
                        targetPath = cible.GamePath,
                        attente.MediaContentChanged,
                        attente.MetadataChanged
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Un systeme frere qui echoue ne doit rien couter au jeu qui vient d'etre scrape.
                _logger?.LogWarning(
                    ex,
                    "Propagation arcade ignoree vers system={TargetSystem} pour game={GameSlug}.",
                    cible.FrontendSystemId,
                    plan.GameSlug);
            }
        }

        if (systemes > 0)
        {
            _logger?.LogInformation(
                "Medias arcade propages depuis system={SystemId}, game={GameSlug} vers {Count} systeme(s) : {Systemes}.",
                plan.FrontendSystemId,
                plan.GameSlug,
                systemes,
                string.Join(", ", cibles.Select(c => c.FrontendSystemId)));
        }

        return new PropagationResult(systemes, entrees);
    }

    internal sealed record Cible(string FrontendSystemId, string GamePath);

    private List<Cible> ResoudreCibles(MediaProjectionPlan plan)
    {
        return CiblesPourRom(
            RetroBatPaths.RomsRoot,
            _systemIdNormalizer.NormalizeFrontend(plan.FrontendSystemId),
            Path.GetFileName(plan.GamePath),
            FrontendsArcadeInstalles());
    }

    /// <summary>
    /// Les autres dossiers roms d'arcade ou CE dump est installe. L'identite retenue est le
    /// nom de fichier : un set d'arcade s'appelle pareil partout (mslug reste mslug). Un
    /// dossier absent, ou sans ce fichier, n'est pas une cible.
    /// </summary>
    internal static List<Cible> CiblesPourRom(
        string romsRoot,
        string frontendCourant,
        string nomFichier,
        IEnumerable<string> frontendsArcade)
    {
        var cibles = new List<Cible>();
        if (string.IsNullOrWhiteSpace(nomFichier) || string.IsNullOrWhiteSpace(romsRoot))
        {
            return cibles;
        }

        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frontend in frontendsArcade)
        {
            if (string.IsNullOrWhiteSpace(frontend) ||
                string.Equals(frontend, frontendCourant, StringComparison.OrdinalIgnoreCase) ||
                !vus.Add(frontend))
            {
                continue;
            }

            var candidat = Path.Combine(romsRoot, frontend, nomFichier);
            if (File.Exists(candidat))
            {
                cibles.Add(new Cible(frontend, candidat));
            }
        }

        return cibles;
    }

    /// <summary>Les dossiers roms d'arcade reellement presents sur cette machine.</summary>
    private IEnumerable<string> FrontendsArcadeInstalles()
    {
        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            yield break;
        }

        foreach (var dossier in Directory.EnumerateDirectories(RetroBatPaths.RomsRoot))
        {
            var nom = _systemIdNormalizer.NormalizeFrontend(Path.GetFileName(dossier));
            if (!string.IsNullOrWhiteSpace(nom) && _systemRules.IsArcadeLike(nom))
            {
                yield return nom;
            }
        }
    }

    private MediaProjectionPlan ClonerVers(MediaProjectionPlan source, Cible cible)
    {
        var identite = _gamelistUpdateService.ReadGamelistEntryIdentity(cible.FrontendSystemId, cible.GamePath);
        var gameId = string.IsNullOrWhiteSpace(identite.EsGameId)
            ? _gamelistUpdateService.GenerateEsGameIdForPath(cible.FrontendSystemId, cible.GamePath)
            : identite.EsGameId;
        return ClonerVers(source, cible, RetroBatPaths.RomsRoot, identite, gameId);
    }

    /// <summary>
    /// Le plan du systeme frere. Les chemins des medias sont ramenes en ABSOLU : laisses
    /// relatifs, ils seraient resolus depuis le mauvais roms/&lt;systeme&gt; et l'entree
    /// designerait un fichier inexistant. L'identite de la fiche (gameid, nom) est celle du
    /// systeme cible quand sa gamelist la connait deja.
    /// </summary>
    internal static MediaProjectionPlan ClonerVers(
        MediaProjectionPlan source,
        Cible cible,
        string romsRoot,
        GamelistUpdateService.GamelistEntryIdentity identite,
        string gameId)
    {
        var racineSource = Path.Combine(romsRoot, source.FrontendSystemId);
        return new MediaProjectionPlan
        {
            SystemId = source.SystemId,
            FrontendSystemId = cible.FrontendSystemId,
            GameSlug = source.GameSlug,
            TextSourceGameSlug = source.TextSourceGameSlug,
            DisplayName = string.IsNullOrWhiteSpace(identite.DisplayName) ? source.DisplayName : identite.DisplayName,
            GamePath = cible.GamePath,
            ProjectionBaseName = source.ProjectionBaseName,
            PreferredImageSource = source.PreferredImageSource,
            PreferredLogoSource = source.PreferredLogoSource,
            PreferredThumbnailSource = source.PreferredThumbnailSource,
            IsArcadeLike = source.IsArcadeLike,
            IsFolderBasedSystem = source.IsFolderBasedSystem,
            SkipCrcComputation = source.SkipCrcComputation,
            GamePathExists = true,
            GamelistMd5 = source.GamelistMd5,
            GamelistCrc32 = source.GamelistCrc32,
            GamelistPath = Path.Combine(romsRoot, cible.FrontendSystemId, "gamelist.xml"),
            EsGameId = gameId,
            ScreenScraperGameId = source.ScreenScraperGameId,
            RomRegions = identite.Regions.Count > 0 ? identite.Regions.ToList() : source.RomRegions.ToList(),
            RomLanguages = identite.Languages.Count > 0 ? identite.Languages.ToList() : source.RomLanguages.ToList(),
            // L'entree part en attente, jamais dans la gamelist depuis ce chemin.
            SuppressImmediateGamelistUpdates = true,
            Needs = source.Needs
                .Select(need => new MediaNeed
                {
                    Kind = need.Kind,
                    IsMissing = need.IsMissing,
                    InitialExistingPath = Absolu(need.InitialExistingPath, racineSource),
                    ExistingPath = Absolu(need.ExistingPath, racineSource),
                    TargetRelativePath = need.TargetRelativePath,
                    ImportedPath = Absolu(need.ImportedPath, racineSource),
                    // La projection dans roms/ n'existe plus : seul le store canonique compte.
                    ProjectedPath = string.Empty,
                    WasImported = need.WasImported,
                    WasProjected = false,
                    WasContentChanged = need.WasContentChanged,
                    SharedFromSystemId = need.SharedFromSystemId
                })
                .ToList()
        };
    }

    /// <summary>
    /// Un chemin de media rendu absolu depuis le dossier roms du systeme d'ORIGINE. C'est la
    /// condition pour qu'il designe encore le meme fichier une fois l'entree portee sur un
    /// autre systeme.
    /// </summary>
    internal static string Absolu(string? chemin, string racineSource)
    {
        var valeur = (chemin ?? string.Empty).Trim();
        if (valeur.Length == 0 || Path.IsPathRooted(valeur))
        {
            return valeur;
        }

        var normalise = valeur.Replace('/', Path.DirectorySeparatorChar);
        if (normalise.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalise = normalise[2..];
        }

        try
        {
            return Path.GetFullPath(Path.Combine(racineSource, normalise));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return valeur;
        }
    }

    /// <summary>Ce que porte le jeu maintenant : deux propagations identiques ne se refont pas.</summary>
    internal static string EmpreinteDesMedias(MediaProjectionPlan plan)
    {
        var parties = plan.Needs
            .Select(need => new
            {
                need.Kind,
                Chemin = !string.IsNullOrWhiteSpace(need.ImportedPath) ? need.ImportedPath : need.ExistingPath
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Chemin))
            .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Kind}={x.Chemin!.Replace('\\', '/').ToLowerInvariant()}");
        return string.Join("|", parties);
    }

    private bool EstArcade(string frontendSystemId, string systemId)
    {
        return _systemRules.IsArcadeLike(frontendSystemId) ||
            _systemRules.IsArcadeLike(systemId) ||
            string.Equals(systemId?.Trim(), StoreArcade, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Les stores canoniques d'arcade : « arcade » d'abord, car il porte le plus de jeux, puis
    /// ceux qui existent reellement sous media/systems et media/user/systems.
    /// </summary>
    private IReadOnlyList<string> DecouvrirStoresArcade()
    {
        var candidats = new List<string>();
        foreach (var racine in new[] { RetroBatPaths.MediaSystemsRoot, RetroBatPaths.MediaUserSystemsRoot })
        {
            if (!Directory.Exists(racine))
            {
                continue;
            }

            foreach (var dossier in Directory.EnumerateDirectories(racine).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var nom = _systemIdNormalizer.NormalizeFrontend(Path.GetFileName(dossier));
                if (!string.IsNullOrWhiteSpace(nom) && _systemRules.IsArcadeLike(nom))
                {
                    candidats.Add(nom);
                }
            }
        }

        var stores = StoresPartages(candidats);
        _logger?.LogInformation(
            "Stores arcade partageant leurs medias : {Stores}.",
            string.Join(", ", stores));
        return stores;
    }

    /// <summary>
    /// L'ordre de consultation : « arcade » d'abord, car il porte de loin le plus de jeux
    /// (mesure du 2026-09-19 : 27 479 groupes dans arcade_lt.json contre 540 pour neogeo et
    /// 78 pour cps2), puis les autres, sans doublon.
    /// </summary>
    internal static IReadOnlyList<string> StoresPartages(IEnumerable<string> candidats)
    {
        var stores = new List<string> { StoreArcade };
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StoreArcade };
        foreach (var candidat in candidats)
        {
            var nom = (candidat ?? string.Empty).Trim();
            if (nom.Length > 0 && vus.Add(nom))
            {
                stores.Add(nom);
            }
        }

        return stores;
    }
}
