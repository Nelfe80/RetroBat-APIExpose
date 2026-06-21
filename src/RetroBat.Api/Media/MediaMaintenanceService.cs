using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Xml;
using RetroBat.Api.Controllers;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public class MediaMaintenanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int FileIoRetryCount = 10;
    private const string GamelistBackupDirectoryName = ".api-expose-gamelist-backups";
    private const string GamelistAuditDirectoryName = ".api-expose-gamelist-audit";
    private const int GamelistBackupRetentionCount = 1;
    private static readonly TimeSpan FileIoRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly string[] GamelistMediaTags =
    {
        "image", "manual", "video", "marquee", "thumbnail", "fanart", "bezel", "boxback", "map", "desc",
        "wheel", "wheelround", "boxart", "cartridge", "label", "titleshot", "screenshot", "mix", "mixvideo", "magazine", "music", "extra1",
        "flyer", "figurine", "boxside", "boxtexture", "screenmarqueesmall", "steamgrid", "mixrbv1", "mixrbv2",
        "videonormalized", "themehb"
    };
    private static readonly string[] GamelistTextTags =
    {
        "name", "desc", "developer", "publisher", "players", "lang", "region", "genre", "family", "genres"
    };

    private readonly IMediaPrefetchService _mediaPrefetchService;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly MediaRuntimeState _runtimeState;
    private readonly ITaskProgressService _taskProgressService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IGamelistStore _gamelistStore;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly ScreenScraperRawCacheMetadataService _rawCacheMetadataService;

    public MediaMaintenanceService(
        IMediaPrefetchService mediaPrefetchService,
        SystemIdNormalizer systemIdNormalizer,
        GameNameNormalizer gameNameNormalizer,
        MediaRuntimeState runtimeState,
        ITaskProgressService taskProgressService,
        EmulationStationSettingsService settingsService,
        IGamelistStore gamelistStore,
        GamelistUpdateService gamelistUpdateService,
        ScreenScraperRawCacheMetadataService rawCacheMetadataService)
    {
        _mediaPrefetchService = mediaPrefetchService;
        _systemIdNormalizer = systemIdNormalizer;
        _gameNameNormalizer = gameNameNormalizer;
        _runtimeState = runtimeState;
        _taskProgressService = taskProgressService;
        _settingsService = settingsService;
        _gamelistStore = gamelistStore;
        _gamelistUpdateService = gamelistUpdateService;
        _rawCacheMetadataService = rawCacheMetadataService;
    }

    public async Task<MediaMaintenanceResponse> ForceLocalResyncAsync(MediaScrapeRequest request, CancellationToken cancellationToken = default)
    {
        using var reallocation = _runtimeState.BeginMediaReallocation("force-local-resync");
        var target = ResolveTargetGame(request);
        EnsureTargetGamePathExists(target);
        _taskProgressService.Report("force-local-resync", "Reallocation locale", 0, 3, target.Game.GameName);
        try
        {
            var cleanup = ClearEsProjection(target);
            _taskProgressService.Report("force-local-resync", "Reallocation locale", 1, 3, "nettoyage projections");
            var result = await _mediaPrefetchService.PrefetchForSelectionAsync(target.Game, allowRemoteScrape: false, cancellationToken);
            _taskProgressService.Report("force-local-resync", "Reallocation locale", 2, 3, "reconstruction gamelist");
            _runtimeState.MarkReloadGamesPending();
            _taskProgressService.Report("force-local-resync", "Reallocation locale", 3, 3, "refresh ES planifie");

            return new MediaMaintenanceResponse
            {
                Action = "local-resync",
                SystemId = target.SystemId,
                GameSlug = target.GameSlug,
                GamePath = target.Game.GamePath,
                GameName = target.Game.GameName,
                DeletedEsMediaFiles = cleanup.DeletedEsMediaFiles,
                RemovedGamelistTags = cleanup.RemovedGamelistTags,
                QueuedRemoteScrape = result.QueuedRemoteScrape
            };
        }
        finally
        {
            _taskProgressService.Complete("force-local-resync");
        }
    }

    public async Task<MediaMaintenanceResponse> ForceRemoteRescrapeAsync(MediaScrapeRequest request, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("Remote scraping is archived in this build. Use rescrape/local or the gamelist generation endpoints.");
    }

    public async Task<MetadataNormalizationResponse> NormalizeLocalMetadataAsync(
        MetadataNormalizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = ResolveNormalizationScope(request.Scope, "metadata normalization");
        EnsureGameTargetIfNeeded(scope, request);

        var systems = ResolveMetadataNormalizationSystems(scope, request.SystemId);
        var response = new MetadataNormalizationResponse
        {
            Scope = scope,
            Systems = systems.ToList()
        };

        foreach (var systemId in systems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScreenScraperRawCacheMetadataRebuildSystemResult? rawSystemResult = null;
            if (request.RebuildFromScreenScraperCache)
            {
                var rawCacheResult = await _rawCacheMetadataService.RebuildAsync(
                    [systemId],
                    ResolveRequestedMetadataGameSlugs(systemId, request),
                    request.PreferredLanguage,
                    cancellationToken);
                response.RawCachePayloadsScanned += rawCacheResult.PayloadsScanned;
                response.RawCachePayloadsFailed += rawCacheResult.PayloadsFailed;
                response.RawCacheMetadataBundlesWritten += rawCacheResult.MetadataBundlesWritten;
                response.RawCacheMetadataBundlesSkipped += rawCacheResult.MetadataBundlesSkipped;
                rawSystemResult = rawCacheResult.SystemResults.FirstOrDefault();
            }

            var result = await NormalizeMetadataSystemAsync(systemId, request, cancellationToken);
            if (rawSystemResult != null)
            {
                result.RawCachePayloadsScanned = rawSystemResult.PayloadsScanned;
                result.RawCachePayloadsFailed = rawSystemResult.PayloadsFailed;
                result.RawCacheMetadataBundlesWritten = rawSystemResult.MetadataBundlesWritten;
                result.RawCacheMetadataBundlesSkipped = rawSystemResult.MetadataBundlesSkipped;
                result.Errors.AddRange(rawSystemResult.Errors);
            }

            response.SystemResults.Add(result);
            response.SystemsProcessed++;
            response.MetadataFilesScanned += result.MetadataFilesScanned;
            response.MetadataFilesUpdated += result.MetadataFilesUpdated;
            response.MetadataFilesRemoved += result.MetadataFilesRemoved;
            response.MetadataFilesFailed += result.MetadataFilesFailed;
            response.FieldsNormalized += result.FieldsNormalized;
        }

        return response;
    }

    public async Task<GamelistTextNormalizationResponse> NormalizeExistingGamelistsAsync(
        MetadataNormalizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = ResolveNormalizationScope(request.Scope, "gamelist normalization");
        EnsureGameTargetIfNeeded(scope, request);

        var systems = ResolveGamelistNormalizationSystems(scope, request.SystemId);
        var language = ResolvePreferredLanguageCode(request.PreferredLanguage)
            ?? _settingsService.GetScrapingSettings().Language;

        var response = new GamelistTextNormalizationResponse
        {
            Scope = scope,
            PreferredLanguage = language,
            Systems = systems.ToList()
        };

        foreach (var systemId in systems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(
                () => NormalizeExistingGamelistSystem(systemId, language, request, cancellationToken),
                cancellationToken);
            response.SystemResults.Add(result);
            response.SystemsProcessed++;
            response.GamelistsScanned += result.GamelistScanned ? 1 : 0;
            response.GamelistsUpdated += result.GamelistUpdated ? 1 : 0;
            response.GamesScanned += result.GamesScanned;
            response.FieldsScanned += result.FieldsScanned;
            response.FieldsNormalized += result.FieldsNormalized;
            response.Errors.AddRange(result.Errors);
        }

        return response;
    }

    private ResolvedGameTarget ResolveTargetGame(MediaScrapeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SystemId))
        {
            throw new InvalidOperationException("SystemId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.GamePath))
        {
            throw new InvalidOperationException("GamePath is required.");
        }

        var systemId = _systemIdNormalizer.Normalize(request.SystemId);
        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(request.SystemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var requestedPath = request.GamePath.Trim();
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");

        XElement? matchedGameNode = null;
        if (File.Exists(gamelistPath))
        {
            lock (GetGamelistLock(gamelistPath))
            {
                var document = LoadGamelistDocument(gamelistPath);
                matchedGameNode = document.Root?
                    .Elements("game")
                    .FirstOrDefault(node => MatchesGameNode(node, requestedPath, request.GameName, systemRoot));
            }
        }

        var absoluteGamePath = ResolveAbsoluteGamePath(requestedPath, systemRoot, matchedGameNode);
        var gameName = matchedGameNode?.Element("name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(gameName))
        {
            gameName = !string.IsNullOrWhiteSpace(request.GameName)
                ? request.GameName.Trim()
                : Path.GetFileNameWithoutExtension(absoluteGamePath);
        }

        var details = matchedGameNode != null ? BuildGameDetails(matchedGameNode) : new GameDetails();
        var game = new GameReference
        {
            SystemId = frontendSystemId,
            GamePath = absoluteGamePath,
            GameName = gameName,
            GameId = details.Md5,
            Details = details
        };

        var gameSlug = _gameNameNormalizer.NormalizeGameSlug(game.GameName, game.GamePath);
        return new ResolvedGameTarget(systemId, frontendSystemId, systemRoot, gamelistPath, gameSlug, game, matchedGameNode);
    }

    private static string ResolveNormalizationScope(string? requestedScope, string operationName)
    {
        var scope = string.IsNullOrWhiteSpace(requestedScope)
            ? "all"
            : requestedScope.Trim().ToLowerInvariant();

        return scope is "all" or "system" or "game"
            ? scope
            : throw new InvalidOperationException($"Unsupported {operationName} scope: {requestedScope}");
    }

    private static void EnsureGameTargetIfNeeded(string scope, MetadataNormalizationRequest request)
    {
        if (scope != "game")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SystemId))
        {
            throw new InvalidOperationException("SystemId is required for scope=game.");
        }

        if (string.IsNullOrWhiteSpace(request.GamePath) &&
            string.IsNullOrWhiteSpace(request.GameName) &&
            string.IsNullOrWhiteSpace(request.GameSlug))
        {
            throw new InvalidOperationException("GamePath, GameName or GameSlug is required for scope=game.");
        }
    }

    private static bool IsGameScope(MetadataNormalizationRequest request)
    {
        return string.Equals((request.Scope ?? string.Empty).Trim(), "game", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> ResolveRequestedMetadataGameSlugs(string systemId, MetadataNormalizationRequest request)
    {
        if (!IsGameScope(request))
        {
            return [];
        }

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSlug(slugs, request.GameSlug);
        AddSlug(slugs, _gameNameNormalizer.NormalizeGameSlug(request.GameName, request.GamePath));

        if (!string.IsNullOrWhiteSpace(request.GamePath))
        {
            AddSlug(slugs, _gameNameNormalizer.NormalizeGameSlug(
                Path.GetFileNameWithoutExtension(request.GamePath),
                request.GamePath));
        }

        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(request.SystemId ?? systemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (File.Exists(gamelistPath))
        {
            lock (GetGamelistLock(gamelistPath))
            {
                var document = LoadGamelistDocument(gamelistPath);
                foreach (var gameNode in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                {
                    if (!MatchesRequestedGameEntry(gameNode, request.GamePath, request.GameName, request.GameSlug, frontendSystemId, systemRoot))
                    {
                        continue;
                    }

                    var rawPath = gameNode.Element("path")?.Value?.Trim();
                    var gameName = gameNode.Element("name")?.Value?.Trim();
                    AddSlug(slugs, _gameNameNormalizer.NormalizeGameSlug(gameName, rawPath));
                    AddSlug(slugs, _gameNameNormalizer.NormalizeGameSlug(Path.GetFileNameWithoutExtension(rawPath), rawPath));
                }
            }
        }

        return slugs
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .OrderBy(slug => slug, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool MatchesRequestedGameEntry(
        XElement gameNode,
        string? requestedPath,
        string? requestedName,
        string? requestedSlug,
        string frontendSystemId,
        string systemRoot)
    {
        _ = frontendSystemId;

        if (!string.IsNullOrWhiteSpace(requestedPath) &&
            MatchesGameNode(gameNode, requestedPath.Trim(), requestedName, systemRoot))
        {
            return true;
        }

        var gamePath = gameNode.Element("path")?.Value?.Trim();
        var gameName = gameNode.Element("name")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedName) &&
            string.Equals(NormalizeCompareValue(gameName), NormalizeCompareValue(requestedName), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestedSlug))
        {
            return false;
        }

        var requestedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSlug(requestedSlugs, requestedSlug);
        AddSlug(requestedSlugs, _gameNameNormalizer.NormalizeGameSlug(requestedSlug, null));

        var nodeSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSlug(nodeSlugs, _gameNameNormalizer.NormalizeGameSlug(gameName, gamePath));
        AddSlug(nodeSlugs, _gameNameNormalizer.NormalizeGameSlug(Path.GetFileNameWithoutExtension(gamePath), gamePath));

        return requestedSlugs.Overlaps(nodeSlugs);
    }

    private static void AddSlug(HashSet<string> slugs, string? slug)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            slugs.Add(slug.Trim());
        }
    }

    private static IEnumerable<string> EnumerateMetadataSearchRoots(string systemRoot, IReadOnlyList<string> targetSlugs)
    {
        if (targetSlugs.Count == 0)
        {
            yield return systemRoot;
            yield break;
        }

        foreach (var targetSlug in targetSlugs)
        {
            var gameRoot = Path.Combine(systemRoot, targetSlug);
            if (Directory.Exists(gameRoot))
            {
                yield return gameRoot;
            }
        }
    }

    private static bool MatchesLanguageFilter(string metadataPath, string languageFilter)
    {
        if (string.IsNullOrWhiteSpace(languageFilter))
        {
            return true;
        }

        return string.Equals(
            NormalizeLanguageFilter(ResolveMetadataFallbackLanguage(metadataPath)),
            languageFilter,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLanguageFilter(string? preferredLanguage)
    {
        var language = ResolvePreferredLanguageCode(preferredLanguage);
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        return language
            .Replace('-', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()?
            .ToLowerInvariant() ?? string.Empty;
    }

    private static string? ResolvePreferredLanguageCode(string? preferredLanguage)
    {
        if (string.IsNullOrWhiteSpace(preferredLanguage))
        {
            return null;
        }

        return ApiExposeProfileResolver.ResolveLanguageCode(preferredLanguage, preferredLanguage);
    }

    private IReadOnlyList<string> ResolveMetadataNormalizationSystems(string scope, string? requestedSystemId)
    {
        if (scope is "system" or "game")
        {
            if (string.IsNullOrWhiteSpace(requestedSystemId))
            {
                throw new InvalidOperationException($"SystemId is required for metadata normalization scope '{scope}'.");
            }

            return [_systemIdNormalizer.Normalize(requestedSystemId)];
        }

        var systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetMetadataSystemRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                var systemId = Path.GetFileName(directory);
                if (!string.IsNullOrWhiteSpace(systemId))
                {
                    systems.Add(_systemIdNormalizer.Normalize(systemId));
                }
            }
        }

        return systems.OrderBy(system => system, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IReadOnlyList<string> ResolveGamelistNormalizationSystems(string scope, string? requestedSystemId)
    {
        if (scope is "system" or "game")
        {
            if (string.IsNullOrWhiteSpace(requestedSystemId))
            {
                throw new InvalidOperationException($"SystemId is required for gamelist normalization scope '{scope}'.");
            }

            return [_systemIdNormalizer.Normalize(requestedSystemId)];
        }

        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return [];
        }

        return Directory.GetDirectories(RetroBatPaths.RomsRoot)
            .Select(Path.GetFileName)
            .Where(system => !string.IsNullOrWhiteSpace(system))
            .Select(system => _systemIdNormalizer.Normalize(system!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(system => system, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private GamelistTextNormalizationSystemResult NormalizeExistingGamelistSystem(
        string systemId,
        string preferredLanguage,
        MetadataNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(systemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        var result = new GamelistTextNormalizationSystemResult
        {
            SystemId = systemId,
            GamelistPath = gamelistPath
        };

        if (!File.Exists(gamelistPath))
        {
            return result;
        }

        result.GamelistScanned = true;
        try
        {
            lock (GetGamelistLock(gamelistPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = LoadGamelistDocument(gamelistPath);
                var changed = false;
                var matchedTargetGames = 0;
                foreach (var gameNode in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsGameScope(request) &&
                        !MatchesRequestedGameEntry(gameNode, request.GamePath, request.GameName, request.GameSlug, frontendSystemId, systemRoot))
                    {
                        continue;
                    }

                    matchedTargetGames++;
                    result.GamesScanned++;
                    foreach (var tagName in GamelistTextTags)
                    {
                        var element = gameNode.Element(tagName);
                        if (element == null)
                        {
                            continue;
                        }

                        result.FieldsScanned++;
                        var raw = element.Value;
                        var normalized = NormalizeExistingGamelistField(tagName, raw, preferredLanguage);
                        if (string.IsNullOrWhiteSpace(normalized) ||
                            string.Equals(raw, normalized, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        element.Value = normalized;
                        result.FieldsNormalized++;
                        changed = true;
                    }
                }

                if (changed)
                {
                    result.GamelistUpdated = _gamelistUpdateService.SaveExternalGamelistDocument(
                        document,
                        gamelistPath,
                        "media-maintenance-normalize-existing",
                        CancellationToken.None);
                }

                if (IsGameScope(request) && matchedTargetGames == 0)
                {
                    result.Errors.Add($"No gamelist entry matched the requested game target in {gamelistPath}.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            result.Errors.Add($"{gamelistPath}: {ex.Message}");
        }

        return result;
    }

    private static string NormalizeExistingGamelistField(string tagName, string? value, string preferredLanguage)
    {
        return tagName.Equals("genre", StringComparison.OrdinalIgnoreCase) ||
               tagName.Equals("genres", StringComparison.OrdinalIgnoreCase) ||
               tagName.Equals("family", StringComparison.OrdinalIgnoreCase)
            ? LocalizedMetadataSanitizer.SanitizeField(tagName, value, preferredLanguage)
            : LocalizedMetadataSanitizer.SanitizeText(value);
    }

    private async Task<MetadataNormalizationSystemResult> NormalizeMetadataSystemAsync(
        string systemId,
        MetadataNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        var result = new MetadataNormalizationSystemResult { SystemId = systemId };
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var languageFilter = NormalizeLanguageFilter(request.PreferredLanguage);
        var targetSlugs = ResolveRequestedMetadataGameSlugs(systemId, request);

        foreach (var root in GetMetadataSystemRoots())
        {
            var systemRoot = Path.Combine(root, systemId, "games");
            if (!Directory.Exists(systemRoot))
            {
                continue;
            }

            foreach (var searchRoot in EnumerateMetadataSearchRoots(systemRoot, targetSlugs))
            foreach (var metadataPath in EnumerateMetadataBundleFiles(searchRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenFiles.Add(metadataPath))
                {
                    continue;
                }

                if (!MatchesLanguageFilter(metadataPath, languageFilter))
                {
                    continue;
                }

                result.MetadataFilesScanned++;
                try
                {
                    var normalized = await NormalizeMetadataFileAsync(metadataPath, cancellationToken);
                    if (normalized.Removed)
                    {
                        result.MetadataFilesRemoved++;
                        continue;
                    }

                    if (normalized.Updated)
                    {
                        result.MetadataFilesUpdated++;
                        result.FieldsNormalized += normalized.FieldsNormalized;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    result.MetadataFilesFailed++;
                    if (result.Errors.Count < 20)
                    {
                        result.Errors.Add($"{metadataPath}: {ex.Message}");
                    }
                }
            }
        }

        if (IsGameScope(request) && result.MetadataFilesScanned == 0)
        {
            result.Errors.Add("No metadata bundle matched the requested game/language target.");
        }

        return result;
    }

    private static async Task<MetadataFileNormalizationResult> NormalizeMetadataFileAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(metadataPath);
        if (fileInfo.Exists && fileInfo.Length == 0)
        {
            await ExecuteFileRetryAsync(() =>
            {
                File.Delete(metadataPath);
                return Task.CompletedTask;
            }, cancellationToken);
            return new MetadataFileNormalizationResult(false, true, 0);
        }

        var bundle = await ExecuteFileRetryAsync(async () =>
        {
            await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer.DeserializeAsync<LocalizedTextBundle>(stream, JsonOptions, cancellationToken)
                ?? new LocalizedTextBundle();
        }, cancellationToken);

        var fallbackLanguage = ResolveMetadataFallbackLanguage(metadataPath);
        var beforeFields = bundle.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var beforeLanguage = bundle.Language;
        var changed = LocalizedMetadataBundleNormalizer.NormalizeBundleFields(bundle, fallbackLanguage);
        if (!changed)
        {
            return new MetadataFileNormalizationResult(false, false, 0);
        }

        var fieldsNormalized = CountNormalizedFields(beforeFields, beforeLanguage, bundle);
        bundle.UpdatedAtUtc = DateTime.UtcNow;
        await SaveMetadataBundleAsync(metadataPath, bundle, cancellationToken);
        return new MetadataFileNormalizationResult(true, false, fieldsNormalized);
    }

    private static IEnumerable<string> EnumerateMetadataBundleFiles(string systemRoot)
    {
        return Directory.EnumerateFiles(systemRoot, "metadata*.json", SearchOption.AllDirectories)
            .Where(IsLocalizedMetadataBundlePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLocalizedMetadataBundlePath(string metadataPath)
    {
        var fileName = Path.GetFileName(metadataPath);
        var parent = new DirectoryInfo(Path.GetDirectoryName(metadataPath) ?? string.Empty);

        if (fileName.StartsWith("metadata-", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(parent.Name, "texts", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ResolveMetadataFallbackLanguage(string metadataPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(metadataPath);
        if (fileName.StartsWith("metadata-", StringComparison.OrdinalIgnoreCase) &&
            fileName.Length > "metadata-".Length)
        {
            return LocalizedMetadataBundleNormalizer.NormalizeLanguage(fileName["metadata-".Length..]);
        }
        return "en";
    }

    private static int CountNormalizedFields(
        IReadOnlyDictionary<string, string> beforeFields,
        string beforeLanguage,
        LocalizedTextBundle after)
    {
        var count = string.Equals(beforeLanguage, after.Language, StringComparison.Ordinal) ? 0 : 1;
        foreach (var field in after.Fields)
        {
            if (!beforeFields.TryGetValue(field.Key, out var beforeValue) ||
                !string.Equals(beforeValue, field.Value, StringComparison.Ordinal))
            {
                count++;
            }
        }

        foreach (var beforeField in beforeFields.Keys)
        {
            if (!after.Fields.ContainsKey(beforeField))
            {
                count++;
            }
        }

        return count;
    }

    private static async Task SaveMetadataBundleAsync(
        string metadataPath,
        LocalizedTextBundle bundle,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await ExecuteFileRetryAsync(async () =>
        {
            var tempPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, bundle, JsonOptions, cancellationToken);
                }

                if (File.Exists(metadataPath))
                {
                    File.Replace(tempPath, metadataPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, metadataPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }, cancellationToken);
    }

    private static IEnumerable<string> GetMetadataSystemRoots()
    {
        yield return RetroBatPaths.MediaUserSystemsRoot;
        yield return RetroBatPaths.MediaSystemsRoot;
    }

    private static void EnsureTargetGamePathExists(ResolvedGameTarget target)
    {
        if (File.Exists(target.Game.GamePath) || Directory.Exists(target.Game.GamePath))
        {
            return;
        }

        throw new InvalidOperationException($"Game path does not exist: {target.Game.GamePath}");
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

    private static GameDetails BuildGameDetails(XElement gameNode)
    {
        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddExtra(extras, "wheel", gameNode.Element("wheel")?.Value);
        AddExtra(extras, "box-2D", gameNode.Element("boxart")?.Value);
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
            Lang = gameNode.Element("lang")?.Value?.Trim() ?? string.Empty,
            Region = gameNode.Element("region")?.Value?.Trim() ?? string.Empty,
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

    private CleanupResult ClearEsProjection(ResolvedGameTarget target)
    {
        var projectionBaseName = Path.GetFileNameWithoutExtension(target.Game.GamePath);
        var deletedFiles = 0;
        foreach (var folder in new[] { "images", "manuals", "videos" })
        {
            var folderPath = Path.Combine(GetProjectionStorageRoot(target.FrontendSystemId), folder);
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(folderPath, projectionBaseName + "-*.*", SearchOption.TopDirectoryOnly))
            {
                File.Delete(file);
                deletedFiles++;
            }
        }

        var removedTags = 0;
        if (File.Exists(target.GamelistPath))
        {
            lock (GetGamelistLock(target.GamelistPath))
            {
                var document = LoadGamelistDocument(target.GamelistPath);
                var gameNode = document.Root?
                    .Elements("game")
                    .FirstOrDefault(node => MatchesGameNode(node, target.Game.GamePath, target.Game.GameName, target.SystemRoot));

                if (gameNode != null)
                {
                    foreach (var tag in GamelistMediaTags)
                    {
                        var element = gameNode.Element(tag);
                        if (element == null)
                        {
                            continue;
                        }

                        element.Remove();
                        removedTags++;
                    }

                    _gamelistUpdateService.SaveExternalGamelistDocument(
                        document,
                        target.GamelistPath,
                        "media-maintenance-cleanup-missing-media",
                        CancellationToken.None,
                        allowMediaTagDrop: true);
                }
            }
        }

        return new CleanupResult(deletedFiles, removedTags);
    }

    private static string GetProjectionStorageRoot(string frontendSystemId)
    {
        var storageSystemId = frontendSystemId switch
        {
            "mame" or "fbneo" or "fba" or "hbmame" => "arcade",
            _ => frontendSystemId
        };

        return Path.Combine(RetroBatPaths.RomsRoot, storageSystemId);
    }

    private static int DeleteCanonicalGameDirectory(string systemId, string gameSlug)
    {
        var gameRoot = Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "games", gameSlug);
        if (!Directory.Exists(gameRoot))
        {
            return 0;
        }

        var count = Directory.GetFiles(gameRoot, "*", SearchOption.AllDirectories).Length;
        Directory.Delete(gameRoot, recursive: true);
        return count;
    }

    private static async Task<int> RemoveJsonEntriesByKeyPrefixAsync(string filePath, string keyPrefix, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return 0;
        }

        var root = await LoadJsonObjectAsync(filePath, cancellationToken);
        var entries = root["Entries"] as JsonObject;
        if (entries == null)
        {
            return 0;
        }

        var keysToRemove = entries
            .Select(pair => pair.Key)
            .Where(key => key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            entries.Remove(key);
        }

        if (keysToRemove.Count > 0)
        {
            await SaveJsonObjectAsync(filePath, root, cancellationToken);
        }

        return keysToRemove.Count;
    }

    private static async Task<int> RemoveJsonEntriesByValuePrefixAsync(string filePath, string valuePrefix, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return 0;
        }

        var root = await LoadJsonObjectAsync(filePath, cancellationToken);
        var entries = root["Entries"] as JsonObject;
        if (entries == null)
        {
            return 0;
        }

        var keysToRemove = entries
            .Where(pair => pair.Value is JsonValue value
                && value.TryGetValue<string>(out var raw)
                && raw.StartsWith(valuePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            entries.Remove(key);
        }

        if (keysToRemove.Count > 0)
        {
            await SaveJsonObjectAsync(filePath, root, cancellationToken);
        }

        return keysToRemove.Count;
    }

    private static async Task<JsonObject> LoadJsonObjectAsync(string filePath, CancellationToken cancellationToken)
    {
        return await ExecuteFileRetryAsync(async () =>
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject;
            return node ?? new JsonObject();
        }, cancellationToken);
    }

    private static async Task SaveJsonObjectAsync(string filePath, JsonObject root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await ExecuteFileRetryAsync(async () =>
        {
            var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken);
                }

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }, cancellationToken);
    }

    private object GetGamelistLock(string gamelistPath)
    {
        return _gamelistStore.GetLock(gamelistPath);
    }

    private static XDocument LoadGamelistDocument(string gamelistPath)
    {
        return ExecuteFileRetry(() =>
        {
            using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        });
    }

    private static bool TryValidateGamelistXmlFile(string path, out string? failure)
    {
        for (var attempt = 1; attempt <= FileIoRetryCount; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0)
                {
                    failure = "empty_file";
                    if (attempt < FileIoRetryCount)
                    {
                        Thread.Sleep(FileIoRetryDelay);
                        continue;
                    }

                    return false;
                }

                var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                if (document.Root == null)
                {
                    failure = "missing_root";
                    return false;
                }

                if (!string.Equals(document.Root.Name.LocalName, "gameList", StringComparison.Ordinal))
                {
                    failure = $"unexpected_root:{document.Root.Name.LocalName}";
                    return false;
                }

                failure = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                failure = ex.Message;
                if (attempt < FileIoRetryCount)
                {
                    Thread.Sleep(FileIoRetryDelay);
                    continue;
                }

                return false;
            }
        }

        failure = "validation_failed";
        return false;
    }

    private static GamelistSaveMetrics? TryReadGamelistMetrics(string gamelistPath)
    {
        try
        {
            using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            return CreateGamelistMetrics(document, new FileInfo(gamelistPath).Length);
        }
        catch
        {
            return null;
        }
    }

    private static GamelistSaveMetrics CreateGamelistMetrics(XDocument document, long byteLength)
    {
        var gameNodes = document.Root?.Elements("game").ToList() ?? new List<XElement>();
        var mediaTagSet = GamelistMediaTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mediaTagCount = 0;
        var gamesWithAnyMedia = 0;

        foreach (var gameNode in gameNodes)
        {
            var gameMediaTagCount = gameNode.Elements()
                .Count(element => mediaTagSet.Contains(element.Name.LocalName) && !string.IsNullOrWhiteSpace(element.Value));
            mediaTagCount += gameMediaTagCount;
            if (gameMediaTagCount > 0)
            {
                gamesWithAnyMedia++;
            }
        }

        return new GamelistSaveMetrics(byteLength, gameNodes.Count, gamesWithAnyMedia, mediaTagCount);
    }

    private static bool IsSuspiciousGamelistRewrite(GamelistSaveMetrics current, GamelistSaveMetrics candidate)
    {
        if (current.GameCount < 20 || current.ByteLength < 20_000)
        {
            return false;
        }

        return IsSharpDrop(candidate.ByteLength, current.ByteLength, 0.65)
            || IsSharpDrop(candidate.GameCount, current.GameCount, 0.80)
            || IsSharpDrop(candidate.GamesWithAnyMedia, current.GamesWithAnyMedia, 0.65)
            || IsSharpDrop(candidate.MediaTagCount, current.MediaTagCount, 0.65);
    }

    private static bool IsSharpDrop(long candidate, long current, double minimumRatio)
    {
        return current > 0 && candidate < current * minimumRatio;
    }

    private static string CreateTimestampedGamelistBackup(string gamelistPath)
    {
        var backupDirectory = GetGamelistSidecarDirectory(gamelistPath, GamelistBackupDirectoryName);
        Directory.CreateDirectory(backupDirectory);

        var backupPath = Path.Combine(
            backupDirectory,
            $"{Path.GetFileNameWithoutExtension(gamelistPath)}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.xml");
        File.Copy(gamelistPath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void CleanupOldGamelistBackups(string gamelistPath)
    {
        var backupDirectory = GetGamelistSidecarDirectory(gamelistPath, GamelistBackupDirectoryName);
        if (!Directory.Exists(backupDirectory))
        {
            return;
        }

        var backups = Directory.GetFiles(backupDirectory)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(GamelistBackupRetentionCount);
        foreach (var backup in backups)
        {
            File.Delete(backup);
        }
    }

    private static void MoveRejectedGamelistCandidate(
        string tempPath,
        string gamelistPath,
        GamelistSaveMetrics currentMetrics,
        GamelistSaveMetrics candidateMetrics)
    {
        var auditDirectory = GetGamelistSidecarDirectory(gamelistPath, GamelistAuditDirectoryName);
        Directory.CreateDirectory(auditDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var rejectedPath = Path.Combine(
            auditDirectory,
            $"{Path.GetFileNameWithoutExtension(gamelistPath)}.{timestamp}.rejected.xml");
        var auditPath = Path.Combine(
            auditDirectory,
            $"{Path.GetFileNameWithoutExtension(gamelistPath)}.{timestamp}.audit.json");

        File.Move(tempPath, rejectedPath, overwrite: true);
        var audit = new
        {
            gamelistPath,
            rejectedPath,
            createdAtUtc = DateTime.UtcNow,
            current = currentMetrics,
            candidate = candidateMetrics,
            reason = "candidate_gamelist_lost_too_much_content"
        };
        File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, JsonOptions));
    }

    private static void MoveInvalidGamelistCandidate(
        string tempPath,
        string gamelistPath,
        string reason,
        string? failure)
    {
        var auditDirectory = GetGamelistSidecarDirectory(gamelistPath, GamelistAuditDirectoryName);
        Directory.CreateDirectory(auditDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var rejectedPath = Path.Combine(
            auditDirectory,
            $"{Path.GetFileNameWithoutExtension(gamelistPath)}.{timestamp}.rejected.xml");
        var auditPath = Path.Combine(
            auditDirectory,
            $"{Path.GetFileNameWithoutExtension(gamelistPath)}.{timestamp}.audit.json");

        File.Move(tempPath, rejectedPath, overwrite: true);
        var audit = new
        {
            gamelistPath,
            rejectedPath,
            createdAtUtc = DateTime.UtcNow,
            reason,
            failure
        };
        File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, JsonOptions));
    }

    private static string WriteGamelistValidationAudit(
        string gamelistPath,
        string reason,
        string? failure,
        string? backupPath,
        GamelistSaveMetrics? currentMetrics,
        GamelistSaveMetrics? candidateMetrics,
        bool restored,
        string? restoreFailure)
    {
        var auditDirectory = GetGamelistSidecarDirectory(gamelistPath, GamelistAuditDirectoryName);
        Directory.CreateDirectory(auditDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var auditPath = Path.Combine(
            auditDirectory,
            $"{Path.GetFileNameWithoutExtension(gamelistPath)}.{timestamp}.audit.json");
        var audit = new
        {
            gamelistPath,
            backupPath,
            createdAtUtc = DateTime.UtcNow,
            reason,
            failure,
            restored,
            restoreFailure,
            current = currentMetrics,
            candidate = candidateMetrics
        };
        File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, JsonOptions));
        return auditPath;
    }

    private static void RestoreGamelistBackup(string backupPath, string gamelistPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            return;
        }

        var restoreTempPath = gamelistPath + "." + Guid.NewGuid().ToString("N") + ".restore.tmp";
        try
        {
            File.Copy(backupPath, restoreTempPath, overwrite: true);
            if (File.Exists(gamelistPath))
            {
                File.Replace(restoreTempPath, gamelistPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(restoreTempPath, gamelistPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(restoreTempPath))
            {
                File.Delete(restoreTempPath);
            }
        }
    }

    private static string GetGamelistSidecarDirectory(string gamelistPath, string directoryName)
    {
        return Path.Combine(Path.GetDirectoryName(gamelistPath) ?? string.Empty, directoryName);
    }

    private static void ExecuteFileRetry(Action operation)
    {
        ExecuteFileRetry<object?>(() =>
        {
            operation();
            return null;
        });
    }

    private static T ExecuteFileRetry<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (attempt < FileIoRetryCount)
            {
                Thread.Sleep(FileIoRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < FileIoRetryCount)
            {
                Thread.Sleep(FileIoRetryDelay);
            }
        }
    }

    private static async Task ExecuteFileRetryAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await ExecuteFileRetryAsync(async () =>
        {
            await operation();
            return true;
        }, cancellationToken);
    }

    private static async Task<T> ExecuteFileRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (IOException) when (attempt < FileIoRetryCount)
            {
                await Task.Delay(FileIoRetryDelay, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < FileIoRetryCount)
            {
                await Task.Delay(FileIoRetryDelay, cancellationToken);
            }
        }
    }

    private static string NormalizeCompareValue(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }

    private async Task RemoveGameAliasEntriesAsync(ResolvedGameTarget target, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(RetroBatPaths.MediaAliasesGamesRoot, $"{target.SystemId}.json");
        if (!File.Exists(filePath))
        {
            return;
        }

        var aliasKeys = BuildAliasKeys(target.Game, target.GameSlug);
        if (aliasKeys.Count == 0)
        {
            return;
        }

        var root = await LoadJsonObjectAsync(filePath, cancellationToken);
        var entries = root["Entries"] as JsonObject;
        if (entries == null)
        {
            return;
        }

        var removed = 0;
        foreach (var aliasKey in aliasKeys)
        {
            if (entries.Remove(aliasKey))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            await SaveJsonObjectAsync(filePath, root, cancellationToken);
        }
    }

    private List<string> BuildAliasKeys(GameReference game, string requestedGameSlug)
    {
        var keys = new List<string>();

        void Add(string prefix, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                keys.Add($"{prefix}:{value.Trim().ToLowerInvariant()}");
            }
        }

        Add("md5", game.Details?.Md5);
        Add("gameid", game.GameId);
        Add("path", NormalizePathKey(game.GamePath));
        Add("file", Path.GetFileName(game.GamePath));
        Add("slug", requestedGameSlug);
        Add("name", _gameNameNormalizer.NormalizeGameSlug(game.GameName, game.GamePath));

        return keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePathKey(string? path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private sealed record ResolvedGameTarget(
        string SystemId,
        string FrontendSystemId,
        string SystemRoot,
        string GamelistPath,
        string GameSlug,
        GameReference Game,
        XElement? GameNode);

    private sealed record CleanupResult(int DeletedEsMediaFiles, int RemovedGamelistTags);

    private sealed record MetadataFileNormalizationResult(bool Updated, bool Removed, int FieldsNormalized);

    private sealed record GamelistSaveMetrics(
        long ByteLength,
        int GameCount,
        int GamesWithAnyMedia,
        int MediaTagCount);
}
