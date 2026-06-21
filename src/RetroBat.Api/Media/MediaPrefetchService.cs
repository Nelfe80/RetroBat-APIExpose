using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Services;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Media;

public class MediaPrefetchService : IMediaPrefetchService
{
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly IMediaAliasStore _mediaAliasStore;
    private readonly ILocalizedTextStore _localizedTextStore;
    private readonly MediaNeedEvaluator _needEvaluator;
    private readonly EsProjectionService _projectionService;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly RemoteScrapingService _remoteScrapingService;
    private readonly MarqueeAutogenService _marqueeAutogenService;
    private readonly RemoteScrapeQueueService _remoteScrapeQueueService;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly MameGamelistGroupIndex _mameGamelistGroupIndex;
    private readonly ScreenScraperRawCacheMetadataService _rawCacheMetadataService;
    private readonly ILogger<MediaPrefetchService>? _logger;

    public MediaPrefetchService(
        SystemIdNormalizer systemIdNormalizer,
        GameNameNormalizer gameNameNormalizer,
        IMediaAliasStore mediaAliasStore,
        ILocalizedTextStore localizedTextStore,
        MediaNeedEvaluator needEvaluator,
        EsProjectionService projectionService,
        GamelistUpdateService gamelistUpdateService,
        RemoteScrapingService remoteScrapingService,
        MarqueeAutogenService marqueeAutogenService,
        RemoteScrapeQueueService remoteScrapeQueueService,
        ApiExposeRuntimeOptionsService runtimeOptions,
        EmulationStationSettingsService settingsService,
        IEmulationStationNotificationService notificationService,
        InterfaceTextService interfaceTextService,
        MameGamelistGroupIndex mameGamelistGroupIndex,
        ScreenScraperRawCacheMetadataService rawCacheMetadataService,
        ILogger<MediaPrefetchService>? logger = null)
    {
        _systemIdNormalizer = systemIdNormalizer;
        _gameNameNormalizer = gameNameNormalizer;
        _mediaAliasStore = mediaAliasStore;
        _localizedTextStore = localizedTextStore;
        _needEvaluator = needEvaluator;
        _projectionService = projectionService;
        _gamelistUpdateService = gamelistUpdateService;
        _remoteScrapingService = remoteScrapingService;
        _marqueeAutogenService = marqueeAutogenService;
        _remoteScrapeQueueService = remoteScrapeQueueService;
        _runtimeOptions = runtimeOptions;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _interfaceTextService = interfaceTextService;
        _mameGamelistGroupIndex = mameGamelistGroupIndex;
        _rawCacheMetadataService = rawCacheMetadataService;
        _logger = logger;
    }

    public Task<MediaPrefetchResult> PrefetchForSelectionAsync(GameReference game, CancellationToken cancellationToken = default)
    {
        return PrefetchForSelectionAsync(game, allowRemoteScrape: false, cancellationToken);
    }

    public Task<MediaPrefetchResult> PrefetchForSelectionAsync(GameReference game, bool allowRemoteScrape, CancellationToken cancellationToken = default)
    {
        return PrefetchForSelectionAsync(game, allowRemoteScrape, forceRemoteScrape: false, cancellationToken);
    }

    public Task<MediaPrefetchResult> PrefetchForSelectionAsync(GameReference game, bool allowRemoteScrape, bool forceRemoteScrape, CancellationToken cancellationToken = default)
    {
        return PrefetchForSelectionAsync(game, allowRemoteScrape, forceRemoteScrape, createUserVariantGuide: false, cancellationToken);
    }

    public Task<MediaPrefetchResult> PrefetchForSelectionAsync(
        GameReference game,
        bool allowRemoteScrape,
        bool forceRemoteScrape,
        bool createUserVariantGuide = false,
        CancellationToken cancellationToken = default)
    {
        return PrefetchForSelectionAsync(
            game,
            allowRemoteScrape,
            forceRemoteScrape,
            createUserVariantGuide,
            suppressImmediateGamelistUpdates: false,
            cancellationToken);
    }

    public async Task<MediaPrefetchResult> PrefetchForSelectionAsync(
        GameReference game,
        bool allowRemoteScrape,
        bool forceRemoteScrape,
        bool createUserVariantGuide,
        bool suppressImmediateGamelistUpdates,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeOptions.IsLocalMediaManagerEnabled())
        {
            return new MediaPrefetchResult
            {
                SystemId = game.SystemId,
                GameSlug = Path.GetFileNameWithoutExtension(game.GamePath ?? game.GameName ?? string.Empty),
                GamePathExists = File.Exists(game.GamePath ?? string.Empty)
            };
        }

        if (IsThemeArchiveSelection(game))
        {
            _logger?.LogInformation(
                "Media prefetch skipped for theme archive exposed as game-selected: system={SystemId}, path={GamePath}.",
                game.SystemId,
                game.GamePath);
            return new MediaPrefetchResult
            {
                SystemId = game.SystemId,
                GameSlug = Path.GetFileNameWithoutExtension(game.GamePath ?? game.GameName ?? string.Empty),
                GamePathExists = File.Exists(game.GamePath ?? string.Empty)
            };
        }

        var preparation = await PrepareLocalProjectionPlanCoreAsync(
            game,
            forceRemoteScrape,
            createUserVariantGuide,
            cancellationToken);
        var plan = preparation.Plan;
        plan.SuppressImmediateGamelistUpdates = suppressImmediateGamelistUpdates;
        var systemId = preparation.SystemId;
        var gameSlug = preparation.GameSlug;
        var frontendSystemId = plan.FrontendSystemId;
        var hadMissingLiveRefreshMediaAtSelection = preparation.HadMissingLiveRefreshMediaAtSelection;
        var scrapingSettings = _settingsService.GetScrapingSettings();
        var localVisibleMediaContentChanged = HasLocalLiveRefreshMediaContentChanged(plan, scrapingSettings.WheelStyle);
        var gamelistUpdate = GamelistEntryUpdateResult.NoChange;
        var gamelistMetadataChanged = false;
        if (!suppressImmediateGamelistUpdates)
        {
            var pendingQueuePersistence = _remoteScrapeQueueService.DrainPendingGamelistPersistence(frontendSystemId, game.GamePath);
            var gamelistPlans = pendingQueuePersistence.Count == 0
                ? new[] { plan }
                : new[] { plan }.Concat(pendingQueuePersistence).ToArray();
            gamelistUpdate = await _gamelistUpdateService.EnsureEntriesAsync(gamelistPlans, cancellationToken);
            gamelistMetadataChanged = gamelistUpdate.MetadataChanged;
            if (pendingQueuePersistence.Count > 0)
            {
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "remote-scrape-queue-persistence",
                    "gamelist",
                    gamelistUpdate.Changed ? "piggybacked" : "unchanged",
                    new
                    {
                        pendingCount = pendingQueuePersistence.Count,
                        gamelistUpdate.MediaContentChanged,
                        gamelistUpdate.MetadataChanged
                    },
                    cancellationToken);
            }
        }
        else
        {
            var pendingUpdate = await _gamelistUpdateService.StageExtendedEntriesAsync(plan, cancellationToken);
            gamelistMetadataChanged = pendingUpdate.MetadataChanged;
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "selection-gamelist-persistence",
                "gamelist",
                pendingUpdate.Changed ? "staged-extended" : "suppressed-unchanged",
                new
                {
                    reason = "game-selected",
                    localMediaContentChanged = plan.Needs.Any(need => need.WasContentChanged),
                    localVisibleMediaContentChanged,
                    hadMissingLiveRefreshMediaAtSelection,
                    pendingUpdate.MediaContentChanged,
                    pendingUpdate.MetadataChanged
                },
                cancellationToken);
        }
        var mediaContentChanged = gamelistUpdate.MediaContentChanged || localVisibleMediaContentChanged;
        var staleEsMediaResolved = hadMissingLiveRefreshMediaAtSelection &&
            !mediaContentChanged &&
            HasAnyProjectedLiveRefreshMediaAvailable(plan, scrapingSettings.WheelStyle);
        var visibleSlotResolved = !mediaContentChanged &&
            hadMissingLiveRefreshMediaAtSelection &&
            HasVisibleSlotResolvedAfterSelection(plan, scrapingSettings.WheelStyle);
        if (mediaContentChanged)
        {
            _logger?.LogDebug(
                "Local media projection changed media for system={SystemId}, game={GameSlug}; live ES addgames is deferred until after the remote scraping decision.",
                systemId,
                gameSlug);
        }
        else if (visibleSlotResolved)
        {
            _logger?.LogDebug(
                "Local media projection resolved a missing visible slot for system={SystemId}, game={GameSlug}; live ES addgames will be attempted after the remote scraping decision.",
                systemId,
                gameSlug);
        }
        else if (gamelistMetadataChanged)
        {
            _logger?.LogDebug(
                "Local metadata projection changed visible metadata for system={SystemId}, game={GameSlug}; live ES addgames is not attempted without a current-card media update.",
                systemId,
                gameSlug);
        }
        else if (staleEsMediaResolved)
        {
            _logger?.LogDebug(
                "Local media projection skipped live ES addgames although ES reported missing media because no media content or metadata change was detected for system={SystemId}, game={GameSlug}.",
                systemId,
                gameSlug);
        }
        else
        {
            _logger?.LogDebug(
                "Local media projection skipped live ES addgames because no visible media or media content change was detected for system={SystemId}, game={GameSlug}; gamelistChanged={GamelistChanged}, metadataChanged={MetadataChanged}.",
                systemId,
                gameSlug,
                gamelistUpdate.Changed,
                gamelistMetadataChanged);
        }

        var remoteDecision = allowRemoteScrape
            ? await _remoteScrapingService.EvaluateAfterLocalAsync(plan, forceRemoteScrape, cancellationToken)
            : new RemoteScrapeDecision
            {
                Enabled = false,
                Provider = "none",
                Status = "not-requested",
                Message = "Remote scraping was not requested for this prefetch.",
                SystemId = plan.SystemId,
                FrontendSystemId = plan.FrontendSystemId,
                GameSlug = plan.GameSlug
            };

        var selectionMarqueeAutogenResult = remoteDecision.ImportedKinds.Contains(MediaKinds.Marquee, StringComparer.OrdinalIgnoreCase)
            ? MarqueeAutogenResult.Skipped("remote-marquee-already-imported")
            : await _marqueeAutogenService.GenerateForSelectedGameAsync(plan, cancellationToken);
        if (selectionMarqueeAutogenResult.WasGenerated)
        {
            mediaContentChanged = true;
            if (!suppressImmediateGamelistUpdates)
            {
                var autogenGamelistUpdate = await _gamelistUpdateService.EnsureEntriesAsync(plan, cancellationToken);
                gamelistMetadataChanged = gamelistMetadataChanged || autogenGamelistUpdate.MetadataChanged;
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "selection-marquee-autogen-persistence",
                    "gamelist",
                    autogenGamelistUpdate.Changed ? "updated" : "unchanged",
                    new
                    {
                        selectionMarqueeAutogenResult.ImportedPath,
                        autogenGamelistUpdate.MediaContentChanged,
                        autogenGamelistUpdate.MetadataChanged,
                        afterRemoteDecision = true
                    },
                    cancellationToken);
            }
            else
            {
                var pendingAutogenUpdate = await _gamelistUpdateService.StageExtendedEntriesAsync(plan, cancellationToken);
                gamelistMetadataChanged = gamelistMetadataChanged || pendingAutogenUpdate.MetadataChanged;
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "selection-marquee-autogen-persistence",
                    "gamelist",
                    pendingAutogenUpdate.Changed ? "staged-extended" : "staged-extended-unchanged",
                    new
                    {
                        selectionMarqueeAutogenResult.ImportedPath,
                        pendingAutogenUpdate.MediaContentChanged,
                        pendingAutogenUpdate.MetadataChanged,
                        afterRemoteDecision = true
                    },
                    cancellationToken);
            }
        }

        if ((mediaContentChanged || visibleSlotResolved || gamelistMetadataChanged) && !remoteDecision.LivePushed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (allowRemoteScrape && !IsRemoteScrapeAttempted(remoteDecision))
            {
                await NotifyLocalScrapeStartedAsync(plan, cancellationToken);
            }

            var livePushed = await _gamelistUpdateService.PushLiveGameUpdateToEsAsync(
                plan,
                cancellationToken,
                LiveGameUpdateNotificationKind.LocalProjection,
                allowLocalizedMetadataRefresh: gamelistMetadataChanged);
            if (!livePushed)
            {
                _logger?.LogDebug(
                    "Local projection prepared visible media or localized metadata without live ES addgames for system={SystemId}, game={GameSlug}.",
                    systemId,
                    gameSlug);
            }
        }

        return new MediaPrefetchResult
        {
            SystemId = systemId,
            GameSlug = gameSlug,
            QueuedRemoteScrape = IsRemoteScrapeAttempted(remoteDecision),
            IsArcadeLike = plan.IsArcadeLike,
            IsFolderBasedSystem = plan.IsFolderBasedSystem,
            SkipCrcComputation = plan.SkipCrcComputation,
            IsFilteredArcadeBiosCandidate = plan.IsFilteredArcadeBiosCandidate,
            GamePathExists = plan.GamePathExists,
            GamelistMd5 = plan.GamelistMd5,
            GamelistCrc32 = plan.GamelistCrc32,
            Needs = plan.Needs
        };
    }

    public async Task<MediaProjectionPlan> PrepareLocalProjectionPlanAsync(GameReference game, CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareLocalProjectionPlanCoreAsync(
            game,
            forceRemoteScrape: false,
            createUserVariantGuide: false,
            cancellationToken);
        preparation.Plan.SuppressImmediateGamelistUpdates = true;
        return preparation.Plan;
    }

    private async Task<LocalProjectionPreparation> PrepareLocalProjectionPlanCoreAsync(
        GameReference game,
        bool forceRemoteScrape,
        bool createUserVariantGuide,
        CancellationToken cancellationToken)
    {
        var systemId = _systemIdNormalizer.Normalize(game.SystemId);
        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(game.SystemId);
        var requestedGameSlug = _gameNameNormalizer.NormalizeGameSlug(game.GameName, game.GamePath);
        var aliasKeys = BuildAliasKeys(game, requestedGameSlug);
        var gameSlug = await _mediaAliasStore.ResolveGameSlugAsync(systemId, aliasKeys, requestedGameSlug, cancellationToken);

        var plan = _needEvaluator.BuildPlan(
            new MediaPrefetchRequest
            {
                SystemId = systemId,
                GameId = game.GameId,
                GameName = game.GameName,
                GamePath = game.GamePath,
                Details = game.Details
            },
            systemId,
            gameSlug,
            frontendSystemId);
        plan.EsGameId = string.IsNullOrWhiteSpace(game.GameId)
            ? _gamelistUpdateService.GenerateEsGameIdForPath(frontendSystemId, game.GamePath)
            : game.GameId.Trim();
        if (string.IsNullOrWhiteSpace(game.GameId) && !string.IsNullOrWhiteSpace(plan.EsGameId))
        {
            _logger?.LogDebug(
                "ES gameid generated on the fly for local media projection: system={SystemId}, path={GamePath}, gameid={GameId}.",
                frontendSystemId,
                game.GamePath,
                plan.EsGameId);
        }

        plan.IgnoreRemoteScrapeCooldown = forceRemoteScrape;
        var scrapingSettings = _settingsService.GetScrapingSettings();
        var legacyCanonicalSlug = BuildLegacyCanonicalSlug(game.GamePath);
        var variantContext = BuildVariantContext(plan);
        var hadMissingLiveRefreshMediaAtSelection = HasAnyMissingLiveRefreshMedia(plan, scrapingSettings.WheelStyle);
        var variantOverrideSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (variantContext.IsVariant)
        {
            foreach (var need in plan.Needs)
            {
                var source = _projectionService.ResolveUserSourcePath(systemId, variantContext.UserVariantSlug, need.Kind);
                if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
                {
                    variantOverrideSources[MediaKinds.Normalize(need.Kind)] = source;
                }
            }

            foreach (var need in plan.Needs)
            {
                var targetBaseName = variantOverrideSources.ContainsKey(MediaKinds.Normalize(need.Kind))
                    ? variantContext.ExactProjectionBaseName
                    : variantContext.FamilyProjectionBaseName;
                need.TargetRelativePath = BuildTargetRelativePath(targetBaseName, need.Kind);
            }

            plan.ProjectionBaseName = variantContext.FamilyProjectionBaseName;
            if (createUserVariantGuide)
            {
                EnsureUserVariantGuide(systemId, variantContext);
            }
        }

        foreach (var need in plan.Needs.Where(n => n.IsMissing))
        {
            var source = variantOverrideSources.TryGetValue(MediaKinds.Normalize(need.Kind), out var userVariantSource)
                ? userVariantSource
                : _projectionService.ResolveCanonicalSourcePath(systemId, gameSlug, need.Kind);
            if (string.IsNullOrWhiteSpace(source) &&
                !string.IsNullOrWhiteSpace(legacyCanonicalSlug) &&
                !string.Equals(legacyCanonicalSlug, gameSlug, StringComparison.OrdinalIgnoreCase))
            {
                source = _projectionService.ResolveCanonicalSourcePath(systemId, legacyCanonicalSlug, need.Kind);
                if (!string.IsNullOrWhiteSpace(source))
                {
                    _logger?.LogInformation(
                        "Media recovered from legacy canonical slug; remote scrape skipped for system={SystemId}, game={GameSlug}, legacySlug={LegacySlug}, kind={Kind}, source={SourcePath}",
                        systemId,
                        gameSlug,
                        legacyCanonicalSlug,
                        need.Kind,
                        source);
                }
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                need.ExistingPath = source;
                _logger?.LogInformation(
                    "Media recovered from existing canonical store; remote scrape skipped for system={SystemId}, game={GameSlug}, kind={Kind}, source={SourcePath}",
                    systemId,
                    gameSlug,
                    need.Kind,
                    source);
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                source = ResolveInheritedSystemGroupSource(frontendSystemId, systemId, game.GamePath, gameSlug, legacyCanonicalSlug, need.Kind);
                if (!string.IsNullOrWhiteSpace(source))
                {
                    need.ExistingPath = source;
                    _logger?.LogInformation(
                        "Media inherited from system group; remote scrape skipped for system={SystemId}, game={GameSlug}, kind={Kind}, source={SourcePath}",
                        systemId,
                        gameSlug,
                        need.Kind,
                        source);
                }
            }
        }

        await _projectionService.ApplyCanonicalImportAsync(plan, cancellationToken);
        await _mediaAliasStore.RecordGameAliasesAsync(systemId, gameSlug, aliasKeys, cancellationToken);
        await _localizedTextStore.PersistAsync(systemId, gameSlug, game.Details, cancellationToken);
        var requestedLanguage = _settingsService.GetScrapingSettings().Language;
        await TryRebuildLocalTextFromRawCacheAsync(systemId, gameSlug, requestedLanguage, cancellationToken);
        if (!string.IsNullOrWhiteSpace(legacyCanonicalSlug) &&
            !string.Equals(legacyCanonicalSlug, gameSlug, StringComparison.OrdinalIgnoreCase))
        {
            await TryRebuildLocalTextFromRawCacheAsync(systemId, legacyCanonicalSlug, requestedLanguage, cancellationToken);
        }

        plan.TextSourceGameSlug = ResolveTextSourceGameSlug(
            frontendSystemId,
            systemId,
            game.GamePath,
            gameSlug,
            legacyCanonicalSlug,
            requestedLanguage,
            cancellationToken);
        var textLookupSlug = ResolveTextLookupSlug(plan);
        var needsRemoteTextScrape = NeedsRemoteTextScrape(
            systemId,
            textLookupSlug,
            requestedLanguage,
            game.Details,
            cancellationToken);
        if (needsRemoteTextScrape &&
            await TryRebuildLocalTextFromRawCacheAsync(systemId, textLookupSlug, requestedLanguage, cancellationToken))
        {
            needsRemoteTextScrape = NeedsRemoteTextScrape(
                systemId,
                textLookupSlug,
                requestedLanguage,
                game.Details,
                cancellationToken);
        }

        plan.NeedsDescriptionScrape = needsRemoteTextScrape;
        await _projectionService.ApplyProjectionAsync(plan, cancellationToken);

        return new LocalProjectionPreparation(plan, systemId, gameSlug, hadMissingLiveRefreshMediaAtSelection);
    }

    private async Task NotifyLocalScrapeStartedAsync(MediaProjectionPlan plan, CancellationToken cancellationToken)
    {
        if (!_gamelistUpdateService.IsCurrentlySelectedGame(plan))
        {
            return;
        }

        var language = (_settingsService.GetScrapingSettings().Language ?? string.Empty).Trim();
        var gameName = EsNotificationText.ShortGameName(!string.IsNullOrWhiteSpace(plan.DisplayName) ? plan.DisplayName.Trim() : plan.GameSlug);
        var message = _interfaceTextService.Format(
            "notification.local.scrape_started",
            language,
            ("game", gameName));
        await _notificationService.NotifyAsync(message, cancellationToken);
    }

    private static bool IsRemoteScrapeAttempted(RemoteScrapeDecision decision)
    {
        if (!decision.Enabled)
        {
            return false;
        }

        return decision.Status is not ("disabled" or "local-satisfied" or "not-requested" or "provider-disabled" or "provider-unsupported" or "missing-user-credentials" or "missing-developer-credentials" or "missing-system-mapping" or "text-no-change-cooldown");
    }

    public async Task<MediaPrefetchResult> QueueRemoteForSelectionAsync(GameReference game, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation(
            "Remote media queue request ignored because legacy scraping is archived: system={SystemId}, path={GamePath}.",
            game.SystemId,
            game.GamePath);
        return await PrefetchForSelectionAsync(game, allowRemoteScrape: false, forceRemoteScrape: false, cancellationToken);
    }

    public async Task<MediaPrefetchResult> ScrapeLivePriorityForSelectionAsync(GameReference game, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation(
            "Live priority scrape request ignored because legacy scraping is archived: system={SystemId}, path={GamePath}.",
            game.SystemId,
            game.GamePath);
        return await PrefetchForSelectionAsync(game, allowRemoteScrape: false, forceRemoteScrape: false, cancellationToken);
    }

    private static void MarkEsApiMediaEndpointsAsMissing(MediaProjectionPlan plan)
    {
        foreach (var need in plan.Needs)
        {
            if (!LooksLikeEsApiMediaEndpoint(need.ExistingPath))
            {
                continue;
            }

            need.ExistingPath = string.Empty;
            need.IsMissing = true;
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

    private bool HasAnyDescription(string systemId, string gameSlug, string requestedLanguage, CancellationToken cancellationToken)
    {
        var bundle = _localizedTextStore.LoadPreferredBundleAsync(
                systemId,
                gameSlug,
                requestedLanguage,
                cancellationToken,
                allowAnyLanguageFallback: false,
                allowEnglishFallback: false)
            .GetAwaiter()
            .GetResult();
        return bundle != null
            && bundle.Fields.TryGetValue("desc", out var content)
            && !string.IsNullOrWhiteSpace(content);
    }

    private async Task<bool> TryRebuildLocalTextFromRawCacheAsync(
        string systemId,
        string gameSlug,
        string requestedLanguage,
        CancellationToken cancellationToken)
    {
        if (HasAnyDescription(systemId, gameSlug, requestedLanguage, cancellationToken))
        {
            return false;
        }

        var rebuilt = await _rawCacheMetadataService.TryRebuildLanguageAsync(
            systemId,
            gameSlug,
            requestedLanguage,
            cancellationToken);
        if (rebuilt)
        {
            _logger?.LogInformation(
                "Localized metadata rebuilt from local ScreenScraper cache before remote decision: system={SystemId}, game={GameSlug}, language={Language}.",
                systemId,
                gameSlug,
                requestedLanguage);
        }

        return rebuilt;
    }

    private bool NeedsRemoteTextScrape(
        string systemId,
        string gameSlug,
        string requestedLanguage,
        GameDetails? gameDetails,
        CancellationToken cancellationToken)
    {
        var bundle = _localizedTextStore.LoadPreferredBundleAsync(
                systemId,
                gameSlug,
                requestedLanguage,
                cancellationToken,
                allowAnyLanguageFallback: false,
                allowEnglishFallback: false)
            .GetAwaiter()
            .GetResult();
        if (bundle == null ||
            !bundle.Fields.TryGetValue("desc", out var description) ||
            string.IsNullOrWhiteSpace(description))
        {
            return true;
        }

        if (IsMissingOrZeroRating(gameDetails?.Rating) &&
            !HasCanonicalRating(bundle))
        {
            _logger?.LogDebug(
                "Remote text scrape requested because the gamelist rating is missing or zero for system={SystemId}, game={GameSlug}.",
                systemId,
                gameSlug);
            return true;
        }

        if (!bundle.Fields.TryGetValue("rating", out var canonicalRating) ||
            IsMissingOrZeroRating(canonicalRating))
        {
            _logger?.LogDebug(
                "Remote text scrape requested because the canonical rating is missing or zero for system={SystemId}, game={GameSlug}.",
                systemId,
                gameSlug);
            return true;
        }

        if (IsMissingOrUnknownReleaseDate(gameDetails?.Releasedate) ||
            !HasCanonicalReleaseDate(bundle))
        {
            _logger?.LogDebug(
                "Remote text scrape not requested for system={SystemId}, game={GameSlug}: description is present and missing/unknown release date alone is not a live scrape trigger.",
                systemId,
                gameSlug);
        }

        return false;
    }

    private static bool HasCanonicalRating(LocalizedTextBundle bundle)
    {
        return bundle.Fields.TryGetValue("rating", out var value) &&
            !IsMissingOrZeroRating(value);
    }

    private static bool HasCanonicalReleaseDate(LocalizedTextBundle bundle)
    {
        return bundle.Fields.TryGetValue("releasedate", out var value) &&
            !IsMissingOrUnknownReleaseDate(value);
    }

    private static bool IsMissingOrZeroRating(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (!double.TryParse(
                normalized.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var rating))
        {
            return true;
        }

        return rating <= 0;
    }

    private static bool IsMissingOrUnknownReleaseDate(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "n/a", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "na", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveTextSourceGameSlug(
        string frontendSystemId,
        string systemId,
        string gamePath,
        string gameSlug,
        string legacyCanonicalSlug,
        string requestedLanguage,
        CancellationToken cancellationToken)
    {
        if (HasAnyDescription(systemId, gameSlug, requestedLanguage, cancellationToken))
        {
            return gameSlug;
        }

        if (!string.IsNullOrWhiteSpace(legacyCanonicalSlug) &&
            !string.Equals(legacyCanonicalSlug, gameSlug, StringComparison.OrdinalIgnoreCase) &&
            HasAnyDescription(systemId, legacyCanonicalSlug, requestedLanguage, cancellationToken))
        {
            _logger?.LogInformation(
                "Localized text recovered from legacy canonical slug for system={SystemId}, game={GameSlug}, textSlug={TextSlug}.",
                systemId,
                gameSlug,
                legacyCanonicalSlug);
            return legacyCanonicalSlug;
        }

        var inheritedSlug = ResolveInheritedMameGroupTextSlug(
            frontendSystemId,
            systemId,
            gamePath,
            gameSlug,
            legacyCanonicalSlug,
            requestedLanguage,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(inheritedSlug))
        {
            _logger?.LogInformation(
                "Localized text inherited from system group for system={SystemId}, game={GameSlug}, textSlug={TextSlug}.",
                systemId,
                gameSlug,
                inheritedSlug);
            return inheritedSlug;
        }

        return gameSlug;
    }

    private string ResolveInheritedMameGroupTextSlug(
        string frontendSystemId,
        string systemId,
        string gamePath,
        string gameSlug,
        string legacyCanonicalSlug,
        string requestedLanguage,
        CancellationToken cancellationToken)
    {
        var safeGamePath = gamePath ?? string.Empty;
        var currentRom = NormalizeRomName(Path.GetFileNameWithoutExtension(safeGamePath));
        foreach (var relatedRom in _mameGamelistGroupIndex.GetRelatedRoms(frontendSystemId, safeGamePath, gameSlug))
        {
            var relatedSlug = NormalizeRomName(relatedRom);
            if (string.IsNullOrWhiteSpace(relatedSlug) ||
                string.Equals(relatedSlug, currentRom, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relatedSlug, gameSlug, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relatedSlug, legacyCanonicalSlug, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasAnyDescription(systemId, relatedSlug, requestedLanguage, cancellationToken))
            {
                return relatedSlug;
            }
        }

        return string.Empty;
    }

    private static string ResolveTextLookupSlug(MediaProjectionPlan plan)
    {
        return string.IsNullOrWhiteSpace(plan.TextSourceGameSlug)
            ? plan.GameSlug
            : plan.TextSourceGameSlug;
    }

    private static string NormalizePathKey(string? path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private static bool LooksLikeEsApiMediaEndpoint(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        return normalized.StartsWith("/systems/", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("/games/", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("/media/", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveInheritedSystemGroupSource(
        string frontendSystemId,
        string systemId,
        string gamePath,
        string gameSlug,
        string legacyCanonicalSlug,
        string kind)
    {
        var safeGamePath = gamePath ?? string.Empty;
        var currentRom = NormalizeRomName(Path.GetFileNameWithoutExtension(safeGamePath));
        foreach (var relatedRom in _mameGamelistGroupIndex.GetRelatedRoms(frontendSystemId, safeGamePath, gameSlug))
        {
            var relatedSlug = NormalizeRomName(relatedRom);
            if (string.IsNullOrWhiteSpace(relatedSlug) ||
                string.Equals(relatedSlug, currentRom, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relatedSlug, gameSlug, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relatedSlug, legacyCanonicalSlug, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = _projectionService.ResolveCanonicalSourcePath(systemId, relatedSlug, kind);
            if (!string.IsNullOrWhiteSpace(source))
            {
                return source;
            }
        }

        return null;
    }

    private static bool HasAllLiveRefreshMediaAvailable(MediaProjectionPlan plan)
    {
        var requiredNeeds = plan.Needs
            .Where(need => IsLiveRefreshRequiredKind(need.Kind))
            .ToList();
        return requiredNeeds.Count > 0 &&
            requiredNeeds.All(need =>
                !need.IsMissing &&
                !string.IsNullOrWhiteSpace(need.ExistingPath) &&
                File.Exists(need.ExistingPath));
    }

    private static bool HasAnyMissingLiveRefreshMedia(MediaProjectionPlan plan, string wheelStyle)
    {
        return plan.Needs.Any(need => need.IsMissing && IsSelectedVisibleKind(plan, need.Kind, wheelStyle));
    }

    private static bool HasAnyProjectedLiveRefreshMediaAvailable(MediaProjectionPlan plan, string wheelStyle)
    {
        return plan.Needs.Any(need =>
            IsSelectedVisibleKind(plan, need.Kind, wheelStyle) &&
            ((!string.IsNullOrWhiteSpace(need.ProjectedPath) && File.Exists(need.ProjectedPath)) ||
                (!string.IsNullOrWhiteSpace(need.ExistingPath) && File.Exists(need.ExistingPath))));
    }

    private static bool HasLocalLiveRefreshMediaContentChanged(MediaProjectionPlan plan, string wheelStyle)
    {
        foreach (var need in plan.Needs.Where(need => need.WasContentChanged))
        {
            if (!IsSelectedVisibleKind(plan, need.Kind, wheelStyle))
            {
                continue;
            }

            var nextPath = ResolveComparableMediaPath(
                plan.FrontendSystemId,
                !string.IsNullOrWhiteSpace(need.ProjectedPath) ? need.ProjectedPath : need.ExistingPath);
            if (string.IsNullOrWhiteSpace(nextPath) || !File.Exists(nextPath))
            {
                continue;
            }

            var previousPath = ResolveComparableMediaPath(plan.FrontendSystemId, need.InitialExistingPath);
            if (string.IsNullOrWhiteSpace(previousPath) || !File.Exists(previousPath))
            {
                return true;
            }

            if (!HaveSameContent(previousPath, nextPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSelectedVisibleKind(MediaProjectionPlan plan, string kind, string wheelStyle)
    {
        var normalized = MediaKinds.Normalize(kind);
        if (string.Equals(normalized, MediaKinds.Fanart, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(normalized, ResolveSelectionSourceToKind(plan.PreferredImageSource, MediaSelectionTarget.Image, wheelStyle), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, ResolveSelectionSourceToKind(plan.PreferredThumbnailSource, MediaSelectionTarget.Thumbnail, wheelStyle), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, ResolveSelectionSourceToKind(plan.PreferredLogoSource, MediaSelectionTarget.Logo, wheelStyle), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveComparableMediaPath(string frontendSystemId, string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith("/systems/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        normalized = normalized.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        return Path.GetFullPath(Path.Combine(systemRoot, normalized));
    }

    private static bool HaveSameContent(string firstPath, string secondPath)
    {
        try
        {
            var firstInfo = new FileInfo(firstPath);
            var secondInfo = new FileInfo(secondPath);
            if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            using var firstStream = File.Open(firstPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var secondStream = File.Open(secondPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(firstStream).SequenceEqual(SHA256.HashData(secondStream));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVisibleSlotResolvedAfterSelection(MediaProjectionPlan plan, string wheelStyle)
    {
        if (string.IsNullOrWhiteSpace(plan.GamelistPath) ||
            !File.Exists(plan.GamelistPath))
        {
            return false;
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
        if (string.IsNullOrWhiteSpace(relativeGamePath))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(plan.GamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var gameNode = document.Root?.Elements("game").FirstOrDefault(game =>
                string.Equals(
                    NormalizeGamelistPath(game.Element("path")?.Value),
                    NormalizeGamelistPath(relativeGamePath),
                    StringComparison.OrdinalIgnoreCase));
            if (gameNode == null)
            {
                return false;
            }

            return IsVisibleSlotResolved(gameNode, "image", ResolveSelectionSourceToKind(plan.PreferredImageSource, MediaSelectionTarget.Image, wheelStyle), plan) ||
                IsVisibleSlotResolved(gameNode, "thumbnail", ResolveSelectionSourceToKind(plan.PreferredThumbnailSource, MediaSelectionTarget.Thumbnail, wheelStyle), plan) ||
                IsVisibleSlotResolved(gameNode, "marquee", ResolveSelectionSourceToKind(plan.PreferredLogoSource, MediaSelectionTarget.Logo, wheelStyle), plan);
        }
        catch
        {
            return false;
        }
    }

    private static string ToGameRelativePath(string? gamePath, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        var resolved = Path.IsPathRooted(gamePath)
            ? gamePath
            : Path.Combine(systemRoot, gamePath.TrimStart('.', '/', '\\'));
        return "./" + Path.GetRelativePath(systemRoot, resolved).Replace('\\', '/');
    }

    private static string NormalizeGamelistPath(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized.TrimStart('/');
        return normalized.StartsWith("./", StringComparison.Ordinal)
            ? normalized
            : "./" + normalized.TrimStart('.');
    }

    private static bool IsVisibleSlotResolved(XElement gameNode, string slotName, string selectedKind, MediaProjectionPlan plan)
    {
        if (string.IsNullOrWhiteSpace(selectedKind))
        {
            return false;
        }

        var slotPath = gameNode.Element(slotName)?.Value?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(slotPath))
        {
            return false;
        }

        return plan.Needs.Any(need =>
            string.Equals(MediaKinds.Normalize(need.Kind), selectedKind, StringComparison.OrdinalIgnoreCase) &&
            ((!string.IsNullOrWhiteSpace(need.ProjectedPath) && File.Exists(need.ProjectedPath)) ||
                (!string.IsNullOrWhiteSpace(need.ExistingPath) && File.Exists(need.ExistingPath))));
    }

    private static bool IsLiveRefreshRequiredKind(string kind)
    {
        return MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Image => true,
            MediaKinds.Thumbnail => true,
            MediaKinds.Marquee => true,
            MediaKinds.Fanart => true,
            _ => false
        };
    }

    private static string ResolveSelectionSourceToKind(string source, MediaSelectionTarget target, string wheelStyle)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "sstitle" or "title" => MediaKinds.Image,
            "ss" or "screenshot" or "thumb" or "thumbnail" => MediaKinds.Thumbnail,
            "logo" => MediaKinds.Logo,
            "wheel-hd" => target == MediaSelectionTarget.Logo && IsSteelWheelStyle(wheelStyle) ? MediaKinds.WheelSteel : MediaKinds.WheelCarbon,
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

    private static bool IsSteelWheelStyle(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized is "steel" or "wheel-steel" or "wheelsteel";
    }

    private static bool IsThemeArchiveSelection(GameReference game)
    {
        var gamePath = game.GamePath ?? string.Empty;
        var normalizedPath = gamePath.Replace('\\', '/');
        if (normalizedPath.Contains("/themes/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileNameWithoutExtension(gamePath);
        return fileName.EndsWith("-themehb", StringComparison.OrdinalIgnoreCase);
    }

    private enum MediaSelectionTarget
    {
        Image,
        Logo,
        Thumbnail
    }

    private static string BuildLegacyCanonicalSlug(string? gamePath)
    {
        var fileStem = Path.GetFileNameWithoutExtension(gamePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileStem))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(fileStem
            .Trim()
            .ToLowerInvariant()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());
    }

    private static string NormalizeRomName(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static VariantContext BuildVariantContext(MediaProjectionPlan plan)
    {
        if (plan.IsArcadeLike)
        {
            return VariantContext.None;
        }

        var exactBaseName = string.IsNullOrWhiteSpace(plan.ProjectionBaseName)
            ? Path.GetFileNameWithoutExtension(plan.GamePath ?? string.Empty)
            : plan.ProjectionBaseName;
        var familyBaseName = BuildFamilyProjectionBaseName(exactBaseName);
        var userVariantSlug = BuildUserVariantSlug(exactBaseName);
        var familySlug = BuildUserVariantSlug(familyBaseName);
        var isVariant = !string.IsNullOrWhiteSpace(userVariantSlug) &&
            !string.IsNullOrWhiteSpace(familySlug) &&
            !string.Equals(userVariantSlug, familySlug, StringComparison.OrdinalIgnoreCase);

        return isVariant
            ? new VariantContext(true, exactBaseName, familyBaseName, userVariantSlug)
            : VariantContext.None;
    }

    private static string BuildFamilyProjectionBaseName(string value)
    {
        var builder = new StringBuilder();
        var depth = 0;
        foreach (var character in value ?? string.Empty)
        {
            if (character is '(' or '[')
            {
                depth++;
                continue;
            }

            if (character is ')' or ']')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
            }

            if (depth == 0)
            {
                builder.Append(character);
            }
        }

        var cleaned = builder.ToString().Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        cleaned = cleaned.Trim(' ', '-', '_', '.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = value ?? string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(cleaned.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
    }

    private static string BuildUserVariantSlug(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        var slug = builder.ToString().Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug;
    }

    private static string BuildTargetRelativePath(string projectionBaseName, string kind)
    {
        var extension = kind switch
        {
            MediaKinds.Manual => ".pdf",
            MediaKinds.Video or MediaKinds.VideoNormalized => ".mp4",
            MediaKinds.ThemeHb => ".zip",
            _ => ".png"
        };

        var folderName = kind switch
        {
            MediaKinds.Map or MediaKinds.Manual or MediaKinds.Magazine => "manuals",
            MediaKinds.Video or MediaKinds.VideoNormalized => "videos",
            MediaKinds.ThemeHb => "themes",
            _ => "images"
        };

        return Path.Combine(folderName, BuildProjectionFileName(projectionBaseName, kind, extension));
    }

    private static string BuildProjectionFileName(string baseName, string kind, string extension)
    {
        var suffix = kind switch
        {
            MediaKinds.Image => "-screentitle",
            MediaKinds.Thumbnail => "-screenshot",
            MediaKinds.Logo => "-logo",
            MediaKinds.Wheel => "-wheel",
            MediaKinds.WheelCarbon => "-wheelcarbon",
            MediaKinds.WheelSteel => "-wheelsteel",
            MediaKinds.Marquee => "-marquee",
            MediaKinds.ScreenMarquee => "-screenmarquee",
            MediaKinds.ScreenMarqueeSmall => "-screenmarqueesmall",
            MediaKinds.SteamGrid => "-steamgrid",
            MediaKinds.MixRbv1 => "-mixrbv1",
            MediaKinds.MixRbv2 => "-mixrbv2",
            MediaKinds.BoxFront => "-box2d",
            MediaKinds.BoxSide => "-boxside",
            MediaKinds.BoxTexture => "-boxtexture",
            MediaKinds.Box3d => "-box3d",
            MediaKinds.Cartridge => "-cartridge",
            MediaKinds.Label => "-label",
            MediaKinds.Fanart => "-fanart",
            MediaKinds.Flyer => "-flyer",
            MediaKinds.Figurine => "-figurine",
            MediaKinds.Bezel => "-bezel",
            MediaKinds.BoxBack => "-boxback",
            MediaKinds.Map => "-map",
            MediaKinds.Manual => "-manual",
            MediaKinds.Magazine => "-magazine",
            MediaKinds.Video => "-video",
            MediaKinds.VideoNormalized => "-video-normalized",
            MediaKinds.ThemeHb => "-themehb",
            _ => "-" + MediaKinds.Normalize(kind)
        };

        return baseName + suffix + extension;
    }

    private static void EnsureUserVariantGuide(string systemId, VariantContext context)
    {
        if (!context.IsVariant || string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(context.UserVariantSlug))
        {
            return;
        }

        var variantRoot = Path.Combine(RetroBatPaths.MediaUserSystemsRoot, systemId, "games", context.UserVariantSlug);
        Directory.CreateDirectory(variantRoot);
        foreach (var directory in ExpectedUserMediaDirectories())
        {
            Directory.CreateDirectory(Path.Combine(variantRoot, directory));
        }

        var readmePath = Path.Combine(variantRoot, "README.txt");
        if (File.Exists(readmePath))
        {
            return;
        }

        File.WriteAllText(readmePath, BuildUserVariantReadme(systemId, context), Encoding.UTF8);
    }

    private static IEnumerable<string> ExpectedUserMediaDirectories()
    {
        yield return "artwork";
        yield return Path.Combine("artwork", "bezels");
        yield return Path.Combine("artwork", "box");
        yield return Path.Combine("artwork", "marquee");
        yield return Path.Combine("artwork", "mix");
        yield return "documents";
        yield return Path.Combine("documents", "maps");
        yield return "themes";
        yield return "ui";
        yield return Path.Combine("ui", "wheels");
    }

    private static string BuildUserVariantReadme(string systemId, VariantContext context)
    {
        var lines = new[]
        {
            "APIExpose user media override folder",
            "",
            $"System: {systemId}",
            $"Variant: {context.ExactProjectionBaseName}",
            $"Inherited media base: {context.FamilyProjectionBaseName}",
            "",
            "Drop files here only when this variant needs media different from the inherited parent/family media.",
            "Missing files keep inheriting the parent/family media and do not duplicate projected roms media.",
            "",
            "Expected filenames:",
            "video.mp4",
            "video-normalized.mp4",
            "artwork/screenshot.png",
            "artwork/screentitle.png",
            "artwork/bezels/bezel.png",
            "artwork/box/front.png",
            "artwork/box/back.png",
            "artwork/box/side.png",
            "artwork/box/texture.png",
            "artwork/box/3d.png",
            "artwork/fanart.jpg",
            "artwork/figurine.png",
            "artwork/flyer.png",
            "artwork/marquee/marquee.png",
            "artwork/mix/mixrbv1.png",
            "artwork/mix/mixrbv2.png",
            "artwork/marquee/screenmarquee.png",
            "artwork/marquee/screenmarquee-small.png",
            "ui/steamgrid.jpg",
            "documents/manual.pdf",
            "documents/maps/map.png",
            "themes/themehb.zip",
            "ui/wheels/wheel.png",
            "ui/wheels/wheel-carbon.png",
            "ui/wheels/wheel-steel.png",
            ""
        };

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record VariantContext(
        bool IsVariant,
        string ExactProjectionBaseName,
        string FamilyProjectionBaseName,
        string UserVariantSlug)
    {
        public static VariantContext None { get; } = new(false, string.Empty, string.Empty, string.Empty);
    }

    private sealed record LocalProjectionPreparation(
        MediaProjectionPlan Plan,
        string SystemId,
        string GameSlug,
        bool HadMissingLiveRefreshMediaAtSelection);
}
