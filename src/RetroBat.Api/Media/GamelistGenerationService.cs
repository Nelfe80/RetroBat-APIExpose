using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Controllers;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public class GamelistGenerationService
{
    private const string ProgressTaskId = "gamelist-generation";
    private static readonly string[] RomCandidateExtensions =
    {
        ".zip", ".7z", ".rar", ".iso", ".cue", ".chd", ".m3u", ".nes", ".sfc", ".smc", ".bin", ".md", ".gen",
        ".gba", ".gbc", ".gb", ".n64", ".z64", ".v64", ".nds", ".cdi", ".gdi", ".pbp", ".elf", ".exe", ".lnk"
    };

    private readonly IMediaPrefetchService _mediaPrefetchService;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly EsProjectionService _projectionService;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly ITaskProgressService _taskProgressService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly ILogger<GamelistGenerationService>? _logger;

    public GamelistGenerationService(
        IMediaPrefetchService mediaPrefetchService,
        GamelistUpdateService gamelistUpdateService,
        EsProjectionService projectionService,
        SystemIdNormalizer systemIdNormalizer,
        ITaskProgressService taskProgressService,
        MediaRuntimeState runtimeState,
        ILogger<GamelistGenerationService>? logger = null)
    {
        _mediaPrefetchService = mediaPrefetchService;
        _gamelistUpdateService = gamelistUpdateService;
        _projectionService = projectionService;
        _systemIdNormalizer = systemIdNormalizer;
        _taskProgressService = taskProgressService;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    public async Task<GamelistGenerationResponse> GenerateAsync(GamelistGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var scope = ResolveScope(request);
        var gamesBySystem = ResolveGameReferences(request, scope)
            .GroupBy(game => _systemIdNormalizer.NormalizeFrontend(game.SystemId), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);
        var systems = gamesBySystem.Keys.ToList();
        var totalGames = gamesBySystem.Values.Sum(games => games.Count);

        if (totalGames == 0)
        {
            return new GamelistGenerationResponse
            {
                Scope = scope,
                Systems = systems,
                SystemsProcessed = systems.Count
            };
        }

        var processed = 0;
        var gamesWithLocalMedia = 0;
        var systemsUpdated = 0;
        using var reallocation = _runtimeState.BeginMediaReallocation($"gamelist-generation:{scope}");
        _taskProgressService.Report(ProgressTaskId, BuildProgressTitle(scope), 0, totalGames, systems[0]);

        try
        {
            foreach (var (systemId, games) in gamesBySystem)
            {
                var plans = new List<MediaProjectionPlan>(games.Count);
                using var canonicalMediaIndex = _projectionService.BeginCanonicalMediaIndexScope(systemId);
                foreach (var game in games)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var plan = await _mediaPrefetchService.PrepareLocalProjectionPlanAsync(game, cancellationToken);
                    plans.Add(plan);
                    if (plan.Needs.Any(need => !need.IsMissing || need.WasImported || need.WasProjected))
                    {
                        gamesWithLocalMedia++;
                    }

                    processed++;
                    if (processed == 1 || processed % 25 == 0 || processed == totalGames)
                    {
                        _taskProgressService.Report(ProgressTaskId, BuildProgressTitle(scope), processed, totalGames, BuildProgressDetail(game));
                    }
                }

                if (plans.Count > 0)
                {
                    var update = await _gamelistUpdateService.EnsureEntriesAsync(plans, cancellationToken);
                    if (update.Changed)
                    {
                        systemsUpdated++;
                    }
                }

                _logger?.LogInformation(
                    "Generation gamelist systeme terminee: system={SystemId}, games={GameCount}, plans={PlanCount}",
                    systemId,
                    games.Count,
                    plans.Count);
            }
        }
        finally
        {
            _taskProgressService.Complete(ProgressTaskId);
        }

        var reloadRequested = processed > 0 && _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(8));

        _logger?.LogInformation(
            "Generation gamelist terminee: scope={Scope}, systems={SystemCount}, games={GameCount}, gamesWithLocalMedia={GamesWithLocalMedia}",
            scope,
            systems.Count,
            processed,
            gamesWithLocalMedia);

        return new GamelistGenerationResponse
        {
            Scope = scope,
            Systems = systems,
            SystemsProcessed = systems.Count,
            GamesProcessed = processed,
            GamesWithLocalMedia = gamesWithLocalMedia,
            SystemsUpdated = systemsUpdated,
            ReloadGamesRequested = reloadRequested
        };
    }

    private IEnumerable<GameReference> ResolveGameReferences(GamelistGenerationRequest request, string scope)
    {
        return scope switch
        {
            "game" => ResolveSingleGame(request),
            "system" => ResolveSystemGames(RequireSystemId(request)),
            "all" => ResolveAllSystemGames(),
            _ => throw new InvalidOperationException($"Unsupported gamelist generation scope: {scope}")
        };
    }

    private IEnumerable<GameReference> ResolveSingleGame(GamelistGenerationRequest request)
    {
        var systemId = RequireSystemId(request);
        if (string.IsNullOrWhiteSpace(request.GamePath))
        {
            throw new InvalidOperationException("GamePath is required for scope=game.");
        }

        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(systemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        XElement? matchedGameNode = null;
        if (File.Exists(gamelistPath))
        {
            matchedGameNode = LoadGamelistDocumentOrEmpty(gamelistPath).Root?
                .Elements("game")
                .FirstOrDefault(node => MatchesGameNode(node, request.GamePath, request.GameName, systemRoot));
        }

        var gamePath = ResolveAbsoluteGamePath(request.GamePath, systemRoot, matchedGameNode);
        if (!File.Exists(gamePath) && !Directory.Exists(gamePath) && matchedGameNode == null)
        {
            throw new InvalidOperationException($"Game path does not exist and no gamelist entry matched it: {gamePath}");
        }

        yield return BuildGameReference(frontendSystemId, systemRoot, matchedGameNode, gamePath, request.GameName);
    }

    private IEnumerable<GameReference> ResolveSystemGames(string systemId)
    {
        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(systemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        if (!Directory.Exists(systemRoot))
        {
            throw new InvalidOperationException($"System roms directory does not exist: {systemRoot}");
        }

        foreach (var game in ResolveGamesFromGamelist(frontendSystemId, systemRoot))
        {
            yield return game;
        }
    }

    private IEnumerable<GameReference> ResolveAllSystemGames()
    {
        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            yield break;
        }

        foreach (var systemDirectory in Directory.GetDirectories(RetroBatPaths.RomsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var systemId = Path.GetFileName(systemDirectory);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                continue;
            }

            foreach (var game in ResolveSystemGames(systemId))
            {
                yield return game;
            }
        }
    }

    private IEnumerable<GameReference> ResolveGamesFromGamelist(string frontendSystemId, string systemRoot)
    {
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (File.Exists(gamelistPath))
        {
            var document = LoadGamelistDocumentOrEmpty(gamelistPath);
            foreach (var gameNode in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
            {
                var rawPath = gameNode.Element("path")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                yield return BuildGameReference(frontendSystemId, systemRoot, gameNode, ResolveAbsolutePath(systemRoot, rawPath), null);
            }

            yield break;
        }

        foreach (var romPath in EnumerateRomCandidates(systemRoot))
        {
            yield return BuildGameReference(frontendSystemId, systemRoot, null, romPath, null);
        }
    }

    private static IEnumerable<string> EnumerateRomCandidates(string systemRoot)
    {
        return Directory.EnumerateFiles(systemRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => RomCandidateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static GameReference BuildGameReference(string frontendSystemId, string systemRoot, XElement? gameNode, string gamePath, string? requestedName)
    {
        var details = gameNode != null ? BuildGameDetails(gameNode) : new GameDetails();
        var gameName = gameNode?.Element("name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(gameName))
        {
            gameName = !string.IsNullOrWhiteSpace(requestedName)
                ? requestedName.Trim()
                : Path.GetFileNameWithoutExtension(gamePath);
        }

        return new GameReference
        {
            SystemId = frontendSystemId,
            GamePath = gamePath,
            GameName = gameName ?? string.Empty,
            GameId = details.Md5,
            Details = details
        };
    }

    private static GameDetails BuildGameDetails(XElement gameNode)
    {
        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in gameNode.Elements())
        {
            if (!string.IsNullOrWhiteSpace(element.Value))
            {
                extras[element.Name.LocalName] = element.Value.Trim();
            }
        }

        AddExtra(extras, "logo", gameNode.Element("logo")?.Value);
        AddExtra(extras, "wheel", gameNode.Element("wheel")?.Value);
        AddExtra(extras, "wheel-carbon", gameNode.Element("wheelcarbon")?.Value);
        AddExtra(extras, "wheel-steel", gameNode.Element("wheelsteel")?.Value);
        AddExtra(extras, "box-2D", gameNode.Element("boxart")?.Value);
        AddExtra(extras, "box-2D-side", gameNode.Element("boxside")?.Value);
        AddExtra(extras, "box-texture", gameNode.Element("boxtexture")?.Value);
        AddExtra(extras, "cartridge", gameNode.Element("cartridge")?.Value);
        AddExtra(extras, "label", gameNode.Element("label")?.Value);
        AddExtra(extras, "flyer", gameNode.Element("extra1")?.Value);
        AddExtra(extras, "figurine", gameNode.Element("figurine")?.Value);
        AddExtra(extras, "titleshot", gameNode.Element("titleshot")?.Value);
        AddExtra(extras, "screenshot", gameNode.Element("screenshot")?.Value);
        AddExtra(extras, "screenmarqueesmall", gameNode.Element("screenmarqueesmall")?.Value);
        AddExtra(extras, "steamgrid", gameNode.Element("steamgrid")?.Value);
        AddExtra(extras, "mix", gameNode.Element("mix")?.Value);
        AddExtra(extras, "mixrbv1", gameNode.Element("mixrbv1")?.Value);
        AddExtra(extras, "mixrbv2", gameNode.Element("mixrbv2")?.Value);
        AddExtra(extras, "video-normalized", gameNode.Element("videonormalized")?.Value);
        AddExtra(extras, "magazine", gameNode.Element("magazine")?.Value);
        AddExtra(extras, "themehb", gameNode.Element("themehb")?.Value);

        return new GameDetails
        {
            Name = gameNode.Element("name")?.Value?.Trim() ?? string.Empty,
            Desc = gameNode.Element("desc")?.Value?.Trim() ?? string.Empty,
            Image = gameNode.Element("image")?.Value?.Trim() ?? string.Empty,
            Video = gameNode.Element("video")?.Value?.Trim() ?? string.Empty,
            Marquee = gameNode.Element("marquee")?.Value?.Trim() ?? string.Empty,
            Thumbnail = gameNode.Element("thumbnail")?.Value?.Trim() ?? string.Empty,
            Fanart = gameNode.Element("fanart")?.Value?.Trim() ?? string.Empty,
            Bezel = gameNode.Element("bezel")?.Value?.Trim() ?? string.Empty,
            Boxback = gameNode.Element("boxback")?.Value?.Trim() ?? string.Empty,
            Map = gameNode.Element("map")?.Value?.Trim() ?? string.Empty,
            Manual = gameNode.Element("manual")?.Value?.Trim() ?? string.Empty,
            Releasedate = gameNode.Element("releasedate")?.Value?.Trim() ?? string.Empty,
            Developer = gameNode.Element("developer")?.Value?.Trim() ?? string.Empty,
            Publisher = gameNode.Element("publisher")?.Value?.Trim() ?? string.Empty,
            Players = gameNode.Element("players")?.Value?.Trim() ?? string.Empty,
            Lang = gameNode.Element("lang")?.Value?.Trim() ?? string.Empty,
            Region = gameNode.Element("region")?.Value?.Trim() ?? string.Empty,
            Genre = gameNode.Element("genre")?.Value?.Trim() ?? string.Empty,
            Genres = gameNode.Element("genres")?.Value?.Trim() ?? string.Empty,
            Family = gameNode.Element("family")?.Value?.Trim() ?? string.Empty,
            Rating = gameNode.Element("rating")?.Value?.Trim() ?? string.Empty,
            Md5 = gameNode.Element("md5")?.Value?.Trim()
                ?? gameNode.Element("cheevosHash")?.Value?.Trim()
                ?? string.Empty,
            Extras = extras
        };
    }

    private static void AddExtra(Dictionary<string, string> extras, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            extras[key] = value.Trim();
        }
    }

    private static bool MatchesGameNode(XElement gameNode, string requestedPath, string? requestedName, string systemRoot)
    {
        var gamePath = gameNode.Element("path")?.Value?.Trim();
        var gameName = gameNode.Element("name")?.Value?.Trim();
        var normalizedRequestedPath = NormalizeCompareValue(requestedPath);
        var requestedFileName = NormalizeCompareValue(Path.GetFileName(requestedPath));
        var requestedNameValue = NormalizeCompareValue(requestedName);

        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var absoluteGamePath = ResolveAbsolutePath(systemRoot, gamePath);
            var candidates = new[]
            {
                NormalizeCompareValue(gamePath),
                NormalizeCompareValue(absoluteGamePath),
                NormalizeCompareValue(Path.GetFileName(gamePath)),
                NormalizeCompareValue(Path.GetFileName(absoluteGamePath))
            };

            if (candidates.Contains(normalizedRequestedPath, StringComparer.OrdinalIgnoreCase)
                || candidates.Contains(requestedFileName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return !string.IsNullOrWhiteSpace(requestedNameValue)
            && string.Equals(NormalizeCompareValue(gameName), requestedNameValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAbsoluteGamePath(string requestedPath, string systemRoot, XElement? matchedGameNode)
    {
        if (Path.IsPathRooted(requestedPath))
        {
            return requestedPath;
        }

        var matchedPath = matchedGameNode?.Element("path")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(matchedPath))
        {
            return ResolveAbsolutePath(systemRoot, matchedPath);
        }

        return ResolveAbsolutePath(systemRoot, requestedPath);
    }

    private static string ResolveAbsolutePath(string systemRoot, string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('/', Path.DirectorySeparatorChar);
        if (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return Path.GetFullPath(Path.Combine(systemRoot, normalized));
    }

    private static XDocument LoadGamelistDocumentOrEmpty(string gamelistPath)
    {
        try
        {
            using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex) when (ex.Message.Contains("Root element is missing", StringComparison.OrdinalIgnoreCase))
        {
            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XElement("gameList"));
        }
    }

    private static string ResolveScope(GamelistGenerationRequest request)
    {
        var requestedScope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(requestedScope))
        {
            return requestedScope switch
            {
                "game" or "entry" => "game",
                "system" or "systeme" => "system",
                "all" or "systems" or "tous" => "all",
                _ => throw new InvalidOperationException("Scope must be one of: game, system, all.")
            };
        }

        if (!string.IsNullOrWhiteSpace(request.GamePath))
        {
            return "game";
        }

        return string.IsNullOrWhiteSpace(request.SystemId) ? "all" : "system";
    }

    private static string RequireSystemId(GamelistGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SystemId))
        {
            throw new InvalidOperationException("SystemId is required for this gamelist generation scope.");
        }

        return request.SystemId.Trim();
    }

    private static string BuildProgressTitle(string scope)
    {
        return scope switch
        {
            "game" => "Gamelist - jeu",
            "system" => "Gamelist - systeme",
            _ => "Gamelist - tous systemes"
        };
    }

    private static string BuildProgressDetail(GameReference game)
    {
        var name = string.IsNullOrWhiteSpace(game.GameName)
            ? Path.GetFileNameWithoutExtension(game.GamePath)
            : game.GameName;
        return $"{game.SystemId} / {name}";
    }

    private static string NormalizeCompareValue(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }
}
