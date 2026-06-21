using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class ScreenScraperRemoteProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions RawCacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, string[]> MediaTypeCandidatesByKind =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [MediaKinds.Image] = ["sstitle", "title"],
            [MediaKinds.Thumbnail] = ["ss", "screenshot"],
            [MediaKinds.Logo] = ["wheel", "wheel-hd"],
            [MediaKinds.Wheel] = ["wheel"],
            [MediaKinds.WheelCarbon] = ["wheel-carbon"],
            [MediaKinds.WheelSteel] = ["wheel-steel"],
            [MediaKinds.Marquee] = ["marquee"],
            [MediaKinds.ScreenMarquee] = ["screenmarquee"],
            [MediaKinds.ScreenMarqueeSmall] = ["screenmarqueesmall"],
            [MediaKinds.SteamGrid] = ["steamgrid"],
            [MediaKinds.MixRbv1] = ["mixrbv1"],
            [MediaKinds.MixRbv2] = ["mixrbv2", "mix"],
            [MediaKinds.BoxFront] = ["box-2D", "box2d"],
            [MediaKinds.BoxSide] = ["box-2D-side", "boxside"],
            [MediaKinds.BoxTexture] = ["box-texture", "boxtexture"],
            [MediaKinds.Box3d] = ["box-3D", "box3d"],
            [MediaKinds.Cartridge] = ["support-2D", "support2d"],
            [MediaKinds.Label] = ["support-texture", "supporttexture"],
            [MediaKinds.Fanart] = ["fanart"],
            [MediaKinds.Flyer] = ["flyer"],
            [MediaKinds.Figurine] = ["figurine"],
            [MediaKinds.BoxBack] = ["box-2D-back", "boxback"],
            [MediaKinds.Map] = ["map", "maps"],
            [MediaKinds.Manual] = ["manuel"],
            [MediaKinds.Magazine] = ["magazine"],
            [MediaKinds.Video] = ["video"],
            [MediaKinds.VideoNormalized] = ["video-normalized"]
        };

    private readonly ScreenScraperConnectionService _connectionService;
    private readonly ScreenScraperCapabilityService _capabilityService;
    private readonly EsProjectionService _projectionService;
    private readonly CollectionPackInstallerService _collectionPackInstallerService;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly ILocalizedTextStore _localizedTextStore;
    private readonly DescriptionTranslationService _descriptionTranslationService;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ApiExposeTaxonomyService _taxonomy;
    private readonly MediaLocalizationResolver _mediaLocalizationResolver;
    private readonly ILogger<ScreenScraperRemoteProvider>? _logger;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public ScreenScraperRemoteProvider(
        ScreenScraperConnectionService connectionService,
        ScreenScraperCapabilityService capabilityService,
        EsProjectionService projectionService,
        CollectionPackInstallerService collectionPackInstallerService,
        GamelistUpdateService gamelistUpdateService,
        MediaRuntimeState runtimeState,
        ILocalizedTextStore localizedTextStore,
        DescriptionTranslationService descriptionTranslationService,
        EmulationStationSettingsService settingsService,
        IOptionsMonitor<ApiExposeOptions> options,
        ApiExposeTaxonomyService taxonomy,
        MediaLocalizationResolver mediaLocalizationResolver,
        ILogger<ScreenScraperRemoteProvider>? logger = null)
    {
        _connectionService = connectionService;
        _capabilityService = capabilityService;
        _projectionService = projectionService;
        _collectionPackInstallerService = collectionPackInstallerService;
        _gamelistUpdateService = gamelistUpdateService;
        _runtimeState = runtimeState;
        _localizedTextStore = localizedTextStore;
        _descriptionTranslationService = descriptionTranslationService;
        _settingsService = settingsService;
        _options = options;
        _taxonomy = taxonomy;
        _mediaLocalizationResolver = mediaLocalizationResolver;
        _logger = logger;
    }

    public async Task<ScreenScraperRemoteScrapeResult> ScrapeAsync(
        MediaProjectionPlan plan,
        IReadOnlyList<string> allowedKinds,
        string screenScraperSystemId,
        string requestedLanguage,
        string bezelAspectRatio,
        string bezelOrientation,
        bool refreshCurrentGameAfterSuccess,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectionToken = _runtimeState.CaptureCurrentGameSelection();

        var connection = _connectionService.Resolve();
        if (!connection.HasUserCredentials || !connection.HasDeveloperCredentials)
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-provider",
                "credentials",
                "missing",
                new
                {
                    connection.HasUserCredentials,
                    connection.HasDeveloperCredentials
                },
                cancellationToken);
            return ScreenScraperRemoteScrapeResult.Failed("missing-credentials", "ScreenScraper credentials are incomplete.");
        }

        if (string.IsNullOrWhiteSpace(screenScraperSystemId))
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-provider",
                "system-mapping",
                "missing",
                cancellationToken: cancellationToken);
            return ScreenScraperRemoteScrapeResult.Failed("missing-system-mapping", "ScreenScraper system id is missing.");
        }

        ScreenScraperGameCatalog? catalog;
        try
        {
            catalog = await FetchCatalogAsync(plan, connection, screenScraperSystemId, requestedLanguage, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _logger?.LogWarning(
                ex,
                "ScreenScraper catalog fetch failed for system={SystemId}, game={GameSlug}.",
                plan.FrontendSystemId,
                plan.GameSlug);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-catalog",
                "catalog",
                "provider-error",
                new { error = ex.GetType().Name },
                cancellationToken);
            return ScreenScraperRemoteScrapeResult.Failed("provider-error", "ScreenScraper catalog fetch failed.");
        }

        if (catalog == null)
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-catalog",
                "catalog",
                "not-found",
                cancellationToken: cancellationToken);
            if (allowedKinds.Any(kind => string.Equals(MediaKinds.Normalize(kind), MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase)) &&
                _gamelistUpdateService.IsCurrentlySelectedGame(plan) &&
                await TryApplyThemeHbFallbackAsync(plan, "catalog-not-found", cancellationToken))
            {
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-catalog",
                    "themehb",
                    "themehb-fallback-applied-after-not-found",
                    cancellationToken: cancellationToken);
                return new ScreenScraperRemoteScrapeResult
                {
                    Status = "completed",
                    Message = "ThemeHb fallback applied after ScreenScraper did not return a matching game.",
                    DownloadedMediaCount = 0,
                    ImportedMediaCount = 0,
                    TextUpdated = false,
                    GamelistChanged = false,
                    MediaContentChanged = false,
                    MetadataChanged = false,
                    LivePushed = false,
                    ReloadRequested = false,
                    RequiresGamelistPersistence = false
                };
            }

            return ScreenScraperRemoteScrapeResult.Failed("not-found", "ScreenScraper did not return a matching game.");
        }

        plan.ScreenScraperGameId = catalog.GameId;
        MergeDistinct(plan.RomRegions, catalog.RomRegions);
        MergeDistinct(plan.RomLanguages, catalog.RomLanguages);
        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "remote-scrape-catalog",
            "catalog",
            "success",
            new
            {
                screenScraperGameId = catalog.GameId,
                catalogName = catalog.Name
            },
            cancellationToken);

        var textPersistResult = await PersistTextAsync(plan, catalog, requestedLanguage, cancellationToken);
        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "remote-scrape-text",
            "metadata",
            textPersistResult.Updated ? "updated" : "unchanged",
            new
            {
                language = requestedLanguage,
                textSlug = string.IsNullOrWhiteSpace(plan.TextSourceGameSlug) ? plan.GameSlug : plan.TextSourceGameSlug,
                rawRating = textPersistResult.RawRating,
                normalizedRating = textPersistResult.NormalizedRating,
                persistedRating = textPersistResult.PersistedRating,
                fieldCount = textPersistResult.FieldCount,
                fields = textPersistResult.Fields
            },
            cancellationToken: cancellationToken);

        var downloadedMedia = 0;
        var importedMedia = 0;
        var collectionThemeApplied = 0;
        var importedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingKinds = allowedKinds
            .Select(MediaKinds.Normalize)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var essentialKinds = ResolveEssentialKinds(plan, missingKinds);
        var secondaryKinds = missingKinds
            .Except(essentialKinds, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var essentialResult = await ProcessMediaKindsAsync(
            plan,
            catalog,
            connection,
            screenScraperSystemId,
            essentialKinds,
            bezelAspectRatio,
            bezelOrientation,
            "essential",
            cancellationToken);
        downloadedMedia += essentialResult.DownloadedMediaCount;
        importedMedia += essentialResult.ImportedMediaCount;
        collectionThemeApplied += essentialResult.CollectionThemeAppliedCount;
        importedKinds.UnionWith(essentialResult.ImportedKinds);

        var livePushed = false;
        var reloadRequested = false;
        var requiresGamelistPersistence = textPersistResult.Updated;
        if (essentialResult.ImportedMediaCount > 0 || textPersistResult.Updated)
        {
            if (essentialResult.ImportedMediaCount > 0)
            {
                await _projectionService.ApplyProjectionAsync(plan, cancellationToken);
                requiresGamelistPersistence = true;
            }

            var essentialLivePushAllowed =
                (essentialResult.ImportedMediaCount > 0 || textPersistResult.Updated) &&
                refreshCurrentGameAfterSuccess &&
                IsCapturedSelectionStillCurrent(selectionToken, plan);
            if (essentialLivePushAllowed)
            {
                livePushed = await _gamelistUpdateService.PushLiveGameUpdateToEsAsync(
                    plan,
                    cancellationToken,
                    LiveGameUpdateNotificationKind.RemoteScrape,
                    allowLocalizedMetadataRefresh: textPersistResult.Updated);
            }

            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-essential-finalize",
                "gamelist",
                requiresGamelistPersistence ? "pending-persist" : "unchanged",
                new
                {
                    essentialResult.DownloadedMediaCount,
                    essentialResult.ImportedMediaCount,
                    essentialResult.CollectionThemeAppliedCount,
                    mediaContentChanged = essentialResult.ImportedMediaCount > 0,
                    metadataChanged = textPersistResult.Updated,
                    requiresGamelistPersistence,
                    essentialLivePushAllowed,
                    livePushed,
                    reloadRequested
                },
                cancellationToken);
        }

        var secondaryResult = await ProcessMediaKindsAsync(
            plan,
            catalog,
            connection,
            screenScraperSystemId,
            secondaryKinds,
            bezelAspectRatio,
            bezelOrientation,
            "secondary",
            cancellationToken);
        downloadedMedia += secondaryResult.DownloadedMediaCount;
        importedMedia += secondaryResult.ImportedMediaCount;
        collectionThemeApplied += secondaryResult.CollectionThemeAppliedCount;
        importedKinds.UnionWith(secondaryResult.ImportedKinds);

        if (secondaryResult.ImportedMediaCount > 0)
        {
            await _projectionService.ApplyProjectionAsync(plan, cancellationToken);
            requiresGamelistPersistence = true;
            var currentVideoLivePushAllowed =
                refreshCurrentGameAfterSuccess &&
                secondaryResult.ImportedKinds.Contains(MediaKinds.Video) &&
                IsCapturedSelectionStillCurrent(selectionToken, plan);
            if (currentVideoLivePushAllowed)
            {
                var videoLivePushed = await _gamelistUpdateService.PushLiveGameUpdateToEsAsync(
                    plan,
                    cancellationToken,
                    LiveGameUpdateNotificationKind.RemoteVideoScrape,
                    allowCurrentVideoRefresh: true);
                livePushed = livePushed || videoLivePushed;
            }

            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-secondary-finalize",
                "gamelist",
                "pending-persist",
                new
                {
                    secondaryResult.DownloadedMediaCount,
                    secondaryResult.ImportedMediaCount,
                    mediaContentChanged = true,
                    metadataChanged = false,
                    requiresGamelistPersistence,
                    currentVideoLivePushAllowed,
                    livePushed
                },
                cancellationToken);
        }

        var mediaContentChanged = importedMedia > 0;
        var changed = mediaContentChanged || collectionThemeApplied > 0 || textPersistResult.Updated;
        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "remote-scrape-complete",
            "summary",
            changed ? "completed" : "no-change",
            new
            {
                downloadedMedia,
                importedMedia,
                collectionThemeApplied,
                textUpdated = textPersistResult.Updated,
                gamelistChanged = false,
                mediaContentChanged,
                metadataChanged = textPersistResult.Updated,
                requiresGamelistPersistence,
                livePushed,
                reloadRequested
            },
            cancellationToken);
        return new ScreenScraperRemoteScrapeResult
        {
            Status = changed ? "completed" : "no-change",
            Message = changed
                ? collectionThemeApplied > 0 && importedMedia == 0 && !textPersistResult.Updated
                    ? "Collection theme fallback applied after ScreenScraper returned no usable theme."
                    : "ScreenScraper data imported through the canonical media pipeline."
                : "ScreenScraper returned no new usable data for this game.",
            ScreenScraperGameId = catalog.GameId,
            DownloadedMediaCount = downloadedMedia,
            ImportedMediaCount = importedMedia,
            TextUpdated = textPersistResult.Updated,
            GamelistChanged = false,
            MediaContentChanged = mediaContentChanged,
            MetadataChanged = textPersistResult.Updated,
            LivePushed = livePushed,
            ReloadRequested = reloadRequested,
            RequiresGamelistPersistence = requiresGamelistPersistence,
            ImportedKinds = importedKinds
                .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private bool IsCapturedSelectionStillCurrent(CurrentGameSelectionToken selectionToken, MediaProjectionPlan plan)
    {
        return (_runtimeState.IsCurrentGameSelectionToken(selectionToken, plan.FrontendSystemId, plan.GamePath) ||
                _runtimeState.IsCurrentGameSelectionToken(selectionToken, plan.SystemId, plan.GamePath)) &&
            _gamelistUpdateService.IsCurrentlySelectedGame(plan);
    }

    private async Task<RemoteMediaBatchResult> ProcessMediaKindsAsync(
        MediaProjectionPlan plan,
        ScreenScraperGameCatalog catalog,
        ScreenScraperConnectionInfo connection,
        string screenScraperSystemId,
        IReadOnlyList<string> kinds,
        string bezelAspectRatio,
        string bezelOrientation,
        string tier,
        CancellationToken cancellationToken)
    {
        if (kinds.Count == 0)
        {
            return new RemoteMediaBatchResult(0, 0, 0, Array.Empty<string>());
        }

        var concurrency = Math.Min(_capabilityService.ResolveMediaConcurrency(), kinds.Count);
        var results = new List<RemoteMediaKindResult>();
        if (concurrency <= 1)
        {
            foreach (var kind in kinds)
            {
                results.Add(await ProcessOneMediaKindAsync(
                    plan,
                    catalog,
                    connection,
                    screenScraperSystemId,
                    kind,
                    bezelAspectRatio,
                    bezelOrientation,
                    tier,
                    cancellationToken));
            }
        }
        else
        {
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            var tasks = kinds.Select(async kind =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await ProcessOneMediaKindAsync(
                        plan,
                        catalog,
                        connection,
                        screenScraperSystemId,
                        kind,
                        bezelAspectRatio,
                        bezelOrientation,
                        tier,
                        cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            results.AddRange(await Task.WhenAll(tasks));
        }

        var importedKinds = results
            .SelectMany(result => result.ImportedKinds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RemoteMediaBatchResult(
            results.Sum(result => result.DownloadedMediaCount),
            results.Sum(result => result.ImportedMediaCount),
            results.Sum(result => result.CollectionThemeAppliedCount),
            importedKinds);
    }

    private async Task<RemoteMediaKindResult> ProcessOneMediaKindAsync(
        MediaProjectionPlan plan,
        ScreenScraperGameCatalog catalog,
        ScreenScraperConnectionInfo connection,
        string screenScraperSystemId,
        string kind,
        string bezelAspectRatio,
        string bezelOrientation,
        string tier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var downloadedMedia = 0;
        var importedMedia = 0;
        var collectionThemeApplied = 0;
        var importedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedKind = MediaKinds.Normalize(kind);
        var isThemeHb = string.Equals(normalizedKind, MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase);
        var importedThisKind = false;
        var collectAllLocalizedVariants = IsAllScreenScraperRegionMode() && ShouldLocalizeMediaKind(normalizedKind);

        var need = plan.Needs.FirstOrDefault(entry =>
            string.Equals(MediaKinds.Normalize(entry.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase));
        if (need == null)
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-media",
                kind,
                "skipped-no-need",
                new { tier },
                cancellationToken);
            return new RemoteMediaKindResult(0, 0, 0, Array.Empty<string>());
        }

        foreach (var mediaType in ResolveMediaTypes(plan, kind, bezelAspectRatio, bezelOrientation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isThemeHb && !_gamelistUpdateService.IsCurrentlySelectedGame(plan))
            {
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-media",
                    kind,
                    "skipped-not-current-before-download",
                    new { tier, mediaType },
                    cancellationToken);
                break;
            }

            var mediaUrl = catalog.FindMediaUrl(mediaType);
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                mediaUrl = BuildMediaDownloadUrl(connection, screenScraperSystemId, catalog.GameId, mediaType, kind);
            }

            var tempPath = await TryDownloadMediaAsync(mediaUrl, kind, cancellationToken);
            if (string.IsNullOrWhiteSpace(tempPath))
            {
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-media",
                    kind,
                    "download-miss",
                    new { tier, mediaType },
                    cancellationToken);
                continue;
            }

            downloadedMedia++;
            try
            {
                var mediaRegion = TryGetMediaTypeRegion(mediaType);
                var localizedVariantChanged = false;
                if (ShouldLocalizeMediaKind(normalizedKind) && !string.IsNullOrWhiteSpace(mediaRegion))
                {
                    localizedVariantChanged = await TryImportLocalizedMediaVariantAsync(
                        plan,
                        normalizedKind,
                        mediaRegion,
                        tempPath,
                        cancellationToken);
                }

                if (isThemeHb && !_gamelistUpdateService.IsCurrentlySelectedGame(plan))
                {
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-media",
                        kind,
                        "skipped-not-current-after-download",
                        new { tier, mediaType },
                        cancellationToken);
                    break;
                }

                if (collectAllLocalizedVariants && importedThisKind)
                {
                    continue;
                }

                var wasImportedBefore = need.WasImported;
                var wasContentChangedBefore = need.WasContentChanged;
                var importedPath = await _projectionService.ImportCanonicalAsync(
                    plan.SystemId,
                    plan.GameSlug,
                    need,
                    tempPath,
                    cancellationToken,
                    plan.FrontendSystemId,
                    plan.GamePath,
                    plan.EsGameId,
                    notifyThemeHbScrape: false);
                var canonicalChanged = need.WasImported != wasImportedBefore ||
                    need.WasContentChanged != wasContentChangedBefore;
                if (!string.IsNullOrWhiteSpace(importedPath) && (canonicalChanged || localizedVariantChanged))
                {
                    importedMedia++;
                    importedThisKind = true;
                    importedKinds.Add(normalizedKind);
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-media",
                        kind,
                        "imported",
                        new
                        {
                            tier,
                            mediaType,
                            importedPath
                        },
                        cancellationToken);
                    if (!collectAllLocalizedVariants)
                    {
                        break;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(importedPath))
                {
                    importedThisKind = true;
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "remote-scrape-media",
                        kind,
                        "already-present",
                        new
                        {
                            tier,
                            mediaType,
                            importedPath,
                            localizedVariantChanged
                        },
                        cancellationToken);
                    if (!collectAllLocalizedVariants)
                    {
                        break;
                    }
                }
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }

        if (isThemeHb &&
            !importedThisKind &&
            _gamelistUpdateService.IsCurrentlySelectedGame(plan) &&
            await TryApplyThemeHbFallbackAsync(plan, "download-miss", cancellationToken))
        {
            collectionThemeApplied++;
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-media",
                kind,
                "themehb-fallback-applied",
                new { tier },
                cancellationToken);
        }

        return new RemoteMediaKindResult(downloadedMedia, importedMedia, collectionThemeApplied, importedKinds);
    }

    private async Task<bool> TryApplyThemeHbFallbackAsync(
        MediaProjectionPlan plan,
        string reason,
        CancellationToken cancellationToken)
    {
        if (await TryApplyCanonicalEquivalentThemeHbAsync(plan, reason, cancellationToken))
        {
            return true;
        }

        if (await _collectionPackInstallerService.TryApplyCollectionThemeToGameAsync(plan, cancellationToken))
        {
            await _projectionService.RefreshThemeHbAfterExternalInstallAsync(plan.FrontendSystemId, plan.GameSlug, cancellationToken);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "themehb-fallback",
                "collection",
                "applied",
                new { reason },
                cancellationToken);
            return true;
        }

        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "themehb-fallback",
            "fallback",
            "not-found",
            new { reason },
            cancellationToken);
        return false;
    }

    private async Task<bool> TryApplyCanonicalEquivalentThemeHbAsync(
        MediaProjectionPlan plan,
        string reason,
        CancellationToken cancellationToken)
    {
        var result = await _collectionPackInstallerService.TryApplyCanonicalEquivalentThemeToGameAsync(plan, cancellationToken);
        if (result.Handled)
        {
            if (result.Changed)
            {
                await _projectionService.RefreshThemeHbAfterExternalInstallAsync(plan.FrontendSystemId, plan.GameSlug, cancellationToken);
            }

            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "themehb-fallback",
                "canonical-equivalent",
                result.Changed ? "applied" : "already-current",
                new
                {
                    reason
                },
                cancellationToken);
            return true;
        }

        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "themehb-fallback",
            "canonical-equivalent",
            "not-found",
            new { reason },
            cancellationToken);
        return false;
    }

    private async Task<ScreenScraperGameCatalog?> FetchCatalogAsync(
        MediaProjectionPlan plan,
        ScreenScraperConnectionInfo connection,
        string screenScraperSystemId,
        string requestedLanguage,
        CancellationToken cancellationToken)
    {
        foreach (var romName in BuildCatalogRomNames(plan))
        {
            var uri = BuildCatalogUrl(connection, screenScraperSystemId, plan, romName);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-catalog-query",
                "catalog",
                "request",
                new
                {
                    romName,
                    screenScraperSystemId
                },
                cancellationToken);
            await _capabilityService.WaitForRemoteRequestSlotAsync(cancellationToken);
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-catalog-query",
                    "catalog",
                    "not-found",
                    new
                    {
                        romName,
                        statusCode = (int)response.StatusCode
                    },
                    cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogInformation(
                    "ScreenScraper catalog returned status={StatusCode} for system={SystemId}, game={GameSlug}.",
                    (int)response.StatusCode,
                    plan.FrontendSystemId,
                    plan.GameSlug);
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-catalog-query",
                    "catalog",
                    "http-error",
                    new
                    {
                        romName,
                        statusCode = (int)response.StatusCode
                    },
                    cancellationToken);
                continue;
            }

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await TryWriteRawCatalogCacheAsync(plan, romName, payload, cancellationToken);
            using var document = JsonDocument.Parse(payload);
            _capabilityService.UpdateFromResponse(document.RootElement, "jeuInfos.php");
            var catalog = ParseCatalog(document.RootElement, requestedLanguage);
            if (catalog != null)
            {
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-catalog-query",
                    "catalog",
                    "matched",
                    new
                    {
                        romName,
                        catalog.GameId,
                        catalog.Name
                    },
                    cancellationToken);
                return catalog;
            }
        }

        return null;
    }

    private async Task TryWriteRawCatalogCacheAsync(
        MediaProjectionPlan plan,
        string romName,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Scraping.ScreenScraperRawCacheEnabled || payload.Length == 0)
        {
            return;
        }

        try
        {
            var cacheDirectory = Path.Combine(
                RetroBatPaths.MediaRoot,
                "scrap-cache",
                "screenscraper",
                "games",
                SafePathSegment(plan.FrontendSystemId));
            Directory.CreateDirectory(cacheDirectory);
            var targetPath = Path.Combine(cacheDirectory, $"{SafePathSegment(plan.GameSlug)}.json");
            var tempPath = Path.Combine(cacheDirectory, $".{SafePathSegment(plan.GameSlug)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(tempPath, SanitizeRawCatalogPayload(payload), cancellationToken);
            File.Move(tempPath, targetPath, overwrite: true);
            CleanupLegacyRawCatalogCacheDirectory(cacheDirectory, plan.GameSlug);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ScreenScraper raw cache write failed for system={SystemId}, game={GameSlug}.", plan.FrontendSystemId, plan.GameSlug);
        }
    }

    private static byte[] SanitizeRawCatalogPayload(byte[] payload)
    {
        try
        {
            var node = JsonNode.Parse(payload);
            if (node == null)
            {
                return payload;
            }

            SanitizeRawCatalogNode(node);
            return JsonSerializer.SerializeToUtf8Bytes(node, RawCacheJsonOptions);
        }
        catch
        {
            return payload;
        }
    }

    private static void SanitizeRawCatalogNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value == null)
                {
                    continue;
                }

                if (IsSensitiveScreenScraperField(property.Key))
                {
                    obj[property.Key] = "[redacted]";
                    continue;
                }

                if (string.Equals(property.Key, "commandRequested", StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue commandValue &&
                    commandValue.TryGetValue<string>(out var command))
                {
                    obj[property.Key] = SanitizeScreenScraperUrl(command);
                    continue;
                }

                SanitizeRawCatalogNode(property.Value);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child != null)
                {
                    SanitizeRawCatalogNode(child);
                }
            }
        }
    }

    private static bool IsSensitiveScreenScraperField(string key)
    {
        var normalized = (key ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is
            "devid" or
            "devpassword" or
            "ssid" or
            "sspassword" or
            "password" or
            "pass" or
            "userpassword";
    }

    private static string SanitizeScreenScraperUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value ?? string.Empty;
        }

        var sanitizedQuery = string.Join(
            "&",
            uri.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part =>
                {
                    var separatorIndex = part.IndexOf('=', StringComparison.Ordinal);
                    var key = separatorIndex >= 0 ? Uri.UnescapeDataString(part[..separatorIndex]) : Uri.UnescapeDataString(part);
                    var encodedKey = Uri.EscapeDataString(key);
                    if (IsSensitiveScreenScraperField(key))
                    {
                        return $"{encodedKey}=[redacted]";
                    }

                    return part;
                }));
        var builder = new UriBuilder(uri)
        {
            Query = sanitizedQuery
        };
        return builder.Uri.ToString();
    }

    private void CleanupLegacyRawCatalogCacheDirectory(string systemCacheDirectory, string gameSlug)
    {
        try
        {
            var legacyDirectory = Path.Combine(systemCacheDirectory, SafePathSegment(gameSlug));
            if (!Directory.Exists(legacyDirectory))
            {
                return;
            }

            Directory.Delete(legacyDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ScreenScraper legacy raw cache cleanup ignored for system cache={SystemCacheDirectory}, game={GameSlug}.", systemCacheDirectory, gameSlug);
        }
    }

    private async Task<bool> TryImportLocalizedMediaVariantAsync(
        MediaProjectionPlan plan,
        string normalizedKind,
        string region,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var targetPath = ResolveLocalizedMediaVariantPath(plan.SystemId, plan.GameSlug, normalizedKind, region, sourcePath);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(targetPath) && HaveSameContent(sourcePath, targetPath))
            {
                return false;
            }

            await CopyFileAtomicallyAsync(sourcePath, targetPath, cancellationToken);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "remote-scrape-localized-media",
                normalizedKind,
                "imported",
                new
                {
                    region,
                    targetPath
                },
                cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Localized ScreenScraper media cache failed for kind={Kind}, region={Region}.", normalizedKind, region);
            return false;
        }
    }

    private static List<string> ResolveEssentialKinds(MediaProjectionPlan plan, IReadOnlyList<string> missingKinds)
    {
        var essential = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MediaKinds.Image,
            MediaKinds.Logo,
            MediaKinds.Thumbnail,
            MediaKinds.Fanart,
            MediaKinds.WheelCarbon,
            MediaKinds.WheelSteel
        };

        AddSelectionKind(essential, plan.PreferredImageSource, MediaSelectionTarget.Image);
        AddSelectionKind(essential, plan.PreferredLogoSource, MediaSelectionTarget.Logo);
        AddSelectionKind(essential, plan.PreferredThumbnailSource, MediaSelectionTarget.Thumbnail);

        return missingKinds
            .Where(kind => essential.Contains(MediaKinds.Normalize(kind)))
            .OrderBy(kind => GetEssentialKindPriority(kind, plan))
            .ThenBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddSelectionKind(ISet<string> kinds, string source, MediaSelectionTarget target)
    {
        var kind = ResolveSelectionSourceToKind(source, target);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            kinds.Add(kind);
        }
    }

    private static string ResolveSelectionSourceToKind(string source, MediaSelectionTarget target)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "sstitle" or "title" => MediaKinds.Image,
            "ss" or "screenshot" or "thumb" or "thumbnail" => MediaKinds.Thumbnail,
            "logo" => MediaKinds.Logo,
            "wheel-hd" => target == MediaSelectionTarget.Logo ? MediaKinds.WheelCarbon : MediaKinds.WheelCarbon,
            "wheel" => MediaKinds.Wheel,
            "wheel-carbon" or "wheelcarbon" => MediaKinds.WheelCarbon,
            "wheel-steel" or "wheelsteel" => MediaKinds.WheelSteel,
            "marquee" => MediaKinds.Marquee,
            "screenmarquee" or "screen-marquee" => MediaKinds.ScreenMarquee,
            "screenmarqueesmall" or "screen-marquee-small" => MediaKinds.ScreenMarqueeSmall,
            "boxback" or "box-back" => MediaKinds.BoxBack,
            "box-2d" or "box2d" => MediaKinds.BoxFront,
            "box-3d" or "box3d" => MediaKinds.Box3d,
            "cartridge" or "cart" or "support-2d" or "support2d" => MediaKinds.Cartridge,
            "label" or "support-texture" or "supporttexture" => MediaKinds.Label,
            "figurine" => MediaKinds.Figurine,
            "fanart" => MediaKinds.Fanart,
            "steamgrid" => MediaKinds.SteamGrid,
            "mix" or "mixrbv2" => MediaKinds.MixRbv2,
            "mixrbv1" => MediaKinds.MixRbv1,
            _ => string.Empty
        };
    }

    private static int GetEssentialKindPriority(string kind, MediaProjectionPlan plan)
    {
        var normalized = MediaKinds.Normalize(kind);
        if (string.Equals(normalized, ResolveSelectionSourceToKind(plan.PreferredImageSource, MediaSelectionTarget.Image), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(normalized, ResolveSelectionSourceToKind(plan.PreferredLogoSource, MediaSelectionTarget.Logo), StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(normalized, ResolveSelectionSourceToKind(plan.PreferredThumbnailSource, MediaSelectionTarget.Thumbnail), StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        return normalized switch
        {
            MediaKinds.Image => 3,
            MediaKinds.Logo => 2,
            MediaKinds.WheelCarbon => 3,
            MediaKinds.WheelSteel => 4,
            MediaKinds.Thumbnail => 5,
            MediaKinds.Fanart => 6,
            MediaKinds.Marquee => 7,
            _ => 50
        };
    }

    private async Task<TextPersistTrace> PersistTextAsync(
        MediaProjectionPlan plan,
        ScreenScraperGameCatalog catalog,
        string requestedLanguage,
        CancellationToken cancellationToken)
    {
        var textSlug = string.IsNullOrWhiteSpace(plan.TextSourceGameSlug) ? plan.GameSlug : plan.TextSourceGameSlug;
        var allFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = false;
        var persistedRating = string.Empty;
        var persistedBundles = 0;

        foreach (var language in catalog.BuildPersistLanguages(requestedLanguage))
        {
            var fields = catalog.BuildFieldsForLanguage(language);
            if (fields.Count == 0)
            {
                continue;
            }

            persistedBundles++;
            foreach (var field in fields.Keys)
            {
                allFields.Add($"{language}:{field}");
            }

            if (string.IsNullOrWhiteSpace(persistedRating) &&
                fields.TryGetValue("rating", out var bundleRating))
            {
                persistedRating = bundleRating;
            }

            updated |= await _localizedTextStore.PersistFieldsAsync(
                plan.SystemId,
                textSlug,
                language,
                fields,
                cancellationToken);
        }

        var translation = await _descriptionTranslationService.ScheduleFromScrapeAsync(
            plan,
            textSlug,
            requestedLanguage,
            catalog.ResolveSynopsis(requestedLanguage),
            catalog.ResolveSynopsis("en"),
            catalog.HasScrapedEvidence(),
            cancellationToken);
        if (translation.PendingQueued)
        {
            allFields.Add($"{LocalizedMetadataBundleNormalizer.NormalizeLanguage(requestedLanguage)}:desc:translation-pending");
        }

        if (translation.TranslationApplied)
        {
            updated = true;
            allFields.Add($"{LocalizedMetadataBundleNormalizer.NormalizeLanguage(requestedLanguage)}:desc:translateLocally");
        }

        return new TextPersistTrace
        {
            Updated = updated,
            RawRating = catalog.RawRating,
            NormalizedRating = catalog.Rating,
            PersistedRating = persistedRating,
            FieldCount = persistedBundles,
            Fields = allFields.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private async Task<string> TryDownloadMediaAsync(string mediaUrl, string kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl))
        {
            return string.Empty;
        }

        try
        {
            await _capabilityService.WaitForRemoteRequestSlotAsync(cancellationToken);
            using var response = await _httpClient.GetAsync(mediaUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var contentType = response.Content.Headers.ContentType;
            var extension = ResolveExtension(kind, contentType, mediaUrl);
            var tempDirectory = Path.Combine(RetroBatPaths.PluginRoot, "temp", "screenscraper");
            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}{extension}");

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = File.Create(tempPath))
            {
                await stream.CopyToAsync(file, cancellationToken);
            }

            var info = new FileInfo(tempPath);
            if (!info.Exists ||
                info.Length <= 64 ||
                IsNoMediaFile(tempPath) ||
                !HasExpectedMediaSignature(tempPath, kind))
            {
                TryDeleteTempFile(tempPath);
                return string.Empty;
            }

            return tempPath;
        }
        catch (Exception ex) when (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "ScreenScraper media download failed for kind={Kind}.", kind);
            return string.Empty;
        }
    }

    private string BuildCatalogUrl(
        ScreenScraperConnectionInfo connection,
        string screenScraperSystemId,
        MediaProjectionPlan plan,
        string romName)
    {
        var parameters = BuildCommonParameters(connection);
        parameters["output"] = "json";
        parameters["systemeid"] = screenScraperSystemId;
        parameters["romnom"] = romName;
        parameters["romtype"] = plan.IsFolderBasedSystem ? "dossier" : "rom";

        if (File.Exists(plan.GamePath))
        {
            parameters["romtaille"] = new FileInfo(plan.GamePath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(plan.GamelistMd5))
        {
            parameters["md5"] = plan.GamelistMd5.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(plan.GamelistCrc32))
        {
            parameters["crc"] = plan.GamelistCrc32.Trim();
        }

        return BuildUrl(connection.BaseUrl, "jeuInfos.php", parameters);
    }

    private string BuildMediaDownloadUrl(
        ScreenScraperConnectionInfo connection,
        string screenScraperSystemId,
        string gameId,
        string mediaType,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(mediaType))
        {
            return string.Empty;
        }

        var endpoint = MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Manual => "mediaManuelJeu.php",
            MediaKinds.Video or MediaKinds.VideoNormalized => "mediaVideoJeu.php",
            _ => "mediaJeu.php"
        };

        var parameters = BuildCommonParameters(connection);
        parameters["systemeid"] = screenScraperSystemId;
        parameters["jeuid"] = gameId;
        parameters["media"] = mediaType;
        return BuildUrl(connection.BaseUrl, endpoint, parameters);
    }

    private static Dictionary<string, string> BuildCommonParameters(ScreenScraperConnectionInfo connection)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["devid"] = connection.DevId,
            ["devpassword"] = connection.DevPassword,
            ["softname"] = connection.SoftName,
            ["ssid"] = connection.User,
            ["sspassword"] = connection.Password
        };
    }

    private static string BuildUrl(string baseUrl, string endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            "&",
            parameters
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"{baseUrl.TrimEnd('/')}/{endpoint}?{query}";
    }

    private static IEnumerable<string> BuildCatalogRomNames(MediaProjectionPlan plan)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[]
                 {
                     Path.GetFileName(plan.GamePath),
                     plan.DisplayName,
                     Path.GetFileNameWithoutExtension(plan.GamePath),
                     plan.GameSlug
                 })
        {
            var normalized = (candidate ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            yield return normalized;
        }
    }

    private IReadOnlyList<string> ResolveMediaTypes(
        MediaProjectionPlan plan,
        string kind,
        string bezelAspectRatio,
        string bezelOrientation)
    {
        var normalizedKind = MediaKinds.Normalize(kind);
        var configuredMediaTypes = ResolveConfiguredScreenScraperMediaTypes(normalizedKind);
        if (configuredMediaTypes.Count > 0)
        {
            return ExpandLocalizedMediaTypes(plan, normalizedKind, configuredMediaTypes);
        }

        if (string.Equals(normalizedKind, MediaKinds.Bezel, StringComparison.OrdinalIgnoreCase))
        {
            return ExpandBezelMediaTypes(plan, ResolveBezelMediaType(bezelAspectRatio, bezelOrientation));
        }

        var baseTypes = MediaTypeCandidatesByKind.TryGetValue(normalizedKind, out var mediaTypes)
            ? mediaTypes
            : [normalizedKind];
        return ExpandLocalizedMediaTypes(plan, normalizedKind, baseTypes);
    }

    private IReadOnlyList<string> ExpandLocalizedMediaTypes(MediaProjectionPlan plan, string normalizedKind, IReadOnlyList<string> baseTypes)
    {
        if (!ShouldLocalizeMediaKind(normalizedKind))
        {
            return baseTypes;
        }

        var priorityRegions = _mediaLocalizationResolver.BuildMediaRegionPriority(plan, normalizedKind);
        var preferredRegion = priorityRegions.FirstOrDefault() ?? ResolvePreferredScreenScraperRegion();
        var regions = BuildStrictVisualRegionFallback(priorityRegions, preferredRegion);
        var relaxedFallbackRegions = BuildRelaxedVisualRegionFallback(normalizedKind, regions);

        var expanded = new List<string>();
        foreach (var mediaType in baseTypes)
        {
            foreach (var region in regions)
            {
                if (!string.IsNullOrWhiteSpace(region))
                {
                    expanded.Add($"{mediaType}({region})");
                }
            }

            expanded.Add($"{mediaType}(wor)");
            expanded.Add(mediaType);

            foreach (var region in relaxedFallbackRegions)
            {
                expanded.Add($"{mediaType}({region})");
            }
        }

        return expanded
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildStrictVisualRegionFallback(
        IReadOnlyList<string> priorityRegions,
        string preferredRegion)
    {
        var seedRegions = priorityRegions
            .Concat([preferredRegion])
            .Select(NormalizeScreenScraperRegionToken)
            .Where(IsAllowedVisualScreenScraperRegion)
            .ToList();
        var exactRegion = seedRegions.FirstOrDefault(region =>
            !string.Equals(region, "wor", StringComparison.OrdinalIgnoreCase));
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(exactRegion))
        {
            AddRegion(result, exactRegion);
            if (IsEuropeanCountryRegion(exactRegion))
            {
                AddRegion(result, "eu");
            }
        }

        AddRegion(result, "wor");
        return result;
    }

    private static string NormalizeScreenScraperRegionToken(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace("screenscraper", "ss", StringComparison.OrdinalIgnoreCase)
            .Replace("screen_scraper", "ss", StringComparison.OrdinalIgnoreCase)
            .Replace("world", "wor", StringComparison.OrdinalIgnoreCase)
            .Replace("europe", "eu", StringComparison.OrdinalIgnoreCase)
            .Replace("usa", "us", StringComparison.OrdinalIgnoreCase)
            .Replace("japan", "jp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedVisualScreenScraperRegion(string region)
    {
        return !string.IsNullOrWhiteSpace(region) &&
            region is not "ss" and not "auto" and not "cus" and not "mor" &&
            !region.Contains('-', StringComparison.Ordinal);
    }

    private static bool IsEuropeanCountryRegion(string region)
    {
        return region is "fr" or "de" or "it" or "nl" or "uk" or "sp" or "es";
    }

    private static IReadOnlyList<string> BuildRelaxedVisualRegionFallback(string normalizedKind, IReadOnlyList<string> strictRegions)
    {
        if (!IsLogoWheelFamily(normalizedKind))
        {
            return [];
        }

        var relaxed = new List<string>();
        foreach (var region in new[]
                 {
                     "us",
                     "jp",
                     "eu",
                     "fr",
                     "de",
                     "it",
                     "sp",
                     "es",
                     "uk",
                     "nl",
                     "br",
                     "kr",
                     "cn",
                     "asi",
                     "au"
                 })
        {
            if (!strictRegions.Contains(region, StringComparer.OrdinalIgnoreCase))
            {
                AddRegion(relaxed, region);
            }
        }

        return relaxed;
    }

    private static bool IsLogoWheelFamily(string normalizedKind)
    {
        return normalizedKind is
            MediaKinds.Logo or
            MediaKinds.Wheel or
            MediaKinds.WheelCarbon or
            MediaKinds.WheelSteel;
    }

    private static void AddRegion(List<string> regions, string region)
    {
        if (!string.IsNullOrWhiteSpace(region) &&
            !regions.Contains(region, StringComparer.OrdinalIgnoreCase))
        {
            regions.Add(region);
        }
    }

    private IReadOnlyList<string> ExpandBezelMediaTypes(MediaProjectionPlan plan, string mediaType)
    {
        var normalized = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "bezel-16-9";
        }

        // Bezels intentionally only fall back across region variants. They must
        // never silently switch aspect ratio or orientation behind the user's choice.
        var regions = _mediaLocalizationResolver.BuildBezelRegionPriority(plan);
        return regions
            .Select(region => string.IsNullOrWhiteSpace(region) ? string.Empty : $"{normalized}({region})")
            .Concat([$"{normalized}(wor)", normalized])
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ResolvePreferredScreenScraperRegion()
    {
        var configured = (_options.CurrentValue.MediaAllocation.UserRegion ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        var language = (_settingsService.GetScrapingSettings().Language ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('_', '-');
        return _taxonomy.ResolvePreferredScreenScraperRegion(configured, language);
    }

    private static bool ShouldLocalizeMediaKind(string normalizedKind)
    {
        return normalizedKind is
            MediaKinds.Image or
            MediaKinds.Thumbnail or
            MediaKinds.Logo or
            MediaKinds.Wheel or
            MediaKinds.WheelCarbon or
            MediaKinds.WheelSteel or
            MediaKinds.Marquee or
            MediaKinds.ScreenMarquee or
            MediaKinds.ScreenMarqueeSmall or
            MediaKinds.MixRbv1 or
            MediaKinds.MixRbv2 or
            MediaKinds.BoxFront or
            MediaKinds.BoxSide or
            MediaKinds.BoxTexture or
            MediaKinds.Box3d or
            MediaKinds.Cartridge or
            MediaKinds.Label or
            MediaKinds.BoxBack or
            MediaKinds.Manual or
            MediaKinds.Map or
            MediaKinds.Magazine;
    }

    private bool IsAllScreenScraperRegionMode()
    {
        return string.Equals(
            _options.CurrentValue.MediaAllocation.MediaRegionMode,
            "all",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetMediaTypeRegion(string mediaType)
    {
        var value = (mediaType ?? string.Empty).Trim();
        var open = value.LastIndexOf('(');
        var close = value.LastIndexOf(')');
        if (open < 0 || close <= open + 1)
        {
            return string.Empty;
        }

        return value[(open + 1)..close].Trim().ToLowerInvariant();
    }

    private static string? ResolveLocalizedMediaVariantPath(
        string systemId,
        string gameSlug,
        string kind,
        string region,
        string sourcePath)
    {
        var safeRegion = SafePathSegment(region).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeRegion))
        {
            return null;
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var (directory, stem) = kind switch
        {
            MediaKinds.Image => (Path.Combine(systemId, "games", gameSlug, "artwork"), "screentitle"),
            MediaKinds.Thumbnail => (Path.Combine(systemId, "games", gameSlug, "artwork"), "screenshot"),
            MediaKinds.Logo or MediaKinds.Wheel => (Path.Combine(systemId, "games", gameSlug, "ui", "wheels"), "wheel"),
            MediaKinds.WheelCarbon => (Path.Combine(systemId, "games", gameSlug, "ui", "wheels"), "wheel-carbon"),
            MediaKinds.WheelSteel => (Path.Combine(systemId, "games", gameSlug, "ui", "wheels"), "wheel-steel"),
            MediaKinds.Marquee => (Path.Combine(systemId, "games", gameSlug, "artwork", "marquee"), "marquee"),
            MediaKinds.ScreenMarquee => (Path.Combine(systemId, "games", gameSlug, "artwork", "marquee"), "screenmarquee"),
            MediaKinds.ScreenMarqueeSmall => (Path.Combine(systemId, "games", gameSlug, "artwork", "marquee"), "screenmarquee-small"),
            MediaKinds.SteamGrid => (Path.Combine(systemId, "games", gameSlug, "ui"), "steamgrid"),
            MediaKinds.MixRbv1 => (Path.Combine(systemId, "games", gameSlug, "artwork", "mix"), "mixrbv1"),
            MediaKinds.MixRbv2 => (Path.Combine(systemId, "games", gameSlug, "artwork", "mix"), "mixrbv2"),
            MediaKinds.BoxFront => (Path.Combine(systemId, "games", gameSlug, "artwork", "box"), "front"),
            MediaKinds.BoxSide => (Path.Combine(systemId, "games", gameSlug, "artwork", "box"), "side"),
            MediaKinds.BoxTexture => (Path.Combine(systemId, "games", gameSlug, "artwork", "box"), "texture"),
            MediaKinds.Box3d => (Path.Combine(systemId, "games", gameSlug, "artwork", "box"), "3d"),
            MediaKinds.Cartridge => (Path.Combine(systemId, "games", gameSlug, "artwork"), "cartridge"),
            MediaKinds.Label => (Path.Combine(systemId, "games", gameSlug, "artwork"), "label"),
            MediaKinds.Figurine => (Path.Combine(systemId, "games", gameSlug, "artwork"), "figurine"),
            MediaKinds.BoxBack => (Path.Combine(systemId, "games", gameSlug, "artwork", "box"), "back"),
            MediaKinds.Map => (Path.Combine(systemId, "games", gameSlug, "documents", "maps"), "map"),
            MediaKinds.Manual => (Path.Combine(systemId, "games", gameSlug, "documents"), "manual"),
            MediaKinds.Magazine => (Path.Combine(systemId, "games", gameSlug, "documents"), "magazine"),
            _ => (Path.Combine(systemId, "games", gameSlug), MediaKinds.Normalize(kind))
        };

        return Path.Combine(
            RetroBatPaths.MediaSystemsRoot,
            directory,
            $"{stem}-{safeRegion}{extension}");
    }

    private static string SafePathSegment(string value)
    {
        var cleaned = string.Concat((value ?? string.Empty)
            .Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }

    private static bool HaveSameContent(string leftPath, string rightPath)
    {
        try
        {
            var leftInfo = new FileInfo(leftPath);
            var rightInfo = new FileInfo(rightPath);
            if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using var leftStream = File.Open(leftPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rightStream = File.Open(rightPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return sha.ComputeHash(leftStream).SequenceEqual(sha.ComputeHash(rightStream));
        }
        catch
        {
            return false;
        }
    }

    private static async Task CopyFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return;
        }

        Directory.CreateDirectory(destinationDirectory);
        var tempPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var sourceStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var targetStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await sourceStream.CopyToAsync(targetStream, cancellationToken);
                await targetStream.FlushAsync(cancellationToken);
            }

            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private IReadOnlyList<string> ResolveConfiguredScreenScraperMediaTypes(string normalizedKind)
    {
        var deployments = _options.CurrentValue.ThemeDeployments;
        if (!deployments.Enabled)
        {
            return Array.Empty<string>();
        }

        return deployments.Rules
            .Where(rule => rule.Enabled &&
                string.Equals(MediaKinds.Normalize(rule.MediaKind), normalizedKind, StringComparison.OrdinalIgnoreCase))
            .SelectMany(rule => rule.ScreenScraperMediaTypes)
            .Select(type => (type ?? string.Empty).Trim())
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ResolveBezelMediaType(string aspectRatio, string orientation)
    {
        var aspect = NormalizeBezelAspectRatio(aspectRatio);
        var orient = _taxonomy.ResolveBezelOrientation(orientation, _options.CurrentValue.RomSetManager.ScreenOrientation);
        var suffix = orient switch
        {
            "vertical" => "-v",
            "cocktail" => "-cocktail",
            _ => string.Empty
        };

        return $"bezel-{aspect}{suffix}";
    }

    private static string NormalizeBezelAspectRatio(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "4:3" or "4-3" or "43" => "4-3",
            "16:9" or "16-9" or "169" or "" => "16-9",
            _ => "16-9"
        };
    }

    private static ScreenScraperGameCatalog? ParseCatalog(JsonElement root, string requestedLanguage)
    {
        if (!TryGetProperty(root, "response", out var response))
        {
            return null;
        }

        JsonElement game;
        if (TryGetProperty(response, "jeu", out var singleGame))
        {
            game = singleGame;
        }
        else if (TryGetProperty(response, "jeux", out var games) &&
                 games.ValueKind == JsonValueKind.Array &&
                 games.GetArrayLength() > 0)
        {
            game = games[0];
        }
        else
        {
            return null;
        }

        if (game.ValueKind == JsonValueKind.Array && game.GetArrayLength() > 0)
        {
            game = game[0];
        }

        if (game.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rawRating = ReadString(game, "score", "note");
        var catalog = new ScreenScraperGameCatalog
        {
            GameId = ReadString(game, "id", "idjeu", "ss_id"),
            Name = ReadLocalizedString(game, "noms", "nom") ?? ReadString(game, "nom", "name"),
            ReleaseDate = NormalizeReleaseDate(ReadFirstNestedString(game, "dates", "date", "text", "value")),
            Developer = ReadLocalizedString(game, "developpeur", "nom") ?? ReadString(game, "developpeur"),
            Publisher = ReadLocalizedString(game, "editeur", "nom") ?? ReadString(game, "editeur"),
            Players = ReadFirstNestedString(game, "joueurs", "text", "nombre", "players"),
            Genre = ReadLocalizedCollection(game, "genres", "genre", requestedLanguage),
            Family = ReadLocalizedCollection(game, "familles", "famille", requestedLanguage),
            Region = ReadRegion(game),
            RomRegions = ReadRomRegions(game).ToList(),
            RomLanguages = ReadRomLanguages(game).ToList(),
            RawRating = rawRating,
            Rating = NormalizeScreenScraperRating(rawRating)
        };

        catalog.SynopsisByLanguage = ReadLocalizedDictionary(game, "synopsis", "text", "synopsis");
        catalog.GenreByLanguage = ReadLocalizedCollectionsByLanguage(game, "genres", "genre", "genre");
        catalog.FamilyByLanguage = ReadLocalizedCollectionsByLanguage(game, "familles", "famille", "family");
        catalog.MediaUrlsByType = ReadMediaUrls(game);
        return catalog;
    }

    private static Dictionary<string, string> ReadMediaUrls(JsonElement game)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetProperty(game, "medias", out var medias))
        {
            return result;
        }

        if (medias.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in medias.EnumerateObject())
            {
                var url = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : ReadString(property.Value, "url", "url_media");
                AddCatalogMediaUrl(result, property.Name, url);
            }

            return result;
        }

        if (medias.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var media in medias.EnumerateArray())
        {
            var type = ReadString(media, "type", "media", "nom");
            var url = ReadString(media, "url", "url_media", "download");
            var region = ReadString(media, "region", "regionshortname", "regions_shortname");
            AddCatalogMediaUrl(result, AppendMediaRegion(type, region), url);
        }

        return result;
    }

    private static void AddCatalogMediaUrl(Dictionary<string, string> result, string mediaType, string? url)
    {
        var normalizedType = RemoveMediaTypeRegion(mediaType);
        var region = TryGetMediaTypeRegion(mediaType);
        if (string.IsNullOrWhiteSpace(region))
        {
            region = TryGetMediaTypeRegion(ExtractMediaParameterFromUrl(url));
        }

        region = NormalizeScreenScraperRegionToken(region);
        if (!string.IsNullOrWhiteSpace(region))
        {
            if (!IsAllowedVisualScreenScraperRegion(region))
            {
                return;
            }

            normalizedType = string.IsNullOrWhiteSpace(normalizedType)
                ? RemoveMediaTypeRegion(ExtractMediaParameterFromUrl(url))
                : normalizedType;
            if (string.IsNullOrWhiteSpace(normalizedType) || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            result[$"{normalizedType}({region})"] = url.Trim();
            return;
        }

        if (!string.IsNullOrWhiteSpace(normalizedType) && !string.IsNullOrWhiteSpace(url))
        {
            result[normalizedType] = url.Trim();
        }
    }

    private static string AppendMediaRegion(string mediaType, string region)
    {
        var normalizedType = (mediaType ?? string.Empty).Trim();
        var normalizedRegion = NormalizeScreenScraperRegionToken(region);
        return string.IsNullOrWhiteSpace(normalizedType) || string.IsNullOrWhiteSpace(normalizedRegion)
            ? normalizedType
            : $"{normalizedType}({normalizedRegion})";
    }

    private static string RemoveMediaTypeRegion(string mediaType)
    {
        var value = (mediaType ?? string.Empty).Trim();
        var open = value.LastIndexOf('(');
        var close = value.LastIndexOf(')');
        return open >= 0 && close > open
            ? value[..open].Trim()
            : value;
    }

    private static string ExtractMediaParameterFromUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        var queryStart = value.IndexOf('?');
        if (queryStart < 0 || queryStart >= value.Length - 1)
        {
            return string.Empty;
        }

        foreach (var part in value[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..equals]);
            if (key.Equals("media", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(equals + 1)..]);
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, string> ReadLocalizedDictionary(JsonElement game, string propertyName, params string[] valueKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return result;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    AddTextField(result, property.Name, property.Value.GetString());
                    continue;
                }

                var language = ReadString(property.Value, "langue", "lang", "language");
                var value = ReadString(property.Value, valueKeys);
                AddTextField(result, string.IsNullOrWhiteSpace(language) ? property.Name : language, value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                var language = ReadString(entry, "langue", "lang", "language");
                var value = ReadString(entry, valueKeys);
                AddTextField(result, string.IsNullOrWhiteSpace(language) ? "en" : language, value);
            }
        }

        return result;
    }

    private static string? ReadLocalizedString(JsonElement game, string propertyName, string valueKey)
    {
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return null;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return node.GetString();
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                var value = ReadString(entry, valueKey, "text", "nom");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            return ReadString(node, valueKey, "text", "nom");
        }

        return null;
    }

    private static string ReadLocalizedCollection(JsonElement game, string propertyName, string singularName, string requestedLanguage)
    {
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return string.Empty;
        }

        var fieldName = string.Equals(propertyName, "familles", StringComparison.OrdinalIgnoreCase)
            ? "family"
            : "genre";

        foreach (var language in BuildStrictCollectionLanguageOrder(requestedLanguage))
        {
            var values = ReadLocalizedCollectionValues(node, singularName, language)
                .Where(IsUsefulCollectionLabel)
                .Select(value => LocalizedMetadataSanitizer.SanitizeField(fieldName, value, language))
                .Where(IsUsefulCollectionLabel)
                .ToList();
            if (values.Count > 0)
            {
                return JoinDistinctValues(values);
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, string> ReadLocalizedCollectionsByLanguage(
        JsonElement game,
        string propertyName,
        string singularName,
        string fieldName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return result;
        }

        foreach (var language in EnumerateCommonTextLanguages())
        {
            var values = ReadLocalizedCollectionValues(node, singularName, language)
                .Where(IsUsefulCollectionLabel)
                .Select(value => LocalizedMetadataSanitizer.SanitizeField(fieldName, value, language))
                .Where(IsUsefulCollectionLabel)
                .ToList();
            if (values.Count > 0)
            {
                result[language] = JoinDistinctValues(values);
            }
        }

        return result;
    }

    private static IEnumerable<string> ReadLocalizedCollectionValues(JsonElement node, string singularName, string? language)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                if (!string.IsNullOrWhiteSpace(language))
                {
                    yield break;
                }

                var text = WebUtility.HtmlDecode(node.GetString() ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
                yield break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                yield break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    foreach (var value in ReadLocalizedCollectionValues(item, singularName, language))
                    {
                        yield return value;
                    }
                }
                yield break;

            case JsonValueKind.Object:
                if (!string.IsNullOrWhiteSpace(language))
                {
                    foreach (var key in BuildLocalizedCollectionKeys(singularName, language))
                    {
                        if (!TryGetProperty(node, key, out var localizedValue))
                        {
                            continue;
                        }

                        foreach (var nested in ReadLocalizedCollectionValues(localizedValue, singularName, language, allowLanguageAgnosticValue: true))
                        {
                            yield return nested;
                        }
                    }

                    var declaredLanguage = ReadString(node, "langue", "lang", "language");
                    if (!string.IsNullOrWhiteSpace(declaredLanguage))
                    {
                        if (IsSameLanguage(declaredLanguage, language))
                        {
                            var declaredValue = ReadString(node, "text", "value", "nom", "name", "libelle", "label");
                            if (!string.IsNullOrWhiteSpace(declaredValue))
                            {
                                yield return declaredValue;
                            }
                        }

                        yield break;
                    }

                    foreach (var key in BuildLocalizedCollectionContainerKeys(singularName))
                    {
                        if (!TryGetProperty(node, key, out var value))
                        {
                            continue;
                        }

                        foreach (var nested in ReadLocalizedCollectionValues(value, singularName, language))
                        {
                            yield return nested;
                        }
                    }

                    yield break;
                }

                foreach (var key in BuildCollectionKeys(singularName))
                {
                    if (!TryGetProperty(node, key, out var value))
                    {
                        continue;
                    }

                    foreach (var nested in ReadLocalizedCollectionValues(value, singularName, language))
                    {
                        yield return nested;
                    }
                }

                foreach (var property in node.EnumerateObject())
                {
                    var name = property.Name.Trim().ToLowerInvariant();
                    if (IsIgnoredCollectionProperty(name) ||
                        IsOtherLanguageCollectionProperty(name, language) ||
                        name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("media", StringComparison.OrdinalIgnoreCase) ||
                        BuildCollectionKeys(singularName).Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var nested in ReadLocalizedCollectionValues(property.Value, singularName, language))
                    {
                        yield return nested;
                    }
                }
                yield break;

            default:
                yield break;
        }
    }

    private static IEnumerable<string> ReadLocalizedCollectionValues(
        JsonElement node,
        string singularName,
        string? language,
        bool allowLanguageAgnosticValue)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                if (!string.IsNullOrWhiteSpace(language) && !allowLanguageAgnosticValue)
                {
                    yield break;
                }

                var text = WebUtility.HtmlDecode(node.GetString() ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }

                yield break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    foreach (var value in ReadLocalizedCollectionValues(item, singularName, language, allowLanguageAgnosticValue))
                    {
                        yield return value;
                    }
                }

                yield break;

            default:
                foreach (var value in ReadLocalizedCollectionValues(node, singularName, language))
                {
                    yield return value;
                }

                yield break;
        }
    }

    private static IEnumerable<string> BuildLocalizedCollectionKeys(string singularName, string language)
    {
        yield return language;
        yield return $"{singularName}_{language}";
        yield return $"nom_{language}";
        yield return $"name_{language}";
        yield return $"label_{language}";
        yield return $"libelle_{language}";
    }

    private static IEnumerable<string> BuildLocalizedCollectionContainerKeys(string singularName)
    {
        yield return singularName;
        yield return "noms";
        yield return "names";
    }

    private static IEnumerable<string> BuildStrictCollectionLanguageOrder(string requestedLanguage)
    {
        var language = (requestedLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (language.Length >= 2)
        {
            language = language[..2];
            yield return language;
        }

        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            yield return "us";
        }
    }

    private static IEnumerable<string> BuildCollectionKeys(string singularName)
    {
        yield return singularName;
        yield return "noms";
        yield return "names";
        yield return "nom";
        yield return "name";
        yield return "text";
        yield return "value";
        yield return "libelle";
        yield return "label";
    }

    private static bool IsOtherLanguageCollectionProperty(string propertyName, string? requestedLanguage)
    {
        if (string.IsNullOrWhiteSpace(requestedLanguage))
        {
            return false;
        }

        var normalizedName = (propertyName ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        if (normalizedName.Length < 2)
        {
            return false;
        }

        var requested = requestedLanguage.Trim().ToLowerInvariant();
        requested = requested.Length >= 2 ? requested[..2] : requested;
        var candidate = normalizedName.Length == 2
            ? normalizedName
            : normalizedName.Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

        return candidate.Length == 2 &&
            IsUsefulCollectionLabel(candidate) == false &&
            !string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredCollectionProperty(string propertyName)
    {
        return propertyName is "id" or "langue" or "lang" or "language" or "region" or "regions" or "type";
    }

    private static bool IsSameLanguage(string? left, string? right)
    {
        var normalizedLeft = (left ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedRight = (right ?? string.Empty).Trim().ToLowerInvariant();
        return normalizedLeft.Length >= 2 &&
            normalizedRight.Length >= 2 &&
            string.Equals(normalizedLeft[..2], normalizedRight[..2], StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsefulCollectionLabel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.All(char.IsDigit))
        {
            return false;
        }

        return normalized.Length != 2 || !BuildLanguageFallbackOrder(normalized).Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadRegion(JsonElement game)
    {
        var direct = ReadString(game, "region");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var shortNames = ReadFirstNestedString(game, "regionshortnames", "regionshortname", "nomcourt", "id");
        if (!string.IsNullOrWhiteSpace(shortNames))
        {
            return shortNames;
        }

        var romRegions = ReadFirstNestedString(game, "roms", "romregions", "regionshortname", "region", "nomcourt");
        return romRegions;
    }

    private static IEnumerable<string> ReadRomRegions(JsonElement game)
    {
        foreach (var value in ReadRomNestedValues(game, "regions", "region", "regions_shortname", "romregions", "regionshortname", "nomcourt"))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> ReadRomLanguages(JsonElement game)
    {
        foreach (var value in ReadRomNestedValues(game, "langues", "langue", "langues_shortname", "languages", "language"))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> ReadRomNestedValues(JsonElement game, params string[] valueKeys)
    {
        if (TryGetProperty(game, "rom", out var rom))
        {
            foreach (var value in ReadNestedValues(rom, valueKeys))
            {
                yield return value;
            }
        }

        if (!TryGetProperty(game, "roms", out var roms))
        {
            yield break;
        }

        foreach (var value in ReadNestedValues(roms, valueKeys))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> ReadNestedValues(JsonElement node, params string[] valueKeys)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            foreach (var value in SplitScreenScraperTokens(node.GetString()))
            {
                yield return value;
            }

            yield break;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                foreach (var value in ReadNestedValues(entry, valueKeys))
                {
                    yield return value;
                }
            }

            yield break;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var key in valueKeys)
        {
            if (!TryGetProperty(node, key, out var child))
            {
                continue;
            }

            foreach (var value in ReadNestedValues(child, valueKeys))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> ReadDirectNestedValues(JsonElement node, string[] rootKeys, string[] valueKeys)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var key in rootKeys)
        {
            if (!TryGetProperty(node, key, out var child))
            {
                continue;
            }

            foreach (var value in ReadNestedValues(child, valueKeys))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> SplitScreenScraperTokens(string? value)
    {
        return (value ?? string.Empty)
            .Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeScreenScraperRating(string value)
    {
        var raw = (value ?? string.Empty).Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
        {
            return string.Empty;
        }

        if (rating > 1)
        {
            rating /= 20.0;
        }

        rating = Math.Clamp(rating, 0, 1);
        return rating.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> BuildLanguageFallbackOrder(string requestedLanguage)
    {
        var language = (requestedLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (language.Length >= 2)
        {
            yield return language[..2];
        }

        yield return "en";
        yield return "us";
    }

    private static IEnumerable<string> EnumerateCommonTextLanguages()
    {
        yield return "en";
        yield return "fr";
        yield return "de";
        yield return "es";
        yield return "it";
        yield return "pt";
        yield return "nl";
        yield return "ja";
    }

    private static string JoinDistinctValues(IEnumerable<string> values)
    {
        return string.Join(
            ", ",
            values
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static void MergeDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(normalized);
            }
        }
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return WebUtility.HtmlDecode(value.GetString() ?? string.Empty).Trim();
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString().Trim();
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadString(value, "text", "value", "note", "score", "nom", "name");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return string.Empty;
    }

    private static string ReadFirstNestedString(JsonElement element, string propertyName, params string[] valueKeys)
    {
        if (!TryGetProperty(element, propertyName, out var node))
        {
            return string.Empty;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return WebUtility.HtmlDecode(node.GetString() ?? string.Empty).Trim();
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                var value = ReadString(entry, valueKeys);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            var direct = ReadString(node, valueKeys);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    return WebUtility.HtmlDecode(property.Value.GetString() ?? string.Empty).Trim();
                }

                var value = ReadString(property.Value, valueKeys);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeReleaseDate(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 10 && trimmed[4] == '-' && trimmed[7] == '-')
        {
            return trimmed.Replace("-", string.Empty) + "T000000";
        }

        if (trimmed.Length == 4 && trimmed.All(char.IsDigit))
        {
            return trimmed + "0101T000000";
        }

        return trimmed;
    }

    private static string ResolveExtension(string kind, MediaTypeHeaderValue? contentType, string mediaUrl)
    {
        if (string.Equals(MediaKinds.Normalize(kind), MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase))
        {
            return ".zip";
        }

        var byContentType = contentType?.MediaType?.Trim().ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "video/mp4" => ".mp4",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(byContentType))
        {
            return byContentType;
        }

        var pathExtension = Path.GetExtension(new Uri(mediaUrl).AbsolutePath).ToLowerInvariant();
        if (pathExtension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".mp4" or ".pdf" or ".zip")
        {
            return pathExtension;
        }

        return MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Manual => ".pdf",
            MediaKinds.Video or MediaKinds.VideoNormalized => ".mp4",
            MediaKinds.ThemeHb => ".zip",
            _ => ".png"
        };
    }

    private static bool IsNoMediaFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64)
            {
                return false;
            }

            var text = File.ReadAllText(path).Trim();
            return string.Equals(text, "NOMEDIA", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExpectedMediaSignature(string path, string kind)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var normalizedKind = MediaKinds.Normalize(kind);
            return normalizedKind switch
            {
                MediaKinds.Manual => StartsWith(bytes, "%PDF"u8.ToArray()),
                MediaKinds.Video or MediaKinds.VideoNormalized => IsMp4(bytes),
                MediaKinds.ThemeHb => StartsWith(bytes, [0x50, 0x4B, 0x03, 0x04]) ||
                    StartsWith(bytes, [0x50, 0x4B, 0x05, 0x06]) ||
                    StartsWith(bytes, [0x50, 0x4B, 0x07, 0x08]),
                _ => IsImage(bytes)
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool IsImage(byte[] bytes)
    {
        return StartsWith(bytes, [0x89, 0x50, 0x4E, 0x47]) ||
            StartsWith(bytes, [0xFF, 0xD8, 0xFF]) ||
            StartsWith(bytes, "GIF87a"u8.ToArray()) ||
            StartsWith(bytes, "GIF89a"u8.ToArray()) ||
            IsWebp(bytes);
    }

    private static bool IsWebp(byte[] bytes)
    {
        return bytes.Length >= 12 &&
            bytes[0] == 0x52 &&
            bytes[1] == 0x49 &&
            bytes[2] == 0x46 &&
            bytes[3] == 0x46 &&
            bytes[8] == 0x57 &&
            bytes[9] == 0x45 &&
            bytes[10] == 0x42 &&
            bytes[11] == 0x50;
    }

    private static bool IsMp4(byte[] bytes)
    {
        return bytes.Length >= 12 &&
            bytes[4] == 0x66 &&
            bytes[5] == 0x74 &&
            bytes[6] == 0x79 &&
            bytes[7] == 0x70;
    }

    private static bool StartsWith(byte[] bytes, byte[] signature)
    {
        if (bytes.Length < signature.Length)
        {
            return false;
        }

        for (var i = 0; i < signature.Length; i++)
        {
            if (bytes[i] != signature[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary download cleanup is best effort.
        }
    }

    private static void AddTextField(IDictionary<string, string> fields, string name, string? value)
    {
        var normalizedName = (name ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedValue = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedName) && !string.IsNullOrWhiteSpace(normalizedValue))
        {
            fields[normalizedName] = normalizedValue;
        }
    }

    private sealed class ScreenScraperGameCatalog
    {
        public string GameId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;
        public string Developer { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Players { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public List<string> RomRegions { get; set; } = new();
        public List<string> RomLanguages { get; set; } = new();
        public string RawRating { get; set; } = string.Empty;
        public string Rating { get; set; } = string.Empty;
        public Dictionary<string, string> SynopsisByLanguage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> GenreByLanguage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> FamilyByLanguage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> MediaUrlsByType { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string FindMediaUrl(string mediaType)
        {
            return MediaUrlsByType.TryGetValue(mediaType, out var url)
                ? url
                : string.Empty;
        }

        public string ResolveSynopsis(string requestedLanguage)
        {
            var normalized = NormalizeLanguage(requestedLanguage);
            return SynopsisByLanguage.TryGetValue(normalized, out var synopsis) ? synopsis : string.Empty;
        }

        public bool HasScrapedEvidence()
        {
            return !string.IsNullOrWhiteSpace(GameId) ||
                !string.IsNullOrWhiteSpace(Name) ||
                !string.IsNullOrWhiteSpace(ReleaseDate) ||
                !string.IsNullOrWhiteSpace(Developer) ||
                !string.IsNullOrWhiteSpace(Publisher) ||
                !string.IsNullOrWhiteSpace(Rating) ||
                !string.IsNullOrWhiteSpace(Region) ||
                MediaUrlsByType.Count > 0 ||
                GenreByLanguage.Count > 0 ||
                FamilyByLanguage.Count > 0;
        }

        public IEnumerable<string> BuildPersistLanguages(string requestedLanguage)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string value)
            {
                var normalized = NormalizeLanguage(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    seen.Add(normalized);
                }
            }

            Add(requestedLanguage);
            foreach (var language in SynopsisByLanguage.Keys)
            {
                Add(language);
            }

            foreach (var language in GenreByLanguage.Keys)
            {
                Add(language);
            }

            foreach (var language in FamilyByLanguage.Keys)
            {
                Add(language);
            }

            return seen.OrderBy(static language => language, StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, string> BuildFieldsForLanguage(string language)
        {
            var normalizedLanguage = NormalizeLanguage(language);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddTextField(fields, "name", Name);
            AddTextField(fields, "desc", ResolveSynopsis(normalizedLanguage));
            AddTextField(fields, "releasedate", ReleaseDate);
            AddTextField(fields, "developer", Developer);
            AddTextField(fields, "publisher", Publisher);
            AddTextField(fields, "players", Players);
            AddTextField(fields, "genre", GenreByLanguage.TryGetValue(normalizedLanguage, out var genre) ? genre : string.Empty);
            AddTextField(fields, "family", FamilyByLanguage.TryGetValue(normalizedLanguage, out var family) ? family : string.Empty);
            AddTextField(fields, "region", Region);
            AddTextField(fields, "rating", Rating);
            fields["lang"] = ResolveRomLanguageField();
            AddTextField(fields, "source", "screenscraper");
            return fields;
        }

        private string ResolveRomLanguageField()
        {
            var languages = RomLanguages
                .SelectMany(SplitRomLanguageTokens)
                .Select(NormalizeRomLanguageToken)
                .Where(language => !string.IsNullOrWhiteSpace(language))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return languages.Count == 0 ? string.Empty : string.Join(", ", languages);
        }

        private static string NormalizeLanguage(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized.Length >= 2 ? normalized[..2] : "en";
        }

        private static IEnumerable<string> SplitRomLanguageTokens(string value)
        {
            return (value ?? string.Empty)
                .Split([',', ';', '/', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        private static string NormalizeRomLanguageToken(string value)
        {
            var normalized = (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace('_', '-');
            return normalized switch
            {
                "english" or "eng" or "en-us" or "en-gb" => "en",
                "french" or "fra" or "fre" or "fr-fr" => "fr",
                "german" or "deu" or "ger" or "de-de" => "de",
                "spanish" or "spa" or "es-es" or "sp" => "es",
                "italian" or "ita" or "it-it" => "it",
                "portuguese" or "por" or "pt-pt" or "pt-br" => "pt",
                "japanese" or "jpn" or "ja-jp" or "jp" => "ja",
                "dutch" or "nld" or "nl-nl" => "nl",
                "russian" or "rus" or "ru-ru" => "ru",
                "polish" or "pol" or "pl-pl" => "pl",
                "czech" or "ces" or "cze" or "cs-cz" => "cs",
                "turkish" or "tur" or "tr-tr" => "tr",
                "korean" or "kor" or "ko-kr" => "ko",
                "chinese" or "chi" or "zho" or "zh-cn" or "zh-tw" => "zh",
                _ => normalized.Length >= 2 ? normalized[..2] : string.Empty
            };
        }
    }

    private sealed class TextPersistTrace
    {
        public bool Updated { get; set; }
        public string RawRating { get; set; } = string.Empty;
        public string NormalizedRating { get; set; } = string.Empty;
        public string PersistedRating { get; set; } = string.Empty;
        public int FieldCount { get; set; }
        public string[] Fields { get; set; } = Array.Empty<string>();
    }

    private enum MediaSelectionTarget
    {
        Image,
        Logo,
        Thumbnail
    }

    private sealed record RemoteMediaBatchResult(
        int DownloadedMediaCount,
        int ImportedMediaCount,
        int CollectionThemeAppliedCount,
        IReadOnlyCollection<string> ImportedKinds);

    private sealed record RemoteMediaKindResult(
        int DownloadedMediaCount,
        int ImportedMediaCount,
        int CollectionThemeAppliedCount,
        IReadOnlyCollection<string> ImportedKinds);
}

public sealed class ScreenScraperRemoteScrapeResult
{
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ScreenScraperGameId { get; set; } = string.Empty;
    public int DownloadedMediaCount { get; set; }
    public int ImportedMediaCount { get; set; }
    public bool TextUpdated { get; set; }
    public bool GamelistChanged { get; set; }
    public bool MediaContentChanged { get; set; }
    public bool MetadataChanged { get; set; }
    public bool LivePushed { get; set; }
    public bool ReloadRequested { get; set; }
    public bool RequiresGamelistPersistence { get; set; }
    public List<string> ImportedKinds { get; set; } = new();

    public static ScreenScraperRemoteScrapeResult Failed(string status, string message)
    {
        return new ScreenScraperRemoteScrapeResult
        {
            Status = status,
            Message = message
        };
    }
}
