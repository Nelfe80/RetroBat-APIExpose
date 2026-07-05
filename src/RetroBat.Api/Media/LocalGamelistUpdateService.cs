using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RetroBat.Api.Controllers;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public partial class LocalGamelistUpdateService
{
    private const string ProgressTaskId = "local-gamelist-update";
    private const int ProgressGameReportStep = 25;
    private static readonly JsonSerializerOptions AuditJsonOptions = new() { WriteIndented = false };
    private static readonly object AuditLogLock = new();
    // Official Batocera ES metadata keys observed in projects-source/batocera-emulationstation-master/es-app/src/MetaData.cpp:
    // element keys: name, desc, genre, tags, sortname, emulator, core, image, video, marquee, thumbnail, fanart,
    // titleshot, manual, magazine, map, bezel, cartridge, boxart, boxback, wheel, mix, rating, releasedate,
    // developer, publisher, family, genres, arcadesystemname, players, favorite, hidden, kidgame, playcount,
    // lastplayed, crc32, md5, gametime, lang, region, cheevosHash, cheevosId; special child: scrap;
    // official attribute: id. Batocera preserves unknown elements as pass-through, but they are not official metadata.
    private static readonly string[] UnsupportedDurableMediaTags = ["wheelcarbon", "wheelsteel"];
    private static readonly string[] IndexedKinds =
    [
        MediaKinds.Image,
        MediaKinds.Thumbnail,
        MediaKinds.Wheel,
        MediaKinds.WheelCarbon,
        MediaKinds.WheelSteel,
        MediaKinds.Marquee,
        MediaKinds.ScreenMarquee,
        MediaKinds.ScreenMarqueeSmall,
        MediaKinds.SteamGrid,
        MediaKinds.MixRbv1,
        MediaKinds.MixRbv2,
        MediaKinds.BoxFront,
        MediaKinds.BoxSide,
        MediaKinds.BoxTexture,
        MediaKinds.Box3d,
        MediaKinds.Cartridge,
        MediaKinds.Label,
        MediaKinds.Fanart,
        MediaKinds.Flyer,
        MediaKinds.Figurine,
        MediaKinds.Bezel,
        MediaKinds.BoxBack,
        MediaKinds.Map,
        MediaKinds.Manual,
        MediaKinds.Magazine,
        MediaKinds.Video,
        MediaKinds.VideoNormalized
    ];

    private readonly LocalMediaIndexService _localMediaIndexService;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly IMediaAliasStore _mediaAliasStore;
    private readonly ILocalizedTextStore _localizedTextStore;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ITaskProgressService _taskProgressService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IGamelistStore _gamelistStore;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly MameGamelistGroupIndex _mameGamelistGroupIndex;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly MediaLocalizationResolver _mediaLocalizationResolver;
    private readonly ILogger<LocalGamelistUpdateService>? _logger;

    public LocalGamelistUpdateService(
        LocalMediaIndexService localMediaIndexService,
        GameNameNormalizer gameNameNormalizer,
        SystemIdNormalizer systemIdNormalizer,
        IMediaAliasStore mediaAliasStore,
        ILocalizedTextStore localizedTextStore,
        EmulationStationSettingsService settingsService,
        InterfaceTextService interfaceTextService,
        ITaskProgressService taskProgressService,
        MediaRuntimeState runtimeState,
        IGamelistStore gamelistStore,
        GamelistUpdateService gamelistUpdateService,
        MameGamelistGroupIndex mameGamelistGroupIndex,
        ApiExposeRuntimeOptionsService runtimeOptions,
        MediaLocalizationResolver mediaLocalizationResolver,
        ILogger<LocalGamelistUpdateService>? logger = null)
    {
        _localMediaIndexService = localMediaIndexService;
        _gameNameNormalizer = gameNameNormalizer;
        _systemIdNormalizer = systemIdNormalizer;
        _mediaAliasStore = mediaAliasStore;
        _localizedTextStore = localizedTextStore;
        _settingsService = settingsService;
        _interfaceTextService = interfaceTextService;
        _taskProgressService = taskProgressService;
        _runtimeState = runtimeState;
        _gamelistStore = gamelistStore;
        _gamelistUpdateService = gamelistUpdateService;
        _mameGamelistGroupIndex = mameGamelistGroupIndex;
        _runtimeOptions = runtimeOptions;
        _mediaLocalizationResolver = mediaLocalizationResolver;
        _logger = logger;
    }

    public async Task<LocalGamelistUpdateResponse> UpdateAsync(LocalGamelistUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var scope = ResolveScope(request);
        var systems = ResolveSystems(request, scope);
        var response = new LocalGamelistUpdateResponse
        {
            Scope = scope,
            Systems = systems,
            SystemsProcessed = systems.Count
        };
        WriteAudit("start", "running", scope, systemId: null, gamelistPath: null, exception: null, new
        {
            systems.Count,
            systems,
            request.GamePath,
            request.GameName,
            request.GameSlug,
            request.PreferredLanguage
        });

        if (!_runtimeOptions.IsLocalMediaManagerEnabled())
        {
            response.SystemsProcessed = 0;
            response.SystemResults.Add(new LocalGamelistUpdateSystemResult
            {
                SystemId = scope,
                Message = "Local Media Manager inactive"
            });
            WriteAudit("complete", "skipped-disabled", scope, systemId: null, gamelistPath: null, exception: null, response);
            return response;
        }

        if (systems.Count == 0)
        {
            WriteAudit("complete", "empty", scope, systemId: null, gamelistPath: null, exception: null, response);
            return response;
        }

        using var reallocation = _runtimeState.BeginMediaReallocation($"local-gamelist-update:{scope}");
        var scrapingSettings = BuildEffectiveScrapingSettings(request.PreferredLanguage);
        var reportTaskProgress = !request.SuppressTaskProgress;
        if (reportTaskProgress)
        {
            _taskProgressService.Report(ProgressTaskId, LocalGamelistProgressTitle(scrapingSettings.Language), 0, systems.Count, systems[0]);
        }

        try
        {
            for (var index = 0; index < systems.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var systemId = systems[index];
                if (reportTaskProgress)
                {
                    _taskProgressService.Report(ProgressTaskId, LocalGamelistProgressTitle(scrapingSettings.Language), index, systems.Count, systemId);
                }

                LocalGamelistUpdateSystemResult systemResult;
                try
                {
                    systemResult = await UpdateSystemAsync(systemId, index, systems.Count, request, scrapingSettings, reportTaskProgress, cancellationToken);
                    WriteAudit("system", systemResult.Changed ? "changed" : "unchanged", scope, systemId, systemResult.GamelistPath, exception: null, systemResult);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var gamelistPath = ResolveGamelistPath(systemId);
                    systemResult = new LocalGamelistUpdateSystemResult
                    {
                        SystemId = systemId,
                        GamelistPath = gamelistPath,
                        Failed = true,
                        Message = exception.Message,
                        Error = exception.ToString()
                    };
                    response.SystemsFailed++;
                    WriteAudit("system", "failed", scope, systemId, gamelistPath, exception, systemResult);
                    _logger?.LogWarning(exception, "Local gamelist update failed for system {SystemId}.", systemId);
                }

                response.SystemResults.Add(systemResult);
                response.GamesProcessed += systemResult.GamesProcessed;
                response.GamesUpdated += systemResult.GamesUpdated;
                response.MediaTagsUpdated += systemResult.MediaTagsUpdated;
                response.TextTagsUpdated += systemResult.TextTagsUpdated;
                response.LocalMediaCandidates += systemResult.LocalMediaCandidates;
                if (systemResult.Changed)
                {
                    response.SystemsUpdated++;
                }

                if (reportTaskProgress)
                {
                    _taskProgressService.Report(ProgressTaskId, LocalGamelistProgressTitle(scrapingSettings.Language), index + 1, systems.Count, systemId);
                }
            }
        }
        finally
        {
            if (reportTaskProgress)
            {
                _taskProgressService.Complete(ProgressTaskId);
            }
        }

        response.ReloadGamesRequested = response.SystemsUpdated > 0 &&
            _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
        WriteAudit("complete", response.SystemsFailed > 0 ? "partial-failure" : "success", scope, systemId: null, gamelistPath: null, exception: null, response);
        return response;
    }

    private async Task<LocalGamelistUpdateSystemResult> UpdateSystemAsync(
        string frontendSystemId,
        int systemIndex,
        int systemCount,
        LocalGamelistUpdateRequest request,
        EmulationStationScrapingSettings scrapingSettings,
        bool reportTaskProgress,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var systemId = _systemIdNormalizer.Normalize(frontendSystemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var gamelistPath = ResolveGamelistPath(frontendSystemId);
        var result = new LocalGamelistUpdateSystemResult
        {
            SystemId = frontendSystemId,
            GamelistPath = gamelistPath
        };

        if (!File.Exists(gamelistPath))
        {
            result.Message = "gamelist.xml introuvable.";
            return result;
        }

        ReportSystemProgress(frontendSystemId, systemIndex, systemCount, currentGame: 0, totalGames: 0, "progress.local_gamelist.index_media", scrapingSettings.Language, reportTaskProgress);
        var mediaIndex = _localMediaIndexService.Build([systemId], cancellationToken);
        result.LocalMediaCandidates = mediaIndex.Candidates.Count(candidate =>
            string.Equals(candidate.SystemId, systemId, StringComparison.OrdinalIgnoreCase));
        var textCache = new Dictionary<string, LocalizedTextBundle?>(StringComparer.OrdinalIgnoreCase);

        ReportSystemProgress(frontendSystemId, systemIndex, systemCount, currentGame: 0, totalGames: 0, "progress.local_gamelist.reading_gamelist", scrapingSettings.Language, reportTaskProgress);
        lock (GetGamelistLock(gamelistPath))
        {
            var document = LoadGamelistDocument(gamelistPath);
            var root = document.Root;
            if (root == null)
            {
                result.Message = "gamelist.xml sans racine.";
                return result;
            }

            var gameNodes = root.Elements("game").ToList();
            ReportSystemProgress(frontendSystemId, systemIndex, systemCount, currentGame: 0, gameNodes.Count, "progress.local_gamelist.analysis", scrapingSettings.Language, reportTaskProgress);
            foreach (var gameNode in gameNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsGameScope(request) && !MatchesRequestedGameNode(gameNode, request, systemRoot))
                {
                    continue;
                }

                result.GamesProcessed++;
                if (ShouldReportGameProgress(result.GamesProcessed, gameNodes.Count))
                {
                    ReportSystemProgress(frontendSystemId, systemIndex, systemCount, result.GamesProcessed, gameNodes.Count, "progress.local_gamelist.analysis", scrapingSettings.Language, reportTaskProgress);
                }

                var rawPath = gameNode.Element("path")?.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                var gameName = gameNode.Element("name")?.Value?.Trim() ?? string.Empty;
                try
                {
                    var displayName = _gameNameNormalizer.NormalizeDisplayName(gameName, rawPath);
                    var baseSlug = _gameNameNormalizer.NormalizeGameSlug(gameName, rawPath);
                    var canonicalSlug = ResolveCanonicalSlugAsync(systemId, rawPath, gameName, baseSlug, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var familySlug = BuildFamilySlug(Path.GetFileNameWithoutExtension(rawPath));
                    if (string.IsNullOrWhiteSpace(familySlug))
                    {
                        familySlug = BuildFamilySlug(canonicalSlug);
                    }

                    var beforeXml = gameNode.ToString(SaveOptions.DisableFormatting);
                    var nameUpdates = TrySetNonEmptyElement(gameNode, "name", displayName) ? 1 : 0;
                    var relatedSlugs = BuildRelatedSlugs(frontendSystemId, rawPath, canonicalSlug, familySlug);
                    var kindPaths = BuildKindPaths(mediaIndex, systemId, canonicalSlug, familySlug, relatedSlugs, rawPath, systemRoot, scrapingSettings);
                    var mediaUpdates = ApplyMediaElements(gameNode, kindPaths, scrapingSettings);
                    var textUpdates = ApplyTextElements(
                        gameNode,
                        LoadBestTextBundle(systemId, canonicalSlug, familySlug, kindPaths, scrapingSettings.Language, textCache, cancellationToken),
                        scrapingSettings.Language);
                    if (nameUpdates > 0 || mediaUpdates > 0 || textUpdates > 0)
                    {
                        var afterXml = gameNode.ToString(SaveOptions.DisableFormatting);
                        if (!string.Equals(beforeXml, afterXml, StringComparison.Ordinal))
                        {
                            result.GamesUpdated++;
                            result.MediaTagsUpdated += mediaUpdates;
                            result.TextTagsUpdated += textUpdates + nameUpdates;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Erreur mise a jour gamelist locale: system={frontendSystemId}, path='{rawPath}', name='{gameName}'.",
                        exception);
                }
            }

            if (result.GamesUpdated > 0)
            {
                ReportSystemProgress(frontendSystemId, systemIndex, systemCount, result.GamesProcessed, result.GamesProcessed, "progress.local_gamelist.writing", scrapingSettings.Language, reportTaskProgress);
                result.Changed = _gamelistUpdateService.SaveExternalGamelistDocument(
                    document,
                    gamelistPath,
                    "local-gamelist-update",
                    cancellationToken);
                result.Message = result.Changed
                    ? "gamelist.xml mis a jour."
                    : "gamelist.xml inchange ou ecriture rejetee.";
            }
            else
            {
                result.Message = IsGameScope(request) && result.GamesProcessed == 0
                    ? "aucune entree gamelist ne correspond a la cible demandee."
                    : "aucun changement gamelist.";
            }
        }

        ReportSystemProgress(
            frontendSystemId,
            systemIndex,
            systemCount,
            result.GamesProcessed,
            result.GamesProcessed,
            result.Changed ? "progress.local_gamelist.completed_changed" : "progress.local_gamelist.completed_unchanged",
            scrapingSettings.Language,
            reportTaskProgress);

        _logger?.LogInformation(
            "Mise a jour gamelist locale terminee: system={SystemId}, changed={Changed}, games={Games}, updated={Updated}, mediaTags={MediaTags}, textTags={TextTags}",
            frontendSystemId,
            result.Changed,
            result.GamesProcessed,
            result.GamesUpdated,
            result.MediaTagsUpdated,
            result.TextTagsUpdated);
        return result;
    }

    private void ReportSystemProgress(
        string frontendSystemId,
        int systemIndex,
        int systemCount,
        int currentGame,
        int totalGames,
        string phaseKey,
        string language,
        bool reportTaskProgress)
    {
        if (!reportTaskProgress)
        {
            return;
        }

        var phase = _interfaceTextService.Text(phaseKey, language);
        if (systemCount <= 1 && totalGames > 0)
        {
            _taskProgressService.Report(
                ProgressTaskId,
                _interfaceTextService.Format(
                    "progress.local_gamelist.single_title",
                    language,
                    ("system", frontendSystemId)),
                currentGame,
                totalGames,
                phase);
            return;
        }

        var detail = totalGames > 0
            ? _interfaceTextService.Format(
                "progress.local_gamelist.detail_with_games",
                language,
                ("system", frontendSystemId),
                ("phase", phase),
                ("current", Math.Min(currentGame, totalGames)),
                ("total", totalGames))
            : $"{frontendSystemId} - {phase}";
        _taskProgressService.Report(
            ProgressTaskId,
            LocalGamelistProgressTitle(language),
            systemIndex,
            Math.Max(1, systemCount),
            detail);
    }

    private string LocalGamelistProgressTitle(string language)
    {
        return _interfaceTextService.Text("progress.local_gamelist.title", language);
    }

    private static bool ShouldReportGameProgress(int currentGame, int totalGames)
    {
        return currentGame == 1 ||
            currentGame == totalGames ||
            currentGame % ProgressGameReportStep == 0;
    }

    private Dictionary<string, string> BuildKindPaths(
        LocalMediaIndex mediaIndex,
        string systemId,
        string canonicalSlug,
        string familySlug,
        IReadOnlyList<string> relatedSlugs,
        string gamePath,
        string systemRoot,
        EmulationStationScrapingSettings scrapingSettings)
    {
        var kindPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in IndexedKinds)
        {
            var plan = new MediaProjectionPlan
            {
                SystemId = systemId,
                FrontendSystemId = systemId,
                GameSlug = canonicalSlug,
                GamePath = gamePath,
                ProjectionBaseName = familySlug
            };
            var preferredRegions = _mediaLocalizationResolver.BuildMediaRegionPriority(plan, kind);
            var candidate = ResolveBestCandidate(mediaIndex, systemId, canonicalSlug, familySlug, relatedSlugs, kind, preferredRegions);
            if (candidate == null)
            {
                continue;
            }

            var relativePath = ToMediaRelativePath(candidate.Path, systemRoot);
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                kindPaths[MediaKinds.Normalize(kind)] = relativePath;
            }
        }

        RemoveMarqueeIfSameAsWheel(kindPaths, systemRoot);
        return kindPaths;
    }

    private static LocalMediaIndexCandidate? ResolveBestCandidate(
        LocalMediaIndex mediaIndex,
        string systemId,
        string canonicalSlug,
        string familySlug,
        IReadOnlyList<string> relatedSlugs,
        string kind,
        IReadOnlyList<string> preferredRegions)
    {
        var candidate = mediaIndex.ResolveBest(systemId, canonicalSlug, familySlug, kind, preferredRegions);
        if (candidate != null)
        {
            return candidate;
        }

        foreach (var relatedSlug in relatedSlugs)
        {
            candidate = mediaIndex.ResolveBest(systemId, relatedSlug, BuildFamilySlug(relatedSlug), kind, preferredRegions);
            if (candidate != null)
            {
                return candidate;
            }
        }

        return null;
    }

    private IReadOnlyList<string> BuildRelatedSlugs(string frontendSystemId, string rawPath, string canonicalSlug, string familySlug)
    {
        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            var normalized = NormalizeSlug(value);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                slugs.Add(normalized);
            }
        }

        Add(canonicalSlug);
        Add(familySlug);

        foreach (var relatedRom in _mameGamelistGroupIndex.GetRelatedRoms(frontendSystemId, rawPath, canonicalSlug))
        {
            Add(relatedRom);
            Add(Path.GetFileNameWithoutExtension(relatedRom));
            Add(BuildFamilySlug(relatedRom));
        }

        return slugs;
    }

    private int ApplyMediaElements(
        XElement gameNode,
        IReadOnlyDictionary<string, string> kindPaths,
        EmulationStationScrapingSettings scrapingSettings)
    {
        var updated = 0;

        updated += RemoveUnsupportedDurableMediaTags(gameNode);
        updated += TrySetNonEmptyElement(gameNode, "wheel", FirstMediaPath(kindPaths, MediaKinds.Wheel)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "boxart", FirstMediaPath(kindPaths, MediaKinds.BoxFront, MediaKinds.Box3d)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "boxback", FirstMediaPath(kindPaths, MediaKinds.BoxBack)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "cartridge", FirstMediaPath(kindPaths, MediaKinds.Cartridge)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "label", FirstMediaPath(kindPaths, MediaKinds.Label)) ? 1 : 0;
        updated += TrySetVisibleSlotElement(gameNode, "fanart", FirstMediaPath(kindPaths, MediaKinds.Fanart)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "extra1", FirstMediaPath(kindPaths, MediaKinds.Flyer, MediaKinds.Figurine, MediaKinds.BoxTexture, MediaKinds.BoxSide)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "figurine", FirstMediaPath(kindPaths, MediaKinds.Figurine)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "mix", FirstMediaPath(kindPaths, MediaKinds.MixRbv2, MediaKinds.MixRbv1)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "titleshot", FirstMediaPath(kindPaths, MediaKinds.Image)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "screenshot", FirstMediaPath(kindPaths, MediaKinds.Thumbnail)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "bezel", FirstMediaPath(kindPaths, MediaKinds.Bezel)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "map", FirstMediaPath(kindPaths, MediaKinds.Map)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "manual", FirstMediaPath(kindPaths, MediaKinds.Manual)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "magazine", FirstMediaPath(kindPaths, MediaKinds.Magazine)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "video", FirstMediaPath(kindPaths, MediaKinds.Video, MediaKinds.VideoNormalized)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "screenmarquee", FirstMediaPath(kindPaths, MediaKinds.ScreenMarquee)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "screenmarqueesmall", FirstMediaPath(kindPaths, MediaKinds.ScreenMarqueeSmall)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "steamgrid", FirstMediaPath(kindPaths, MediaKinds.SteamGrid)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "mixrbv1", FirstMediaPath(kindPaths, MediaKinds.MixRbv1)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "mixrbv2", FirstMediaPath(kindPaths, MediaKinds.MixRbv2)) ? 1 : 0;
        updated += TrySetNonEmptyElement(gameNode, "videonormalized", FirstMediaPath(kindPaths, MediaKinds.VideoNormalized)) ? 1 : 0;

        updated += TrySetVisibleSlotElement(gameNode, "image", ResolveSelectedMediaPath(scrapingSettings.ImageSource, kindPaths, MediaSelectionTarget.Image, scrapingSettings.WheelStyle)) ? 1 : 0;
        updated += TrySetVisibleSlotElement(gameNode, "marquee", ResolveSelectedMediaPath(scrapingSettings.LogoSource, kindPaths, MediaSelectionTarget.Logo, scrapingSettings.WheelStyle)) ? 1 : 0;
        updated += TrySetVisibleSlotElement(gameNode, "thumbnail", ResolveSelectedMediaPath(scrapingSettings.ThumbSource, kindPaths, MediaSelectionTarget.Thumbnail, scrapingSettings.WheelStyle)) ? 1 : 0;

        return updated;
    }

    private static int RemoveUnsupportedDurableMediaTags(XElement gameNode)
    {
        var removed = 0;
        foreach (var tagName in UnsupportedDurableMediaTags)
        {
            var nodes = gameNode.Elements(tagName).ToList();
            removed += nodes.Count;
            foreach (var node in nodes)
            {
                node.Remove();
            }
        }

        return removed;
    }

    private int ApplyTextElements(XElement gameNode, LocalizedTextBundle? bundle, string requestedLanguage)
    {
        var targetLanguage = NormalizeLanguageCode(bundle?.Language);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            targetLanguage = NormalizeLanguageCode(requestedLanguage);
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            targetLanguage = NormalizeLanguageCode(gameNode.Element("lang")?.Value);
        }

        var updated = 0;
        updated += ApplyLocalizedTextElement(gameNode, "desc", bundle, targetLanguage) ? 1 : 0;
        foreach (var tagName in MetadataTagNames())
        {
            var changed = IsLocalizedHumanTextField(tagName)
                ? ApplyLocalizedTextElement(gameNode, tagName, bundle, targetLanguage)
                : ShouldClearMissingRomLanguage(bundle, tagName)
                    ? TryRemoveElement(gameNode, tagName)
                    : TrySetNonEmptyElement(gameNode, tagName, ResolveBundleField(bundle, tagName));
            updated += changed ? 1 : 0;
        }

        return updated;
    }

    private static bool ShouldClearMissingRomLanguage(LocalizedTextBundle? bundle, string tagName)
    {
        return string.Equals(tagName, "lang", StringComparison.OrdinalIgnoreCase) &&
            bundle?.Fields != null &&
            !bundle.Fields.ContainsKey("lang") &&
            bundle.Fields.TryGetValue("source", out var source) &&
            source.Contains("screenscraper", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ApplyLocalizedTextElement(
        XElement gameNode,
        string tagName,
        LocalizedTextBundle? bundle,
        string targetLanguage)
    {
        var incoming = ResolveBundleField(bundle, tagName);
        if (IsLikelyWrongLanguageForTarget(tagName, incoming, targetLanguage))
        {
            incoming = string.Empty;
        }

        if (TrySetNonEmptyElement(gameNode, tagName, incoming))
        {
            return true;
        }

        if (TryNormalizeLocalizedElement(gameNode, tagName, targetLanguage))
        {
            return true;
        }

        return TryRemoveWrongLanguageElement(gameNode, tagName, targetLanguage);
    }

    private LocalizedTextBundle? LoadBestTextBundle(
        string systemId,
        string canonicalSlug,
        string familySlug,
        IReadOnlyDictionary<string, string> kindPaths,
        string language,
        IDictionary<string, LocalizedTextBundle?> cache,
        CancellationToken cancellationToken)
    {
        foreach (var slug in BuildTextSlugCandidates(canonicalSlug, familySlug, kindPaths))
        {
            if (cache.TryGetValue(slug, out var cached))
            {
                if (cached != null)
                {
                    return cached;
                }

                continue;
            }

            var bundle = _localizedTextStore.LoadPreferredBundleAsync(
                    systemId,
                    slug,
                    language,
                    cancellationToken,
                    allowAnyLanguageFallback: false)
                .GetAwaiter()
                .GetResult();
            cache[slug] = bundle;
            if (bundle != null)
            {
                return bundle;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildTextSlugCandidates(
        string canonicalSlug,
        string familySlug,
        IReadOnlyDictionary<string, string> kindPaths)
    {
        yield return canonicalSlug;
        if (!string.Equals(familySlug, canonicalSlug, StringComparison.OrdinalIgnoreCase))
        {
            yield return familySlug;
        }

        foreach (var path in kindPaths.Values)
        {
            var slug = TryExtractGameSlugFromMediaPath(path);
            if (!string.IsNullOrWhiteSpace(slug))
            {
                yield return slug;
            }
        }
    }

    private async Task<string> ResolveCanonicalSlugAsync(
        string systemId,
        string gamePath,
        string gameName,
        string fallbackSlug,
        CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            "path:" + NormalizePathKey(gamePath),
            "file:" + Path.GetFileName(gamePath),
            "slug:" + fallbackSlug,
            "name:" + _gameNameNormalizer.NormalizeGameSlug(gameName, gamePath)
        };
        return await _mediaAliasStore.ResolveGameSlugAsync(systemId, keys, fallbackSlug, cancellationToken);
    }

    private static string ResolveSelectedMediaPath(
        string selectedSource,
        IReadOnlyDictionary<string, string> kindPaths,
        MediaSelectionTarget target,
        string wheelStyle)
    {
        var kind = NormalizeSelectionSourceToKind(selectedSource, target, wheelStyle);
        return string.IsNullOrWhiteSpace(kind) ? string.Empty : FirstMediaPath(kindPaths, kind);
    }

    private static string NormalizeSelectionSourceToKind(string selectedSource, MediaSelectionTarget target, string wheelStyle)
    {
        var normalized = (selectedSource ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "logo" => MediaKinds.Wheel,
            "wheel-hd" => target == MediaSelectionTarget.Logo ? ResolveWheelStyleKind(wheelStyle) : MediaKinds.WheelCarbon,
            "wheel" => MediaKinds.Wheel,
            "wheel-carbon" or "wheelcarbon" => MediaKinds.WheelCarbon,
            "wheel-steel" or "wheelsteel" => MediaKinds.WheelSteel,
            "marquee" => MediaKinds.Marquee,
            "screenmarquee" or "screen-marquee" => MediaKinds.ScreenMarquee,
            "screenmarqueesmall" or "screen-marquee-small" => MediaKinds.ScreenMarqueeSmall,
            "boxback" or "box-back" => MediaKinds.BoxBack,
            "ss" or "screenshot" or "thumb" or "thumbnail" => MediaKinds.Thumbnail,
            "sstitle" or "title" => MediaKinds.Image,
            "fanart" => MediaKinds.Fanart,
            "box-2d" or "box2d" => MediaKinds.BoxFront,
            "box-2d-side" or "boxside" or "box-side" => MediaKinds.BoxSide,
            "box-texture" or "boxtexture" => MediaKinds.BoxTexture,
            "box-3d" or "box3d" => MediaKinds.Box3d,
            "cartridge" or "cart" or "support-2d" or "support2d" => MediaKinds.Cartridge,
            "label" or "support-texture" or "supporttexture" => MediaKinds.Label,
            "flyer" => MediaKinds.Flyer,
            "figurine" => MediaKinds.Figurine,
            "steamgrid" => MediaKinds.SteamGrid,
            "mix" or "mixrbv2" => MediaKinds.MixRbv2,
            "mixrbv1" => MediaKinds.MixRbv1,
            "magazine" => MediaKinds.Magazine,
            _ => string.Empty
        };
    }

    private static string ResolveWheelStyleKind(string wheelStyle)
    {
        var normalized = (wheelStyle ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "steel" or "wheel-steel" or "wheelsteel" => MediaKinds.WheelSteel,
            _ => MediaKinds.WheelCarbon
        };
    }

    private static string FirstMediaPath(IReadOnlyDictionary<string, string> kindPaths, params string[] kinds)
    {
        foreach (var kind in kinds.Select(MediaKinds.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (kindPaths.TryGetValue(kind, out var path) && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static bool TrySetNonEmptyElement(XElement parent, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var element = parent.Element(name);
        if (element == null)
        {
            parent.Add(new XElement(name, value));
            return true;
        }

        if (string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        element.Value = value;
        return true;
    }

    private static bool TryRemoveElement(XElement parent, string name)
    {
        var element = parent.Element(name);
        if (element == null)
        {
            return false;
        }

        element.Remove();
        return true;
    }

    private static bool TryRemoveWrongLanguageElement(XElement parent, string name, string targetLanguage)
    {
        if (!ShouldAggressivelyCleanLanguage(targetLanguage))
        {
            return false;
        }

        var element = parent.Element(name);
        if (element == null || !IsLikelyWrongLanguageForTarget(name, element.Value, targetLanguage))
        {
            return false;
        }

        var normalized = NormalizeGamelistText(LocalizedMetadataSanitizer.SanitizeField(name, element.Value, targetLanguage));
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !IsLikelyWrongLanguageForTarget(name, normalized, targetLanguage))
        {
            if (string.Equals(element.Value, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            element.Value = normalized;
            return true;
        }

        element.Remove();
        return true;
    }

    private static bool TryNormalizeLocalizedElement(XElement parent, string name, string targetLanguage)
    {
        if (!ShouldAggressivelyCleanLanguage(targetLanguage))
        {
            return false;
        }

        var element = parent.Element(name);
        if (element == null)
        {
            return false;
        }

        var normalized = NormalizeGamelistText(LocalizedMetadataSanitizer.SanitizeField(name, element.Value, targetLanguage));
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(element.Value, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        element.Value = normalized;
        return true;
    }

    private static bool TrySetVisibleSlotElement(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (element == null)
            {
                return false;
            }

            element.Remove();
            return true;
        }

        if (element == null)
        {
            parent.Add(new XElement(name, value));
            return true;
        }

        if (string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        element.Value = value;
        return true;
    }

    private static string ResolveBundleField(LocalizedTextBundle? bundle, string fieldName)
    {
        if (bundle == null || !bundle.Fields.TryGetValue(fieldName, out var value))
        {
            return string.Empty;
        }

        return NormalizeGamelistText(LocalizedMetadataSanitizer.SanitizeField(fieldName, value, bundle.Language));
    }

    private static bool ShouldAggressivelyCleanLanguage(string language)
    {
        return string.Equals(NormalizeLanguageCode(language), "en", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalizedHumanTextField(string fieldName)
    {
        return fieldName.Equals("desc", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("genre", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("family", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("genres", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyWrongLanguageForTarget(string fieldName, string value, string targetLanguage)
    {
        var normalizedTarget = NormalizeLanguageCode(targetLanguage);
        if (!string.Equals(normalizedTarget, "en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(DetectSupportedTextLanguage(fieldName, value), "fr", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectSupportedTextLanguage(string fieldName, string? text)
    {
        var value = NormalizeGamelistText(text);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (fieldName.Equals("genre", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("genres", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("family", StringComparison.OrdinalIgnoreCase))
        {
            return DetectShortLocalizedLabelLanguage(value);
        }

        if (value.Length < 24)
        {
            return string.Empty;
        }

        var padded = " " + value.ToLowerInvariant() + " ";
        var frenchScore = CountMatches(
            padded,
            " le ", " la ", " les ", " des ", " une ", " un ", " vous ", " joueur", " jeu ",
            " dans ", " avec ", " pour ", " est ", " sont ", " qui ", " que ",
            " \u00e0 ", "\u00e9", "\u00e8", "\u00e7", "\u00f9", "\u00ea", "\u00fb", "\u00ee", "\u00f4");
        var englishScore = CountMatches(
            padded,
            " the ", " and ", " you ", " your ", " player", " game ", " with ", " for ",
            " is ", " are ", " in ", " on ", " to ", " of ", " from ", " this ", " that ");

        if (frenchScore >= englishScore + 2 && frenchScore >= 3)
        {
            return "fr";
        }

        if (englishScore >= frenchScore + 2 && englishScore >= 3)
        {
            return "en";
        }

        return string.Empty;
    }

    private static string DetectShortLocalizedLabelLanguage(string text)
    {
        var tokens = text
            .ToLowerInvariant()
            .Replace("\\", "/", StringComparison.Ordinal)
            .Split([',', '/', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var frenchScore = tokens.Count(IsLikelyFrenchLocalizedLabel);
        var englishScore = tokens.Count(IsLikelyEnglishLocalizedLabel);
        if (frenchScore > 0 && frenchScore > englishScore)
        {
            return "fr";
        }

        if (englishScore > 0 && englishScore > frenchScore)
        {
            return "en";
        }

        return string.Empty;
    }

    private static bool IsLikelyFrenchLocalizedLabel(string token)
    {
        return token is "aventure" or "plateforme" or "tir" or "course" or "conduite" or
            "jeu de roles" or "jeu de role" or "jeu de r\u00f4les" or "jeu de r\u00f4le" or
            "jeu de societe" or "jeu de soci\u00e9t\u00e9" or "reflexion" or "r\u00e9flexion" or
            "labyrinthe" or "avion" or "gestion" or "beat'em all" or "divers" or "flipper" or
            "tir avec accessoire" or "sport" or "boxe" or "multisports" or
            "combat";
    }

    private static bool IsLikelyEnglishLocalizedLabel(string token)
    {
        return token is "adventure" or "platform" or "platformer" or "shooter" or "shooting" or
            "racing" or "driving" or "board game" or "board games" or "role playing game" or
            "role playing games" or "rpg" or "puzzle" or "maze" or "flight" or "management" or
            "miscellaneous" or "pinball" or "lightgun shooter" or "sports" or "boxing" or
            "multisport" or "fighting";
    }

    private static int CountMatches(string value, params string[] needles)
    {
        var count = 0;
        foreach (var needle in needles)
        {
            var index = -needle.Length;
            while ((index = value.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string NormalizeLanguageCode(string? rawLanguage)
    {
        var normalized = (rawLanguage ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized.Length >= 2 ? normalized[..2] : string.Empty;
    }

    private static IEnumerable<string> MetadataTagNames()
    {
        yield return "releasedate";
        yield return "developer";
        yield return "publisher";
        yield return "players";
        yield return "md5";
        yield return "lang";
        yield return "region";
        yield return "genre";
        yield return "family";
        yield return "genres";
        yield return "rating";
        yield return "source";
    }

    private static string NormalizeGamelistText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        for (var i = 0; i < 2 && normalized.Contains('&', StringComparison.Ordinal); i++)
        {
            var decoded = WebUtility.HtmlDecode(normalized);
            if (string.Equals(decoded, normalized, StringComparison.Ordinal))
            {
                break;
            }

            normalized = decoded;
        }

        return normalized.Trim();
    }

    private static XDocument LoadGamelistDocument(string gamelistPath)
    {
        using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex) when (ex.Message.Contains("Root", StringComparison.OrdinalIgnoreCase))
        {
            return new XDocument(new XElement("gameList"));
        }
    }

    private EmulationStationScrapingSettings BuildEffectiveScrapingSettings(string? preferredLanguage)
    {
        var current = _settingsService.GetScrapingSettings();
        var copy = new EmulationStationScrapingSettings
        {
            PublicWebAccessEnabled = current.PublicWebAccessEnabled,
            ScrapeBezel = current.ScrapeBezel,
            ScrapeBoxBack = current.ScrapeBoxBack,
            ScrapeFanart = current.ScrapeFanart,
            ScrapeManual = current.ScrapeManual,
            ScrapeMap = current.ScrapeMap,
            ScrapeVideos = current.ScrapeVideos,
            ShowManualIcon = current.ShowManualIcon,
            Language = current.Language,
            ImageSource = current.ImageSource,
            LogoSource = current.LogoSource,
            ThumbSource = current.ThumbSource,
            WheelStyle = current.WheelStyle,
            MediaRegionMode = current.MediaRegionMode,
            LogoRegionMode = current.LogoRegionMode,
            UserRegion = current.UserRegion,
            ContentRegionProfile = current.ContentRegionProfile,
            ContentLanguageProfile = current.ContentLanguageProfile,
            ScreenScraperUser = current.ScreenScraperUser,
            ScreenScraperPassword = current.ScreenScraperPassword,
            ThemeSet = current.ThemeSet
        };

        if (string.IsNullOrWhiteSpace(preferredLanguage))
        {
            return copy;
        }

        copy.Language = ApiExposeProfileResolver.ResolveLanguageCode(preferredLanguage, preferredLanguage);
        var profiles = ApiExposeProfileResolver.Resolve(copy.Language, "auto", "auto");
        copy.ContentLanguageProfile = profiles.LanguageProfile;
        copy.ContentRegionProfile = profiles.RegionProfile;
        return copy;
    }

    private bool MatchesRequestedGameNode(XElement gameNode, LocalGamelistUpdateRequest request, string systemRoot)
    {
        if (!string.IsNullOrWhiteSpace(request.GamePath) &&
            MatchesRequestedGamePath(gameNode, request.GamePath, systemRoot))
        {
            return true;
        }

        var gamePath = gameNode.Element("path")?.Value?.Trim();
        var gameName = gameNode.Element("name")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(request.GameName) &&
            string.Equals(NormalizeCompareValue(gameName), NormalizeCompareValue(request.GameName), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(request.GameSlug))
        {
            return false;
        }

        var requestedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeSlug(request.GameSlug),
            _gameNameNormalizer.NormalizeGameSlug(request.GameSlug, null)
        };
        requestedSlugs.RemoveWhere(string.IsNullOrWhiteSpace);

        var nodeSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _gameNameNormalizer.NormalizeGameSlug(gameName, gamePath),
            _gameNameNormalizer.NormalizeGameSlug(Path.GetFileNameWithoutExtension(gamePath), gamePath),
            NormalizeSlug(gameName),
            NormalizeSlug(Path.GetFileNameWithoutExtension(gamePath))
        };
        nodeSlugs.RemoveWhere(string.IsNullOrWhiteSpace);

        return requestedSlugs.Overlaps(nodeSlugs);
    }

    private static bool MatchesRequestedGamePath(XElement gameNode, string requestedPath, string systemRoot)
    {
        var gamePath = gameNode.Element("path")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return false;
        }

        var absoluteGamePath = ResolveEsRelativePath(systemRoot, gamePath);
        var normalizedRequestedPath = NormalizeCompareValue(requestedPath);
        var requestedFileName = NormalizeCompareValue(Path.GetFileName(requestedPath));
        var candidates = new[]
        {
            NormalizeCompareValue(gamePath),
            NormalizeCompareValue(absoluteGamePath),
            NormalizeCompareValue(Path.GetFileName(gamePath)),
            NormalizeCompareValue(Path.GetFileName(absoluteGamePath))
        };

        return candidates.Contains(normalizedRequestedPath, StringComparer.OrdinalIgnoreCase) ||
            candidates.Contains(requestedFileName, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ResolveSystems(LocalGamelistUpdateRequest request, string scope)
    {
        if (scope is "system" or "game")
        {
            var systemId = (request.SystemId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(systemId))
            {
                throw new InvalidOperationException($"SystemId is required for scope={scope}.");
            }

            if (scope == "game" &&
                string.IsNullOrWhiteSpace(request.GamePath) &&
                string.IsNullOrWhiteSpace(request.GameName) &&
                string.IsNullOrWhiteSpace(request.GameSlug))
            {
                throw new InvalidOperationException("GamePath, GameName or GameSlug is required for scope=game.");
            }

            return [systemId];
        }

        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(RetroBatPaths.RomsRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
            .Where(systemId => File.Exists(Path.Combine(RetroBatPaths.RomsRoot, systemId!, "gamelist.xml")))
            .Select(systemId => systemId!)
            .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveScope(LocalGamelistUpdateRequest request)
    {
        var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(scope))
        {
            var hasGameTarget = !string.IsNullOrWhiteSpace(request.GamePath) ||
                !string.IsNullOrWhiteSpace(request.GameName) ||
                !string.IsNullOrWhiteSpace(request.GameSlug);
            if (!string.IsNullOrWhiteSpace(request.SystemId) && hasGameTarget)
            {
                return "game";
            }

            return string.IsNullOrWhiteSpace(request.SystemId) ? "all" : "system";
        }

        return scope is "all" or "system" or "game"
            ? scope
            : throw new InvalidOperationException($"Unsupported local gamelist update scope: {scope}");
    }

    private static string ResolveGamelistPath(string frontendSystemId)
    {
        return Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId, "gamelist.xml");
    }

    private static void WriteAudit(
        string action,
        string status,
        string scope,
        string? systemId,
        string? gamelistPath,
        Exception? exception,
        object? details)
    {
        try
        {
            var logPath = Path.Combine(RetroBatPaths.PluginRoot, ".log", "local-gamelist-update.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                action,
                status,
                scope,
                systemId,
                gamelistPath,
                error = exception == null
                    ? null
                    : new
                    {
                        type = exception.GetType().FullName,
                        exception.Message,
                        exception.StackTrace,
                        innerType = exception.InnerException?.GetType().FullName,
                        innerMessage = exception.InnerException?.Message,
                        innerStackTrace = exception.InnerException?.StackTrace
                    },
                details
            }, AuditJsonOptions);

            lock (AuditLogLock)
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Audit logging must never break the gamelist update path.
        }
    }

    private static string ToMediaRelativePath(string mediaPath, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return string.Empty;
        }

        if (!Path.IsPathRooted(mediaPath))
        {
            return EnsureEsRelativePrefix(mediaPath.Replace('\\', '/'));
        }

        var relative = Path.GetRelativePath(systemRoot, mediaPath);
        return EnsureEsRelativePrefix(relative.Replace('\\', '/'));
    }

    private static string EnsureEsRelativePrefix(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            return normalized;
        }

        return "./" + normalized.TrimStart('/');
    }

    private static void RemoveMarqueeIfSameAsWheel(IDictionary<string, string> kindPaths, string systemRoot)
    {
        if (!kindPaths.TryGetValue(MediaKinds.Marquee, out var marqueePath) ||
            !kindPaths.TryGetValue(MediaKinds.Wheel, out var wheelPath))
        {
            return;
        }

        var marqueeDiskPath = ResolveEsRelativePath(systemRoot, marqueePath);
        var wheelDiskPath = ResolveEsRelativePath(systemRoot, wheelPath);
        if (!string.IsNullOrWhiteSpace(marqueeDiskPath) &&
            !string.IsNullOrWhiteSpace(wheelDiskPath) &&
            File.Exists(marqueeDiskPath) &&
            File.Exists(wheelDiskPath) &&
            FilesLookSame(marqueeDiskPath, wheelDiskPath))
        {
            kindPaths.Remove(MediaKinds.Marquee);
        }
    }

    private static string ResolveEsRelativePath(string systemRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        var normalized = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return Path.GetFullPath(Path.Combine(systemRoot, normalized));
    }

    private static bool FilesLookSame(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        return first.Exists &&
            second.Exists &&
            first.Length == second.Length &&
            string.Equals(Path.GetFileName(first.FullName), Path.GetFileName(second.FullName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathKey(string value)
    {
        return (value ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private static string NormalizeCompareValue(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }

    private static bool IsGameScope(LocalGamelistUpdateRequest request)
    {
        return string.Equals((request.Scope ?? string.Empty).Trim(), "game", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(request.SystemId) &&
             (string.IsNullOrWhiteSpace(request.Scope) || string.Equals(request.Scope.Trim(), "game", StringComparison.OrdinalIgnoreCase)) &&
             (!string.IsNullOrWhiteSpace(request.GamePath) ||
              !string.IsNullOrWhiteSpace(request.GameName) ||
              !string.IsNullOrWhiteSpace(request.GameSlug)));
    }

    private static string BuildFamilySlug(string? value)
    {
        var fileName = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        var cleaned = VariantTagRegex().Replace(fileName, " ");
        return NormalizeSlug(cleaned);
    }

    private static string? TryExtractGameSlugFromMediaPath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/');
        var match = Regex.Match(normalized, @"/games/(?<slug>[^/]+)/", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeSlug(match.Groups["slug"].Value) : null;
    }

    private static string NormalizeSlug(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim().ToLowerInvariant();
        cleaned = cleaned.Replace('&', ' ');
        cleaned = NonAlphaNumericRegex().Replace(cleaned, " ");
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim();
        return cleaned.Replace(' ', '_');
    }

    private object GetGamelistLock(string gamelistPath)
    {
        return _gamelistStore.GetLock(gamelistPath);
    }

    private enum MediaSelectionTarget
    {
        Image,
        Logo,
        Thumbnail
    }

    [GeneratedRegex(@"\((Europe|USA|Japan|World|France|Rev[^)]*|Beta|Proto|Prototype|Demo|Sample|Unl|Unlicensed)[^)]*\)|\[(T\+[^]]+|Rev[^]]*|Beta|Proto|Demo|Sample|Unl|Unlicensed)[^]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VariantTagRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
