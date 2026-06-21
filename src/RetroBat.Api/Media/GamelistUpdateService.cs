using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Controllers;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Net;

namespace RetroBat.Api.Media;

public enum LiveGameUpdateNotificationKind
{
    None,
    LocalProjection,
    RemoteScrape,
    RemoteVideoScrape
}

public sealed record GamelistEntryUpdateResult(
    bool Changed,
    bool MediaContentChanged,
    bool MetadataChanged)
{
    public static GamelistEntryUpdateResult NoChange { get; } = new(false, false, false);
    public bool ShouldPushLiveAddGames => MediaContentChanged;
}

public class GamelistUpdateService : IGamelistSelectionSyncService, IDisposable
{
    private const int BootstrapPlaceholderStateVersion = 3;
    private const int SelectionNormalizationStateVersion = 5;
    private const int GameIdSyncStateVersion = 1;
    private const string GameIdSyncPhaseName = "gameid-sync";
    private const string GameIdSyncNormalizerVersion = "20260522-es-gameid-md5-path-v1";
    private const string DefaultPlaceholderFileName = "defgame.png";
    private const string ScrapingPlaceholderFileName = "scraping_in_progress.png";
    private const string NoMediaFoundPlaceholderFileName = "no_media_found.png";
    private const string LegacyOriginalVisibleSlotTagPrefix = "apiexpose_original";
    private const string GamelistBackupDirectoryName = ".api-expose-gamelist-backups";
    private const string GamelistAuditDirectoryName = ".api-expose-gamelist-audit";
    private const int GamelistBackupRetentionCount = 1;
    private const int GamelistIoRetryCount = 5;
    private static readonly TimeSpan GamelistIoRetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly ConcurrentDictionary<string, DirtyLiveGamelistPlan> DirtyLiveGamelistPlans = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, MediaProjectionPlan> PendingLiveGamelistWriteBehindPlans = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> LiveGamelistWriteBehindDebounces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> LastLiveAddGamesCurrentMediaSignatures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CachedProjectedFolderIndex> ProjectedFolderIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim LiveEsMediaPushGate = new(1, 1);
    private static readonly SemaphoreSlim LiveEsAddGamesGate = new(1, 1);
    private static readonly TimeSpan LiveGamelistWriteBehindDelay = TimeSpan.FromSeconds(3);
    private static DateTimeOffset LastLiveEsAddGamesPostUtc = DateTimeOffset.MinValue;
    private readonly record struct SelectionRefreshResult(bool Processed, bool Changed, bool SaveSucceeded);
    // Official Batocera ES metadata keys observed in projects-source/batocera-emulationstation-master/es-app/src/MetaData.cpp:
    // element keys: name, desc, genre, tags, sortname, emulator, core, image, video, marquee, thumbnail, fanart,
    // titleshot, manual, magazine, map, bezel, cartridge, boxart, boxback, wheel, mix, rating, releasedate,
    // developer, publisher, family, genres, arcadesystemname, players, favorite, hidden, kidgame, playcount,
    // lastplayed, crc32, md5, gametime, lang, region, cheevosHash, cheevosId; special child: scrap;
    // official attribute: id. Batocera preserves unknown elements as pass-through, but they are not official metadata.
    private static readonly HashSet<string> LiveAddGamesAllowedElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "name",
        "desc",
        "image",
        "thumbnail",
        "marquee",
        "fanart",
        "video",
        "manual",
        "magazine",
        "map",
        "bezel",
        "cartridge",
        "boxart",
        "boxback",
        "wheel",
        "mix",
        "titleshot",
        "releasedate",
        "developer",
        "publisher",
        "players",
        "md5",
        "lang",
        "region",
        "genre",
        "family",
        "rating"
    };

    private static readonly HashSet<string> LiveAddGamesMetadataElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "desc",
        "releasedate",
        "developer",
        "publisher",
        "players",
        "md5",
        "lang",
        "region",
        "genre",
        "family",
        "rating"
    };

    private static readonly HashSet<string> LiveAddGamesLocalizedMetadataRefreshElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "desc",
        "releasedate",
        "developer",
        "publisher",
        "players",
        "lang",
        "region",
        "genre",
        "family",
        "rating"
    };

    private static readonly HashSet<string> LiveAddGamesSanitizedTextElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "desc",
        "developer",
        "publisher",
        "players",
        "lang",
        "region",
        "genre",
        "family"
    };

    private static readonly string[] UnsupportedDurableMediaTags = ["wheelcarbon", "wheelsteel"];
    private static readonly string[] CanonicalIndexedKinds =
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
    private static readonly JsonSerializerOptions BootstrapStateJsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> GamelistMediaElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "image",
        "thumbnail",
        "marquee",
        "fanart",
        "bezel",
        "boxback",
        "manual",
        "video",
        "wheel",
        "wheelcarbon",
        "wheelsteel",
        "boxart",
        "cartridge",
        "label",
        "extra1",
        "extra2",
        "extra3",
        "extra4",
        "map",
        "mix",
        "magazine",
        "titleshot",
        "screenshot",
        "screenmarquee",
        "screenmarqueesmall",
        "steamgrid",
        "mixrbv1",
        "mixrbv2",
        "box2d",
        "box3d",
        "boxside",
        "boxtexture",
        "figurine",
        "videonormalized"
    };
    private static readonly JsonSerializerOptions EsApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string[] GamelistMetricMediaTags =
    {
        "image",
        "thumbnail",
        "marquee",
        "logo",
        "wheel",
        "wheelround",
        "wheelcarbon",
        "wheelsteel",
        "screenshot",
        "titleshot",
        "boxart",
        "box2d",
        "box3d",
        "boxback",
        "boxside",
        "boxtexture",
        "cartridge",
        "label",
        "fanart",
        "extra1",
        "flyer",
        "figurine",
        "mix",
        "mixvideo",
        "bezel",
        "map",
        "manual",
        "video",
        "screenmarquee",
        "screenmarqueesmall",
        "steamgrid",
        "mixrbv1",
        "mixrbv2",
        "videonormalized"
    };
    private static readonly string[] GamelistConsolidationTextMetadataTags =
    {
        "desc",
        "rating",
        "releasedate",
        "developer",
        "publisher",
        "genre",
        "players",
        "md5"
    };
    private static readonly string[] PendingExtendedGamelistMergeTags =
    {
        "gameid",
        "desc",
        "releasedate",
        "developer",
        "publisher",
        "players",
        "md5",
        "lang",
        "region",
        "genre",
        "family",
        "genres",
        "rating",
        "source",
        "image",
        "thumbnail",
        "marquee",
        "fanart",
        "wheel",
        "boxart",
        "boxback",
        "cartridge",
        "label",
        "extra1",
        "figurine",
        "mix",
        "titleshot",
        "screenshot",
        "bezel",
        "map",
        "magazine",
        "manual",
        "video",
        "screenmarquee",
        "screenmarqueesmall",
        "steamgrid",
        "mixrbv1",
        "mixrbv2",
        "videonormalized"
    };
    private static readonly HashSet<string> PendingExtendedLocalizedMetadataTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "desc",
        "releasedate",
        "developer",
        "publisher",
        "players",
        "lang",
        "region",
        "genre",
        "family",
        "genres",
        "rating",
        "source"
    };
    private static readonly HashSet<string> PendingExtendedPotentiallyLocalizedMediaTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "image",
        "thumbnail",
        "marquee",
        "fanart",
        "wheel",
        "boxart",
        "boxback",
        "cartridge",
        "label",
        "extra1",
        "figurine",
        "mix",
        "titleshot",
        "screenshot",
        "bezel",
        "map",
        "magazine",
        "manual",
        "video",
        "screenmarquee",
        "screenmarqueesmall",
        "steamgrid",
        "mixrbv1",
        "mixrbv2",
        "videonormalized"
    };
    private static readonly HashSet<string> MediaLocalizationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "fr",
        "en",
        "us",
        "usa",
        "uk",
        "gb",
        "eu",
        "eur",
        "wor",
        "world",
        "de",
        "ger",
        "it",
        "sp",
        "es",
        "jp",
        "jpn",
        "ja",
        "br",
        "nl",
        "cn",
        "asi"
    };
    private static readonly IReadOnlyDictionary<string, string[]> TemplateTagExpectedKinds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["wheel"] = [MediaKinds.Wheel],
        ["wheelcarbon"] = [MediaKinds.WheelCarbon],
        ["wheelsteel"] = [MediaKinds.WheelSteel],
        ["boxart"] = [MediaKinds.BoxFront, MediaKinds.Box3d],
        ["boxback"] = [MediaKinds.BoxBack],
        ["cartridge"] = [MediaKinds.Cartridge],
        ["label"] = [MediaKinds.Label],
        ["fanart"] = [MediaKinds.Fanart],
        ["extra1"] = [MediaKinds.Flyer, MediaKinds.Figurine, MediaKinds.BoxTexture, MediaKinds.BoxSide],
        ["figurine"] = [MediaKinds.Figurine],
        ["mix"] = [MediaKinds.MixRbv2, MediaKinds.MixRbv1],
        ["titleshot"] = [MediaKinds.Image],
        ["screenshot"] = [MediaKinds.Thumbnail],
        ["bezel"] = [MediaKinds.Bezel],
        ["map"] = [MediaKinds.Map],
        ["manual"] = [MediaKinds.Manual],
        ["magazine"] = [MediaKinds.Magazine],
        ["video"] = [MediaKinds.Video, MediaKinds.VideoNormalized],
        ["screenmarquee"] = [MediaKinds.ScreenMarquee],
        ["screenmarqueesmall"] = [MediaKinds.ScreenMarqueeSmall],
        ["steamgrid"] = [MediaKinds.SteamGrid],
        ["mixrbv1"] = [MediaKinds.MixRbv1],
        ["mixrbv2"] = [MediaKinds.MixRbv2],
        ["videonormalized"] = [MediaKinds.VideoNormalized]
    };
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly ILocalizedTextStore _localizedTextStore;
    private readonly IStartupOverlayService _startupOverlayService;
    private readonly ITaskProgressService _taskProgressService;
    private readonly LocalMediaIndexService _localMediaIndexService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IGamelistStore _gamelistStore;
    private readonly ApiContext _context;
    private readonly MameGamelistGroupIndex _mameGamelistGroupIndex;
    private readonly ScreenScraperRawCacheMetadataService _rawCacheMetadataService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly MediaLocalizationResolver _mediaLocalizationResolver;
    private readonly RomMetadataResolver _romMetadataResolver;
    private readonly EsNotifyDeduplicationService _notifyDeduplication;
    private readonly ILogger<GamelistUpdateService>? _logger;
    private readonly object _selectionNormalizationStateLock = new();
    private GamelistSelectionNormalizationState? _selectionNormalizationState;
    private bool _selectionNormalizationStateDirty;
    private readonly HttpClient _esHttpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:1234"),
        Timeout = TimeSpan.FromSeconds(2)
    };

    public GamelistUpdateService(
        EmulationStationSettingsService settingsService,
        IOptionsMonitor<ApiExposeOptions> options,
        SystemIdNormalizer systemIdNormalizer,
        GameNameNormalizer gameNameNormalizer,
        ILocalizedTextStore localizedTextStore,
        IStartupOverlayService startupOverlayService,
        ITaskProgressService taskProgressService,
        LocalMediaIndexService localMediaIndexService,
        MediaRuntimeState runtimeState,
        IGamelistStore gamelistStore,
        ApiContext context,
        MameGamelistGroupIndex mameGamelistGroupIndex,
        ScreenScraperRawCacheMetadataService rawCacheMetadataService,
        InterfaceTextService interfaceTextService,
        MediaLocalizationResolver mediaLocalizationResolver,
        RomMetadataResolver romMetadataResolver,
        EsNotifyDeduplicationService notifyDeduplication,
        ILogger<GamelistUpdateService>? logger = null)
    {
        _settingsService = settingsService;
        _options = options;
        _systemIdNormalizer = systemIdNormalizer;
        _gameNameNormalizer = gameNameNormalizer;
        _localizedTextStore = localizedTextStore;
        _startupOverlayService = startupOverlayService;
        _taskProgressService = taskProgressService;
        _localMediaIndexService = localMediaIndexService;
        _runtimeState = runtimeState;
        _gamelistStore = gamelistStore;
        _context = context;
        _mameGamelistGroupIndex = mameGamelistGroupIndex;
        _rawCacheMetadataService = rawCacheMetadataService;
        _interfaceTextService = interfaceTextService;
        _mediaLocalizationResolver = mediaLocalizationResolver;
        _romMetadataResolver = romMetadataResolver;
        _notifyDeduplication = notifyDeduplication;
        _logger = logger;
    }

    public bool SaveExternalGamelistDocument(
        XDocument document,
        string gamelistPath,
        string reason,
        CancellationToken cancellationToken = default,
        bool allowMediaTagDrop = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(gamelistPath))
        {
            return false;
        }

        lock (GetGamelistLock(gamelistPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GamelistDocumentMatchesFile(document, gamelistPath))
            {
                return false;
            }

            var saved = SaveGamelistDocument(document, gamelistPath, cancellationToken, allowMediaTagDrop);
            if (saved)
            {
                _logger?.LogDebug(
                    "gamelist.xml saved through central writer: path={GamelistPath}, reason={Reason}, allowMediaTagDrop={AllowMediaTagDrop}.",
                    gamelistPath,
                    reason,
                    allowMediaTagDrop);
            }

            return saved;
        }
    }

    public Task<GamelistEntryUpdateResult> EnsureEntriesAsync(MediaProjectionPlan plan, CancellationToken cancellationToken = default)
    {
        return EnsureEntriesAsync(new[] { plan }, cancellationToken);
    }

    public Task<GamelistEntryUpdateResult> EnsureEntriesAsync(IReadOnlyList<MediaProjectionPlan> plans, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plans.Count == 0)
        {
            return Task.FromResult(GamelistEntryUpdateResult.NoChange);
        }

        var firstPlan = plans[0];
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, firstPlan.FrontendSystemId);
        Directory.CreateDirectory(systemRoot);

        var gamelistPath = firstPlan.GamelistPath;
        lock (GetGamelistLock(gamelistPath))
        {
            var document = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, "mise a jour des medias");
            if (document == null)
            {
                return Task.FromResult(GamelistEntryUpdateResult.NoChange);
            }

            var root = document.Root ?? new XElement("gameList");
            if (document.Root == null)
            {
                document.Add(root);
            }

            var changed = false;
            var mediaContentChanged = false;
            var metadataChanged = false;

            var scrapingSettings = _settingsService.GetScrapingSettings();
            foreach (var plan in plans)
            {
                var update = ApplyPlanToGamelistDocument(
                    root,
                    plan,
                    systemRoot,
                    scrapingSettings,
                    cancellationToken);
                changed |= update.Changed;
                mediaContentChanged |= update.MediaContentChanged;
                metadataChanged |= update.MetadataChanged;
            }

            if (changed)
            {
                changed = SaveGamelistDocument(document, gamelistPath, cancellationToken);
                if (!changed)
                {
                    mediaContentChanged = false;
                    metadataChanged = false;
                }
            }

            return Task.FromResult(new GamelistEntryUpdateResult(changed, mediaContentChanged, metadataChanged));
        }
    }

    public Task<GamelistEntryUpdateResult> StageExtendedEntriesAsync(MediaProjectionPlan plan, CancellationToken cancellationToken = default)
    {
        return StageExtendedEntriesAsync(new[] { plan }, cancellationToken);
    }

    public Task<GamelistEntryUpdateResult> StageExtendedEntriesAsync(IReadOnlyList<MediaProjectionPlan> plans, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plans.Count == 0)
        {
            return Task.FromResult(GamelistEntryUpdateResult.NoChange);
        }

        var firstPlan = plans[0];
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, firstPlan.FrontendSystemId);
        Directory.CreateDirectory(systemRoot);

        var pendingPath = GetPendingExtendedGamelistPath(firstPlan.FrontendSystemId);
        lock (GetGamelistLock(pendingPath))
        {
            var document = TryLoadOrCreateGamelistDocument(pendingPath, cancellationToken, "gamelist extended en attente")
                ?? CreateEmptyGamelistDocument();
            var root = document.Root ?? new XElement("gameList");
            if (document.Root == null)
            {
                document.Add(root);
            }

            var changed = false;
            var mediaContentChanged = false;
            var metadataChanged = false;
            var scrapingSettings = _settingsService.GetScrapingSettings();
            foreach (var plan in plans)
            {
                var update = ApplyPlanToGamelistDocument(
                    root,
                    plan,
                    systemRoot,
                    scrapingSettings,
                    cancellationToken);
                changed |= update.Changed;
                mediaContentChanged |= update.MediaContentChanged;
                metadataChanged |= update.MetadataChanged;
            }

            if (changed)
            {
                changed = SaveGamelistDocument(document, pendingPath, cancellationToken, allowMediaTagDrop: true);
                if (!changed)
                {
                    mediaContentChanged = false;
                    metadataChanged = false;
                }
            }

            return Task.FromResult(new GamelistEntryUpdateResult(changed, mediaContentChanged, metadataChanged));
        }
    }

    public Task<int> ApplyPendingExtendedGamelistsAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = GetPendingExtendedGamelistRoot();
        if (!Directory.Exists(root))
        {
            return Task.FromResult(0);
        }

        var appliedSystems = 0;
        foreach (var pendingPath in Directory.GetFiles(root, "*.xml").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var systemId = Path.GetFileNameWithoutExtension(pendingPath);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                continue;
            }

            try
            {
                if (ApplyPendingExtendedGamelist(systemId, pendingPath, reason, cancellationToken))
                {
                    appliedSystems++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                _logger?.LogWarning(
                    ex,
                    "Application gamelist extended pending ignoree pour system={SystemId}, path={PendingPath}.",
                    systemId,
                    pendingPath);
            }
        }

        return Task.FromResult(appliedSystems);
    }

    public Task<int> DiscardPendingExtendedGamelistsAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = GetPendingExtendedGamelistRoot();
        if (!Directory.Exists(root))
        {
            return Task.FromResult(0);
        }

        var discarded = 0;
        foreach (var pendingPath in Directory.GetFiles(root, "*.xml").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (GetGamelistLock(pendingPath))
            {
                if (!File.Exists(pendingPath))
                {
                    continue;
                }

                TryDeletePendingExtendedGamelist(pendingPath);
                if (!File.Exists(pendingPath))
                {
                    discarded++;
                }
            }
        }

        if (discarded > 0)
        {
            _logger?.LogInformation(
                "Pending extended gamelists discarded: count={Count}, reason={Reason}.",
                discarded,
                string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason);
        }

        return Task.FromResult(discarded);
    }

    private bool ApplyPendingExtendedGamelist(string systemId, string pendingPath, string reason, CancellationToken cancellationToken)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return false;
        }

        lock (GetGamelistLock(gamelistPath))
        lock (GetGamelistLock(pendingPath))
        {
            var currentDocument = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, "application gamelist extended pending");
            var pendingDocument = TryLoadOrCreateGamelistDocument(pendingPath, cancellationToken, "lecture gamelist extended pending");
            if (currentDocument?.Root == null || pendingDocument?.Root == null)
            {
                return false;
            }

            var currentRoot = currentDocument.Root;
            var targetLanguage = ResolveDominantGamelistLanguage(currentRoot);
            var baseAllowedMediaTokens = ResolvePendingExtendedAllowedMediaTokens(
                targetLanguage,
                _settingsService.GetScrapingSettings());
            var updatedGames = 0;
            var updatedTags = 0;
            foreach (var pendingGameNode in pendingDocument.Root.Elements("game").ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeGamePath = pendingGameNode.Element("path")?.Value;
                if (string.IsNullOrWhiteSpace(relativeGamePath))
                {
                    continue;
                }

                var currentGameNode = FindOrCreateGameNode(currentRoot, relativeGamePath);
                SetOrCreateElement(currentGameNode, "path", relativeGamePath);
                var gameIdentity = ResolvePendingExtendedGameIdentity(
                    systemId,
                    relativeGamePath,
                    currentGameNode,
                    pendingGameNode,
                    baseAllowedMediaTokens);
                var gameUpdatedTags = MergePendingExtendedGameNode(
                    currentGameNode,
                    pendingGameNode,
                    targetLanguage,
                    gameIdentity.AllowedMediaTokens);
                if (TrySetRomIdentityMetadataElements(currentGameNode, gameIdentity.RomRegions, gameIdentity.RomLanguages))
                {
                    gameUpdatedTags++;
                }

                if (gameUpdatedTags > 0)
                {
                    updatedGames++;
                    updatedTags += gameUpdatedTags;
                }
            }

            if (updatedTags <= 0)
            {
                TryDeletePendingExtendedGamelist(pendingPath);
                return false;
            }

            if (!SaveGamelistDocument(currentDocument, gamelistPath, cancellationToken, allowMediaTagDrop: true))
            {
                _logger?.LogWarning(
                    "Gamelist extended pending not exposed because persistence failed: system={SystemId}, path={GamelistPath}, reason={Reason}.",
                    systemId,
                    gamelistPath,
                    string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason);
                return false;
            }

            TryDeletePendingExtendedGamelist(pendingPath);
            _logger?.LogInformation(
                "Gamelist extended pending exposee: system={SystemId}, games={UpdatedGames}, tags={UpdatedTags}, reason={Reason}.",
                systemId,
                updatedGames,
                updatedTags,
                string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason);
            return true;
        }
    }

    private PendingExtendedGameIdentity ResolvePendingExtendedGameIdentity(
        string systemId,
        string relativeGamePath,
        XElement currentGameNode,
        XElement pendingGameNode,
        IReadOnlySet<string> baseAllowedMediaTokens)
    {
        var tokens = new HashSet<string>(baseAllowedMediaTokens, StringComparer.OrdinalIgnoreCase);
        var gameName = pendingGameNode.Element("name")?.Value;
        if (string.IsNullOrWhiteSpace(gameName))
        {
            gameName = currentGameNode.Element("name")?.Value;
        }

        var metadata = _romMetadataResolver.Resolve(systemId, relativeGamePath, gameName);
        foreach (var region in metadata.Regions)
        {
            AddMediaRegionToken(tokens, region);
        }

        foreach (var language in metadata.Languages)
        {
            AddLanguageMediaTokens(tokens, language);
        }

        return new PendingExtendedGameIdentity(tokens, metadata.Regions, metadata.Languages);
    }

    private static int MergePendingExtendedGameNode(
        XElement currentGameNode,
        XElement pendingGameNode,
        string targetLanguage,
        IReadOnlySet<string> allowedMediaTokens)
    {
        var updatedTags = 0;
        var currentLanguage = NormalizeGamelistLanguage(currentGameNode.Element("lang")?.Value);
        if (string.IsNullOrWhiteSpace(currentLanguage))
        {
            currentLanguage = targetLanguage;
        }

        var pendingLanguage = NormalizeGamelistLanguage(pendingGameNode.Element("lang")?.Value);
        var skipLocalizedMetadata = !string.IsNullOrWhiteSpace(currentLanguage) &&
            !string.IsNullOrWhiteSpace(pendingLanguage) &&
            !string.Equals(currentLanguage, pendingLanguage, StringComparison.OrdinalIgnoreCase);

        foreach (var tagName in PendingExtendedGamelistMergeTags)
        {
            var pendingElement = pendingGameNode.Element(tagName);
            if (pendingElement == null)
            {
                continue;
            }

            if (skipLocalizedMetadata && PendingExtendedLocalizedMetadataTags.Contains(tagName))
            {
                continue;
            }

            var pendingValue = pendingElement.Value;
            if (skipLocalizedMetadata &&
                PendingExtendedPotentiallyLocalizedMediaTags.Contains(tagName) &&
                !IsPendingExtendedMediaPathCompatibleWithTarget(pendingValue, allowedMediaTokens))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(pendingValue) && !IsVisibleSlotTag(tagName))
            {
                continue;
            }

            if (TrySetPendingExtendedElement(currentGameNode, tagName, pendingValue))
            {
                updatedTags++;
            }
        }

        return updatedTags;
    }

    private static bool TrySetPendingExtendedElement(XElement gameNode, string tagName, string value)
    {
        var element = gameNode.Element(tagName);
        if (element == null)
        {
            gameNode.Add(new XElement(tagName, value));
            return true;
        }

        if (string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        element.Value = value;
        return true;
    }

    private static bool IsVisibleSlotTag(string tagName)
    {
        return string.Equals(tagName, "image", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tagName, "thumbnail", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tagName, "marquee", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tagName, "fanart", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDominantGamelistLanguage(XElement root)
    {
        return root.Elements("game")
            .Select(node => NormalizeGamelistLanguage(node.Element("lang")?.Value))
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .GroupBy(language => language, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string NormalizeGamelistLanguage(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        return normalized.Length >= 2 ? normalized[..2] : string.Empty;
    }

    private static IReadOnlySet<string> ResolvePendingExtendedAllowedMediaTokens(
        string targetLanguage,
        EmulationStationScrapingSettings settings)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wor",
            "world"
        };

        AddLanguageMediaTokens(tokens, targetLanguage);
        AddLanguageMediaTokens(tokens, settings.Language);
        AddLanguageMediaTokens(tokens, settings.ContentLanguageProfile);

        var mediaRegionMode = NormalizePendingExtendedMediaMode(settings.MediaRegionMode);
        if (mediaRegionMode is "content_region_profile" or "all")
        {
            AddMediaRegionToken(tokens, settings.UserRegion);
            AddMediaRegionToken(tokens, settings.ContentRegionProfile);
        }
        else if (mediaRegionMode is "interface_locale")
        {
            AddLanguageMediaTokens(tokens, settings.Language);
        }

        var logoRegionMode = NormalizePendingExtendedMediaMode(settings.LogoRegionMode);
        if (logoRegionMode is "content_region_profile" or "all")
        {
            AddMediaRegionToken(tokens, settings.UserRegion);
            AddMediaRegionToken(tokens, settings.ContentRegionProfile);
        }
        else if (logoRegionMode is "user_language" or "interface_locale")
        {
            AddLanguageMediaTokens(tokens, settings.ContentLanguageProfile);
            AddLanguageMediaTokens(tokens, settings.Language);
        }
        else if (logoRegionMode is "match_rom_region")
        {
            AddMediaRegionToken(tokens, settings.ContentRegionProfile);
        }

        AddMediaRegionToken(tokens, settings.UserRegion);
        AddMediaRegionToken(tokens, settings.ContentRegionProfile);
        return tokens;
    }

    private static bool IsPendingExtendedMediaPathCompatibleWithTarget(
        string? mediaPath,
        IReadOnlySet<string> allowedMediaTokens)
    {
        var suffixToken = ExtractMediaLocalizationSuffixToken(mediaPath);
        if (string.IsNullOrWhiteSpace(suffixToken))
        {
            return true;
        }

        return allowedMediaTokens.Contains(suffixToken);
    }

    private static string ExtractMediaLocalizationSuffixToken(string? mediaPath)
    {
        var stem = Path.GetFileNameWithoutExtension((mediaPath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(stem))
        {
            return string.Empty;
        }

        var parts = stem
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse();
        foreach (var part in parts)
        {
            var normalized = NormalizeMediaLocalizationToken(part);
            if (MediaLocalizationTokens.Contains(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static void AddLanguageMediaTokens(HashSet<string> target, string? language)
    {
        switch (NormalizeLanguageCode(language))
        {
            case "en":
                AddMediaRegionToken(target, "en");
                AddMediaRegionToken(target, "us");
                AddMediaRegionToken(target, "uk");
                AddMediaRegionToken(target, "eu");
                break;
            case "fr":
                AddMediaRegionToken(target, "fr");
                AddMediaRegionToken(target, "eu");
                break;
            case "de":
                AddMediaRegionToken(target, "de");
                AddMediaRegionToken(target, "eu");
                break;
            case "it":
                AddMediaRegionToken(target, "it");
                AddMediaRegionToken(target, "eu");
                break;
            case "es":
                AddMediaRegionToken(target, "sp");
                AddMediaRegionToken(target, "es");
                AddMediaRegionToken(target, "eu");
                break;
            case "pt":
                AddMediaRegionToken(target, "br");
                AddMediaRegionToken(target, "eu");
                break;
            case "ja":
                AddMediaRegionToken(target, "jp");
                break;
            case "nl":
                AddMediaRegionToken(target, "nl");
                AddMediaRegionToken(target, "eu");
                break;
            default:
                AddLanguageProfileMediaTokens(target, language);
                break;
        }
    }

    private static void AddLanguageProfileMediaTokens(HashSet<string> target, string? profile)
    {
        switch ((profile ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "english":
                AddLanguageMediaTokens(target, "en");
                break;
            case "french":
                AddLanguageMediaTokens(target, "fr");
                break;
            case "german":
                AddLanguageMediaTokens(target, "de");
                break;
            case "italian":
                AddLanguageMediaTokens(target, "it");
                break;
            case "spanish":
                AddLanguageMediaTokens(target, "es");
                break;
            case "portuguese":
                AddLanguageMediaTokens(target, "pt");
                break;
            case "japanese":
                AddLanguageMediaTokens(target, "ja");
                break;
            case "dutch":
                AddLanguageMediaTokens(target, "nl");
                break;
        }
    }

    private static void AddMediaRegionToken(HashSet<string> target, string? value)
    {
        var token = NormalizeMediaLocalizationToken(value);
        if (!string.IsNullOrWhiteSpace(token) && MediaLocalizationTokens.Contains(token))
        {
            target.Add(token);
        }
    }

    private static string NormalizeMediaLocalizationToken(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "english" => "en",
            "french" or "france" => "fr",
            "german" or "germany" => "de",
            "italian" or "italy" => "it",
            "spanish" or "spain" => "sp",
            "japanese" or "japan" => "jp",
            "dutch" or "netherlands" => "nl",
            "portuguese" or "brazil" => "br",
            "u" or "america" => "us",
            "e" or "europe" => "eu",
            "w" => "wor",
            "j" => "jp",
            "en-us" or "usa" => "us",
            "en-gb" or "gb" => "uk",
            "eur" => "eu",
            "world" => "wor",
            "ger" => "de",
            "spa" => "sp",
            "jpn" or "ja" => "jp",
            _ => normalized
        };
    }

    private static string NormalizePendingExtendedMediaMode(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "content_region_profile" or "content_profile" or "region_profile" or "region" or "preferred" => "content_region_profile",
            "interface_locale" or "interface" or "locale" or "ui" or "user_locale" => "interface_locale",
            "user_language" or "language" or "lang" => "user_language",
            "match_rom_region" or "match_rom" or "rom" or "original" => "match_rom_region",
            "all" => "all",
            _ => string.Empty
        };
    }

    private static string GetPendingExtendedGamelistRoot()
    {
        return Path.Combine(RetroBatPaths.MediaAliasesSharedRoot, "gamelist-extended-pending");
    }

    private static string GetPendingExtendedGamelistPath(string systemId)
    {
        var normalized = NormalizeSelectionValue(systemId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "unknown";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(c, '_');
        }

        return Path.Combine(GetPendingExtendedGamelistRoot(), normalized + ".xml");
    }

    private void TryDeletePendingExtendedGamelist(string pendingPath)
    {
        try
        {
            File.Delete(pendingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Suppression pending extended gamelist ignoree: {PendingPath}", pendingPath);
        }
    }

    private GamelistEntryUpdateResult ApplyPlanToGamelistDocument(
        XElement root,
        MediaProjectionPlan plan,
        string systemRoot,
        EmulationStationScrapingSettings scrapingSettings,
        CancellationToken cancellationToken)
    {
        var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
        var gameNode = FindOrCreateGameNode(root, relativeGamePath);
        var beforeGameXml = gameNode.ToString(SaveOptions.DisableFormatting);

        SetOrCreateElement(gameNode, "path", relativeGamePath);
        ApplyGameIdentityElements(gameNode, plan);
        var projectedKindPaths = EmptyKindPaths();
        var kindPaths = BuildCanonicalKindPathsFromPlan(plan, systemRoot);
        var preferredBundle = ResolvePreferredBundle(plan.SystemId, ResolveTextLookupSlug(plan), scrapingSettings.Language, cancellationToken);

        foreach (var need in plan.Needs)
        {
            var sourcePath = ResolveCanonicalPlanMediaPath(need, systemRoot);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                continue;
            }

            var relativeMediaPath = ToMediaRelativePath(sourcePath, systemRoot);
            if (string.IsNullOrWhiteSpace(relativeMediaPath))
            {
                continue;
            }

            kindPaths[need.Kind] = relativeMediaPath;
            var tagName = need.Kind switch
            {
                MediaKinds.Fanart => "fanart",
                MediaKinds.Bezel => "bezel",
                MediaKinds.Cartridge => "cartridge",
                MediaKinds.Label => "label",
                MediaKinds.BoxBack => "boxback",
                MediaKinds.Map => "map",
                MediaKinds.Manual => "manual",
                MediaKinds.Magazine => "magazine",
                MediaKinds.Video => "video",
                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(tagName))
            {
                SetOrCreateElement(gameNode, tagName, relativeMediaPath);
            }
        }

        ApplyTemplateMediaElements(gameNode, kindPaths);

        TrySetVisibleSlotElement(gameNode, "image", ResolveSelectedMediaPathStrict(
            scrapingSettings.ImageSource,
            kindPaths,
            projectedKindPaths,
            MediaSelectionTarget.Image,
            scrapingSettings.WheelStyle));
        TrySetVisibleSlotElement(gameNode, "marquee", ResolveSelectedMediaPathStrict(
            scrapingSettings.LogoSource,
            kindPaths,
            projectedKindPaths,
            MediaSelectionTarget.Logo,
            scrapingSettings.WheelStyle));
        TrySetVisibleSlotElement(gameNode, "thumbnail", ResolveSelectedMediaPathStrict(
            scrapingSettings.ThumbSource,
            kindPaths,
            projectedKindPaths,
            MediaSelectionTarget.Thumbnail,
            scrapingSettings.WheelStyle));

        var preferredDescription = ResolveBundleField(preferredBundle, "desc", scrapingSettings.Language);
        if (!string.IsNullOrWhiteSpace(preferredDescription))
        {
            SetOrCreateElement(gameNode, "desc", preferredDescription);
        }
        else
        {
            RemoveWrongLanguageElement(gameNode, "desc", ResolveTargetLanguage(preferredBundle, scrapingSettings.Language, gameNode));
        }

        ApplyBundleMetadata(gameNode, preferredBundle, scrapingSettings.Language);
        ApplyPlanRomIdentityMetadata(gameNode, plan);
        var afterGameXml = gameNode.ToString(SaveOptions.DisableFormatting);
        var changed = !string.Equals(
            beforeGameXml,
            afterGameXml,
            StringComparison.Ordinal);
        if (!changed)
        {
            return GamelistEntryUpdateResult.NoChange;
        }

        var beforeSignature = BuildGamelistEntryContentSignature(XElement.Parse(beforeGameXml), systemRoot);
        var afterSignature = BuildGamelistEntryContentSignature(gameNode, systemRoot);
        return new GamelistEntryUpdateResult(
            true,
            HasMeaningfulMediaContentChange(beforeSignature, afterSignature),
            !string.Equals(beforeSignature.NonMediaXml, afterSignature.NonMediaXml, StringComparison.Ordinal));
    }

    public async Task<bool> PushLiveGameUpdateToEsAsync(
        MediaProjectionPlan plan,
        CancellationToken cancellationToken = default,
        LiveGameUpdateNotificationKind notificationKind = LiveGameUpdateNotificationKind.RemoteScrape,
        bool allowCurrentVideoRefresh = false,
        bool allowLocalizedMetadataRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(plan.FrontendSystemId) ||
            string.IsNullOrWhiteSpace(plan.GamePath))
        {
            return false;
        }

        EnsurePlanEsGameId(plan);
        if (string.IsNullOrWhiteSpace(plan.EsGameId))
        {
            _logger?.LogDebug(
                "Live ES game fragment skipped for system={SystemId}, game={GameSlug}: ES gameid could not be generated from path={GamePath}.",
                plan.FrontendSystemId,
                plan.GameSlug,
                plan.GamePath);
            return false;
        }

        if (await TryQueueLiveAddGamesBlockedByGameSessionAsync(
                plan,
                "before-build",
                dirtyBatchCount: 0,
                relatedBatchCount: 0,
                cancellationToken))
        {
            return false;
        }

        if (!IsCurrentlySelectedGame(plan))
        {
            _logger?.LogInformation(
                "Live ES game fragment skipped for system={SystemId}, game={GameSlug}: target is not the currently selected game.",
                plan.FrontendSystemId,
                plan.GameSlug);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "skipped-not-current",
                new { stage = "before-build" },
                cancellationToken);
            await MediaUpdateAuditLog.AppendAsync(plan, "live-addgames", "gamelist", "skipped-not-current", cancellationToken: cancellationToken);
            return false;
        }

        var hasPendingDirtyBatch = HasPendingDirtyLiveGamelistBatch(plan);
        // Hard contract: a single game-selected card can produce at most one live /addgames,
        // except the explicit video rule: a freshly scraped video for the still-current
        // card may consume one additional /addgames. Timing delays may only space
        // different selections; they must never reopen this gate.
        if (_runtimeState.ShouldSuppressLiveAddGamesForSelection(
                plan.FrontendSystemId,
                plan.GamePath,
                out var alreadyPushedReason,
                allowVideoException: allowCurrentVideoRefresh))
        {
            _logger?.LogInformation(
                "Live ES game fragment skipped for system={SystemId}, game={GameSlug}: addgames already pushed for the current selected card.",
                plan.FrontendSystemId,
                plan.GameSlug);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "live-addgames",
                "gamelist",
                "skipped-current-selection-already-pushed",
                new { reason = alreadyPushedReason, hasPendingDirtyBatch, allowCurrentVideoRefresh, allowLocalizedMetadataRefresh },
                cancellationToken);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "skipped-current-selection-already-pushed",
                new { reason = alreadyPushedReason, hasPendingDirtyBatch, allowCurrentVideoRefresh, allowLocalizedMetadataRefresh },
                cancellationToken);
            return false;
        }

        var gameElement = BuildLiveGameElement(plan, cancellationToken);
        if (_runtimeState.ShouldSuppressLiveAddGames(plan.FrontendSystemId, plan.GameSlug, out var suppressReason))
        {
            MarkLiveGamelistDirty(plan);
            _logger?.LogInformation(
                "Live ES game fragment queued instead of pushed for system={SystemId}, game={GameSlug}: addgames suppressed after HyperBat refresh ({Reason}).",
                plan.FrontendSystemId,
                plan.GameSlug,
                suppressReason);
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "live-addgames",
                "gamelist",
                "queued-after-hyperbat-refresh",
                new { reason = suppressReason, hasPendingDirtyBatch },
                cancellationToken);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "queued-after-hyperbat-refresh",
                new { reason = suppressReason, hasPendingDirtyBatch },
                cancellationToken);
            return false;
        }

        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "live-addgames-rating",
            "rating",
            "built",
            new
            {
                xmlRating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                xmlName = gameElement.Element("name")?.Value?.Trim() ?? string.Empty,
                xmlPath = gameElement.Element("path")?.Value?.Trim() ?? string.Empty,
                existingGamelistRating = ReadExistingGamelistRating(plan, cancellationToken),
                metadataPayloadRating = BuildLiveMetadataPayload(plan, gameElement).TryGetValue("rating", out var payloadRating)
                    ? payloadRating
                    : string.Empty,
                hasLiveVisibleMedia = HasLiveVisibleMedia(gameElement),
                allowLocalizedMetadataRefresh,
                liveEsMediaPushEnabled = _options.CurrentValue.Scraping.LiveEsMediaPushEnabled,
                liveEsMetadataPushEnabled = _options.CurrentValue.Scraping.LiveEsMetadataPushEnabled
            },
            cancellationToken);
        if (_options.CurrentValue.Scraping.LiveEsMediaPushEnabled)
        {
            var pushed = await PushLiveGamelistFragmentToEsAsync(
                plan,
                gameElement,
                includeDirtySameSystem: true,
                allowCurrentVideoRefresh,
                allowLocalizedMetadataRefresh,
                cancellationToken);
            if (pushed && ShouldNotifyLiveAddGamesUpdate(plan, gameElement))
            {
                var notification = ResolveLiveGameUpdateNotification(plan, notificationKind, gameElement, cancellationToken);
                if (string.IsNullOrWhiteSpace(notification))
                {
                    notification = ResolveGameCardUpdatedMessage(plan);
                }

                if (!string.IsNullOrWhiteSpace(notification))
                {
                    await NotifyEsAsync(notification, cancellationToken);
                }
            }
            return pushed;
        }

        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "live-addgames-rating",
            "rating",
            "skipped-disabled",
            new
            {
                xmlRating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                liveEsMediaPushEnabled = _options.CurrentValue.Scraping.LiveEsMediaPushEnabled
            },
            cancellationToken);
        return false;
    }

    private bool ShouldNotifyLiveAddGamesUpdate(MediaProjectionPlan plan, XElement gameElement)
    {
        if (!IsCurrentlySelectedGame(plan))
        {
            return false;
        }

        var payloadPath = gameElement.Element("path")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            return false;
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var expectedPath = Path.IsPathRooted(payloadPath)
            ? plan.GamePath
            : ToGameRelativePath(plan.GamePath, systemRoot);

        return string.Equals(
            NormalizeGamePathForSelection(payloadPath),
            NormalizeGamePathForSelection(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

    public void MarkLiveGamelistDirty(MediaProjectionPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.FrontendSystemId) ||
            string.IsNullOrWhiteSpace(plan.GamePath) ||
            string.IsNullOrWhiteSpace(plan.GameSlug))
        {
            return;
        }

        if (IsCurrentlySelectedGame(plan))
        {
            _logger?.LogDebug(
                "Current game queued for a future live addgames batch: system={SystemId}, game={GameSlug}, path={GamePath}.",
                plan.FrontendSystemId,
                plan.GameSlug,
                plan.GamePath);
        }

        var key = BuildDirtyLiveGamelistKey(plan.FrontendSystemId, plan.GameSlug, plan.GamePath);
        DirtyLiveGamelistPlans[key] = new DirtyLiveGamelistPlan(
            key,
            plan.FrontendSystemId,
            plan.GameSlug,
            plan.GamePath,
            ClonePlanForDirtyLiveGamelist(plan),
            DateTime.UtcNow);

        _logger?.LogDebug(
            "Live ES dirty game queued for next addgames batch: system={SystemId}, game={GameSlug}, path={GamePath}.",
            plan.FrontendSystemId,
            plan.GameSlug,
            plan.GamePath);
    }

    private async Task<bool> TryQueueLiveAddGamesBlockedByGameSessionAsync(
        MediaProjectionPlan plan,
        string stage,
        int dirtyBatchCount,
        int relatedBatchCount,
        CancellationToken cancellationToken)
    {
        if (!_runtimeState.ShouldBlockLiveAddGames(out var reason, out var retryAfter))
        {
            return false;
        }

        MarkLiveGamelistDirty(plan);
        var status = string.Equals(reason, "game-start-active", StringComparison.OrdinalIgnoreCase)
            ? "queued-game-session-active"
            : "queued-post-game-end-quiet";
        _logger?.LogInformation(
            "Live ES addgames deferred for system={SystemId}, game={GameSlug}: {Reason} at {Stage}. It will be included in a later addgames batch.",
            plan.FrontendSystemId,
            plan.GameSlug,
            reason,
            stage);
        var details = new
        {
            reason,
            stage,
            retryAfterMilliseconds = retryAfter > TimeSpan.Zero ? (int)Math.Ceiling(retryAfter.TotalMilliseconds) : 0,
            deferredMode = "next-addgames-batch",
            dirtyBatchCount,
            relatedBatchCount
        };
        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "live-addgames",
            "gamelist",
            status,
            details,
            cancellationToken);
        await RefreshTrackingLog.AppendAsync(
            plan,
            "addgames",
            status,
            details,
            cancellationToken);
        return true;
    }

    public string GenerateEsGameIdForPath(string frontendSystemId, string gamePath)
    {
        if (string.IsNullOrWhiteSpace(frontendSystemId) || string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        return BuildEsGameIdFromPath(systemRoot, gamePath);
    }

    public Task<bool> PushScrapedMediaToEsAsync(MediaProjectionPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger?.LogDebug(
            "Direct ES media POST disabled; live refresh is limited to addgames for system={SystemId}, game={GameSlug}.",
            plan.FrontendSystemId,
            plan.GameSlug);
        return Task.FromResult(false);
    }

    private async Task<string> ResolveEsGameIdForPlanAsync(MediaProjectionPlan plan, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(plan.EsGameId))
        {
            return plan.EsGameId.Trim();
        }

        return GenerateEsGameIdForPath(plan.FrontendSystemId, plan.GamePath);
    }

    private void EnsurePlanEsGameId(MediaProjectionPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.EsGameId))
        {
            plan.EsGameId = plan.EsGameId.Trim();
            return;
        }

        plan.EsGameId = GenerateEsGameIdForPath(plan.FrontendSystemId, plan.GamePath);
    }

    public async Task NotifyEsAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string? acceptedNotifyMessage = null;
        try
        {
            var safeMessage = EsNotificationText.SanitizeForEsPopup(message);
            if (string.IsNullOrWhiteSpace(safeMessage))
            {
                return;
            }

            if (!_notifyDeduplication.TryAccept(safeMessage))
            {
                _logger?.LogDebug("ES notify duplicate suppressed: {Message}", safeMessage);
                return;
            }

            acceptedNotifyMessage = safeMessage;
            using var content = new StringContent(safeMessage, Encoding.UTF8, "text/plain");
            using var response = await _esHttpClient.PostAsync("/notify", content, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                _notifyDeduplication.ForgetIfCurrent(safeMessage);
                _logger?.LogDebug(
                    "ES notify returned HTTP {StatusCode}: {Message}",
                    (int)response.StatusCode,
                    safeMessage);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notifyDeduplication.ForgetIfCurrent(acceptedNotifyMessage ?? message);
            _logger?.LogDebug(ex, "ES notify skipped: EmulationStation API unavailable.");
        }
    }

    public async Task NotifyLiveScrapeStartedIfCurrentAsync(
        MediaProjectionPlan plan,
        string message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (!IsCurrentlySelectedGame(plan))
            {
                return;
            }

            await NotifyEsAsync(message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Delayed live scrape-start notify skipped.");
        }
    }

    public bool IsCurrentlySelectedGame(MediaProjectionPlan plan)
    {
        if (_runtimeState.HasCurrentGameSelection())
        {
            return _runtimeState.IsCurrentGameSelection(plan.FrontendSystemId, plan.GamePath) ||
                _runtimeState.IsCurrentGameSelection(plan.SystemId, plan.GamePath);
        }

        if (TryReadCurrentSelectedGameFromEventsIni(out var currentSystemId, out var currentGamePath))
        {
            return IsSelectionMatch(currentSystemId, currentGamePath, plan);
        }

        var selected = _context.Ui.Selected;
        if (selected == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selected.GameId) &&
            !string.IsNullOrWhiteSpace(plan.EsGameId) &&
            string.Equals(selected.GameId, plan.EsGameId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var selectedSystem = selected.SystemId;
        if (string.IsNullOrWhiteSpace(selectedSystem))
        {
            selectedSystem = _context.Ui.SelectedSystem?.Name ?? string.Empty;
        }

        return IsSelectionMatch(selectedSystem, selected.GamePath, plan);
    }

    private static bool IsSelectionMatch(string selectedSystem, string? selectedGamePath, MediaProjectionPlan plan)
    {
        var sameSystem =
            string.Equals(selectedSystem, plan.FrontendSystemId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectedSystem, plan.SystemId, StringComparison.OrdinalIgnoreCase);
        if (!sameSystem)
        {
            return false;
        }

        return string.Equals(
            NormalizeGamePathForSelection(selectedGamePath),
            NormalizeGamePathForSelection(plan.GamePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadCurrentSelectedGameFromEventsIni(out string systemId, out string gamePath)
    {
        systemId = string.Empty;
        gamePath = string.Empty;

        try
        {
            if (!File.Exists(RetroBatPaths.EventsIniPath))
            {
                return false;
            }

            var lines = File.ReadAllLines(RetroBatPaths.EventsIniPath);
            if (lines.Length < 2 ||
                !string.Equals(lines[0].Trim(), "event=game-selected", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var eventLine = lines.Skip(1).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            if (string.IsNullOrWhiteSpace(eventLine))
            {
                return false;
            }

            var args = ParseEventArguments(eventLine);
            if (args.Count < 2)
            {
                return false;
            }

            systemId = args[0];
            gamePath = args[1];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ParseEventArguments(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }

                continue;
            }

            currentArg.Append(c);
        }

        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }

        return args;
    }

    private bool HasKnownSelectedGame()
    {
        return _context.Ui.Selected != null;
    }

    private static string NormalizeGamePathForSelection(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .TrimStart('.', '/');
    }

    private static string NormalizeSelectionValue(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string BuildDirtyLiveGamelistKey(string frontendSystemId, string gameSlug, string gamePath)
    {
        return string.Join(
            "|",
            (frontendSystemId ?? string.Empty).Trim().ToLowerInvariant(),
            (gameSlug ?? string.Empty).Trim().ToLowerInvariant(),
            NormalizeGamePathForSelection(gamePath).ToLowerInvariant());
    }

    private static MediaProjectionPlan ClonePlanForDirtyLiveGamelist(MediaProjectionPlan source)
    {
        return new MediaProjectionPlan
        {
            SystemId = source.SystemId,
            FrontendSystemId = source.FrontendSystemId,
            GameSlug = source.GameSlug,
            TextSourceGameSlug = source.TextSourceGameSlug,
            DisplayName = source.DisplayName,
            GamePath = source.GamePath,
            ProjectionBaseName = source.ProjectionBaseName,
            PreferredImageSource = source.PreferredImageSource,
            PreferredLogoSource = source.PreferredLogoSource,
            PreferredThumbnailSource = source.PreferredThumbnailSource,
            GamePathExists = source.GamePathExists,
            GamelistMd5 = source.GamelistMd5,
            GamelistCrc32 = source.GamelistCrc32,
            GamelistPath = source.GamelistPath,
            EsGameId = source.EsGameId,
            ScreenScraperGameId = source.ScreenScraperGameId,
            RomRegions = source.RomRegions.ToList(),
            RomLanguages = source.RomLanguages.ToList(),
            NeedsDescriptionScrape = source.NeedsDescriptionScrape,
            SuppressImmediateGamelistUpdates = source.SuppressImmediateGamelistUpdates,
            IsArcadeLike = source.IsArcadeLike,
            IsFolderBasedSystem = source.IsFolderBasedSystem,
            SkipCrcComputation = source.SkipCrcComputation,
            IsFilteredArcadeBiosCandidate = source.IsFilteredArcadeBiosCandidate,
            IgnoreRemoteScrapeCooldown = source.IgnoreRemoteScrapeCooldown,
            Needs = source.Needs
                .Select(static need => new MediaNeed
                {
                    Kind = need.Kind,
                    IsMissing = need.IsMissing,
                    InitialExistingPath = need.InitialExistingPath,
                    ExistingPath = need.ExistingPath,
                    TargetRelativePath = need.TargetRelativePath,
                    ImportedPath = need.ImportedPath,
                    ProjectedPath = need.ProjectedPath,
                    WasImported = need.WasImported,
                    WasProjected = need.WasProjected,
                    WasContentChanged = need.WasContentChanged
                })
                .ToList()
        };
    }

    private string ResolveLiveGameUpdateNotification(
        MediaProjectionPlan plan,
        LiveGameUpdateNotificationKind notificationKind,
        XElement gameElement,
        CancellationToken cancellationToken)
    {
        return notificationKind switch
        {
            LiveGameUpdateNotificationKind.RemoteScrape => ResolveRemoteScrapeLiveUpdateMessage(plan, gameElement, cancellationToken),
            LiveGameUpdateNotificationKind.RemoteVideoScrape => ResolveRemoteVideoScrapeLiveUpdateMessage(plan),
            LiveGameUpdateNotificationKind.LocalProjection => ResolveLocalProjectionSuccessfulMessage(plan, gameElement),
            _ => string.Empty
        };
    }

    private string ResolveRemoteVideoScrapeLiveUpdateMessage(MediaProjectionPlan plan)
    {
        var language = _settingsService.GetScrapingSettings().Language;
        var gameName = ResolveNotifyGameName(plan);

        return _interfaceTextService.Format(
            "notification.live.video_added",
            language,
            ("game", gameName));
    }

    private string ResolveRemoteScrapeLiveUpdateMessage(
        MediaProjectionPlan plan,
        XElement gameElement,
        CancellationToken cancellationToken)
    {
        var updatedLabels = ResolveLiveRefreshLabels(plan, gameElement, cancellationToken)
            .Concat(ResolveMediaRefreshLabels(plan, gameElement))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        if (updatedLabels.Length == 0)
        {
            return string.Empty;
        }

        var language = _settingsService.GetScrapingSettings().Language;
        var gameName = ResolveNotifyGameName(plan);
        var details = string.Join(", ", updatedLabels);

        return _interfaceTextService.Format(
            "notification.live.remote_updated",
            language,
            ("details", details),
            ("game", gameName));
    }

    private IEnumerable<string> ResolveLiveRefreshLabels(
        MediaProjectionPlan plan,
        XElement gameElement,
        CancellationToken cancellationToken)
    {
        var language = _settingsService.GetScrapingSettings().Language;
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
        var existingGameNode = TryLoadExistingGameNode(plan.GamelistPath, relativeGamePath, cancellationToken);
        if (existingGameNode == null)
        {
            yield break;
        }

        foreach (var pair in BuildLiveMetadataPayload(plan, gameElement))
        {
            if (string.Equals(pair.Key, "scraperId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingValue = existingGameNode.Element(pair.Key)?.Value?.Trim() ?? string.Empty;
            if (!string.Equals(existingValue, pair.Value, StringComparison.Ordinal))
            {
                yield return ResolveLiveRefreshLabel(pair.Key, language);
            }
        }
    }

    private string ResolveLiveRefreshLabel(string tagName, string? language)
    {
        var key = tagName.ToLowerInvariant() switch
        {
            "name" => "label.metadata.name",
            "image" => "label.metadata.image",
            "thumbnail" => "label.metadata.thumbnail",
            "marquee" => "label.metadata.marquee",
            "desc" => "label.metadata.desc",
            "releasedate" => "label.metadata.releasedate",
            "developer" => "label.metadata.developer",
            "publisher" => "label.metadata.publisher",
            "players" => "label.metadata.players",
            "rating" => "label.metadata.rating",
            "genre" => "label.metadata.genre",
            "genres" => "label.metadata.genres",
            "family" => "label.metadata.family",
            "region" => "label.metadata.region",
            "lang" => "label.metadata.lang",
            "md5" => "label.metadata.md5",
            "source" => "label.metadata.source",
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(key)
            ? tagName
            : _interfaceTextService.Text(key, language);
    }

    private string ResolveGameCardUpdatedMessage(MediaProjectionPlan plan)
    {
        var language = _settingsService.GetScrapingSettings().Language;
        var gameName = ResolveNotifyGameName(plan);

        return _interfaceTextService.Format(
            "notification.live.remote_updated_simple",
            language,
            ("game", gameName));
    }

    private string ResolveLocalProjectionSuccessfulMessage(MediaProjectionPlan plan, XElement gameElement)
    {
        var language = _settingsService.GetScrapingSettings().Language;
        var gameName = ResolveNotifyGameName(plan);
        var updatedLabels = ResolveMediaRefreshLabels(plan, gameElement)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        if (updatedLabels.Length == 0)
        {
            return string.Empty;
        }

        return _interfaceTextService.Format(
            "notification.live.local_applied",
            language,
            ("details", string.Join(", ", updatedLabels)),
            ("game", gameName));
    }

    private IReadOnlyList<string> ResolveMediaRefreshLabels(MediaProjectionPlan plan, XElement gameElement)
    {
        var changedKinds = plan.Needs
            .Where(need => need.WasContentChanged || need.WasProjected || need.WasImported)
            .Select(need => MediaKinds.Normalize(need.Kind))
            .Where(kind => !string.IsNullOrWhiteSpace(kind) &&
                !string.Equals(kind, MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changedKinds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var scrapingSettings = _settingsService.GetScrapingSettings();
        var visibleSlots = new[]
        {
            ("image", NormalizeSelectionSourceToKind(scrapingSettings.ImageSource, MediaSelectionTarget.Image, scrapingSettings.WheelStyle)),
            ("thumbnail", NormalizeSelectionSourceToKind(scrapingSettings.ThumbSource, MediaSelectionTarget.Thumbnail, scrapingSettings.WheelStyle)),
            ("marquee", NormalizeSelectionSourceToKind(scrapingSettings.LogoSource, MediaSelectionTarget.Logo, scrapingSettings.WheelStyle))
        };

        var labels = visibleSlots
            .Where(slot =>
                changedKinds.Contains(MediaKinds.Normalize(slot.Item2)) &&
                !string.IsNullOrWhiteSpace(gameElement.Element(slot.Item1)?.Value))
            .Select(slot => ResolveLiveRefreshLabel(slot.Item1, scrapingSettings.Language))
            .ToList();
        if (labels.Count > 0)
        {
            return labels;
        }

        return changedKinds
            .Select(kind => ResolveMediaKindRefreshLabel(kind, scrapingSettings.Language))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToArray();
    }

    private string ResolveMediaKindRefreshLabel(string kind, string? language)
    {
        var key = MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Image => "label.media.image",
            MediaKinds.Thumbnail => "label.media.thumbnail",
            MediaKinds.Wheel or MediaKinds.WheelCarbon or MediaKinds.WheelSteel => "label.media.logo",
            MediaKinds.Marquee => "label.media.marquee",
            MediaKinds.BoxFront => "label.media.boxfront",
            MediaKinds.Box3d => "label.media.box3d",
            MediaKinds.BoxBack => "label.media.boxback",
            MediaKinds.BoxSide => "label.media.boxside",
            MediaKinds.BoxTexture => "label.media.boxtexture",
            MediaKinds.Cartridge => "label.media.cartridge",
            MediaKinds.Label => "label.media.label",
            MediaKinds.Fanart => "label.media.fanart",
            MediaKinds.Flyer => "label.media.flyer",
            MediaKinds.Figurine => "label.media.figurine",
            MediaKinds.Bezel => "label.media.bezel",
            MediaKinds.Map => "label.media.map",
            MediaKinds.Manual => "label.media.manual",
            MediaKinds.Magazine => "label.media.magazine",
            MediaKinds.Video => "label.media.video",
            MediaKinds.VideoNormalized => "label.media.video_normalized",
            MediaKinds.ThemeHb => "label.media.themehb",
            MediaKinds.ScreenMarquee => "label.media.screenmarquee",
            MediaKinds.ScreenMarqueeSmall => "label.media.screenmarqueesmall",
            MediaKinds.SteamGrid => "label.media.steamgrid",
            MediaKinds.MixRbv1 => "label.media.mixrbv1",
            MediaKinds.MixRbv2 => "label.media.mixrbv2",
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(key)
            ? kind
            : _interfaceTextService.Text(key, language);
    }

    private static string ResolveNotifyGameName(MediaProjectionPlan plan)
    {
        string gameName;
        if (!string.IsNullOrWhiteSpace(plan.DisplayName))
        {
            gameName = plan.DisplayName.Trim();
            return EsNotificationText.ShortGameName(gameName);
        }

        if (!string.IsNullOrWhiteSpace(plan.ProjectionBaseName))
        {
            gameName = plan.ProjectionBaseName.Trim();
            return EsNotificationText.ShortGameName(gameName);
        }

        var fileName = Path.GetFileNameWithoutExtension(plan.GamePath ?? string.Empty);
        gameName = string.IsNullOrWhiteSpace(fileName)
            ? plan.GameSlug
            : fileName.Trim();
        return EsNotificationText.ShortGameName(gameName);
    }

    private XElement BuildLiveGameElement(MediaProjectionPlan plan, CancellationToken cancellationToken)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
        var gameNode = new XElement("game");
        var existingGameNode = TryLoadExistingGameNode(plan.GamelistPath, relativeGamePath, cancellationToken);
        var scrapingSettings = _settingsService.GetScrapingSettings();

        SetOrCreateElement(gameNode, "path", relativeGamePath);
        ApplyGameIdentityElements(gameNode, plan);

        if (!string.IsNullOrWhiteSpace(plan.DisplayName))
        {
            SetOrCreateElement(gameNode, "name", _gameNameNormalizer.NormalizeDisplayName(plan.DisplayName, plan.GamePath));
        }

        var projectedKindPaths = EmptyKindPaths();
        var canonicalSystemId = _systemIdNormalizer.Normalize(plan.FrontendSystemId);
        if (string.IsNullOrWhiteSpace(canonicalSystemId))
        {
            canonicalSystemId = _systemIdNormalizer.Normalize(plan.SystemId);
        }

        var familySlug = _gameNameNormalizer.NormalizeGameSlug(null, plan.ProjectionBaseName);
        var mediaIndex = _localMediaIndexService.Build([canonicalSystemId], cancellationToken);
        var kindPaths = BuildCanonicalKindPathsFromMediaIndex(
            mediaIndex,
            canonicalSystemId,
            plan.FrontendSystemId,
            plan.GamePath,
            plan.GameSlug,
            familySlug,
            systemRoot,
            scrapingSettings,
            plan.RomRegions,
            plan.RomLanguages);
        foreach (var need in plan.Needs)
        {
            var normalizedKind = MediaKinds.Normalize(need.Kind);
            if (IsSelectedLiveVisibleKind(normalizedKind, scrapingSettings, kindPaths))
            {
                continue;
            }

            var sourcePath = ResolveCanonicalPlanMediaPath(need, systemRoot);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                continue;
            }

            var relativeMediaPath = ToMediaRelativePath(sourcePath, systemRoot);
            if (!string.IsNullOrWhiteSpace(relativeMediaPath))
            {
                kindPaths[normalizedKind] = relativeMediaPath;
            }
        }

        var preferredBundle = ResolvePreferredBundle(plan.SystemId, ResolveTextLookupSlug(plan), scrapingSettings.Language, cancellationToken);

        SetLiveVisibleSlotElement(
            gameNode,
            "fanart",
            FirstLiveAvailableMediaPath(
                kindPaths,
                projectedKindPaths,
                MediaKinds.Fanart));

        SetLiveVisibleSlotElement(
            gameNode,
            "image",
            ResolveSelectedMediaPathStrict(
                scrapingSettings.ImageSource,
                kindPaths,
                projectedKindPaths,
                MediaSelectionTarget.Image,
                scrapingSettings.WheelStyle));
        SetLiveVisibleSlotElement(
            gameNode,
            "marquee",
            ResolveSelectedMediaPathStrict(
                scrapingSettings.LogoSource,
                kindPaths,
                projectedKindPaths,
                MediaSelectionTarget.Logo,
                scrapingSettings.WheelStyle));
        SetLiveVisibleSlotElement(
            gameNode,
            "thumbnail",
            ResolveSelectedMediaPathStrict(
                scrapingSettings.ThumbSource,
                kindPaths,
                projectedKindPaths,
                MediaSelectionTarget.Thumbnail,
                scrapingSettings.WheelStyle));
        ApplyLiveOfficialSecondaryMediaElements(gameNode, existingGameNode, kindPaths);

        var preferredDescription = ResolveBundleField(preferredBundle, "desc", scrapingSettings.Language);
        if (!string.IsNullOrWhiteSpace(preferredDescription))
        {
            SetOrCreateElement(gameNode, "desc", preferredDescription);
        }
        else
        {
            RemoveWrongLanguageElement(gameNode, "desc", ResolveTargetLanguage(preferredBundle, scrapingSettings.Language, gameNode));
        }

        ApplyBundleMetadata(gameNode, preferredBundle, scrapingSettings.Language);
        ApplyPendingLiveMetadataRestores(gameNode, plan);
        ApplyPlanRomIdentityMetadata(gameNode, plan);
        if (!string.IsNullOrWhiteSpace(plan.ScreenScraperGameId))
        {
            gameNode.Add(new XElement(
                "scrap",
                new XAttribute("name", "ScreenScraper"),
                new XAttribute("date", DateTime.Now.ToString("yyyyMMddTHHmmss"))));
        }

        return gameNode;
    }

    private static bool IsSelectedLiveVisibleKind(
        string normalizedKind,
        EmulationStationScrapingSettings scrapingSettings,
        IReadOnlyDictionary<string, string> kindPaths)
    {
        if (string.IsNullOrWhiteSpace(normalizedKind) ||
            !kindPaths.ContainsKey(normalizedKind))
        {
            return false;
        }

        return string.Equals(
                normalizedKind,
                NormalizeSelectionSourceToKind(scrapingSettings.ImageSource, MediaSelectionTarget.Image, scrapingSettings.WheelStyle),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                normalizedKind,
                NormalizeSelectionSourceToKind(scrapingSettings.ThumbSource, MediaSelectionTarget.Thumbnail, scrapingSettings.WheelStyle),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                normalizedKind,
                NormalizeSelectionSourceToKind(scrapingSettings.LogoSource, MediaSelectionTarget.Logo, scrapingSettings.WheelStyle),
                StringComparison.OrdinalIgnoreCase);
    }

    private static XElement ToLiveAddGamesNode(XElement gameNode)
    {
        var liveNode = new XElement(gameNode);
        // /addgames is a visible refresh fragment, not the full durable gamelist entry.
        // Allowed fields: path, id attribute, visible slots (image, thumbnail,
        // marquee, fanart), official secondary media links piggybacked from the
    // dirty batch (video, manual, magazine, map, bezel, cartridge, boxart,
    // boxback, wheel, mix, titleshot), visible metadata (name, desc,
        // releasedate, developer, publisher, players, lang, region, genre,
        // family, rating) and md5.
        // md5 is non-visual but important for local/ES game qualification.
        // Do not post pass-through/extended fields: gameid, source, genres, scrap,
    // wheelcarbon, wheelsteel, label, screenmarquee, videonormalized, themehb,
        // apiexpose_*.
        liveNode.Attributes()
            .Where(attribute => !string.Equals(attribute.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase))
            .Remove();
        foreach (var element in liveNode.Elements().ToList())
        {
            if (!LiveAddGamesAllowedElementNames.Contains(element.Name.LocalName))
            {
                element.Remove();
                continue;
            }

            element.Value = NormalizeLiveAddGamesElementValue(element.Name.LocalName, element.Value);
        }

        return liveNode;
    }

    private static string NormalizeLiveAddGamesElementValue(string tagName, string? value)
    {
        if (string.Equals(tagName, "rating", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeGamelistRating(value);
        }

        if (LiveAddGamesSanitizedTextElementNames.Contains(tagName))
        {
            var normalized = string.Equals(tagName, "name", StringComparison.OrdinalIgnoreCase)
                ? GameNameNormalizer.NormalizeDisplayNameValue(value)
                : LocalizedMetadataSanitizer.SanitizeField(tagName, value, null);
            return NormalizeGamelistText(normalized);
        }

        return NormalizeGamelistText(value);
    }

    private static XElement? TryLoadExistingGameNode(string gamelistPath, string relativeGamePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gamelistPath) || !File.Exists(gamelistPath))
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var normalizedTarget = NormalizeForCompare(relativeGamePath);
            var existing = document.Root?.Elements("game")
                .FirstOrDefault(e => NormalizeForCompare(e.Element("path")?.Value) == normalizedTarget);
            return existing == null ? null : new XElement(existing);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadExistingGamelistRating(MediaProjectionPlan plan, CancellationToken cancellationToken)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
        var existing = TryLoadExistingGameNode(plan.GamelistPath, relativeGamePath, cancellationToken);
        return existing?.Element("rating")?.Value?.Trim() ?? string.Empty;
    }

    private static void SetLiveVisibleSlotElement(
        XElement target,
        string tagName,
        string preferredPath)
    {
        if (string.IsNullOrWhiteSpace(preferredPath))
        {
            return;
        }

        SetOrCreateElement(target, tagName, preferredPath);
    }

    private static void SetLiveMediaSlotOrPreserveExisting(
        XElement target,
        XElement? existingGameNode,
        string tagName,
        string preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            SetOrCreateElement(target, tagName, preferredPath);
            return;
        }

        var existingPath = existingGameNode?.Element(tagName)?.Value?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            SetOrCreateElement(target, tagName, existingPath);
        }
    }

    private static void ApplyLiveOfficialSecondaryMediaElements(
        XElement target,
        XElement? existingGameNode,
        IReadOnlyDictionary<string, string> kindPaths)
    {
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "video", FirstMediaPath(kindPaths, MediaKinds.Video, MediaKinds.VideoNormalized));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "manual", FirstMediaPath(kindPaths, MediaKinds.Manual));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "map", FirstMediaPath(kindPaths, MediaKinds.Map));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "bezel", FirstMediaPath(kindPaths, MediaKinds.Bezel));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "cartridge", FirstMediaPath(kindPaths, MediaKinds.Cartridge));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "boxart", FirstMediaPath(kindPaths, MediaKinds.BoxFront, MediaKinds.Box3d));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "boxback", FirstMediaPath(kindPaths, MediaKinds.BoxBack));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "wheel", FirstMediaPath(kindPaths, MediaKinds.Wheel));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "mix", FirstMediaPath(kindPaths, MediaKinds.MixRbv2, MediaKinds.MixRbv1));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "titleshot", FirstMediaPath(kindPaths, MediaKinds.Image));
        SetLiveMediaSlotOrPreserveExisting(target, existingGameNode, "magazine", FirstMediaPath(kindPaths, MediaKinds.Magazine));
    }

    private async Task<bool> PushLiveGamelistFragmentToEsAsync(
        MediaProjectionPlan plan,
        XElement gameElement,
        bool includeDirtySameSystem,
        bool allowCurrentVideoRefresh,
        bool allowLocalizedMetadataRefresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.EsGameId))
        {
            return false;
        }

        var hasCurrentVideoRefreshContent = allowCurrentVideoRefresh && HasLiveOfficialVideoMedia(gameElement);
        var hasLocalizedMetadataRefreshContent =
            allowLocalizedMetadataRefresh &&
            _options.CurrentValue.Scraping.LiveEsMetadataPushEnabled &&
            HasLiveLocalizedMetadataPayloadContent(plan, gameElement);
        var hasLiveVisibleSlotElement = HasLiveVisibleSlotElement(gameElement);
        if (!hasLiveVisibleSlotElement &&
            !hasCurrentVideoRefreshContent &&
            !hasLocalizedMetadataRefreshContent)
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "live-addgames-rating",
                "rating",
                "skipped-no-live-content",
                new
                {
                    xmlRating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                    hasLiveVisibleMedia = HasLiveVisibleMedia(gameElement),
                    hasLiveVisibleSlotElement,
                    hasCurrentVideoRefreshContent,
                    hasLocalizedMetadataRefreshContent,
                    allowLocalizedMetadataRefresh,
                    metadataPayloadCount = BuildLiveMetadataPayload(plan, gameElement).Count
                },
                cancellationToken);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "skipped-no-live-content",
                new
                {
                    hasLiveVisibleMedia = HasLiveVisibleMedia(gameElement),
                    hasLiveVisibleSlotElement,
                    hasCurrentVideoRefreshContent,
                    hasLocalizedMetadataRefreshContent,
                    allowLocalizedMetadataRefresh,
                    metadataPayloadCount = BuildLiveMetadataPayload(plan, gameElement).Count
                },
                cancellationToken);
            return false;
        }

        if (!IsCurrentlySelectedGame(plan))
        {
            _logger?.LogInformation(
                "Live ES addgames skipped before batch build for system={SystemId}, game={GameSlug}: target is no longer the currently selected game.",
                plan.FrontendSystemId,
                plan.GameSlug);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "skipped-not-current",
                new { stage = "before-batch-build" },
                cancellationToken);
            await MediaUpdateAuditLog.AppendAsync(plan, "live-addgames", "gamelist", "skipped-not-current-before-build", cancellationToken: cancellationToken);
            return false;
        }

        var dirtyBatch = includeDirtySameSystem
            ? CollectDirtyLiveGamelistBatch(plan, cancellationToken)
            : new List<DirtyLiveGamelistPlan>();
        var relatedBatch = CollectRelatedLiveGameElements(plan, cancellationToken);
        if (!HasLiveGamelistRefreshDelta(plan, gameElement, dirtyBatch, allowCurrentVideoRefresh, allowLocalizedMetadataRefresh, cancellationToken))
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "live-addgames",
                "gamelist",
                "skipped-no-delta",
                new
                {
                    visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                    currentVideoRefresh = allowCurrentVideoRefresh && HasLiveOfficialVideoMedia(gameElement),
                    localizedMetadataRefresh = hasLocalizedMetadataRefreshContent,
                    rating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                    dirtyBatchCount = dirtyBatch.Count,
                    relatedBatchCount = relatedBatch.Count
                },
                cancellationToken);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "skipped-no-delta",
                new
                {
                    visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                    currentVideoRefresh = allowCurrentVideoRefresh && HasLiveOfficialVideoMedia(gameElement),
                    localizedMetadataRefresh = hasLocalizedMetadataRefreshContent,
                    rating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                    dirtyBatchCount = dirtyBatch.Count,
                    relatedBatchCount = relatedBatch.Count
                },
                cancellationToken);
            return false;
        }

        var currentMediaSignature = BuildCurrentLiveMediaSignature(plan, gameElement);
        var currentMediaSignatureKey = BuildLiveAddGamesCurrentMediaSignatureKey(plan);
        if (!allowCurrentVideoRefresh &&
            !string.IsNullOrWhiteSpace(currentMediaSignature) &&
            LastLiveAddGamesCurrentMediaSignatures.TryGetValue(currentMediaSignatureKey, out var previousMediaSignature) &&
            string.Equals(previousMediaSignature, currentMediaSignature, StringComparison.Ordinal))
        {
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "live-addgames",
                "gamelist",
                "skipped-current-media-signature-unchanged",
                new
                {
                    visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                    dirtyBatchCount = dirtyBatch.Count,
                    relatedBatchCount = relatedBatch.Count
                },
                cancellationToken);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "skipped-current-media-signature-unchanged",
                new
                {
                    visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                    dirtyBatchCount = dirtyBatch.Count,
                    relatedBatchCount = relatedBatch.Count
                },
                cancellationToken);
            return false;
        }

        var gameNodes = new List<XElement> { ToLiveAddGamesNode(gameElement) };
        gameNodes.AddRange(relatedBatch.Select(ToLiveAddGamesNode));
        foreach (var dirty in dirtyBatch)
        {
            gameNodes.Add(ToLiveAddGamesNode(BuildLiveGameElement(dirty.Plan, cancellationToken)));
        }

        var document = new XDocument(new XElement("gameList", gameNodes));
        var xml = document.ToString(SaveOptions.DisableFormatting);

        var payloadTrace = await WriteLiveAddGamesPayloadTraceAsync(
            plan,
            xml,
            gameNodes,
            dirtyBatch,
            relatedBatch,
            cancellationToken);
        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "live-addgames-payload",
            "gamelist",
            payloadTrace.XmlWritten ? "written" : "disabled",
            payloadTrace,
            cancellationToken);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            if (await TryQueueLiveAddGamesBlockedByGameSessionAsync(
                    plan,
                    "before-post",
                    dirtyBatch.Count,
                    relatedBatch.Count,
                    cancellationToken))
            {
                return false;
            }

            if (!IsCurrentlySelectedGame(plan))
            {
                _logger?.LogInformation(
                    "Live ES addgames skipped before POST for system={SystemId}, game={GameSlug}: target is no longer the currently selected game.",
                    plan.FrontendSystemId,
                    plan.GameSlug);
                await RefreshTrackingLog.AppendAsync(
                    plan,
                    "addgames",
                    "skipped-not-current",
                    new { stage = "before-post", dirtyBatchCount = dirtyBatch.Count, relatedBatchCount = relatedBatch.Count },
                    cancellationToken);
                await MediaUpdateAuditLog.AppendAsync(plan, "live-addgames", "gamelist", "skipped-not-current-before-post", cancellationToken: cancellationToken);
                return false;
            }

            if (_runtimeState.ShouldSuppressLiveAddGames(plan.FrontendSystemId, plan.GameSlug, out var suppressReason))
            {
                MarkLiveGamelistDirty(plan);
                _logger?.LogInformation(
                    "Live ES addgames queued instead of pushed for system={SystemId}, game={GameSlug}: addgames suppressed after HyperBat refresh ({Reason}).",
                    plan.FrontendSystemId,
                    plan.GameSlug,
                    suppressReason);
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "live-addgames",
                    "gamelist",
                    "queued-after-hyperbat-refresh",
                    new { reason = suppressReason, stage = "before-post", dirtyBatchCount = dirtyBatch.Count, relatedBatchCount = relatedBatch.Count },
                    cancellationToken);
                await RefreshTrackingLog.AppendAsync(
                    plan,
                    "addgames",
                    "queued-after-hyperbat-refresh",
                    new { reason = suppressReason, stage = "before-post", dirtyBatchCount = dirtyBatch.Count, relatedBatchCount = relatedBatch.Count },
                    cancellationToken);
                return false;
            }

            await LiveEsAddGamesGate.WaitAsync(cancellationToken);
            HttpResponseMessage? response = null;
            try
            {
                await DelayBeforeLiveAddGamesPostAsync(cancellationToken);
                // The min interval is only a safety pause between POSTs. It must not
                // authorize a second refresh for the same game-selected card.
                if (await TryQueueLiveAddGamesBlockedByGameSessionAsync(
                        plan,
                        "after-min-interval-delay",
                        dirtyBatch.Count,
                        relatedBatch.Count,
                        cancellationToken))
                {
                    return false;
                }

                if (!IsCurrentlySelectedGame(plan))
                {
                    _logger?.LogInformation(
                        "Live ES addgames skipped after min interval delay for system={SystemId}, game={GameSlug}: target is no longer the currently selected game.",
                        plan.FrontendSystemId,
                        plan.GameSlug);
                    await RefreshTrackingLog.AppendAsync(
                        plan,
                        "addgames",
                        "skipped-not-current",
                        new { stage = "after-min-interval-delay", dirtyBatchCount = dirtyBatch.Count, relatedBatchCount = relatedBatch.Count },
                        cancellationToken);
                    await MediaUpdateAuditLog.AppendAsync(plan, "live-addgames", "gamelist", "skipped-not-current-after-min-interval", cancellationToken: cancellationToken);
                    return false;
                }

                if (_runtimeState.ShouldSuppressLiveAddGamesForSelection(
                        plan.FrontendSystemId,
                        plan.GamePath,
                        out var alreadyPushedReason,
                        allowVideoException: allowCurrentVideoRefresh))
                {
                    _logger?.LogInformation(
                        "Live ES addgames skipped before POST for system={SystemId}, game={GameSlug}: addgames already pushed for the current selected card.",
                        plan.FrontendSystemId,
                        plan.GameSlug);
                    await MediaUpdateAuditLog.AppendAsync(
                        plan,
                        "live-addgames",
                        "gamelist",
                        "skipped-current-selection-already-pushed-before-post",
                        new
                        {
                            reason = alreadyPushedReason,
                            stage = "inside-post-gate",
                            dirtyBatchCount = dirtyBatch.Count,
                            relatedBatchCount = relatedBatch.Count,
                            allowCurrentVideoRefresh,
                            allowLocalizedMetadataRefresh
                        },
                        cancellationToken);
                    await RefreshTrackingLog.AppendAsync(
                        plan,
                        "addgames",
                        "skipped-current-selection-already-pushed-before-post",
                        new
                        {
                            reason = alreadyPushedReason,
                            stage = "inside-post-gate",
                            dirtyBatchCount = dirtyBatch.Count,
                            relatedBatchCount = relatedBatch.Count,
                            allowCurrentVideoRefresh,
                            allowLocalizedMetadataRefresh
                        },
                        cancellationToken);
                    return false;
                }

                using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
                response = await _esHttpClient.PostAsync(
                    $"/addgames/{Uri.EscapeDataString(plan.FrontendSystemId)}",
                    content,
                    cancellationToken);
                LastLiveEsAddGamesPostUtc = DateTimeOffset.UtcNow;
            }
            finally
            {
                LiveEsAddGamesGate.Release();
            }

            using (response)
            {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                _logger?.LogInformation(
                    "Live ES game fragment produced no update for system={SystemId}, gameid={EsGameId}: ES returned 204 No Content.",
                    plan.FrontendSystemId,
                    plan.EsGameId);
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "live-addgames",
                    "gamelist",
                    "no-content",
                    new
                    {
                        payloadTrace,
                        statusCode = (int)response.StatusCode,
                        visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                        hasLiveVisibleSlotElement,
                        rating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                        dirtyBatchCount = dirtyBatch.Count,
                        relatedBatchCount = relatedBatch.Count
                    },
                    cancellationToken);
                await RefreshTrackingLog.AppendAsync(
                    plan,
                    "addgames",
                    "no-content",
                    new
                    {
                        payloadTrace,
                        statusCode = (int)response.StatusCode,
                        visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                        hasLiveVisibleSlotElement,
                        rating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                        totalGameNodes = gameNodes.Count,
                        dirtyBatchCount = dirtyBatch.Count,
                        relatedBatchCount = relatedBatch.Count
                    },
                    cancellationToken);
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                _logger?.LogInformation(
                    "Live ES game fragment pushed for system={SystemId}, gameid={EsGameId}, visibleMedia={VisibleMedia}, dirtyBatchCount={DirtyBatchCount}.",
                    plan.FrontendSystemId,
                    plan.EsGameId,
                    string.Join(",", LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value))),
                    dirtyBatch.Count + relatedBatch.Count);
                ClearDirtyLiveGamelistBatch(dirtyBatch);
                ClearPendingLiveMetadataRestores(plan, dirtyBatch);
                var videoExceptionConsumed = allowCurrentVideoRefresh && HasLiveOfficialVideoMedia(gameElement);
                var localizedMetadataRefreshConsumed = hasLocalizedMetadataRefreshContent;
                var consumesSelectionLiveRefresh = hasLiveVisibleSlotElement || videoExceptionConsumed || localizedMetadataRefreshConsumed;
                if (consumesSelectionLiveRefresh)
                {
                    _runtimeState.MarkLiveAddGamesPushedForSelection(
                        plan.FrontendSystemId,
                        plan.GamePath,
                        videoException: videoExceptionConsumed);
                }
                if (!string.IsNullOrWhiteSpace(currentMediaSignature))
                {
                    LastLiveAddGamesCurrentMediaSignatures[currentMediaSignatureKey] = currentMediaSignature;
                }
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "live-gamelist-write-behind",
                    "gamelist",
                    "skipped-es-addgames-persistence",
                    new
                    {
                        reason = "es-addgames-updates-gamelist",
                        dirtyBatchCount = dirtyBatch.Count,
                        relatedBatchCount = relatedBatch.Count
                    },
                    cancellationToken);
                await MediaUpdateAuditLog.AppendAsync(
                    plan,
                    "live-addgames",
                    "gamelist",
                    "success",
                    new
                    {
                        payloadTrace,
                        visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                        videoException = videoExceptionConsumed,
                        localizedMetadataRefresh = localizedMetadataRefreshConsumed,
                        consumesSelectionLiveRefresh,
                        rating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                        batchRatings = gameNodes
                            .Select(node => new
                            {
                                path = node.Element("path")?.Value?.Trim() ?? string.Empty,
                                rating = node.Element("rating")?.Value?.Trim() ?? string.Empty
                            })
                            .Where(entry => !string.IsNullOrWhiteSpace(entry.rating))
                            .ToArray(),
                        dirtyBatchCount = dirtyBatch.Count,
                        relatedBatchCount = relatedBatch.Count
                    },
                    cancellationToken);
                await RefreshTrackingLog.AppendAsync(
                    plan,
                    "addgames",
                    "success",
                    new
                    {
                        payloadTrace,
                        visibleMedia = LiveVisibleMediaTags().Where(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value)).ToArray(),
                        videoException = videoExceptionConsumed,
                        localizedMetadataRefresh = localizedMetadataRefreshConsumed,
                        consumesSelectionLiveRefresh,
                        rating = gameElement.Element("rating")?.Value?.Trim() ?? string.Empty,
                        totalGameNodes = gameNodes.Count,
                        dirtyBatchCount = dirtyBatch.Count,
                        relatedBatchCount = relatedBatch.Count,
                        dirtyBatch = dirtyBatch
                            .Select(dirty => new
                            {
                                dirty.FrontendSystemId,
                                dirty.GameSlug,
                                dirty.GamePath
                            })
                            .ToArray(),
                        relatedBatchPaths = relatedBatch
                            .Select(node => node.Element("path")?.Value?.Trim() ?? string.Empty)
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .ToArray()
                    },
                    cancellationToken);
                return true;
            }

            _logger?.LogWarning(
                "Live ES game fragment push returned HTTP {StatusCode} for system={SystemId}, gameid={EsGameId}.",
                (int)response.StatusCode,
                plan.FrontendSystemId,
                plan.EsGameId);
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "http-failed",
                new { statusCode = (int)response.StatusCode, dirtyBatchCount = dirtyBatch.Count, relatedBatchCount = relatedBatch.Count },
                cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogInformation(ex, "Live ES game fragment push skipped: EmulationStation API unavailable.");
            await RefreshTrackingLog.AppendAsync(
                plan,
                "addgames",
                "exception",
                new { exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
        }

        return false;
    }

    private async Task DelayBeforeLiveAddGamesPostAsync(CancellationToken cancellationToken)
    {
        var delayMs = Math.Clamp(_options.CurrentValue.Scraping.LiveEsAddGamesMinIntervalMs, 0, 10000);
        if (delayMs <= 0 || LastLiveEsAddGamesPostUtc == DateTimeOffset.MinValue)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - LastLiveEsAddGamesPostUtc;
        var remaining = TimeSpan.FromMilliseconds(delayMs) - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private async Task<LiveAddGamesPayloadTrace> WriteLiveAddGamesPayloadTraceAsync(
        MediaProjectionPlan plan,
        string xml,
        IReadOnlyList<XElement> gameNodes,
        IReadOnlyList<DirtyLiveGamelistPlan> dirtyBatch,
        IReadOnlyList<XElement> relatedBatch,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var xmlBytes = Encoding.UTF8.GetBytes(xml);
        var hash = Convert.ToHexString(SHA256.HashData(xmlBytes)).ToLowerInvariant();
        var traceEnabled = _options.CurrentValue.Scraping.TraceLiveAddGamesPayloads;
        string? relativePath = null;
        string? fullPath = null;
        if (traceEnabled)
        {
            var directory = Path.Combine(
                RetroBatPaths.PluginRoot,
                "logs",
                "addgames-payloads",
                now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(directory);

            var fileName = string.Join(
                "-",
                now.ToString("HHmmss-fff"),
                SanitizeTraceFilePart(plan.FrontendSystemId),
                SanitizeTraceFilePart(plan.GameSlug),
                hash[..12]) + ".xml";
            fullPath = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(fullPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            relativePath = Path.GetRelativePath(RetroBatPaths.PluginRoot, fullPath);
        }

        var settings = _settingsService.GetScrapingSettings();
        var gamelistInfo = TryGetFileTraceInfo(plan.GamelistPath);
        var esSettingsInfo = TryGetFileTraceInfo(RetroBatPaths.EmulationStationSettingsPath);
        var esLogInfo = TryGetFileTraceInfo(Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_log.txt"));

        return new LiveAddGamesPayloadTrace(
            relativePath,
            fullPath,
            traceEnabled,
            xmlBytes.Length,
            hash,
            gameNodes.Count,
            dirtyBatch.Count,
            relatedBatch.Count,
            settings.ThemeSet,
            settings.ImageSource,
            settings.LogoSource,
            settings.ThumbSource,
            settings.WheelStyle,
            _options.CurrentValue.Scraping.LiveEsMediaPushEnabled,
            _options.CurrentValue.Scraping.LiveEsMetadataPushEnabled,
            gamelistInfo.LastWriteUtc,
            gamelistInfo.Length,
            esSettingsInfo.LastWriteUtc,
            esLogInfo.LastWriteUtc,
            gameNodes.Select(CreatePayloadNodeTrace).ToArray(),
            dirtyBatch
                .Select(dirty => new LiveAddGamesBatchTrace(dirty.FrontendSystemId, dirty.GameSlug, dirty.GamePath))
                .ToArray(),
            relatedBatch
                .Select(node => node.Element("path")?.Value?.Trim() ?? string.Empty)
                .Where(pathValue => !string.IsNullOrWhiteSpace(pathValue))
                .ToArray());
    }

    private static LiveAddGamesPayloadNodeTrace CreatePayloadNodeTrace(XElement node)
    {
        var tags = node.Elements()
            .Select(element => element.Name.LocalName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        return new LiveAddGamesPayloadNodeTrace(
            node.Attribute("id")?.Value?.Trim() ?? string.Empty,
            node.Element("path")?.Value?.Trim() ?? string.Empty,
            node.Element("name")?.Value?.Trim() ?? string.Empty,
            tags,
            LiveVisibleMediaTags()
                .Where(tag => !string.IsNullOrWhiteSpace(node.Element(tag)?.Value))
                .ToArray(),
            node.Element("md5")?.Value?.Trim() ?? string.Empty);
    }

    private static FileTraceInfo TryGetFileTraceInfo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new FileTraceInfo(null, null);
            }

            var info = new FileInfo(path);
            return new FileTraceInfo(info.LastWriteTimeUtc, info.Length);
        }
        catch
        {
            return new FileTraceInfo(null, null);
        }
    }

    private static string SanitizeTraceFilePart(string value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '_');
        }

        sanitized = sanitized.Replace(' ', '_');
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private void ApplyPendingLiveMetadataRestores(XElement gameNode, MediaProjectionPlan plan)
    {
        var pending = _runtimeState.GetPendingLiveMetadataRestore(
            plan.FrontendSystemId,
            plan.GameSlug,
            plan.GamePath);
        foreach (var pair in pending)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                SetOrCreateElement(gameNode, pair.Key, pair.Value ?? string.Empty);
            }
        }
    }

    private void ClearPendingLiveMetadataRestores(
        MediaProjectionPlan currentPlan,
        IEnumerable<DirtyLiveGamelistPlan> dirtyBatch)
    {
        _runtimeState.ClearPendingLiveMetadataRestore(
            currentPlan.FrontendSystemId,
            currentPlan.GameSlug,
            currentPlan.GamePath);

        foreach (var dirty in dirtyBatch)
        {
            _runtimeState.ClearPendingLiveMetadataRestore(
                dirty.Plan.FrontendSystemId,
                dirty.Plan.GameSlug,
                dirty.Plan.GamePath);
        }
    }

    private List<XElement> CollectRelatedLiveGameElements(MediaProjectionPlan currentPlan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var relatedEntries = _mameGamelistGroupIndex.GetRelatedRomEntries(currentPlan.FrontendSystemId, currentPlan.GamePath, currentPlan.GameSlug);
        if (relatedEntries.Count == 0)
        {
            return new List<XElement>();
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, currentPlan.FrontendSystemId);
        var currentRom = NormalizeRomName(Path.GetFileNameWithoutExtension(currentPlan.GamePath));
        var relatedNodes = new List<XElement>();
        var relatedPathSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relatedEntry in relatedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relatedRom = relatedEntry.Rom;

            if (string.Equals(NormalizeRomName(relatedRom), currentRom, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var relatedPath in ResolveRelatedRomPaths(systemRoot, relatedRom, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(relatedPath))
                {
                    continue;
                }

                var normalizedRelatedPath = NormalizeForCompare(relatedPath);
                if (string.Equals(normalizedRelatedPath, NormalizeForCompare(currentPlan.GamePath), StringComparison.OrdinalIgnoreCase) ||
                    !relatedPathSeen.Add(normalizedRelatedPath))
                {
                    continue;
                }

                var relatedInfo = ResolveRelatedRomGamelistInfo(systemRoot, relatedPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(relatedInfo.EsGameId))
                {
                    relatedInfo = relatedInfo with { EsGameId = BuildEsGameIdFromPath(systemRoot, relatedPath) };
                    _logger?.LogDebug(
                        "Live ES related group entry gameid generated from path: system={SystemId}, rom={Rom}, path={Path}, gameid={GameId}.",
                        currentPlan.FrontendSystemId,
                        relatedRom,
                        relatedPath,
                        relatedInfo.EsGameId);
                }

                relatedInfo = relatedInfo with
                {
                    RomRegions = relatedEntry.Regions.Count > 0 ? relatedEntry.Regions.ToList() : relatedInfo.RomRegions,
                    RomLanguages = relatedEntry.Languages.Count > 0 ? relatedEntry.Languages.ToList() : relatedInfo.RomLanguages
                };
                var relatedPlan = ClonePlanForRelatedLiveGamelist(currentPlan, relatedPath, relatedInfo);
                relatedNodes.Add(BuildLiveGameElement(relatedPlan, cancellationToken));
            }
        }

        if (relatedNodes.Count > 0)
        {
            _logger?.LogInformation(
                "Live ES related system group added to addgames batch: system={SystemId}, game={GameSlug}, relatedCount={RelatedCount}.",
                currentPlan.FrontendSystemId,
                currentPlan.GameSlug,
                relatedNodes.Count);
        }

        return relatedNodes;
    }

    private static RelatedGamelistInfo ResolveRelatedRomGamelistInfo(string systemRoot, string relatedGamePath, CancellationToken cancellationToken)
    {
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return RelatedGamelistInfo.Empty(relatedGamePath);
        }

        try
        {
            using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var targetAbsolutePath = NormalizeForCompare(relatedGamePath);
            var targetFileName = NormalizeForCompare(Path.GetFileName(relatedGamePath));

            foreach (var gameNode in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var gamePath = gameNode.Element("path")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(gamePath))
                {
                    continue;
                }

                var absoluteGamePath = NormalizeForCompare(ResolveEsRelativePath(systemRoot, gamePath));
                var gameFileName = NormalizeForCompare(Path.GetFileName(gamePath));
                if (absoluteGamePath == targetAbsolutePath || gameFileName == targetFileName)
                {
                    return new RelatedGamelistInfo(
                        relatedGamePath,
                        gameNode.Element("gameid")?.Value?.Trim() ?? string.Empty,
                        gameNode.Element("name")?.Value?.Trim() ?? string.Empty,
                        SplitRelatedMetadataTokens(gameNode.Element("region")?.Value),
                        SplitRelatedMetadataTokens(gameNode.Element("lang")?.Value));
                }
            }
        }
        catch
        {
            return RelatedGamelistInfo.Empty(relatedGamePath);
        }

        return RelatedGamelistInfo.Empty(relatedGamePath);
    }

    private static MediaProjectionPlan ClonePlanForRelatedLiveGamelist(MediaProjectionPlan source, string relatedGamePath, RelatedGamelistInfo relatedInfo)
    {
        return new MediaProjectionPlan
        {
            SystemId = source.SystemId,
            FrontendSystemId = source.FrontendSystemId,
            GameSlug = source.GameSlug,
            TextSourceGameSlug = source.TextSourceGameSlug,
            DisplayName = relatedInfo.DisplayName,
            GamePath = relatedGamePath,
            ProjectionBaseName = source.ProjectionBaseName,
            PreferredImageSource = source.PreferredImageSource,
            PreferredLogoSource = source.PreferredLogoSource,
            PreferredThumbnailSource = source.PreferredThumbnailSource,
            GamePathExists = File.Exists(relatedGamePath) || Directory.Exists(relatedGamePath),
            GamelistMd5 = source.GamelistMd5,
            GamelistCrc32 = source.GamelistCrc32,
            GamelistPath = source.GamelistPath,
            EsGameId = relatedInfo.EsGameId,
            ScreenScraperGameId = source.ScreenScraperGameId,
            RomRegions = relatedInfo.RomRegions.Count > 0 ? relatedInfo.RomRegions.ToList() : source.RomRegions.ToList(),
            RomLanguages = relatedInfo.RomLanguages.Count > 0 ? relatedInfo.RomLanguages.ToList() : source.RomLanguages.ToList(),
            SuppressImmediateGamelistUpdates = true,
            Needs = source.Needs
                .Select(need => new MediaNeed
                {
                    Kind = need.Kind,
                    IsMissing = need.IsMissing,
                    InitialExistingPath = need.InitialExistingPath,
                    ExistingPath = need.ExistingPath,
                    TargetRelativePath = need.TargetRelativePath,
                    ImportedPath = need.ImportedPath,
                    ProjectedPath = need.ProjectedPath,
                    WasImported = need.WasImported,
                    WasProjected = need.WasProjected,
                    WasContentChanged = need.WasContentChanged
                })
                .ToList()
        };
    }

    private static List<string> SplitRelatedMetadataTokens(string? value)
    {
        return (value ?? string.Empty)
            .Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ResolveRelatedRomPaths(string systemRoot, string relatedRom, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(systemRoot) || string.IsNullOrWhiteSpace(relatedRom))
        {
            return results;
        }

        foreach (var extension in RelatedRomExtensions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Path.Combine(systemRoot, relatedRom + extension);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                AddRelatedRomPath(results, seen, candidate);
            }
        }

        if (Directory.Exists(systemRoot))
        {
            foreach (var directMatch in Directory.EnumerateFiles(systemRoot, relatedRom + ".*", SearchOption.TopDirectoryOnly)
                .Where(IsLikelyGameFile))
            {
                AddRelatedRomPath(results, seen, directMatch);
            }
        }

        foreach (var gamelistMatch in ResolveRelatedRomPathsFromGamelist(systemRoot, relatedRom, cancellationToken))
        {
            AddRelatedRomPath(results, seen, gamelistMatch);
        }

        return results;
    }

    private static List<string> ResolveRelatedRomPathsFromGamelist(string systemRoot, string relatedRom, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return results;
        }

        try
        {
            var document = XDocument.Load(gamelistPath, LoadOptions.PreserveWhitespace);
            foreach (var gameNode in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rawPath = gameNode.Element("path")?.Value;
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                var absolutePath = ResolveEsRelativePath(systemRoot, rawPath);
                var candidateStem = Path.GetFileNameWithoutExtension(absolutePath);
                if (string.Equals(NormalizeRomName(candidateStem), NormalizeRomName(relatedRom), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(MameGamelistGroupIndex.NormalizeRomSlug(candidateStem), MameGamelistGroupIndex.NormalizeRomSlug(relatedRom), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(MameGamelistGroupIndex.NormalizeCompactRomSlug(candidateStem), MameGamelistGroupIndex.NormalizeCompactRomSlug(relatedRom), StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(absolutePath);
                }
            }
        }
        catch
        {
            return results;
        }

        return results;
    }

    private static void AddRelatedRomPath(ICollection<string> results, ISet<string> seen, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && seen.Add(NormalizeForCompare(path)))
        {
            results.Add(path);
        }
    }

    private static IEnumerable<string> RelatedRomExtensions()
    {
        yield return ".zip";
        yield return ".7z";
        yield return ".chd";
        yield return ".iso";
        yield return ".nes";
        yield return ".fds";
        yield return ".sfc";
        yield return ".smc";
        yield return ".gb";
        yield return ".gbc";
        yield return ".gba";
        yield return ".gen";
        yield return ".md";
    }

    private static bool IsLikelyGameFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".chd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".nes", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fds", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sfc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".smc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gba", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gen", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRomName(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private List<DirtyLiveGamelistPlan> CollectDirtyLiveGamelistBatch(MediaProjectionPlan currentPlan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var currentKey = BuildDirtyLiveGamelistKey(currentPlan.FrontendSystemId, currentPlan.GameSlug, currentPlan.GamePath);
        return DirtyLiveGamelistPlans.Values
            .Where(dirty =>
                string.Equals(dirty.FrontendSystemId, currentPlan.FrontendSystemId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(dirty.Key, currentKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(dirty => dirty.LastUpdatedUtc)
            .ToList();
    }

    private static bool HasPendingDirtyLiveGamelistBatch(MediaProjectionPlan currentPlan)
    {
        var currentKey = BuildDirtyLiveGamelistKey(currentPlan.FrontendSystemId, currentPlan.GameSlug, currentPlan.GamePath);
        return DirtyLiveGamelistPlans.Values.Any(dirty =>
            string.Equals(dirty.FrontendSystemId, currentPlan.FrontendSystemId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dirty.Key, currentKey, StringComparison.OrdinalIgnoreCase));
    }

    private static void ClearDirtyLiveGamelistBatch(IEnumerable<DirtyLiveGamelistPlan> batch)
    {
        foreach (var dirty in batch)
        {
            DirtyLiveGamelistPlans.TryRemove(dirty.Key, out _);
        }
    }

    private void ScheduleLiveGamelistWriteBehind(
        MediaProjectionPlan currentPlan,
        IReadOnlyCollection<DirtyLiveGamelistPlan> dirtyBatch)
    {
        QueuePendingLiveGamelistWriteBehindPlan(ClonePlanForDirtyLiveGamelist(currentPlan));
        foreach (var dirty in dirtyBatch)
        {
            QueuePendingLiveGamelistWriteBehindPlan(ClonePlanForDirtyLiveGamelist(dirty.Plan));
        }

        var systemKey = NormalizeSelectionValue(currentPlan.FrontendSystemId);
        if (string.IsNullOrWhiteSpace(systemKey))
        {
            return;
        }

        var nextCts = new CancellationTokenSource();
        var previous = LiveGamelistWriteBehindDebounces.AddOrUpdate(
            systemKey,
            nextCts,
            (_, oldCts) =>
            {
                oldCts.Cancel();
                return nextCts;
            });

        if (!ReferenceEquals(previous, nextCts))
        {
            nextCts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LiveGamelistWriteBehindDelay, nextCts.Token);
                if (!LiveGamelistWriteBehindDebounces.TryGetValue(systemKey, out var activeCts) ||
                    !ReferenceEquals(activeCts, nextCts) ||
                    !LiveGamelistWriteBehindDebounces.TryRemove(systemKey, out _))
                {
                    return;
                }

                var plans = DrainPendingLiveGamelistWriteBehindPlans(systemKey);
                if (plans.Count == 0)
                {
                    return;
                }

                var result = await StageExtendedEntriesAsync(plans, CancellationToken.None);
                _logger?.LogInformation(
                    "Live gamelist extended pending staged: system={SystemId}, plans={PlanCount}, changed={Changed}, mediaChanged={MediaChanged}, metadataChanged={MetadataChanged}.",
                    currentPlan.FrontendSystemId,
                    plans.Count,
                    result.Changed,
                    result.MediaContentChanged,
                    result.MetadataChanged);
                await MediaUpdateAuditLog.AppendAsync(
                    currentPlan,
                    "live-gamelist-write-behind",
                    "gamelist",
                    result.Changed ? "staged-extended" : "staged-extended-unchanged",
                    new
                    {
                        plans.Count,
                        result.MediaContentChanged,
                        result.MetadataChanged
                    },
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Live gamelist write-behind failed for system={SystemId}, game={GameSlug}.",
                    currentPlan.FrontendSystemId,
                    currentPlan.GameSlug);
            }
            finally
            {
                nextCts.Dispose();
            }
        });
    }

    private static void QueuePendingLiveGamelistWriteBehindPlan(MediaProjectionPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.FrontendSystemId) ||
            string.IsNullOrWhiteSpace(plan.GamePath) ||
            string.IsNullOrWhiteSpace(plan.GameSlug))
        {
            return;
        }

        var key = BuildDirtyLiveGamelistKey(plan.FrontendSystemId, plan.GameSlug, plan.GamePath);
        PendingLiveGamelistWriteBehindPlans[key] = plan;
    }

    private static List<MediaProjectionPlan> DrainPendingLiveGamelistWriteBehindPlans(string systemKey)
    {
        var plans = new List<MediaProjectionPlan>();
        foreach (var pair in PendingLiveGamelistWriteBehindPlans.ToArray())
        {
            if (!string.Equals(NormalizeSelectionValue(pair.Value.FrontendSystemId), systemKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PendingLiveGamelistWriteBehindPlans.TryRemove(pair.Key, out var plan))
            {
                plans.Add(plan);
            }
        }

        return plans;
    }

    private static bool HasLiveVisibleMedia(XElement gameElement)
    {
        return LiveVisibleMediaTags().Any(tag => !string.IsNullOrWhiteSpace(gameElement.Element(tag)?.Value));
    }

    private static bool HasLiveVisibleSlotElement(XElement gameElement)
    {
        return LiveVisibleMediaTags().Any(tag => gameElement.Element(tag) != null);
    }

    private static bool HasLiveLocalizedMetadataPayloadContent(MediaProjectionPlan plan, XElement gameElement)
    {
        return BuildLiveMetadataPayload(plan, gameElement)
            .Keys
            .Any(key => LiveAddGamesLocalizedMetadataRefreshElementNames.Contains(key));
    }

    private static bool HasLiveGamelistRefreshDelta(
        MediaProjectionPlan plan,
        XElement gameElement,
        IReadOnlyCollection<DirtyLiveGamelistPlan> dirtyBatch,
        bool allowCurrentVideoRefresh,
        bool allowLocalizedMetadataRefresh,
        CancellationToken cancellationToken)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        if (HasChangedVisibleMediaContent(plan, gameElement, systemRoot))
        {
            return true;
        }

        var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
        var existingGameNode = TryLoadExistingGameNode(plan.GamelistPath, relativeGamePath, cancellationToken);
        if (existingGameNode == null)
        {
            return false;
        }

        if (allowCurrentVideoRefresh &&
            HasOfficialSecondaryMediaRefreshDelta(
                plan,
                gameElement,
                existingGameNode,
                systemRoot,
                "video",
                [MediaKinds.Video, MediaKinds.VideoNormalized]))
        {
            return true;
        }

        if (allowLocalizedMetadataRefresh &&
            HasLocalizedMetadataRefreshDelta(plan, gameElement, existingGameNode))
        {
            return true;
        }

        return false;
    }

    private static bool HasElementValueDelta(XElement nextNode, XElement existingNode, string tagName)
    {
        var nextElement = nextNode.Element(tagName);
        if (nextElement == null)
        {
            return false;
        }

        var nextValue = nextElement.Value.Trim();
        var existingValue = existingNode.Element(tagName)?.Value?.Trim() ?? string.Empty;
        return !string.Equals(NormalizeForCompare(nextValue), NormalizeForCompare(existingValue), StringComparison.Ordinal);
    }

    private static bool HasLocalizedMetadataRefreshDelta(
        MediaProjectionPlan plan,
        XElement gameElement,
        XElement existingGameNode)
    {
        foreach (var pair in BuildLiveMetadataPayload(plan, gameElement))
        {
            if (!LiveAddGamesLocalizedMetadataRefreshElementNames.Contains(pair.Key))
            {
                continue;
            }

            var existingValue = existingGameNode.Element(pair.Key)?.Value?.Trim() ?? string.Empty;
            if (!string.Equals(NormalizeLiveAddGamesElementValue(pair.Key, existingValue), pair.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildLiveAddGamesCurrentMediaSignatureKey(MediaProjectionPlan plan)
    {
        return string.Join(
            "|",
            NormalizeSelectionValue(plan.FrontendSystemId),
            NormalizeSelectionValue(plan.GamePath));
    }

    private static string BuildCurrentLiveMediaSignature(MediaProjectionPlan plan, XElement gameElement)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var parts = new List<string>();
        foreach (var tagName in LiveVisibleMediaTags().Concat(["video"]))
        {
            var value = gameElement.Element(tagName)?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var diskPath = ResolveEsRelativePath(systemRoot, value);
            parts.Add(string.Join(
                "=",
                tagName,
                NormalizeForCompare(value),
                NormalizeForCompare(diskPath),
                BuildFileStampSignature(diskPath)));
        }

        return string.Join("|", parts);
    }

    private static string BuildFileStampSignature(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return "missing";
            }

            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool HasLiveOfficialVideoMedia(XElement gameElement)
    {
        return !string.IsNullOrWhiteSpace(gameElement.Element("video")?.Value);
    }

    private static bool HasOfficialSecondaryMediaRefreshDelta(
        MediaProjectionPlan plan,
        XElement gameElement,
        XElement existingGameNode,
        string systemRoot,
        string tagName,
        IReadOnlyCollection<string> mediaKinds)
    {
        var nextValue = gameElement.Element(tagName)?.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(nextValue))
        {
            return false;
        }

        var existingValue = existingGameNode.Element(tagName)?.Value?.Trim() ?? string.Empty;
        if (!string.Equals(NormalizeForCompare(nextValue), NormalizeForCompare(existingValue), StringComparison.Ordinal))
        {
            return true;
        }

        var nextDiskPath = NormalizeForCompare(ResolveEsRelativePath(systemRoot, nextValue));
        return plan.Needs.Any(need =>
            need.WasContentChanged &&
            mediaKinds.Contains(MediaKinds.Normalize(need.Kind), StringComparer.OrdinalIgnoreCase) &&
            string.Equals(
                NormalizeForCompare(ResolveCanonicalPlanMediaPath(need, systemRoot)),
                nextDiskPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasLiveVisibleMediaRefreshDelta(
        MediaProjectionPlan plan,
        XElement gameElement,
        CancellationToken cancellationToken)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
        var existingGameNode = TryLoadExistingGameNode(plan.GamelistPath, relativeGamePath, cancellationToken);
        if (existingGameNode == null)
        {
            return HasLiveVisibleSlotElement(gameElement);
        }

        foreach (var tagName in LiveVisibleMediaTags())
        {
            var nextElement = gameElement.Element(tagName);
            if (nextElement == null)
            {
                continue;
            }

            var nextValue = nextElement.Value.Trim();
            var existingValue = existingGameNode.Element(tagName)?.Value?.Trim() ?? string.Empty;
            if (!string.Equals(NormalizeForCompare(nextValue), NormalizeForCompare(existingValue), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return HasChangedVisibleMediaContent(plan, gameElement, systemRoot);
    }

    private static bool HasChangedVisibleMediaContent(MediaProjectionPlan plan, XElement gameElement, string systemRoot)
    {
        var visibleDiskPaths = LiveVisibleMediaTags()
            .Select(tag => gameElement.Element(tag)?.Value?.Trim() ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeForCompare(ResolveEsRelativePath(systemRoot, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (visibleDiskPaths.Count == 0)
        {
            return false;
        }

        foreach (var need in plan.Needs.Where(need => need.WasContentChanged || need.WasImported || need.WasProjected))
        {
            var mediaPath = ResolveCanonicalPlanMediaPath(need, systemRoot);
            if (!string.IsNullOrWhiteSpace(mediaPath) &&
                visibleDiskPaths.Contains(NormalizeForCompare(mediaPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> LiveVisibleMediaTags()
    {
        yield return "image";
        yield return "thumbnail";
        yield return "marquee";
        yield return "fanart";
    }

    private async Task<bool> PushLiveMediaToEsAsync(MediaProjectionPlan plan, XElement gameElement, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.EsGameId))
        {
            return false;
        }

        var mediaSlot = ResolveLiveEsMediaSlot(plan, gameElement);
        if (mediaSlot == null)
        {
            return false;
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var diskPath = ResolveGamelistMediaDiskPath(systemRoot, mediaSlot.Value.MediaPath);
        if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
        {
            return false;
        }

        return await PushSingleLiveMediaToEsAsync(plan, mediaSlot.Value.MediaType, diskPath, cancellationToken);
    }

    private async Task<bool> PushSingleLiveMediaToEsAsync(
        MediaProjectionPlan plan,
        string mediaType,
        string diskPath,
        CancellationToken cancellationToken)
    {
        await LiveEsMediaPushGate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessContentType(diskPath, mediaType));
            using var response = await _esHttpClient.PostAsync(
                $"/systems/{Uri.EscapeDataString(plan.FrontendSystemId)}/games/{Uri.EscapeDataString(plan.EsGameId)}/media/{Uri.EscapeDataString(mediaType)}",
                content,
                cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                _logger?.LogInformation(
                    "Live ES media pushed for system={SystemId}, gameid={EsGameId}, mediaType={MediaType}, path={MediaPath}.",
                    plan.FrontendSystemId,
                    plan.EsGameId,
                    mediaType,
                    diskPath);
                return true;
            }

            _logger?.LogWarning(
                "Live ES media push returned HTTP {StatusCode} for system={SystemId}, gameid={EsGameId}, mediaType={MediaType}.",
                (int)response.StatusCode,
                plan.FrontendSystemId,
                plan.EsGameId,
                mediaType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogInformation(
                ex,
                "Live ES media push skipped: EmulationStation API unavailable or media unreadable for system={SystemId}, gameid={EsGameId}, mediaType={MediaType}.",
                plan.FrontendSystemId,
                plan.EsGameId,
                mediaType);
        }
        finally
        {
            try
            {
                var delayMs = Math.Clamp(_options.CurrentValue.Scraping.LiveEsMediaPushDelayMs, 0, 10000);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
            finally
            {
                LiveEsMediaPushGate.Release();
            }
        }

        return false;
    }

    private static Dictionary<string, string> BuildLiveMetadataPayload(MediaProjectionPlan plan, XElement gameElement)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tagName in MetadataTagNames().Prepend("desc").Prepend("name"))
        {
            if (!LiveAddGamesMetadataElementNames.Contains(tagName))
            {
                continue;
            }

            var element = gameElement.Element(tagName);
            var value = element?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                payload[tagName] = NormalizeLiveAddGamesElementValue(tagName, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.ScreenScraperGameId))
        {
            payload["scraperId"] = plan.ScreenScraperGameId.Trim();
        }

        return payload;
    }

    private (string MediaType, string MediaPath)? ResolveLiveEsMediaSlot(MediaProjectionPlan plan, XElement gameElement)
    {
        var currentKinds = plan.Needs
            .Select(need => MediaKinds.Normalize(need.Kind))
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (currentKinds.Count == 0)
        {
            return null;
        }

        var scrapingSettings = _settingsService.GetScrapingSettings();
        var visibleSlots = new[]
        {
            ("image", NormalizeSelectionSourceToKind(scrapingSettings.ImageSource, MediaSelectionTarget.Image, scrapingSettings.WheelStyle)),
            ("thumbnail", NormalizeSelectionSourceToKind(scrapingSettings.ThumbSource, MediaSelectionTarget.Thumbnail, scrapingSettings.WheelStyle)),
            ("marquee", NormalizeSelectionSourceToKind(scrapingSettings.LogoSource, MediaSelectionTarget.Logo, scrapingSettings.WheelStyle))
        };

        foreach (var slot in visibleSlots)
        {
            if (!currentKinds.Contains(MediaKinds.Normalize(slot.Item2)))
            {
                continue;
            }

            var value = gameElement.Element(slot.Item1)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (slot.Item1, value);
            }
        }

        foreach (var kind in currentKinds)
        {
            var mediaSlot = ResolveDirectMediaSlot(kind, gameElement);
            if (mediaSlot != null)
            {
                return mediaSlot;
            }
        }

        return null;
    }

    private static (string MediaType, string MediaPath)? ResolveDirectMediaSlot(string kind, XElement gameElement)
    {
        var mediaType = MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Image => "titleshot",
            MediaKinds.Thumbnail => "thumbnail",
            MediaKinds.Marquee => "marquee",
            MediaKinds.Fanart => "fanart",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var value = gameElement.Element(mediaType)?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? null
            : (mediaType, value);
    }

    private static string ResolveGamelistMediaDiskPath(string systemRoot, string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) ||
            mediaPath.StartsWith("/systems/", StringComparison.OrdinalIgnoreCase) ||
            mediaPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            mediaPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return ResolveEsRelativePath(systemRoot, mediaPath);
    }

    private static string GuessContentType(string mediaPath, string mediaType)
    {
        var extension = Path.GetExtension(mediaPath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".pdf" => "application/pdf",
            ".cbz" => "application/cbz",
            _ when string.Equals(mediaType, "manual", StringComparison.OrdinalIgnoreCase) => "application/pdf",
            _ when string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mediaType, "videonormalized", StringComparison.OrdinalIgnoreCase) => "video/mp4",
            _ => "image/png"
        };
    }

    public async Task<bool> RefreshSelectionsForSystemAsync(string systemId, EmulationStationScrapingSettings? settingsSnapshot = null, CancellationToken cancellationToken = default)
    {
        var result = await RefreshSelectionsForSystemAsync(
            systemId,
            settingsSnapshot ?? _settingsService.GetScrapingSettings(),
            completeProgress: true,
            cancellationToken);
        return result.Changed;
    }

    private Task<SelectionRefreshResult> RefreshSelectionsForSystemAsync(
        string systemId,
        EmulationStationScrapingSettings settingsSnapshot,
        bool completeProgress,
        CancellationToken cancellationToken,
        string? progressTitle = null,
        string? loadReason = null,
        bool visibleSlotsOnly = false,
        bool updateTextMetadata = false,
        IDictionary<string, LocalMediaIndex>? mediaIndexCache = null,
        bool reportTaskProgress = true,
        string? startupProgressKey = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(systemId))
        {
            return Task.FromResult(new SelectionRefreshResult(false, false, false));
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return Task.FromResult(new SelectionRefreshResult(false, false, false));
        }

        var canonicalSystemId = _systemIdNormalizer.Normalize(systemId);
        var mediaIndex = GetOrBuildLocalMediaIndex(canonicalSystemId, mediaIndexCache, cancellationToken);
        using var reallocation = _runtimeState.BeginMediaReallocation($"refresh-system-selections:{systemId}");
        lock (GetGamelistLock(gamelistPath))
        {
            var document = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, loadReason ?? "reallocation des medias");
            if (document == null)
            {
                return Task.FromResult(new SelectionRefreshResult(false, false, false));
            }

            var root = document.Root;
            if (root == null)
            {
                return Task.FromResult(new SelectionRefreshResult(false, false, false));
            }

            var scrapingSettings = settingsSnapshot;
            var projectedKindPaths = EmptyKindPaths();
            var updated = false;
            var gameNodes = root.Elements("game").ToList();
            var currentSelectionGamePath = ResolveCurrentSelectionGamePathForSystem(systemId, systemRoot);
            MoveCurrentGameLast(gameNodes, currentSelectionGamePath, systemRoot);
            var totalGames = Math.Max(1, gameNodes.Count);
            var title = string.IsNullOrWhiteSpace(progressTitle)
                ? $"Normalisation medias ES - {systemId}"
                : progressTitle;
            if (reportTaskProgress)
            {
                _taskProgressService.Report(
                    "refresh-system-selections",
                    title,
                    0,
                    totalGames,
                    systemId);
            }
            ReportStartupProgress(0, totalGames, systemId);

            try
            {
                var processedGames = 0;
                var currentSelectionProcessed = false;
                foreach (var gameNode in gameNodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var gamePath = gameNode.Element("path")?.Value;
                    if (string.IsNullOrWhiteSpace(gamePath))
                    {
                        processedGames++;
                        if (processedGames == 1 || processedGames % 25 == 0 || processedGames == totalGames)
                        {
                            if (reportTaskProgress)
                            {
                                _taskProgressService.Report(
                                    "refresh-system-selections",
                                    title,
                                    processedGames,
                                    totalGames,
                                    systemId);
                            }
                            ReportStartupProgress(processedGames, totalGames, systemId);
                        }

                        continue;
                    }

                    var projectionBaseName = Path.GetFileNameWithoutExtension(gamePath.Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(projectionBaseName))
                    {
                        processedGames++;
                        if (processedGames == 1 || processedGames % 25 == 0 || processedGames == totalGames)
                        {
                            if (reportTaskProgress)
                            {
                                _taskProgressService.Report(
                                    "refresh-system-selections",
                                    title,
                                    processedGames,
                                    totalGames,
                                    systemId);
                            }
                            ReportStartupProgress(processedGames, totalGames, systemId);
                        }

                        continue;
                    }

                    var isCurrentSelectionGame = IsSameSelectionGamePath(systemRoot, gamePath, currentSelectionGamePath);
                    var currentSelectionBefore = isCurrentSelectionGame
                        ? gameNode.ToString(SaveOptions.DisableFormatting)
                        : string.Empty;
                    var gameName = gameNode.Element("name")?.Value;
                    var gameSlug = _gameNameNormalizer.NormalizeGameSlug(gameName, gamePath);
                    var familySlug = _gameNameNormalizer.NormalizeGameSlug(null, projectionBaseName);
                    var romMetadata = _romMetadataResolver.Resolve(systemId, gamePath, gameName);
                    var canonicalKindPaths = BuildCanonicalKindPathsFromMediaIndex(
                        mediaIndex,
                        canonicalSystemId,
                        systemId,
                        gamePath,
                        gameSlug,
                        familySlug,
                        systemRoot,
                        scrapingSettings,
                        romMetadata.Regions,
                        romMetadata.Languages);
                    if (!visibleSlotsOnly)
                    {
                        updated |= CleanupMismatchedApiProjectedTemplateElements(gameNode, projectedKindPaths) > 0;
                        var originalKindPaths = ResolveExistingGamelistKindPaths(gameNode, systemRoot, projectionBaseName, projectedKindPaths);
                        AddVerifiedCanonicalPathsFromExistingTags(canonicalKindPaths, originalKindPaths, systemRoot);
                        var kindPaths = MergeKindPaths(canonicalKindPaths, originalKindPaths);

                        updated |= TrySetTemplateMediaElements(gameNode, kindPaths);
                    }

                    updated |= RemoveLegacyOriginalVisibleSlotElements(gameNode) > 0;
                    var preferredImagePath = ResolveSelectedCanonicalMediaPathStrict(
                        scrapingSettings.ImageSource,
                        canonicalKindPaths,
                        MediaSelectionTarget.Image,
                        scrapingSettings.WheelStyle);

                    updated |= TrySetVisibleSlotElement(gameNode, "image", preferredImagePath);
                    updated |= TrySetVisibleSlotElement(gameNode, "marquee", ResolveSelectedCanonicalMediaPathStrict(
                        scrapingSettings.LogoSource,
                        canonicalKindPaths,
                        MediaSelectionTarget.Logo,
                        scrapingSettings.WheelStyle));
                    updated |= TrySetVisibleSlotElement(gameNode, "thumbnail", ResolveSelectedCanonicalMediaPathStrict(
                        scrapingSettings.ThumbSource,
                        canonicalKindPaths,
                        MediaSelectionTarget.Thumbnail,
                        scrapingSettings.WheelStyle));
                    if (!visibleSlotsOnly || updateTextMetadata)
                    {
                        var preferredBundle = ResolvePreferredBundle(systemId, gameSlug, scrapingSettings.Language, cancellationToken)
                            ?? ResolvePreferredBundle(systemId, familySlug, scrapingSettings.Language, cancellationToken);
                        updated |= TrySetLocalizedSelectionElement(gameNode, "desc", preferredBundle, scrapingSettings.Language);
                        updated |= TrySetMetadataElements(gameNode, preferredBundle, scrapingSettings.Language);
                        updated |= TrySetRomIdentityMetadataElements(gameNode, romMetadata.Regions, romMetadata.Languages);
                    }

                    if (isCurrentSelectionGame)
                    {
                        currentSelectionProcessed = true;
                        _logger?.LogInformation(
                            "Media selection normalization processed current game: system={SystemId}, path={GamePath}, changed={Changed}, image={Image}, marquee={Marquee}, thumbnail={Thumbnail}.",
                            systemId,
                            gamePath,
                            !string.Equals(currentSelectionBefore, gameNode.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal),
                            gameNode.Element("image")?.Value ?? string.Empty,
                            gameNode.Element("marquee")?.Value ?? string.Empty,
                            gameNode.Element("thumbnail")?.Value ?? string.Empty);
                    }

                    processedGames++;
                    if (processedGames == 1 || processedGames % 25 == 0 || processedGames == totalGames)
                    {
                        if (reportTaskProgress)
                        {
                            _taskProgressService.Report(
                                "refresh-system-selections",
                                title,
                                processedGames,
                                totalGames,
                                gameName ?? projectionBaseName);
                        }
                        ReportStartupProgress(processedGames, totalGames, $"{systemId}: {gameName ?? projectionBaseName}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(currentSelectionGamePath) && !currentSelectionProcessed)
                {
                    _logger?.LogWarning(
                        "Media selection normalization did not find current game in gamelist: system={SystemId}, path={GamePath}.",
                        systemId,
                        currentSelectionGamePath);
                }

                if (updated)
                {
                    if (GamelistDocumentMatchesFile(document, gamelistPath))
                    {
                        updated = false;
                    }
                    else
                    {
                        var saved = SaveGamelistDocument(document, gamelistPath, cancellationToken, allowMediaTagDrop: true);
                        if (!saved)
                        {
                            _logger?.LogWarning(
                                "Media selection normalization changed an in-memory gamelist but did not persist it: system={SystemId}, path={GamelistPath}.",
                                systemId,
                                gamelistPath);
                            return Task.FromResult(new SelectionRefreshResult(true, false, false));
                        }
                    }
                }

                return Task.FromResult(new SelectionRefreshResult(true, updated, true));
            }
            finally
            {
                if (completeProgress && reportTaskProgress)
                {
                    _taskProgressService.Complete("refresh-system-selections");
                }
            }

            void ReportStartupProgress(int current, int total, string detail)
            {
                if (!string.IsNullOrWhiteSpace(startupProgressKey))
                {
                    _startupOverlayService.UpdateStartupProgress(startupProgressKey!, current, total, detail);
                }
            }
        }
    }

    private LocalMediaIndex GetOrBuildLocalMediaIndex(
        string canonicalSystemId,
        IDictionary<string, LocalMediaIndex>? mediaIndexCache,
        CancellationToken cancellationToken)
    {
        if (mediaIndexCache != null &&
            mediaIndexCache.TryGetValue(canonicalSystemId, out var cachedIndex))
        {
            return cachedIndex;
        }

        var mediaIndex = _localMediaIndexService.Build([canonicalSystemId], cancellationToken);
        mediaIndexCache?.TryAdd(canonicalSystemId, mediaIndex);
        return mediaIndex;
    }

    public Task<int> RefreshSelectionsForAllSystemsAsync(EmulationStationScrapingSettings? settingsSnapshot = null, CancellationToken cancellationToken = default)
    {
        return RefreshSelectionsForAllSystemsAsync(
            settingsSnapshot,
            cancellationToken,
            updateTextMetadata: false);
    }

    public Task<int> RefreshSelectionsForAllSystemsWithTextMetadataAsync(
        EmulationStationScrapingSettings? settingsSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshSelectionsForAllSystemsAsync(
            settingsSnapshot,
            cancellationToken,
            updateTextMetadata: true);
    }

    private Task<int> RefreshSelectionsForAllSystemsAsync(
        EmulationStationScrapingSettings? settingsSnapshot,
        CancellationToken cancellationToken,
        bool updateTextMetadata)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return Task.FromResult(0);
        }

        var systemIds = Directory.GetDirectories(RetroBatPaths.RomsRoot)
            .Select(Path.GetFileName)
            .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
            .Select(systemId => systemId!)
            .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentSystemId = ResolveCurrentFrontendSystemId();
        MoveCurrentSystemLast(systemIds, currentSystemId);

        var scrapingSettings = settingsSnapshot ?? _settingsService.GetScrapingSettings();
        var refreshedSystems = 0;
        var totalSystems = Math.Max(1, systemIds.Count);
        var mediaIndexCache = new Dictionary<string, LocalMediaIndex>(StringComparer.OrdinalIgnoreCase);
        _logger?.LogInformation(
            "Global media selection normalization starting: systems={SystemCount}, currentSystem={CurrentSystemId}. Current system is processed last to avoid ES overwriting its active gamelist.",
            systemIds.Count,
            string.IsNullOrWhiteSpace(currentSystemId) ? "(none)" : currentSystemId);
        _taskProgressService.Report(
            "refresh-system-selections",
            "Normalisation medias ES - tous les systemes",
            0,
            totalSystems,
            "startup");

        try
        {
            var selectionState = LoadSelectionNormalizationState();
            var settingsSignature = BuildSelectionNormalizationSettingsSignature(scrapingSettings);
            for (var index = 0; index < systemIds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var systemId = systemIds[index]!;
                _taskProgressService.Report(
                    "refresh-system-selections",
                    "Normalisation medias ES - tous les systemes",
                    index,
                    totalSystems,
                    systemId);

                var refreshResult = RefreshSelectionsForSystemAsync(
                        systemId,
                        scrapingSettings,
                        completeProgress: false,
                        cancellationToken,
                        visibleSlotsOnly: true,
                        updateTextMetadata: updateTextMetadata,
                        mediaIndexCache: mediaIndexCache).GetAwaiter().GetResult();
                if (refreshResult.Changed)
                {
                    refreshedSystems++;
                    _logger?.LogInformation(
                        "Global media selection normalization updated system={SystemId}{CurrentMarker}.",
                        systemId,
                        string.Equals(systemId, currentSystemId, StringComparison.OrdinalIgnoreCase) ? " (current)" : string.Empty);
                }

                if (refreshResult.Processed && refreshResult.SaveSucceeded)
                {
                    UpdateSelectionNormalizationStateForSystem(systemId, scrapingSettings, settingsSignature, selectionState);
                }
                else if (refreshResult.Processed)
                {
                    _logger?.LogWarning(
                        "Global media selection normalization did not update cache because persistence failed: system={SystemId}.",
                        systemId);
                }
            }

            SaveSelectionNormalizationStateIfDirty();
            _taskProgressService.Report(
                "refresh-system-selections",
                "Normalisation medias ES - tous les systemes",
                totalSystems,
                totalSystems,
                "termine");
        }
        finally
        {
            _taskProgressService.Complete("refresh-system-selections");
        }

        return Task.FromResult(refreshedSystems);
    }

    private string ResolveCurrentFrontendSystemId()
    {
        if (!string.IsNullOrWhiteSpace(_context.Ui.Selected?.SystemId))
        {
            return _context.Ui.Selected.SystemId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_context.Ui.Running?.SystemId))
        {
            return _context.Ui.Running.SystemId.Trim();
        }

        return _context.Ui.SelectedSystem?.Name?.Trim() ?? string.Empty;
    }

    private string ResolveCurrentSelectionGamePathForSystem(string systemId, string systemRoot)
    {
        if (TryReadCurrentSelectedGameFromEventsIni(out var currentSystemId, out var currentGamePath) &&
            IsSameFrontendSystem(systemId, currentSystemId))
        {
            return ResolveGamePathForSelectionComparison(systemRoot, currentGamePath);
        }

        var selected = _context.Ui.Selected;
        if (selected == null)
        {
            return string.Empty;
        }

        var selectedSystem = selected.SystemId;
        if (string.IsNullOrWhiteSpace(selectedSystem))
        {
            selectedSystem = _context.Ui.SelectedSystem?.Name ?? string.Empty;
        }

        return IsSameFrontendSystem(systemId, selectedSystem)
            ? ResolveGamePathForSelectionComparison(systemRoot, selected.GamePath)
            : string.Empty;
    }

    private bool IsSameFrontendSystem(string systemId, string selectedSystemId)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(selectedSystemId))
        {
            return false;
        }

        if (string.Equals(systemId, selectedSystemId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            _systemIdNormalizer.Normalize(systemId),
            _systemIdNormalizer.Normalize(selectedSystemId),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveCurrentSystemLast(List<string> systemIds, string currentSystemId)
    {
        if (string.IsNullOrWhiteSpace(currentSystemId))
        {
            return;
        }

        var index = systemIds.FindIndex(systemId => string.Equals(systemId, currentSystemId, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index == systemIds.Count - 1)
        {
            return;
        }

        var systemId = systemIds[index];
        systemIds.RemoveAt(index);
        systemIds.Add(systemId);
    }

    private static void MoveCurrentGameLast(List<XElement> gameNodes, string currentSelectionGamePath, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(currentSelectionGamePath))
        {
            return;
        }

        var index = gameNodes.FindIndex(gameNode =>
            IsSameSelectionGamePath(systemRoot, gameNode.Element("path")?.Value, currentSelectionGamePath));
        if (index < 0 || index == gameNodes.Count - 1)
        {
            return;
        }

        var gameNode = gameNodes[index];
        gameNodes.RemoveAt(index);
        gameNodes.Add(gameNode);
    }

    private static bool IsSameSelectionGamePath(string systemRoot, string? gamePath, string currentSelectionGamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(currentSelectionGamePath))
        {
            return false;
        }

        return string.Equals(
            ResolveGamePathForSelectionComparison(systemRoot, gamePath),
            currentSelectionGamePath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveGamePathForSelectionComparison(string systemRoot, string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(ResolveEsRelativePath(systemRoot, gamePath));
        }
        catch
        {
            return NormalizeGamePathForSelection(gamePath);
        }
    }

    public async Task<int> RefreshSelectionsForAllSystemsAtStartupAsync(EmulationStationScrapingSettings? settingsSnapshot = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            _startupOverlayService.UpdateStartupProgress("startup_gamelist_media_normalization", 1, 1, "no roms");
            await StartupGamelistPreparationLog.AppendAsync(
                "visible-media-selection",
                "skipped",
                new { reason = "no-roms-root", elapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
            return 0;
        }

        var systemIds = Directory.GetDirectories(RetroBatPaths.RomsRoot)
            .Select(Path.GetFileName)
            .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
            .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var scrapingSettings = settingsSnapshot ?? _settingsService.GetScrapingSettings();
        var settingsSignature = BuildSelectionNormalizationSettingsSignature(scrapingSettings);
        var state = LoadSelectionNormalizationState();
        var systemsToProcess = new List<string>();
        var skippedSystems = 0;
        var missingGamelistSystems = 0;
        var mediaIndexCache = new Dictionary<string, LocalMediaIndex>(StringComparer.OrdinalIgnoreCase);
        var totalStartupSystems = Math.Max(1, systemIds.Count);
        _startupOverlayService.UpdateStartupProgress("startup_gamelist_media_normalization", 0, totalStartupSystems, "controle cache");

        for (var index = 0; index < systemIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var systemId = systemIds[index]!;
            _startupOverlayService.UpdateStartupProgress(
                "startup_gamelist_media_normalization",
                index,
                totalStartupSystems,
                systemId);

            var gamelistPath = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
            if (!File.Exists(gamelistPath))
            {
                missingGamelistSystems++;
                continue;
            }

            if (ShouldSkipStartupSelectionNormalization(systemId!, scrapingSettings, settingsSignature, state))
            {
                skippedSystems++;
                continue;
            }

            systemsToProcess.Add(systemId);
        }

        if (systemsToProcess.Count == 0)
        {
            _startupOverlayService.UpdateStartupProgress(
                "startup_gamelist_media_normalization",
                totalStartupSystems,
                totalStartupSystems,
                $"cache OK ({skippedSystems})");
            _logger?.LogInformation(
                "Startup media selection normalization cache hit: {SkippedSystems}/{TotalSystems} systemes inchanges.",
                skippedSystems,
                systemIds.Count);
            await StartupGamelistPreparationLog.AppendAsync(
                "visible-media-selection",
                "cache-hit",
                new
                {
                    totalSystems = systemIds.Count,
                    skippedSystems,
                    missingGamelistSystems,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                },
                cancellationToken);
            return 0;
        }

        var refreshedSystems = 0;
        var totalSystems = Math.Max(1, systemsToProcess.Count);

        for (var index = 0; index < systemsToProcess.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var systemId = systemsToProcess[index];
            _startupOverlayService.UpdateStartupProgress(
                "startup_gamelist_media_normalization",
                index,
                totalSystems,
                systemId);

            var refreshResult = RefreshSelectionsForSystemAsync(
                    systemId,
                    scrapingSettings,
                    completeProgress: false,
                    cancellationToken,
                    progressTitle: $"Normalisation medias ES - {systemId}",
                    loadReason: "normalisation medias ES",
                    visibleSlotsOnly: true,
                    updateTextMetadata: true,
                    mediaIndexCache: mediaIndexCache,
                    reportTaskProgress: false,
                    startupProgressKey: "startup_gamelist_media_normalization").GetAwaiter().GetResult();
            if (refreshResult.Changed)
            {
                refreshedSystems++;
            }

            if (refreshResult.Processed && refreshResult.SaveSucceeded)
            {
                UpdateSelectionNormalizationStateForSystem(systemId, scrapingSettings, settingsSignature, state);
            }
            else if (refreshResult.Processed)
            {
                _logger?.LogWarning(
                    "Startup media selection normalization did not update cache because persistence failed: system={SystemId}.",
                    systemId);
            }
        }

        SaveSelectionNormalizationStateIfDirty();
        _startupOverlayService.UpdateStartupProgress(
            "startup_gamelist_media_normalization",
            totalSystems,
            totalSystems,
            skippedSystems > 0 ? $"termine, {skippedSystems} skips" : "termine");
        _logger?.LogInformation(
            "Startup media selection normalization completed: processed={ProcessedSystems}, skipped={SkippedSystems}, updated={UpdatedSystems}.",
            systemsToProcess.Count,
            skippedSystems,
            refreshedSystems);

        await StartupGamelistPreparationLog.AppendAsync(
            "visible-media-selection",
            "completed",
            new
            {
                processedSystems = systemsToProcess.Count,
                skippedSystems,
                missingGamelistSystems,
                updatedSystems = refreshedSystems,
                totalSystems = systemIds.Count,
                elapsedMs = stopwatch.ElapsedMilliseconds
            },
            cancellationToken);

        return refreshedSystems;
    }

    public Task<GamelistConsolidateResponse> ConsolidateGamelistFromRichBackupAsync(
        GamelistConsolidateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var systemId = _systemIdNormalizer.NormalizeFrontend(request.SystemId);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            throw new InvalidOperationException("SystemId is required.");
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            throw new InvalidOperationException($"gamelist.xml introuvable pour le systeme {systemId}: {gamelistPath}");
        }

        var response = new GamelistConsolidateResponse
        {
            SystemId = systemId,
            DryRun = request.DryRun,
            GamelistPath = gamelistPath
        };

        using var reallocation = _runtimeState.BeginMediaReallocation($"gamelist-consolidate:{systemId}");
        lock (GetGamelistLock(gamelistPath))
        {
            var currentDocument = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, "consolidation depuis backup riche");
            var currentRoot = currentDocument?.Root;
            if (currentDocument == null || currentRoot == null)
            {
                throw new InvalidOperationException($"gamelist.xml invalide ou illisible pour le systeme {systemId}: {gamelistPath}");
            }

            var currentMetrics = CreateGamelistMetrics(
                currentDocument,
                GetWriteTimeTicksUtc(gamelistPath),
                new FileInfo(gamelistPath).Length);
            response.CurrentGames = currentMetrics.GameCount;
            response.CurrentGamesWithMedia = currentMetrics.GamesWithAnyMedia;
            response.CurrentMediaTags = currentMetrics.MediaTagCount;

            var richCandidate = ResolveRichGamelistCandidate(systemRoot, gamelistPath, request, currentMetrics, cancellationToken);
            if (richCandidate == null)
            {
                response.Message = "Aucun backup plus riche en medias n'a ete trouve.";
                return Task.FromResult(response);
            }

            response.RichGamelistPath = richCandidate.Path;
            response.RichGames = richCandidate.Metrics.GameCount;
            response.RichGamesWithMedia = richCandidate.Metrics.GamesWithAnyMedia;
            response.RichMediaTags = richCandidate.Metrics.MediaTagCount;

            var richIndex = BuildConsolidationGameIndex(richCandidate.Document.Root);
            var updated = false;
            foreach (var currentGameNode in currentRoot.Elements("game").ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                response.GamesProcessed++;

                if (!TryFindConsolidationSourceGame(currentGameNode, richIndex, out var richGameNode))
                {
                    continue;
                }

                response.GamesMatched++;
                var restored = ConsolidateGameMediaElements(
                    currentGameNode,
                    richGameNode,
                    request.OverwriteExistingMedia,
                    request.IncludeTextMetadata,
                    request.DryRun);
                if (restored <= 0)
                {
                    continue;
                }

                response.TagsRestored += restored;
                response.GamesUpdated++;
                updated = true;
            }

            if (updated && !request.DryRun)
            {
                response.Saved = SaveGamelistDocument(currentDocument, gamelistPath, cancellationToken);
                if (response.Saved)
                {
                    _runtimeState.MarkReloadGamesPending();
                    response.ReloadGamesRequested = true;
                }
            }

            response.Message = updated
                ? request.DryRun
                    ? "Consolidation disponible; relancer avec dryRun=false pour ecrire le gamelist."
                    : response.Saved
                        ? "Consolidation appliquee depuis le backup riche."
                        : "Consolidation calculee, mais l'ecriture du gamelist a ete rejetee ou a echoue."
                : "Backup riche trouve, mais aucune balise manquante n'a ete restauree.";
        }

        return Task.FromResult(response);
    }

    public async Task<int> EnsureDefaultPlaceholdersForAllSystemsAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Scraping.BootstrapDefaultPlaceholdersOnStartup)
        {
            _startupOverlayService.UpdateStartupProgress("startup_gamelist_media_normalization", 1, 1, "disabled");
            _logger?.LogInformation("Bootstrap gamelist placeholders desactive par appsettings: aucun slot media n'est modifie au demarrage.");
            await StartupGamelistPreparationLog.AppendAsync(
                "default-placeholders",
                "skipped",
                new { reason = "disabled", elapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
            return 0;
        }

        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            await StartupGamelistPreparationLog.AppendAsync(
                "default-placeholders",
                "skipped",
                new { reason = "no-roms-root", elapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
            return 0;
        }

        var systemDirectories = Directory.GetDirectories(RetroBatPaths.RomsRoot)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var bootstrapState = LoadBootstrapState();
        var placeholderSourceWriteTicksUtc = GetDefaultPlaceholderSourceWriteTicksUtc();
        var systemsToProcess = systemDirectories
            .Where(path => NeedsBootstrapScan(path, bootstrapState, placeholderSourceWriteTicksUtc))
            .ToArray();

        if (systemsToProcess.Length == 0)
        {
            _startupOverlayService.UpdateStartupProgress("startup_gamelist_media_normalization", 1, 1, "cache-hit");
            _logger?.LogInformation("Bootstrap placeholders startup cache hit: aucun systeme a rescanner.");
            await StartupGamelistPreparationLog.AppendAsync(
                "default-placeholders",
                "cache-hit",
                new
                {
                    totalSystems = systemDirectories.Length,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                },
                cancellationToken);
            return 0;
        }

        using var reallocation = _runtimeState.BeginMediaReallocation("bootstrap-default-placeholders");
        _taskProgressService.Report(
            "bootstrap-default-placeholders",
            "Initialisation des images par defaut",
            0,
            Math.Max(1, systemsToProcess.Length),
            "demarrage");

        try
        {
            var updatedEntries = 0;
            var processedEntries = 0;
            var totalEntries = 0;
            var processedSystems = 0;

            foreach (var systemDirectory in systemsToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var systemId = Path.GetFileName(systemDirectory);
                if (string.IsNullOrWhiteSpace(systemId))
                {
                    continue;
                }

                _taskProgressService.Report(
                    "bootstrap-default-placeholders",
                    "Initialisation des images par defaut",
                    processedSystems,
                    Math.Max(1, systemsToProcess.Length),
                    systemId);

                try
                {
                    updatedEntries += EnsureDefaultPlaceholdersForSystem(systemId, ref processedEntries, ref totalEntries, cancellationToken);
                    UpdateBootstrapStateForSystem(systemDirectory, systemId, bootstrapState, placeholderSourceWriteTicksUtc);
                    processedSystems++;
                    _taskProgressService.Report(
                        "bootstrap-default-placeholders",
                        "Initialisation des images par defaut",
                        processedSystems,
                        Math.Max(1, systemsToProcess.Length),
                        systemId);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Impossible d'initialiser les placeholders par defaut pour le systeme {SystemId}.", systemId);
                }
            }

            SaveBootstrapState(bootstrapState);
            _startupOverlayService.UpdateStartupProgress("startup_gamelist_media_normalization", processedEntries, Math.Max(1, totalEntries), null);
            _logger?.LogInformation(
                "Bootstrap placeholders startup cache processed {ProcessedSystems}/{TotalSystems} systemes, {UpdatedEntries} entrees mises a jour.",
                processedSystems,
                systemDirectories.Length,
                updatedEntries);

            await StartupGamelistPreparationLog.AppendAsync(
                "default-placeholders",
                "completed",
                new
                {
                    processedSystems,
                    totalSystems = systemDirectories.Length,
                    updatedEntries,
                    processedEntries,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                },
                cancellationToken);

            return updatedEntries;
        }
        finally
        {
            _taskProgressService.Complete("bootstrap-default-placeholders");
        }
    }

    public Task<bool> EnsureScrapeInProgressPlaceholderAsync(MediaProjectionPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var placeholderPath = EnsureScrapingInProgressPlaceholderImage(plan.FrontendSystemId, plan.ProjectionBaseName);
        if (string.IsNullOrWhiteSpace(placeholderPath))
        {
            _logger?.LogInformation(
                "Scrape placeholder switch skipped for system={SystemId}, game={GameSlug}: scraping_in_progress placeholder source is unavailable",
                plan.FrontendSystemId,
                plan.GameSlug);
            return Task.FromResult(false);
        }

        var updated = false;
        var saveFailed = false;
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var gamelistPath = plan.GamelistPath;
        lock (GetGamelistLock(gamelistPath))
        {
            if (File.Exists(gamelistPath))
            {
                var document = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, "placeholder de scraping");
                if (document == null)
                {
                    return Task.FromResult(false);
                }

                var root = document.Root;
                if (root != null)
                {
                    var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
                    var gameNode = FindOrCreateGameNode(root, relativeGamePath);
                    updated = TrySetSelectionElement(gameNode, "image", placeholderPath);
                    if (updated)
                    {
                        saveFailed = !SaveGamelistDocument(document, gamelistPath, cancellationToken);
                        updated = !saveFailed;
                    }
                }
            }
        }

        _logger?.LogInformation(
            updated
                ? "Scrape placeholder switched to scraping_in_progress for system={SystemId}, game={GameSlug}, projectionBaseName={ProjectionBaseName}, imagePath={ImagePath}, diskPath={DiskPath}"
                : "Scrape placeholder file refreshed on disk for system={SystemId}, game={GameSlug}, projectionBaseName={ProjectionBaseName}, imagePath={ImagePath}, diskPath={DiskPath}; gamelist path was unchanged",
            plan.FrontendSystemId,
            plan.GameSlug,
            plan.ProjectionBaseName,
            placeholderPath,
            GetPlaceholderDiskPath(plan.FrontendSystemId, plan.ProjectionBaseName));

        return Task.FromResult(!saveFailed);
    }

    public Task<bool> FinalizeScrapePlaceholderAsync(MediaProjectionPlan plan, bool noMediaFound, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, plan.FrontendSystemId);
        var gamelistPath = plan.GamelistPath;
        var updated = false;
        var saveFailed = false;
        var finalImagePath = string.Empty;

        lock (GetGamelistLock(gamelistPath))
        {
            var document = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, "finalisation du placeholder de scraping");
            if (document == null)
            {
                return Task.FromResult(false);
            }

            var root = document.Root ?? new XElement("gameList");
            if (document.Root == null)
            {
                document.Add(root);
            }

            var relativeGamePath = ToGameRelativePath(plan.GamePath, systemRoot);
            var gameNode = FindOrCreateGameNode(root, relativeGamePath);
            var kindPaths = BuildCanonicalKindPathsFromPlan(plan, systemRoot);
            var scrapingSettings = _settingsService.GetScrapingSettings();
            finalImagePath = ResolveSelectedMediaPath(scrapingSettings.ImageSource, kindPaths, MediaSelectionTarget.Image, scrapingSettings.WheelStyle);

            if (string.IsNullOrWhiteSpace(finalImagePath))
            {
                finalImagePath = noMediaFound
                    ? EnsureNoMediaFoundPlaceholderImage(plan.FrontendSystemId, plan.ProjectionBaseName)
                    : string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(finalImagePath))
            {
                updated = TrySetSelectionElement(gameNode, "image", finalImagePath);
                if (updated)
                {
                    saveFailed = !SaveGamelistDocument(document, gamelistPath, cancellationToken);
                    updated = !saveFailed;
                }
            }
        }

        _logger?.LogInformation(
            noMediaFound
                ? "Scrape placeholder finalized to no_media_found for system={SystemId}, game={GameSlug}, projectionBaseName={ProjectionBaseName}, imagePath={ImagePath}"
                : "Scrape placeholder finalized for system={SystemId}, game={GameSlug}, projectionBaseName={ProjectionBaseName}, imagePath={ImagePath}",
            plan.FrontendSystemId,
            plan.GameSlug,
            plan.ProjectionBaseName,
            finalImagePath);

        return Task.FromResult(!string.IsNullOrWhiteSpace(finalImagePath) && !saveFailed);
    }

    private static string ResolveSelectedMediaPath(string selectedSource, IReadOnlyDictionary<string, string> kindPaths, MediaSelectionTarget target, string wheelStyle)
    {
        var preferredKind = NormalizeSelectionSourceToKind(
            selectedSource,
            target,
            wheelStyle);
        return kindPaths.TryGetValue(preferredKind, out var path) ? path : string.Empty;
    }

    private string ResolveSelectedMediaPathStrict(
        string selectedSource,
        IReadOnlyDictionary<string, string> kindPaths,
        IReadOnlyDictionary<string, string> projectedKindPaths,
        MediaSelectionTarget target,
        string wheelStyle)
    {
        var preferredKind = NormalizeSelectionSourceToKind(
            selectedSource,
            target,
            wheelStyle);
        if (string.IsNullOrWhiteSpace(preferredKind))
        {
            return string.Empty;
        }

        return kindPaths.TryGetValue(preferredKind, out var path) &&
            IsMediaPathCompatibleWithKind(path, preferredKind, projectedKindPaths)
            ? path
            : projectedKindPaths.TryGetValue(preferredKind, out var projectedPath) &&
                !string.IsNullOrWhiteSpace(projectedPath)
                ? projectedPath
                : string.Empty;
    }

    private static string FirstLiveAvailableMediaPath(
        IReadOnlyDictionary<string, string> kindPaths,
        IReadOnlyDictionary<string, string> projectedKindPaths,
        params string[] kinds)
    {
        foreach (var kind in kinds.Select(MediaKinds.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (kindPaths.TryGetValue(kind, out var path) && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            if (projectedKindPaths.TryGetValue(kind, out var projectedPath) &&
                !string.IsNullOrWhiteSpace(projectedPath))
            {
                return projectedPath;
            }
        }

        return string.Empty;
    }

    private static string ResolveSelectedCanonicalMediaPathStrict(
        string selectedSource,
        IReadOnlyDictionary<string, string> canonicalKindPaths,
        MediaSelectionTarget target,
        string wheelStyle)
    {
        var preferredKind = NormalizeSelectionSourceToKind(
            selectedSource,
            target,
            wheelStyle);
        if (string.IsNullOrWhiteSpace(preferredKind))
        {
            return string.Empty;
        }

        return canonicalKindPaths.TryGetValue(preferredKind, out var path) &&
            IsMediaPathCompatibleWithKind(path, preferredKind, null)
            ? path
            : string.Empty;
    }

    private static void ApplyTemplateMediaElements(XElement gameNode, IReadOnlyDictionary<string, string> kindPaths)
    {
        RemoveUnsupportedDurableMediaTags(gameNode);
        foreach (var pair in ResolveTemplateMediaElements(kindPaths))
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                SetOrCreateElement(gameNode, pair.Key, pair.Value);
            }
        }
    }

    private static bool TrySetTemplateMediaElements(XElement gameNode, IReadOnlyDictionary<string, string> kindPaths)
    {
        var updated = RemoveUnsupportedDurableMediaTags(gameNode) > 0;
        foreach (var pair in ResolveTemplateMediaElements(kindPaths))
        {
            updated |= TrySetSelectionElement(gameNode, pair.Key, pair.Value);
        }

        return updated;
    }

    private static int TrySetTemplateMediaElements(XElement gameNode, IReadOnlyDictionary<string, string> kindPaths, bool dryRun)
    {
        var updated = CountUnsupportedDurableMediaTags(gameNode);
        if (updated > 0 && !dryRun)
        {
            RemoveUnsupportedDurableMediaTags(gameNode);
        }

        foreach (var pair in ResolveTemplateMediaElements(kindPaths))
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            var element = gameNode.Element(pair.Key);
            if (element != null && string.Equals(element.Value, pair.Value, StringComparison.Ordinal))
            {
                continue;
            }

            updated++;
            if (!dryRun)
            {
                SetOrCreateElement(gameNode, pair.Key, pair.Value);
            }
        }

        return updated;
    }

    private static Dictionary<string, string> ResolveExistingGamelistKindPaths(
        XElement gameNode,
        string systemRoot,
        string projectionBaseName,
        IReadOnlyDictionary<string, string>? projectedKindPaths = null)
    {
        var kindPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddExistingWheelLikeGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "wheel");
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "wheelcarbon", MediaKinds.WheelCarbon);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "wheelsteel", MediaKinds.WheelSteel);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "boxart", MediaKinds.BoxFront);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "boxback", MediaKinds.BoxBack);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "cartridge", MediaKinds.Cartridge);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "label", MediaKinds.Label);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "fanart", MediaKinds.Fanart);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "extra1", MediaKinds.Flyer);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "figurine", MediaKinds.Figurine);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "mix", MediaKinds.MixRbv2);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "titleshot", MediaKinds.Image);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "screenshot", MediaKinds.Thumbnail);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "bezel", MediaKinds.Bezel);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "map", MediaKinds.Map);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "manual", MediaKinds.Manual);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "magazine", MediaKinds.Magazine);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "video", MediaKinds.Video);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "screenmarquee", MediaKinds.ScreenMarquee);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "screenmarqueesmall", MediaKinds.ScreenMarqueeSmall);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "steamgrid", MediaKinds.SteamGrid);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "mixrbv1", MediaKinds.MixRbv1);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "mixrbv2", MediaKinds.MixRbv2);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "videonormalized", MediaKinds.VideoNormalized);
        AddExistingGamelistKind(kindPaths, gameNode, systemRoot, projectionBaseName, projectedKindPaths, "themehb", MediaKinds.ThemeHb);

        return kindPaths;
    }

    private static Dictionary<string, string> MergeKindPaths(
        IReadOnlyDictionary<string, string> normalizedTagKindPaths,
        IReadOnlyDictionary<string, string> projectedKindPaths)
    {
        var kindPaths = new Dictionary<string, string>(normalizedTagKindPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in projectedKindPaths)
        {
            kindPaths.TryAdd(MediaKinds.Normalize(pair.Key), pair.Value);
        }

        return kindPaths;
    }

    private static IReadOnlyDictionary<string, string> EmptyKindPaths()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildCanonicalKindPathsFromPlan(MediaProjectionPlan plan, string systemRoot)
    {
        var kindPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var need in plan.Needs)
        {
            var sourcePath = ResolveCanonicalPlanMediaPath(need, systemRoot);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                continue;
            }

            var relativeMediaPath = ToMediaRelativePath(sourcePath, systemRoot);
            if (!string.IsNullOrWhiteSpace(relativeMediaPath))
            {
                kindPaths[MediaKinds.Normalize(need.Kind)] = relativeMediaPath;
            }
        }

        return kindPaths;
    }

    private Dictionary<string, string> BuildCanonicalKindPathsFromMediaIndex(
        LocalMediaIndex mediaIndex,
        string canonicalSystemId,
        string frontendSystemId,
        string gamePath,
        string gameSlug,
        string familySlug,
        string systemRoot,
        EmulationStationScrapingSettings scrapingSettings,
        IReadOnlyList<string>? romRegions = null,
        IReadOnlyList<string>? romLanguages = null)
    {
        var kindPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var relatedSlugs = BuildRelatedCanonicalMediaSlugs(frontendSystemId, gamePath, gameSlug, familySlug);

        foreach (var kind in CanonicalIndexedKinds)
        {
            var plan = new MediaProjectionPlan
            {
                SystemId = canonicalSystemId,
                FrontendSystemId = frontendSystemId,
                GameSlug = gameSlug,
                GamePath = gamePath,
                ProjectionBaseName = familySlug,
                RomRegions = romRegions?.ToList() ?? new List<string>(),
                RomLanguages = romLanguages?.ToList() ?? new List<string>()
            };
            var preferredRegions = _mediaLocalizationResolver.BuildMediaRegionPriority(plan, kind);
            var candidate = ResolveBestCanonicalMediaCandidate(mediaIndex, canonicalSystemId, gameSlug, familySlug, relatedSlugs, kind, preferredRegions);
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

        RemoveProjectedMarqueeIfPollutedByWheel(kindPaths, systemRoot);
        AddLogoAliasForSimpleWheel(kindPaths);
        return kindPaths;
    }

    private IReadOnlyList<string> BuildRelatedCanonicalMediaSlugs(string frontendSystemId, string gamePath, string gameSlug, string familySlug)
    {
        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            var normalized = _gameNameNormalizer.NormalizeGameSlug(null, value);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                slugs.Add(normalized);
            }
        }

        Add(gameSlug);
        Add(familySlug);

        foreach (var relatedRom in _mameGamelistGroupIndex.GetRelatedRoms(frontendSystemId, gamePath, gameSlug))
        {
            Add(relatedRom);
            Add(Path.GetFileNameWithoutExtension(relatedRom));
        }

        return slugs;
    }

    private static LocalMediaIndexCandidate? ResolveBestCanonicalMediaCandidate(
        LocalMediaIndex mediaIndex,
        string systemId,
        string gameSlug,
        string familySlug,
        IReadOnlyList<string> relatedSlugs,
        string kind,
        IReadOnlyList<string> preferredRegions)
    {
        var candidate = mediaIndex.ResolveBest(systemId, gameSlug, familySlug, kind, preferredRegions);
        if (candidate != null)
        {
            return candidate;
        }

        foreach (var relatedSlug in relatedSlugs)
        {
            candidate = mediaIndex.ResolveBest(systemId, relatedSlug, relatedSlug, kind, preferredRegions);
            if (candidate != null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveCanonicalPlanMediaPath(MediaNeed need, string systemRoot)
    {
        // Full canonical mode intentionally ignores ProjectedPath. It may still be present
        // on old in-memory plans, but new gamelist/addgames entries must not point to roms projections.
        foreach (var rawPath in new[] { need.ImportedPath, need.ExistingPath })
        {
            var resolvedPath = ResolveEsRelativePath(systemRoot, rawPath);
            if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
            {
                return resolvedPath;
            }
        }

        return string.Empty;
    }

    private static int RemoveLegacyOriginalVisibleSlotElements(XElement gameNode, bool dryRun = false)
    {
        var nodesToRemove = gameNode.Elements()
            .Where(element => element.Name.LocalName.StartsWith($"{LegacyOriginalVisibleSlotTagPrefix}_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!dryRun)
        {
            foreach (var node in nodesToRemove)
            {
                node.Remove();
            }
        }

        return nodesToRemove.Count;
    }

    private static int CleanupMismatchedApiProjectedTemplateElements(
        XElement gameNode,
        IReadOnlyDictionary<string, string> projectedKindPaths,
        bool dryRun = false)
    {
        var cleaned = 0;
        foreach (var pair in TemplateTagExpectedKinds)
        {
            var element = gameNode.Element(pair.Key);
            if (element == null ||
                !IsMismatchedProjectedKind(element.Value, projectedKindPaths, pair.Value))
            {
                continue;
            }

            cleaned++;
            if (!dryRun)
            {
                element.Value = string.Empty;
            }
        }

        return cleaned;
    }

    private static bool IsMismatchedProjectedKind(
        string? mediaPath,
        IReadOnlyDictionary<string, string>? projectedKindPaths,
        params string[] expectedKinds)
    {
        if (!TryGetProjectedKindForPath(mediaPath, projectedKindPaths, out var projectedKind))
        {
            return false;
        }

        var expected = expectedKinds
            .Select(MediaKinds.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !expected.Contains(MediaKinds.Normalize(projectedKind));
    }

    private static bool IsMediaPathCompatibleWithKind(
        string? mediaPath,
        string expectedKind,
        IReadOnlyDictionary<string, string>? projectedKindPaths)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || string.IsNullOrWhiteSpace(expectedKind))
        {
            return false;
        }

        var normalizedExpectedKind = MediaKinds.Normalize(expectedKind);
        if (TryGetProjectedKindForPath(mediaPath, projectedKindPaths, out var projectedKind))
        {
            return string.Equals(
                MediaKinds.Normalize(projectedKind),
                normalizedExpectedKind,
                StringComparison.OrdinalIgnoreCase);
        }

        var normalizedPath = mediaPath.Replace('\\', '/').Trim().ToLowerInvariant();
        var fileName = Path.GetFileName(normalizedPath);
        var fileStem = Path.GetFileNameWithoutExtension(normalizedPath);

        static bool FileNameHasAnySuffix(string fileName, params string[] suffixes)
        {
            return suffixes.Any(suffix => fileName.Contains($"{suffix}.", StringComparison.OrdinalIgnoreCase));
        }

        static bool StemMatchesCanonicalVariant(string fileStem, params string[] canonicalStems)
        {
            return canonicalStems.Any(stem =>
                string.Equals(fileStem, stem, StringComparison.OrdinalIgnoreCase) ||
                fileStem.StartsWith($"{stem}-", StringComparison.OrdinalIgnoreCase));
        }

        static bool PathHasCanonicalStem(string normalizedPath, string directory, string fileStem, params string[] canonicalStems)
        {
            return normalizedPath.Contains(directory, StringComparison.OrdinalIgnoreCase) &&
                StemMatchesCanonicalVariant(fileStem, canonicalStems);
        }

        return normalizedExpectedKind switch
        {
            MediaKinds.Image => FileNameHasAnySuffix(fileName, "-image", "-titleshot", "-screentitle") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/", fileStem, "image", "titleshot", "screentitle", "screen-title", "sstitle") ||
                normalizedPath.Contains("/games/", StringComparison.OrdinalIgnoreCase) &&
                PathHasCanonicalStem(normalizedPath, "/", fileStem, "image"),
            MediaKinds.Thumbnail => FileNameHasAnySuffix(fileName, "-thumb", "-thumbnail", "-screenshot") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/", fileStem, "screenshot", "thumbnail", "thumb"),
            MediaKinds.Logo => FileNameHasAnySuffix(fileName, "-logo", "-wheel") ||
                PathHasCanonicalStem(normalizedPath, "/ui/logos/", fileStem, "logo") ||
                PathHasCanonicalStem(normalizedPath, "/ui/wheels/", fileStem, "wheel"),
            MediaKinds.Wheel => FileNameHasAnySuffix(fileName, "-wheel") ||
                PathHasCanonicalStem(normalizedPath, "/ui/wheels/", fileStem, "wheel"),
            MediaKinds.WheelCarbon => FileNameHasAnySuffix(fileName, "-wheelcarbon") ||
                PathHasCanonicalStem(normalizedPath, "/ui/wheels/", fileStem, "wheel-carbon"),
            MediaKinds.WheelSteel => FileNameHasAnySuffix(fileName, "-wheelsteel") ||
                PathHasCanonicalStem(normalizedPath, "/ui/wheels/", fileStem, "wheel-steel"),
            MediaKinds.Marquee => FileNameHasAnySuffix(fileName, "-marquee") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/marquee/", fileStem, "marquee"),
            MediaKinds.ScreenMarquee => FileNameHasAnySuffix(fileName, "-screenmarquee") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/marquee/", fileStem, "screenmarquee") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/screenmarquee/", fileStem, "screenmarquee"),
            MediaKinds.ScreenMarqueeSmall => FileNameHasAnySuffix(fileName, "-screenmarqueesmall") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/marquee/", fileStem, "screenmarquee-small") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/screenmarquee/", fileStem, "screenmarquee-small"),
            MediaKinds.SteamGrid => FileNameHasAnySuffix(fileName, "-steamgrid") ||
                PathHasCanonicalStem(normalizedPath, "/ui/", fileStem, "steamgrid") ||
                PathHasCanonicalStem(normalizedPath, "/ui/steamgrid/", fileStem, "steamgrid") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/steamgrid/", fileStem, "steamgrid"),
            MediaKinds.Figurine => FileNameHasAnySuffix(fileName, "-figurine") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/", fileStem, "figurine") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/figurines/", fileStem, "figurine"),
            MediaKinds.BoxFront => FileNameHasAnySuffix(fileName, "-box2d") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/box/", fileStem, "front"),
            MediaKinds.Box3d => FileNameHasAnySuffix(fileName, "-box3d") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/box/", fileStem, "3d"),
            MediaKinds.BoxBack => FileNameHasAnySuffix(fileName, "-boxback") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/box/", fileStem, "back"),
            MediaKinds.Cartridge => FileNameHasAnySuffix(fileName, "-cartridge", "-support2d") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/", fileStem, "cartridge", "support2d"),
            MediaKinds.Label => FileNameHasAnySuffix(fileName, "-label", "-supporttexture", "-support-texture") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/", fileStem, "label", "supporttexture", "support-texture"),
            MediaKinds.Fanart => FileNameHasAnySuffix(fileName, "-fanart") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/", fileStem, "fanart") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/fanart/", fileStem, "fanart"),
            MediaKinds.Flyer => FileNameHasAnySuffix(fileName, "-flyer") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/", fileStem, "flyer") ||
                PathHasCanonicalStem(normalizedPath, "/artwork/flyers/", fileStem, "flyer"),
            MediaKinds.Magazine => FileNameHasAnySuffix(fileName, "-magazine") ||
                PathHasCanonicalStem(normalizedPath, "/documents/", fileStem, "magazine"),
            _ => false
        };
    }

    private static bool TryGetProjectedKindForPath(
        string? mediaPath,
        IReadOnlyDictionary<string, string>? projectedKindPaths,
        out string kind)
    {
        kind = string.Empty;
        if (string.IsNullOrWhiteSpace(mediaPath) || projectedKindPaths == null || projectedKindPaths.Count == 0)
        {
            return false;
        }

        var normalizedPath = NormalizeForCompare(NormalizeGamelistMediaValue(mediaPath));
        foreach (var pair in projectedKindPaths)
        {
            if (string.Equals(NormalizeForCompare(pair.Value), normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                kind = pair.Key;
                return true;
            }
        }

        return false;
    }

    private RichGamelistCandidate? ResolveRichGamelistCandidate(
        string systemRoot,
        string gamelistPath,
        GamelistConsolidateRequest request,
        GamelistSaveMetrics currentMetrics,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RichGamelistPath))
        {
            var requestedPath = ResolveConsolidationGamelistPath(systemRoot, request.RichGamelistPath);
            var candidate = TryLoadRichGamelistCandidate(requestedPath, cancellationToken);
            if (candidate == null)
            {
                throw new InvalidOperationException($"Le gamelist riche demande est introuvable ou invalide: {requestedPath}");
            }

            if (candidate.Metrics.MediaTagCount < Math.Max(1, request.MinimumRichMediaTags))
            {
                throw new InvalidOperationException(
                    $"Le gamelist riche demande ne contient pas assez de balises media: {candidate.Metrics.MediaTagCount}.");
            }

            return candidate;
        }

        var backupDirectory = GetGamelistSidecarDirectory(gamelistPath, GamelistBackupDirectoryName);
        if (!Directory.Exists(backupDirectory))
        {
            return null;
        }

        var minimumMediaTags = Math.Max(1, request.MinimumRichMediaTags);
        foreach (var backupPath in Directory.GetFiles(backupDirectory, $"{Path.GetFileNameWithoutExtension(gamelistPath)}.*.xml")
                     .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = TryLoadRichGamelistCandidate(backupPath, cancellationToken);
            if (candidate == null)
            {
                continue;
            }

            if (candidate.Metrics.MediaTagCount < minimumMediaTags)
            {
                continue;
            }

            if (candidate.Metrics.MediaTagCount <= currentMetrics.MediaTagCount &&
                candidate.Metrics.GamesWithAnyMedia <= currentMetrics.GamesWithAnyMedia)
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static string ResolveConsolidationGamelistPath(string systemRoot, string requestedPath)
    {
        var candidatePath = requestedPath.Trim();
        if (!Path.IsPathRooted(candidatePath))
        {
            candidatePath = Path.Combine(systemRoot, candidatePath);
        }

        var fullSystemRoot = Path.GetFullPath(systemRoot);
        var fullCandidatePath = Path.GetFullPath(candidatePath);
        var relativePath = Path.GetRelativePath(fullSystemRoot, fullCandidatePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RichGamelistPath doit rester dans le dossier du systeme demande.");
        }

        return fullCandidatePath;
    }

    private RichGamelistCandidate? TryLoadRichGamelistCandidate(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            if (document.Root == null ||
                !string.Equals(document.Root.Name.LocalName, "gameList", StringComparison.Ordinal))
            {
                return null;
            }

            var metrics = CreateGamelistMetrics(document, GetWriteTimeTicksUtc(path), new FileInfo(path).Length);
            return new RichGamelistCandidate(path, document, metrics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            _logger?.LogDebug(ex, "Backup gamelist ignore pendant consolidation: {Path}", path);
            return null;
        }
    }

    private static Dictionary<string, XElement> BuildConsolidationGameIndex(XElement? root)
    {
        var index = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var gameNode in root?.Elements("game") ?? Enumerable.Empty<XElement>())
        {
            AddConsolidationIndexKeys(index, gameNode);
        }

        return index;
    }

    private static void AddConsolidationIndexKeys(IDictionary<string, XElement> index, XElement gameNode)
    {
        AddConsolidationIndexKey(index, $"path:{NormalizeConsolidationKey(gameNode.Element("path")?.Value)}", gameNode);
        AddConsolidationIndexKey(index, $"file:{NormalizeConsolidationKey(GetConsolidationFileName(gameNode.Element("path")?.Value))}", gameNode);
        AddConsolidationIndexKey(index, $"md5:{NormalizeConsolidationKey(gameNode.Element("md5")?.Value)}", gameNode);
        AddConsolidationIndexKey(index, $"name:{NormalizeConsolidationKey(gameNode.Element("name")?.Value)}", gameNode);
    }

    private static void AddConsolidationIndexKey(IDictionary<string, XElement> index, string key, XElement gameNode)
    {
        if (key.EndsWith(":", StringComparison.Ordinal) || index.ContainsKey(key))
        {
            return;
        }

        index[key] = gameNode;
    }

    private static bool TryFindConsolidationSourceGame(
        XElement currentGameNode,
        IReadOnlyDictionary<string, XElement> richIndex,
        out XElement richGameNode)
    {
        var keys = new[]
        {
            $"path:{NormalizeConsolidationKey(currentGameNode.Element("path")?.Value)}",
            $"file:{NormalizeConsolidationKey(GetConsolidationFileName(currentGameNode.Element("path")?.Value))}",
            $"md5:{NormalizeConsolidationKey(currentGameNode.Element("md5")?.Value)}",
            $"name:{NormalizeConsolidationKey(currentGameNode.Element("name")?.Value)}"
        };

        foreach (var key in keys)
        {
            if (!key.EndsWith(":", StringComparison.Ordinal) && richIndex.TryGetValue(key, out richGameNode!))
            {
                return true;
            }
        }

        richGameNode = null!;
        return false;
    }

    private static string NormalizeConsolidationKey(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }

    private static string GetConsolidationFileName(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        var index = normalized.LastIndexOf('/');
        return index >= 0 ? normalized[(index + 1)..] : normalized;
    }

    private static int ConsolidateGameMediaElements(
        XElement currentGameNode,
        XElement richGameNode,
        bool overwriteExistingMedia,
        bool includeTextMetadata,
        bool dryRun)
    {
        var restored = 0;
        foreach (var richElement in richGameNode.Elements())
        {
            var elementName = richElement.Name.LocalName;
            if (!ShouldConsolidateGamelistElement(elementName, includeTextMetadata) ||
                string.IsNullOrWhiteSpace(richElement.Value))
            {
                continue;
            }

            var currentElement = currentGameNode.Element(richElement.Name);
            if (currentElement != null &&
                !overwriteExistingMedia &&
                !string.IsNullOrWhiteSpace(currentElement.Value))
            {
                continue;
            }

            if (currentElement != null &&
                string.Equals(currentElement.Value?.Trim(), richElement.Value.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            restored++;
            if (dryRun)
            {
                continue;
            }

            var replacement = new XElement(richElement);
            if (currentElement == null)
            {
                currentGameNode.Add(replacement);
            }
            else
            {
                currentElement.ReplaceWith(replacement);
            }
        }

        return restored;
    }

    private static bool ShouldConsolidateGamelistElement(
        string elementName,
        bool includeTextMetadata)
    {
        if (GamelistMetricMediaTags.Contains(elementName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return includeTextMetadata &&
               GamelistConsolidationTextMetadataTags.Contains(elementName, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddExistingGamelistKind(
        IDictionary<string, string> kindPaths,
        XElement gameNode,
        string systemRoot,
        string projectionBaseName,
        IReadOnlyDictionary<string, string>? projectedKindPaths,
        string tagName,
        params string[] kinds)
    {
        var mediaPath = gameNode.Element(tagName)?.Value;
        if (!TryNormalizeExistingGamelistMedia(systemRoot, projectionBaseName, mediaPath, out var normalizedPath))
        {
            return;
        }

        if (IsMismatchedProjectedKind(mediaPath, projectedKindPaths, kinds))
        {
            return;
        }

        foreach (var kind in kinds.Select(MediaKinds.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            kindPaths.TryAdd(kind, normalizedPath);
        }
    }

    private static bool TryNormalizeExistingGamelistMedia(
        string systemRoot,
        string projectionBaseName,
        string? mediaPath,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(mediaPath) ||
            IsApiPlaceholderPath(mediaPath, projectionBaseName))
        {
            return false;
        }

        var diskPath = ResolveEsRelativePath(systemRoot, mediaPath);
        if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
        {
            return false;
        }

        normalizedPath = NormalizeGamelistMediaValue(mediaPath);
        return true;
    }

    private static void AddExistingWheelLikeGamelistKind(
        IDictionary<string, string> kindPaths,
        XElement gameNode,
        string systemRoot,
        string projectionBaseName,
        IReadOnlyDictionary<string, string>? projectedKindPaths,
        string tagName)
    {
        var mediaPath = gameNode.Element(tagName)?.Value;
        if (!TryNormalizeExistingGamelistMedia(systemRoot, projectionBaseName, mediaPath, out var normalizedPath))
        {
            return;
        }

        if (!IsMediaPathCompatibleWithKind(mediaPath, MediaKinds.Wheel, projectedKindPaths))
        {
            return;
        }

        kindPaths.TryAdd(MediaKinds.Wheel, normalizedPath);
        kindPaths.TryAdd(MediaKinds.Logo, normalizedPath);
    }

    private static IEnumerable<KeyValuePair<string, string>> ResolveTemplateMediaElements(IReadOnlyDictionary<string, string> kindPaths)
    {
        yield return new KeyValuePair<string, string>("wheel", FirstMediaPath(kindPaths, MediaKinds.Wheel));
        yield return new KeyValuePair<string, string>("boxart", FirstMediaPath(kindPaths, MediaKinds.BoxFront, MediaKinds.Box3d));
        yield return new KeyValuePair<string, string>("boxback", FirstMediaPath(kindPaths, MediaKinds.BoxBack));
        yield return new KeyValuePair<string, string>("cartridge", FirstMediaPath(kindPaths, MediaKinds.Cartridge));
        yield return new KeyValuePair<string, string>("label", FirstMediaPath(kindPaths, MediaKinds.Label));
        yield return new KeyValuePair<string, string>("fanart", FirstMediaPath(kindPaths, MediaKinds.Fanart));
        yield return new KeyValuePair<string, string>("extra1", FirstMediaPath(kindPaths, MediaKinds.Flyer, MediaKinds.Figurine, MediaKinds.BoxTexture, MediaKinds.BoxSide));
        yield return new KeyValuePair<string, string>("figurine", FirstMediaPath(kindPaths, MediaKinds.Figurine));
        yield return new KeyValuePair<string, string>("mix", FirstMediaPath(kindPaths, MediaKinds.MixRbv2, MediaKinds.MixRbv1));
        yield return new KeyValuePair<string, string>("titleshot", FirstMediaPath(kindPaths, MediaKinds.Image));
        yield return new KeyValuePair<string, string>("screenshot", FirstMediaPath(kindPaths, MediaKinds.Thumbnail));
        yield return new KeyValuePair<string, string>("bezel", FirstMediaPath(kindPaths, MediaKinds.Bezel));
        yield return new KeyValuePair<string, string>("map", FirstMediaPath(kindPaths, MediaKinds.Map));
        yield return new KeyValuePair<string, string>("manual", FirstMediaPath(kindPaths, MediaKinds.Manual));
        yield return new KeyValuePair<string, string>("magazine", FirstMediaPath(kindPaths, MediaKinds.Magazine));
        yield return new KeyValuePair<string, string>("video", FirstMediaPath(kindPaths, MediaKinds.Video, MediaKinds.VideoNormalized));
        yield return new KeyValuePair<string, string>("screenmarquee", FirstMediaPath(kindPaths, MediaKinds.ScreenMarquee));
        yield return new KeyValuePair<string, string>("screenmarqueesmall", FirstMediaPath(kindPaths, MediaKinds.ScreenMarqueeSmall));
        yield return new KeyValuePair<string, string>("steamgrid", FirstMediaPath(kindPaths, MediaKinds.SteamGrid));
        yield return new KeyValuePair<string, string>("mixrbv1", FirstMediaPath(kindPaths, MediaKinds.MixRbv1));
        yield return new KeyValuePair<string, string>("mixrbv2", FirstMediaPath(kindPaths, MediaKinds.MixRbv2));
        yield return new KeyValuePair<string, string>("videonormalized", FirstMediaPath(kindPaths, MediaKinds.VideoNormalized));
    }

    private static int CountUnsupportedDurableMediaTags(XElement gameNode)
    {
        return UnsupportedDurableMediaTags.Sum(tagName => gameNode.Elements(tagName).Count());
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

    private static string FirstMediaPath(IReadOnlyDictionary<string, string> kindPaths, params string[] kinds)
    {
        return FirstMediaPath(kindPaths, (IEnumerable<string>)kinds);
    }

    private static string FirstMediaPath(IReadOnlyDictionary<string, string> kindPaths, IEnumerable<string> kinds)
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

    private static string EnsureDefaultPlaceholderImage(string systemId, string projectionBaseName, bool overwriteExisting = true)
    {
        return EnsurePlaceholderImage(systemId, projectionBaseName, DefaultPlaceholderFileName, overwriteExisting);
    }

    private static string EnsureScrapingInProgressPlaceholderImage(string systemId, string projectionBaseName)
    {
        return EnsurePlaceholderImage(systemId, projectionBaseName, ScrapingPlaceholderFileName, overwriteExisting: true);
    }

    private static string EnsureNoMediaFoundPlaceholderImage(string systemId, string projectionBaseName)
    {
        return EnsurePlaceholderImage(systemId, projectionBaseName, NoMediaFoundPlaceholderFileName, overwriteExisting: true);
    }

    private static string GetPlaceholderDiskPath(string systemId, string projectionBaseName)
    {
        var sourcePath = Path.Combine(RetroBatPaths.EmulationStationThemeMediasRoot, DefaultPlaceholderFileName);
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        return Path.Combine(RetroBatPaths.RomsRoot, ResolvePlaceholderStorageSystemId(systemId), "images", projectionBaseName + "_default" + extension);
    }

    private int EnsureDefaultPlaceholdersForSystem(string systemId, ref int processedEntries, ref int totalEntries, CancellationToken cancellationToken)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var projectionStorageRoot = Path.Combine(RetroBatPaths.RomsRoot, ResolvePlaceholderStorageSystemId(systemId));
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return 0;
        }

        lock (GetGamelistLock(gamelistPath))
        {
            var document = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, "initialisation des placeholders par defaut");
            if (document == null)
            {
                return 0;
            }

            var root = document.Root;
            if (root == null)
            {
                return 0;
            }

            var updatedEntries = 0;
            var systemEntries = root.Elements("game").Count();
            totalEntries += systemEntries;
            var processedInSystem = 0;

            foreach (var gameNode in root.Elements("game"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var gamePath = gameNode.Element("path")?.Value;
                if (string.IsNullOrWhiteSpace(gamePath))
                {
                    continue;
                }

                var projectionBaseName = Path.GetFileNameWithoutExtension(gamePath.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(projectionBaseName))
                {
                    continue;
                }

                var currentImagePath = gameNode.Element("image")?.Value;
                if (TryNormalizeExistingGamelistMedia(systemRoot, projectionBaseName, currentImagePath, out _) ||
                    (!string.IsNullOrWhiteSpace(currentImagePath) && !IsApiPlaceholderPath(currentImagePath, projectionBaseName)))
                {
                    processedEntries++;
                    processedInSystem++;
                    continue;
                }

                var preferredImagePath = EnsureDefaultPlaceholderImage(systemId, projectionBaseName, overwriteExisting: false);
                if (!string.IsNullOrWhiteSpace(preferredImagePath) &&
                    TrySetSelectionElement(gameNode, "image", preferredImagePath))
                {
                    updatedEntries++;
                }

                processedEntries++;
                processedInSystem++;
                if (processedInSystem == 1 || processedInSystem % 25 == 0 || processedInSystem == systemEntries)
                {
                    _startupOverlayService.UpdateStartupProgress(
                        "startup_processing_system",
                        processedEntries,
                        totalEntries,
                        $"{systemId} ({processedInSystem}/{systemEntries})");
                }
            }

            if (updatedEntries > 0)
            {
                if (!SaveGamelistDocument(document, gamelistPath, cancellationToken))
                {
                    return 0;
                }
            }

            return updatedEntries;
        }
    }

    public async Task<int> SyncEsGameIdsForAllSystemsAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            await StartupGamelistPreparationLog.AppendAsync(
                GameIdSyncPhaseName,
                "skipped",
                new { reason = "no-roms-root", elapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
            return 0;
        }

        var systemDirectories = Directory.GetDirectories(RetroBatPaths.RomsRoot)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (systemDirectories.Length == 0)
        {
            await StartupGamelistPreparationLog.AppendAsync(
                GameIdSyncPhaseName,
                "skipped",
                new { reason = "no-systems", elapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
            return 0;
        }

        await Task.CompletedTask;
        _logger?.LogInformation("ES gameid startup sync will generate gameids locally from the EmulationStation MD5(path) formula.");

        var updatedEntries = 0;
        var processedSystems = 0;
        var skippedSystems = 0;
        var missingGamelistSystems = 0;
        var state = StartupGamelistPreparationStateStore.Load();
        var stateDirty = false;
        var cacheMissReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _startupOverlayService.UpdateStartupProgress("startup_processing_system", 0, Math.Max(1, systemDirectories.Length), "gameid");
        foreach (var systemDirectory in systemDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var systemId = Path.GetFileName(systemDirectory);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                continue;
            }

            var gamelistPath = Path.Combine(systemDirectory, "gamelist.xml");
            if (!File.Exists(gamelistPath))
            {
                missingGamelistSystems++;
                processedSystems++;
                _startupOverlayService.UpdateStartupProgress(
                    "startup_processing_system",
                    processedSystems,
                    Math.Max(1, systemDirectories.Length),
                    $"{systemId} no gamelist");
                continue;
            }

            var cacheStatus = StartupGamelistPreparationStateStore.GetSystemPhaseCacheStatus(
                    state,
                    systemId,
                    GameIdSyncPhaseName,
                    gamelistPath,
                    GameIdSyncStateVersion,
                    GameIdSyncNormalizerVersion);
            if (cacheStatus.IsClean)
            {
                skippedSystems++;
                processedSystems++;
                _startupOverlayService.UpdateStartupProgress(
                    "startup_processing_system",
                    processedSystems,
                    Math.Max(1, systemDirectories.Length),
                    $"{systemId} gameid cache");
                continue;
            }

            Increment(cacheMissReasons, cacheStatus.Reason);
            updatedEntries += SyncEsGameIdsForSystem(systemId, cancellationToken);
            if (File.Exists(gamelistPath))
            {
                StartupGamelistPreparationStateStore.MarkSystemPhaseClean(
                    state,
                    systemId,
                    GameIdSyncPhaseName,
                    gamelistPath,
                    GameIdSyncStateVersion,
                    GameIdSyncNormalizerVersion);
                stateDirty = true;
            }

            processedSystems++;
            _startupOverlayService.UpdateStartupProgress(
                "startup_processing_system",
                processedSystems,
                Math.Max(1, systemDirectories.Length),
                $"{systemId} gameid");
        }

        if (stateDirty)
        {
            StartupGamelistPreparationStateStore.Save(state);
        }

        await StartupGamelistPreparationLog.AppendAsync(
            GameIdSyncPhaseName,
            "completed",
            new
            {
                processedSystems,
                skippedSystems,
                missingGamelistSystems,
                updatedEntries,
                totalSystems = systemDirectories.Length,
                cacheMissReasons,
                elapsedMs = stopwatch.ElapsedMilliseconds
            },
            cancellationToken);

        if (updatedEntries > 0)
        {
            _logger?.LogInformation("ES gameid startup sync updated {UpdatedEntries} gamelist entries.", updatedEntries);
        }
        else
        {
            _logger?.LogInformation("ES gameid startup sync completed: no gamelist entry needed an update.");
        }

        return updatedEntries;
    }

    private static void Increment(IDictionary<string, int> values, string key)
    {
        values[key] = values.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private async Task<bool> IsEsApiAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _esHttpClient.GetAsync("/systems", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "EmulationStation API unavailable during startup gameid preflight.");
            return false;
        }
    }

    private async Task<List<EsGameIdentityEntry>> FetchEsGameIdentityEntriesAsync(string systemId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _esHttpClient.GetAsync($"/systems/{Uri.EscapeDataString(systemId)}/games", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug(
                    "ES gameid sync skipped system={SystemId}: /games returned HTTP {StatusCode}.",
                    systemId,
                    (int)response.StatusCode);
                return new List<EsGameIdentityEntry>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<List<EsGameIdentityEntry>>(stream, EsApiJsonOptions, cancellationToken)
                ?? new List<EsGameIdentityEntry>();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ES gameid sync skipped system={SystemId}: EmulationStation API unavailable.", systemId);
            return new List<EsGameIdentityEntry>();
        }
    }

    private int SyncEsGameIdsForSystem(string systemId, CancellationToken cancellationToken)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return 0;
        }

        var updatedEntries = 0;
        lock (GetGamelistLock(gamelistPath))
        {
            var document = TryLoadOrCreateGamelistDocument(gamelistPath, cancellationToken, "synchronisation des gameid ES");
            if (document?.Root == null)
            {
                return 0;
            }

            foreach (var gameNode in document.Root.Elements("game"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gamePath = gameNode.Element("path")?.Value;
                if (string.IsNullOrWhiteSpace(gamePath))
                {
                    continue;
                }

                var currentGameId = gameNode.Element("gameid")?.Value?.Trim();
                var generatedGameId = BuildEsGameIdFromPath(systemRoot, gamePath);
                if (string.IsNullOrWhiteSpace(generatedGameId) ||
                    !IsLocalEsGamePathPresent(systemRoot, gamePath))
                {
                    continue;
                }

                if (!string.Equals(currentGameId, generatedGameId, StringComparison.OrdinalIgnoreCase) &&
                    TrySetGameIdElement(gameNode, generatedGameId))
                {
                    updatedEntries++;
                }
            }

            if (updatedEntries > 0)
            {
                if (!SaveGamelistDocument(document, gamelistPath, cancellationToken))
                {
                    return 0;
                }
            }
        }

        return updatedEntries;
    }

    private static string BuildEsGameIdFromPath(string systemRoot, string gamePath)
    {
        var esApiPath = TryResolveEsApiPath(systemRoot, gamePath);
        if (string.IsNullOrWhiteSpace(esApiPath))
        {
            return string.Empty;
        }

        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(esApiPath))).ToLowerInvariant();
    }

    private static bool IsLocalEsGamePathPresent(string systemRoot, string gamePath)
    {
        var esApiPath = TryResolveEsApiPath(systemRoot, gamePath);
        return !string.IsNullOrWhiteSpace(esApiPath) &&
            (File.Exists(esApiPath) || Directory.Exists(esApiPath));
    }

    private static string TryResolveEsApiPath(string systemRoot, string gamePath)
    {
        try
        {
            return ResolveEsRelativePath(systemRoot, gamePath).Replace('\\', '/').Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static EsGameIdentityIndex BuildEsGameIdentityIndex(string systemRoot, IReadOnlyList<EsGameIdentityEntry> esGames)
    {
        var index = new EsGameIdentityIndex();
        foreach (var game in esGames)
        {
            if (string.IsNullOrWhiteSpace(game.Id) || string.IsNullOrWhiteSpace(game.Path))
            {
                continue;
            }

            AddUniqueIdentity(index.ByAbsolutePath, index.AmbiguousAbsolutePaths, NormalizeForCompare(ResolveEsRelativePath(systemRoot, game.Path)), game);
            AddUniqueIdentity(index.ByRelativePath, index.AmbiguousRelativePaths, NormalizeForCompare(ToGameRelativePath(game.Path, systemRoot)), game);
            AddUniqueIdentity(index.ByFileName, index.AmbiguousFileNames, NormalizeForCompare(GetPortableFileName(game.Path)), game);
        }

        return index;
    }

    private static EsGameIdentityEntry? ResolveEsGameIdentity(string systemRoot, string gamePath, EsGameIdentityIndex index)
    {
        var absoluteKey = NormalizeForCompare(ResolveEsRelativePath(systemRoot, gamePath));
        if (index.ByAbsolutePath.TryGetValue(absoluteKey, out var absoluteMatch))
        {
            return absoluteMatch;
        }

        var relativeKey = NormalizeForCompare(ToGameRelativePath(gamePath, systemRoot));
        if (index.ByRelativePath.TryGetValue(relativeKey, out var relativeMatch))
        {
            return relativeMatch;
        }

        var fileNameKey = NormalizeForCompare(GetPortableFileName(gamePath));
        return index.ByFileName.TryGetValue(fileNameKey, out var fileNameMatch)
            ? fileNameMatch
            : null;
    }

    private static void AddUniqueIdentity(
        Dictionary<string, EsGameIdentityEntry> values,
        HashSet<string> ambiguousKeys,
        string key,
        EsGameIdentityEntry entry)
    {
        if (string.IsNullOrWhiteSpace(key) || ambiguousKeys.Contains(key))
        {
            return;
        }

        if (values.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                values.Remove(key);
                ambiguousKeys.Add(key);
            }
            return;
        }

        values[key] = entry;
    }

    private object GetGamelistLock(string gamelistPath)
    {
        return _gamelistStore.GetLock(gamelistPath);
    }

    private BootstrapPlaceholderState LoadBootstrapState()
    {
        var path = RetroBatPaths.BootstrapPlaceholderStatePath;
        try
        {
            if (!File.Exists(path))
            {
                return new BootstrapPlaceholderState();
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<BootstrapPlaceholderState>(stream, BootstrapStateJsonOptions)
                ?? new BootstrapPlaceholderState();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Impossible de charger le cache de bootstrap des placeholders.");
            return new BootstrapPlaceholderState();
        }
    }

    private void SaveBootstrapState(BootstrapPlaceholderState state)
    {
        var path = RetroBatPaths.BootstrapPlaceholderStatePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(stream, state, BootstrapStateJsonOptions);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Impossible de sauvegarder le cache de bootstrap des placeholders.");
        }
    }

    private GamelistSelectionNormalizationState LoadSelectionNormalizationState()
    {
        lock (_selectionNormalizationStateLock)
        {
            if (_selectionNormalizationState != null)
            {
                return _selectionNormalizationState;
            }

            var path = RetroBatPaths.GamelistSelectionNormalizationStatePath;
            try
            {
                if (!File.Exists(path))
                {
                    _selectionNormalizationState = new GamelistSelectionNormalizationState();
                    return _selectionNormalizationState;
                }

                using var stream = File.OpenRead(path);
                _selectionNormalizationState = JsonSerializer.Deserialize<GamelistSelectionNormalizationState>(stream, BootstrapStateJsonOptions)
                    ?? new GamelistSelectionNormalizationState();
                return _selectionNormalizationState;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Impossible de charger le cache de normalisation media ES.");
                _selectionNormalizationState = new GamelistSelectionNormalizationState();
                return _selectionNormalizationState;
            }
        }
    }

    private void SaveSelectionNormalizationStateIfDirty()
    {
        lock (_selectionNormalizationStateLock)
        {
            if (!_selectionNormalizationStateDirty || _selectionNormalizationState == null)
            {
                return;
            }

            var path = RetroBatPaths.GamelistSelectionNormalizationStatePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tempPath = path + ".tmp";
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    JsonSerializer.Serialize(stream, _selectionNormalizationState, BootstrapStateJsonOptions);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path, overwrite: true);
                }

                _selectionNormalizationStateDirty = false;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Impossible de sauvegarder le cache de normalisation media ES.");
            }
        }
    }

    private bool ShouldSkipStartupSelectionNormalization(
        string systemId,
        EmulationStationScrapingSettings settings,
        string settingsSignature,
        GamelistSelectionNormalizationState state)
    {
        if (!state.Systems.TryGetValue(systemId, out var cached) ||
            cached.StateVersion != SelectionNormalizationStateVersion ||
            !string.Equals(cached.SettingsSignature, settingsSignature, StringComparison.Ordinal))
        {
            return false;
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return false;
        }

        var canonicalSystemId = _systemIdNormalizer.Normalize(systemId);
        var mediaFingerprint = ComputeSelectionNormalizationMediaFingerprint(systemId, canonicalSystemId);
        if (!string.Equals(cached.MediaFingerprint, mediaFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileInfo = new FileInfo(gamelistPath);
        var writeTicks = fileInfo.LastWriteTimeUtc.Ticks;
        if (cached.GamelistWriteTicksUtc == writeTicks &&
            cached.GamelistByteLength == fileInfo.Length)
        {
            return true;
        }

        var gamelistFingerprint = ComputeFileContentHash(gamelistPath);
        return string.Equals(cached.GamelistFingerprint, gamelistFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSelectionNormalizationStateForSystem(
        string systemId,
        EmulationStationScrapingSettings settings,
        string settingsSignature,
        GamelistSelectionNormalizationState state)
    {
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return;
        }

        var canonicalSystemId = _systemIdNormalizer.Normalize(systemId);
        var fileInfo = new FileInfo(gamelistPath);
        var entry = new GamelistSelectionNormalizationSystemState
        {
            StateVersion = SelectionNormalizationStateVersion,
            GamelistWriteTicksUtc = fileInfo.LastWriteTimeUtc.Ticks,
            GamelistByteLength = fileInfo.Length,
            GamelistFingerprint = ComputeFileContentHash(gamelistPath),
            MediaFingerprint = ComputeSelectionNormalizationMediaFingerprint(systemId, canonicalSystemId),
            SettingsSignature = settingsSignature,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        lock (_selectionNormalizationStateLock)
        {
            state.Systems[systemId] = entry;
            _selectionNormalizationStateDirty = true;
        }
    }

    private static string BuildSelectionNormalizationSettingsSignature(EmulationStationScrapingSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append(settings.ImageSource).Append('|')
            .Append(settings.LogoSource).Append('|')
            .Append(settings.ThumbSource).Append('|')
            .Append(settings.WheelStyle).Append('|')
            .Append(settings.Language).Append('|')
            .Append(settings.MediaRegionMode).Append('|')
            .Append(settings.LogoRegionMode).Append('|')
            .Append(settings.ContentRegionProfile).Append('|')
            .Append(settings.ContentLanguageProfile).Append('|')
            .Append(settings.UserRegion);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private string ComputeSelectionNormalizationMediaFingerprint(string systemId, string canonicalSystemId)
    {
        var builder = new StringBuilder();
        AppendFileStampFingerprint(builder, Path.Combine(RetroBatPaths.MediaAliasesSharedRoot, "media-hashes.json"));
        AppendFileStampFingerprint(builder, Path.Combine(RetroBatPaths.PluginRoot, "logs", "package-installer", "index.json"));
        AppendFileStampFingerprint(builder, RetroBatPaths.RomPackInstallerStartupStatePath);
        AppendDirectoryListingFingerprint(builder, Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId));
        AppendDirectoryListingFingerprint(builder, Path.Combine(RetroBatPaths.MediaUserSystemsRoot, systemId));
        if (!string.Equals(systemId, canonicalSystemId, StringComparison.OrdinalIgnoreCase))
        {
            AppendDirectoryListingFingerprint(builder, Path.Combine(RetroBatPaths.MediaSystemsRoot, canonicalSystemId));
            AppendDirectoryListingFingerprint(builder, Path.Combine(RetroBatPaths.MediaUserSystemsRoot, canonicalSystemId));
        }

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendFileStampFingerprint(StringBuilder builder, string path)
    {
        builder.Append(path).Append('|');
        if (!File.Exists(path))
        {
            builder.AppendLine("missing");
            return;
        }

        var info = new FileInfo(path);
        builder.Append(info.Length)
            .Append('|')
            .Append(info.LastWriteTimeUtc.Ticks)
            .AppendLine();
    }

    private static void AppendDirectoryListingFingerprint(StringBuilder builder, string path)
    {
        builder.Append(path).Append('|');
        if (!Directory.Exists(path))
        {
            builder.AppendLine("missing");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                     .OrderBy(static filePath => filePath, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            builder.Append(Path.GetRelativePath(path, file).Replace('\\', '/'))
                .Append('|')
                .Append(info.Length)
                .Append('|')
                .Append(info.LastWriteTimeUtc.Ticks)
                .AppendLine();
        }
    }

    private bool NeedsBootstrapScan(string systemDirectory, BootstrapPlaceholderState state, long placeholderSourceWriteTicksUtc)
    {
        var systemId = Path.GetFileName(systemDirectory);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return false;
        }

        var gamelistPath = Path.Combine(systemDirectory, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return false;
        }

        if (!state.Systems.TryGetValue(systemId, out var cached))
        {
            return true;
        }

        if (cached.StateVersion != BootstrapPlaceholderStateVersion)
        {
            return true;
        }

        var currentGamelistWriteTicksUtc = GetWriteTimeTicksUtc(gamelistPath);
        var projectionStorageRoot = Path.Combine(RetroBatPaths.RomsRoot, ResolvePlaceholderStorageSystemId(systemId));
        var currentImagesWriteTicksUtc = GetDirectoryWriteTimeTicksUtc(Path.Combine(projectionStorageRoot, "images"));
        if (cached.PlaceholderSourceWriteTicksUtc != placeholderSourceWriteTicksUtc)
        {
            return true;
        }

        var gamelistTicksChanged = cached.GamelistWriteTicksUtc != currentGamelistWriteTicksUtc;
        var imagesTicksChanged = cached.ImagesWriteTicksUtc != currentImagesWriteTicksUtc;
        if (!gamelistTicksChanged && !imagesTicksChanged)
        {
            return false;
        }

        if (gamelistTicksChanged)
        {
            var currentGamelistFingerprint = ComputeFileContentHash(gamelistPath);
            if (!string.Equals(cached.GamelistFingerprint, currentGamelistFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (imagesTicksChanged)
        {
            var currentImagesFingerprint = ComputeDirectoryListingHash(Path.Combine(projectionStorageRoot, "images"));
            if (!string.Equals(cached.ImagesFingerprint, currentImagesFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateBootstrapStateForSystem(string systemDirectory, string systemId, BootstrapPlaceholderState state, long placeholderSourceWriteTicksUtc)
    {
        var gamelistPath = Path.Combine(systemDirectory, "gamelist.xml");
        var projectionStorageRoot = Path.Combine(RetroBatPaths.RomsRoot, ResolvePlaceholderStorageSystemId(systemId));
        var imagesPath = Path.Combine(projectionStorageRoot, "images");
        state.Systems[systemId] = new BootstrapPlaceholderSystemState
        {
            StateVersion = BootstrapPlaceholderStateVersion,
            GamelistWriteTicksUtc = GetWriteTimeTicksUtc(gamelistPath),
            ImagesWriteTicksUtc = GetDirectoryWriteTimeTicksUtc(imagesPath),
            PlaceholderSourceWriteTicksUtc = placeholderSourceWriteTicksUtc,
            GamelistFingerprint = ComputeFileContentHash(gamelistPath),
            ImagesFingerprint = ComputeDirectoryListingHash(imagesPath)
        };
    }

    private static long GetDefaultPlaceholderSourceWriteTicksUtc()
    {
        var path = Path.Combine(RetroBatPaths.EmulationStationThemeMediasRoot, DefaultPlaceholderFileName);
        return GetWriteTimeTicksUtc(path);
    }

    private static long GetWriteTimeTicksUtc(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
    }

    private static long GetDirectoryWriteTimeTicksUtc(string path)
    {
        return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks : 0L;
    }

    private static string ComputeFileContentHash(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static string ComputeDirectoryListingHash(string path)
    {
        if (!Directory.Exists(path))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static filePath => filePath, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            builder.Append(Path.GetFileName(file))
                .Append('|')
                .Append(info.Length)
                .Append('|')
                .Append(info.LastWriteTimeUtc.Ticks)
                .AppendLine();
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private XDocument LoadOrCreateGamelistDocument(string gamelistPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(gamelistPath))
        {
            return CreateEmptyGamelistDocument();
        }

        return LoadGamelistDocument(gamelistPath, cancellationToken);
    }

    private XDocument? TryLoadOrCreateGamelistDocument(string gamelistPath, CancellationToken cancellationToken, string operation)
    {
        try
        {
            return LoadOrCreateGamelistDocument(gamelistPath, cancellationToken);
        }
        catch (XmlException ex) when (!IsMissingRootXml(ex))
        {
            _logger?.LogWarning(
                "gamelist.xml invalide ignore pour {Operation}: {GamelistPath}; {Message}",
                operation,
                gamelistPath,
                ex.Message);
            return null;
        }
    }

    private XDocument LoadGamelistDocument(string gamelistPath, CancellationToken cancellationToken)
    {
        return ExecuteWithGamelistIoRetry(
            gamelistPath,
            "lecture",
            cancellationToken,
            () =>
            {
                using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                try
                {
                    return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                }
                catch (XmlException ex) when (IsMissingRootXml(ex))
                {
                    _logger?.LogInformation("gamelist.xml vide detecte pour {GamelistPath}, recreation d'une structure minimale.", gamelistPath);
                    return CreateEmptyGamelistDocument();
                }
            });
    }

    private bool SaveGamelistDocument(XDocument document, string gamelistPath, CancellationToken cancellationToken, bool allowMediaTagDrop = false)
    {
        return ExecuteWithGamelistIoRetry(
            gamelistPath,
            "ecriture",
            cancellationToken,
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(gamelistPath)!);
                var tempPath = gamelistPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        document.Save(stream);
                        stream.Flush(true);
                    }

                    if (!TryValidateGamelistXmlFile(tempPath, out var candidateValidationFailure))
                    {
                        var rejectedPath = MoveInvalidGamelistCandidate(
                            tempPath,
                            gamelistPath,
                            "candidate_gamelist_xml_invalid",
                            candidateValidationFailure);
                        _logger?.LogError(
                            "Candidate gamelist XML invalide rejetee pour {GamelistPath}; rejected={RejectedPath}; failure={Failure}",
                            gamelistPath,
                            rejectedPath,
                            candidateValidationFailure);
                        return false;
                    }

                    var candidateMetrics = CreateGamelistMetrics(document, GetWriteTimeTicksUtc(tempPath), new FileInfo(tempPath).Length);
                    if (File.Exists(gamelistPath))
                    {
                        var currentMetrics = TryReadGamelistMetrics(gamelistPath);
                        if (currentMetrics != null && IsSuspiciousGamelistRewrite(currentMetrics, candidateMetrics, allowMediaTagDrop))
                        {
                            var rejectedPath = MoveRejectedGamelistCandidate(tempPath, gamelistPath, currentMetrics, candidateMetrics);
                            _logger?.LogWarning(
                                "Ecriture gamelist suspecte rejetee pour {GamelistPath}; current={CurrentMetrics}; candidate={CandidateMetrics}; rejected={RejectedPath}",
                                gamelistPath,
                                currentMetrics,
                                candidateMetrics,
                                rejectedPath);
                            return false;
                        }

                        var backupPath = CreateTimestampedGamelistBackup(gamelistPath);
                        File.Replace(tempPath, gamelistPath, null, ignoreMetadataErrors: true);
                        if (!TryValidateGamelistXmlFile(gamelistPath, out var finalValidationFailure))
                        {
                            RestoreGamelistBackup(backupPath, gamelistPath);
                            var restored = TryValidateGamelistXmlFile(gamelistPath, out var restoreValidationFailure);
                            var auditPath = WriteGamelistValidationAudit(
                                gamelistPath,
                                "final_gamelist_xml_invalid_after_write",
                                finalValidationFailure,
                                backupPath,
                                currentMetrics,
                                candidateMetrics,
                                restored,
                                restoreValidationFailure);
                            _logger?.LogError(
                                "gamelist.xml invalide apres remplacement; backup restaure={Restored}; path={GamelistPath}; backup={BackupPath}; audit={AuditPath}; failure={Failure}",
                                restored,
                                gamelistPath,
                                backupPath,
                                auditPath,
                                finalValidationFailure);
                            return false;
                        }

                        CleanupOldGamelistBackups(gamelistPath);
                        _logger?.LogDebug(
                            "gamelist.xml sauvegarde avant remplacement: path={GamelistPath}, backup={BackupPath}, current={CurrentMetrics}, candidate={CandidateMetrics}",
                            gamelistPath,
                            backupPath,
                            currentMetrics,
                            candidateMetrics);
                    }
                    else
                    {
                        File.Move(tempPath, gamelistPath, overwrite: true);
                        if (!TryValidateGamelistXmlFile(gamelistPath, out var finalValidationFailure))
                        {
                            var auditPath = WriteGamelistValidationAudit(
                                gamelistPath,
                                "new_gamelist_xml_invalid_after_write",
                                finalValidationFailure,
                                backupPath: null,
                                currentMetrics: null,
                                candidateMetrics: candidateMetrics,
                                restored: false,
                                restoreFailure: null);
                            _logger?.LogError(
                                "Nouveau gamelist.xml invalide apres ecriture: path={GamelistPath}; audit={AuditPath}; failure={Failure}",
                                gamelistPath,
                                auditPath,
                                finalValidationFailure);
                            throw new IOException($"gamelist.xml invalide apres ecriture: {gamelistPath}");
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }

                return true;
            });
    }

    private static bool GamelistDocumentMatchesFile(XDocument document, string gamelistPath)
    {
        if (!File.Exists(gamelistPath))
        {
            return false;
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        var candidate = stream.ToArray();
        var current = File.ReadAllBytes(gamelistPath);
        return candidate.AsSpan().SequenceEqual(current);
    }

    private static bool TryValidateGamelistXmlFile(string path, out string? failure)
    {
        for (var attempt = 1; attempt <= GamelistIoRetryCount; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0)
                {
                    failure = "empty_file";
                    if (attempt < GamelistIoRetryCount)
                    {
                        Thread.Sleep(GamelistIoRetryDelay);
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
                if (attempt < GamelistIoRetryCount)
                {
                    Thread.Sleep(GamelistIoRetryDelay);
                    continue;
                }

                return false;
            }
        }

        failure = "validation_failed";
        return false;
    }

    private GamelistSaveMetrics? TryReadGamelistMetrics(string gamelistPath)
    {
        try
        {
            using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            return CreateGamelistMetrics(document, GetWriteTimeTicksUtc(gamelistPath), new FileInfo(gamelistPath).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            _logger?.LogWarning(ex, "Impossible de calculer les metriques de securite du gamelist avant sauvegarde: {GamelistPath}", gamelistPath);
            return null;
        }
    }

    private static GamelistSaveMetrics CreateGamelistMetrics(XDocument document, long writeTicksUtc, long byteLength)
    {
        var gameNodes = document.Root?.Elements("game").ToList() ?? new List<XElement>();
        var mediaTagSet = GamelistMetricMediaTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        return new GamelistSaveMetrics(
            byteLength,
            gameNodes.Count,
            gamesWithAnyMedia,
            mediaTagCount,
            writeTicksUtc);
    }

    private static bool IsSuspiciousGamelistRewrite(GamelistSaveMetrics current, GamelistSaveMetrics candidate, bool allowMediaTagDrop = false)
    {
        if (current.GameCount < 20 || current.ByteLength < 20_000)
        {
            return false;
        }

        if (IsSharpDrop(candidate.ByteLength, current.ByteLength, 0.65) ||
            IsSharpDrop(candidate.GameCount, current.GameCount, 0.80))
        {
            return true;
        }

        return !allowMediaTagDrop &&
               (IsSharpDrop(candidate.GamesWithAnyMedia, current.GamesWithAnyMedia, 0.65)
                || IsSharpDrop(candidate.MediaTagCount, current.MediaTagCount, 0.65));
    }

    private static bool IsSharpDrop(long candidate, long current, double minimumRatio)
    {
        if (current <= 0)
        {
            return false;
        }

        return candidate < current * minimumRatio;
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

    private string MoveRejectedGamelistCandidate(
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

        if (File.Exists(rejectedPath))
        {
            File.Delete(rejectedPath);
        }

        File.Move(tempPath, rejectedPath);

        var audit = new
        {
            gamelistPath,
            rejectedPath,
            createdAtUtc = DateTime.UtcNow,
            current = currentMetrics,
            candidate = candidateMetrics,
            reason = "candidate_gamelist_lost_too_much_content"
        };
        File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, BootstrapStateJsonOptions), Encoding.UTF8);
        return rejectedPath;
    }

    private static string MoveInvalidGamelistCandidate(
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

        if (File.Exists(rejectedPath))
        {
            File.Delete(rejectedPath);
        }

        File.Move(tempPath, rejectedPath);
        var audit = new
        {
            gamelistPath,
            rejectedPath,
            createdAtUtc = DateTime.UtcNow,
            reason,
            failure
        };
        File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, BootstrapStateJsonOptions), Encoding.UTF8);
        return rejectedPath;
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
        File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, BootstrapStateJsonOptions), Encoding.UTF8);
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

    private T ExecuteWithGamelistIoRetry<T>(string gamelistPath, string operation, CancellationToken cancellationToken, Func<T> action)
    {
        IOException? lastIoException = null;

        for (var attempt = 1; attempt <= GamelistIoRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return action();
            }
            catch (IOException ex) when (attempt < GamelistIoRetryCount)
            {
                lastIoException = ex;
                _logger?.LogDebug(ex, "Retry {Attempt}/{RetryCount} pour la {Operation} de {GamelistPath}.", attempt, GamelistIoRetryCount, operation, gamelistPath);
                Thread.Sleep(GamelistIoRetryDelay);
            }
        }

        throw lastIoException ?? new IOException($"Echec de la {operation} de {gamelistPath}.");
    }

    private static XDocument CreateEmptyGamelistDocument()
    {
        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XElement("gameList"));
    }

    private static bool IsMissingRootXml(XmlException ex)
    {
        return ex.Message.Contains("Root element is missing", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsurePlaceholderImage(string systemId, string projectionBaseName, string sourceFileName, bool overwriteExisting)
    {
        var sourcePath = Path.Combine(RetroBatPaths.EmulationStationThemeMediasRoot, sourceFileName);
        if (!File.Exists(sourcePath))
        {
            return string.Empty;
        }

        var storageSystemId = ResolvePlaceholderStorageSystemId(systemId);
        var destinationDirectory = Path.Combine(RetroBatPaths.RomsRoot, storageSystemId, "images");
        Directory.CreateDirectory(destinationDirectory);

        var destinationFileName = projectionBaseName + "_default" + Path.GetExtension(sourcePath);
        var destinationPath = Path.Combine(destinationDirectory, destinationFileName);
        if (overwriteExisting || !File.Exists(destinationPath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        return BuildPlaceholderRelativePath(systemId, storageSystemId, destinationFileName);
    }

    private static string BuildPlaceholderRelativePath(string frontendSystemId, string storageSystemId, string destinationFileName)
    {
        var frontendRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var destinationPath = Path.Combine(RetroBatPaths.RomsRoot, storageSystemId, "images", destinationFileName);
        var relative = Path.GetRelativePath(frontendRoot, destinationPath).Replace('\\', '/');
        return EnsureEsRelativePrefix(relative);
    }

    private static string ResolvePlaceholderStorageSystemId(string systemId)
    {
        return systemId switch
        {
            "mame" or "fbneo" or "fba" or "hbmame" => "arcade",
            _ => systemId
        };
    }

    private static bool IsDefaultPlaceholderPath(string? imagePath, string projectionBaseName)
    {
        var normalized = (imagePath ?? string.Empty).Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var expectedSuffix = $"/{projectionBaseName}_default.png";
        return normalized.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith($"{projectionBaseName}_default.png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApiPlaceholderPath(string? mediaPath, string projectionBaseName)
    {
        return IsDefaultPlaceholderPath(mediaPath, projectionBaseName);
    }

    private static string NormalizeGamelistMediaValue(string mediaPath)
    {
        var normalized = mediaPath.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized))
        {
            return normalized;
        }

        return EnsureEsRelativePrefix(normalized);
    }

    private static string NormalizeSelectionSourceToKind(string selectedSource, MediaSelectionTarget target, string wheelStyle)
    {
        var normalized = (selectedSource ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized switch
        {
            "logo" => MediaKinds.Logo,
            "wheel-hd" => target == MediaSelectionTarget.Logo ? ResolveWheelStyleKind(wheelStyle) : MediaKinds.WheelCarbon,
            "wheel" => MediaKinds.Wheel,
            "wheel-carbon" => MediaKinds.WheelCarbon,
            "wheelcarbon" => MediaKinds.WheelCarbon,
            "wheel-steel" => MediaKinds.WheelSteel,
            "wheelsteel" => MediaKinds.WheelSteel,
            "marquee" => MediaKinds.Marquee,
            "screenmarquee" => MediaKinds.ScreenMarquee,
            "screen-marquee" => MediaKinds.ScreenMarquee,
            "screenmarqueesmall" => MediaKinds.ScreenMarqueeSmall,
            "screen-marquee-small" => MediaKinds.ScreenMarqueeSmall,
            "boxback" => MediaKinds.BoxBack,
            "box-back" => MediaKinds.BoxBack,
            "ss" => MediaKinds.Thumbnail,
            "screenshot" => MediaKinds.Thumbnail,
            "thumb" => MediaKinds.Thumbnail,
            "thumbnail" => MediaKinds.Thumbnail,
            "sstitle" => MediaKinds.Image,
            "title" => MediaKinds.Image,
            "fanart" => MediaKinds.Fanart,
            "box-2d" => MediaKinds.BoxFront,
            "box2d" => MediaKinds.BoxFront,
            "box-2d-side" => MediaKinds.BoxSide,
            "boxside" => MediaKinds.BoxSide,
            "box-side" => MediaKinds.BoxSide,
            "box-texture" => MediaKinds.BoxTexture,
            "boxtexture" => MediaKinds.BoxTexture,
            "box-3d" => MediaKinds.Box3d,
            "box3d" => MediaKinds.Box3d,
            "cartridge" => MediaKinds.Cartridge,
            "cart" => MediaKinds.Cartridge,
            "support-2d" => MediaKinds.Cartridge,
            "support2d" => MediaKinds.Cartridge,
            "label" => MediaKinds.Label,
            "support-texture" => MediaKinds.Label,
            "supporttexture" => MediaKinds.Label,
            "flyer" => MediaKinds.Flyer,
            "figurine" => MediaKinds.Figurine,
            "magazine" => MediaKinds.Magazine,
            "steamgrid" => MediaKinds.SteamGrid,
            "mix" => MediaKinds.MixRbv2,
            "mixrbv1" => MediaKinds.MixRbv1,
            "mixrbv2" => MediaKinds.MixRbv2,
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

    private static XElement FindOrCreateGameNode(XElement root, string relativeGamePath)
    {
        var normalizedTarget = NormalizeForCompare(relativeGamePath);
        var existing = root.Elements("game")
            .FirstOrDefault(e => NormalizeForCompare(e.Element("path")?.Value) == normalizedTarget);

        if (existing != null)
        {
            return existing;
        }

        var created = new XElement("game");
        root.Add(created);
        return created;
    }

    private static void ApplyGameIdentityElements(XElement gameNode, MediaProjectionPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.ScreenScraperGameId))
        {
            gameNode.SetAttributeValue("id", plan.ScreenScraperGameId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(plan.EsGameId))
        {
            SetOrCreateElement(gameNode, "gameid", plan.EsGameId.Trim());
        }
    }

    private static bool TrySetGameIdElement(XElement gameNode, string esGameId)
    {
        if (string.IsNullOrWhiteSpace(esGameId))
        {
            return false;
        }

        return TrySetSelectionElement(gameNode, "gameid", esGameId.Trim());
    }

    private static GamelistEntryContentSignature BuildGamelistEntryContentSignature(XElement gameNode, string systemRoot)
    {
        var mediaHashesByTag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allMediaHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in gameNode.Elements())
        {
            var tagName = element.Name.LocalName;
            if (!IsGamelistMediaElementName(tagName))
            {
                continue;
            }

            var mediaPath = element.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                continue;
            }

            var diskPath = ResolveEsRelativePath(systemRoot, mediaPath);
            if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
            {
                continue;
            }

            var hash = ComputeFileContentHash(diskPath);
            if (string.IsNullOrWhiteSpace(hash))
            {
                continue;
            }

            mediaHashesByTag[tagName] = hash;
            allMediaHashes.Add(hash);
        }

        return new GamelistEntryContentSignature(
            BuildNonMediaGameXml(gameNode),
            mediaHashesByTag,
            allMediaHashes);
    }

    private static string BuildNonMediaGameXml(XElement gameNode)
    {
        var clone = new XElement(gameNode);
        foreach (var mediaElement in clone.Elements().Where(element => IsGamelistMediaElementName(element.Name.LocalName)).ToList())
        {
            mediaElement.Remove();
        }

        return clone.ToString(SaveOptions.DisableFormatting);
    }

    private static bool HasMeaningfulMediaContentChange(
        GamelistEntryContentSignature before,
        GamelistEntryContentSignature after)
    {
        foreach (var pair in after.MediaHashesByTag)
        {
            if (before.MediaHashesByTag.TryGetValue(pair.Key, out var beforeHash))
            {
                if (!string.Equals(beforeHash, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (!before.AllMediaHashes.Contains(pair.Value))
            {
                return true;
            }
        }

        return before.MediaHashesByTag.Keys.Any(tag => !after.MediaHashesByTag.ContainsKey(tag));
    }

    private static bool IsGamelistMediaElementName(string tagName)
    {
        return GamelistMediaElementNames.Contains(tagName);
    }

    private static void SetOrCreateElement(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element == null)
        {
            parent.Add(new XElement(name, value));
            return;
        }

        element.Value = value;
    }

    private static bool TrySetSelectionElement(XElement parent, string name, string value)
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

    private static void ApplyBundleMetadata(XElement gameNode, LocalizedTextBundle? bundle, string requestedLanguage, bool includeRating = true)
    {
        var targetLanguage = ResolveTargetLanguage(bundle, requestedLanguage, gameNode);
        foreach (var tagName in MetadataTagNames())
        {
            if (!includeRating && string.Equals(tagName, "rating", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = ResolveBundleField(bundle, tagName, targetLanguage);
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetOrCreateElement(gameNode, tagName, value);
                continue;
            }

            if (IsLocalizedHumanTextField(tagName))
            {
                NormalizeLocalizedElement(gameNode, tagName, targetLanguage);
                RemoveWrongLanguageElement(gameNode, tagName, targetLanguage);
            }
        }
    }

    private static bool TrySetMetadataElements(XElement gameNode, LocalizedTextBundle? bundle, string requestedLanguage)
    {
        var targetLanguage = ResolveTargetLanguage(bundle, requestedLanguage, gameNode);
        var updated = false;
        foreach (var tagName in MetadataTagNames())
        {
            updated |= IsLocalizedHumanTextField(tagName)
                ? TrySetLocalizedSelectionElement(gameNode, tagName, bundle, targetLanguage)
                : TrySetSelectionElement(gameNode, tagName, ResolveBundleField(bundle, tagName, targetLanguage));
        }

        return updated;
    }

    private void ApplyPlanRomIdentityMetadata(
        XElement gameNode,
        MediaProjectionPlan plan)
    {
        TrySetRomIdentityMetadataElements(gameNode, plan.RomRegions, plan.RomLanguages);
    }

    private bool TrySetRomIdentityMetadataElements(
        XElement gameNode,
        IReadOnlyList<string>? regions,
        IReadOnlyList<string>? languages)
    {
        var updated = false;
        var region = ResolveEsDisplayRegion(regions);
        updated |= TrySetSelectionElement(gameNode, "region", region);

        var language = ResolveEsDisplayLanguage(languages, region);
        updated |= TrySetSelectionElement(gameNode, "lang", language);

        return updated;
    }

    private string ResolveEsDisplayRegion(IReadOnlyList<string>? regions)
    {
        var romRegions = NormalizeEsDisplayRegionCodes(regions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (romRegions.Count == 0)
        {
            return "wr";
        }

        var preferred = BuildEsDisplayRegionPriority();
        foreach (var candidate in preferred)
        {
            if (romRegions.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return romRegions.FirstOrDefault(region => !string.Equals(region, "wr", StringComparison.OrdinalIgnoreCase))
            ?? romRegions[0];
    }

    private string ResolveEsDisplayLanguage(IReadOnlyList<string>? languages, string region)
    {
        var romLanguages = NormalizeEsDisplayLanguageCodes(languages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (romLanguages.Count == 0)
        {
            return ResolveEsDisplayLanguageFromRegion(region);
        }

        var preferred = BuildEsDisplayLanguagePriority();
        foreach (var candidate in preferred)
        {
            if (romLanguages.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        if (romLanguages.Contains("multi", StringComparer.OrdinalIgnoreCase))
        {
            var userLanguage = NormalizeEsDisplayLanguageCode(_settingsService.GetScrapingSettings().Language);
            return string.IsNullOrWhiteSpace(userLanguage)
                ? ResolveEsDisplayLanguageFromRegion(region)
                : userLanguage;
        }

        return string.Join(",", romLanguages);
    }

    private static string ResolveEsDisplayLanguageFromRegion(string? region)
    {
        return NormalizeEsDisplayRegionCode(region) switch
        {
            "fr" => "fr",
            "de" => "de",
            "es" => "es",
            "it" => "it",
            "nl" => "nl",
            "jp" => "jp",
            "br" => "pt",
            "kr" => "kr",
            "cn" => "cn",
            "ru" => "ru",
            "pl" => "pl",
            "se" or "sw" => "sw",
            "no" => "no",
            "gr" => "gr",
            "eu" or "uk" or "us" or "au" or "wr" or "asi" => "en",
            _ => "en"
        };
    }

    private List<string> BuildEsDisplayLanguagePriority()
    {
        var settings = _settingsService.GetScrapingSettings();
        var result = new List<string>();

        AddEsDisplayLanguageCandidates(result, settings.Language);
        AddEsDisplayLanguageCandidates(result, settings.ContentLanguageProfile);
        AddEsDisplayLanguageCandidates(result, _options.CurrentValue.ApiSettings.LanguageProfile);

        return result;
    }

    private List<string> BuildEsDisplayRegionPriority()
    {
        var settings = _settingsService.GetScrapingSettings();
        var result = new List<string>();

        AddEsDisplayRegionCandidates(result, settings.UserRegion);
        AddEsDisplayRegionCandidates(result, settings.ContentRegionProfile);
        AddEsDisplayRegionCandidates(result, _options.CurrentValue.ApiSettings.RegionProfile);
        AddEsDisplayRegionCandidates(result, settings.ContentLanguageProfile);
        AddEsDisplayRegionCandidates(result, _options.CurrentValue.ApiSettings.LanguageProfile);
        AddEsDisplayRegionCandidates(result, settings.Language);

        return result;
    }

    private static List<string> NormalizeEsDisplayRegionCodes(IReadOnlyList<string>? values)
    {
        var result = new List<string>();
        if (values == null)
        {
            return result;
        }

        foreach (var value in values)
        {
            foreach (var token in SplitRegionIdentityTokens(value))
            {
                AddDistinctRegion(result, NormalizeEsDisplayRegionCode(token));
            }
        }

        return result;
    }

    private static List<string> NormalizeEsDisplayLanguageCodes(IReadOnlyList<string>? values)
    {
        var result = new List<string>();
        if (values == null)
        {
            return result;
        }

        foreach (var value in values)
        {
            foreach (var token in SplitRomIdentityTokens(value))
            {
                AddDistinctLanguage(result, NormalizeEsDisplayLanguageCode(token));
            }
        }

        return result;
    }

    private static void AddEsDisplayRegionCandidates(List<string> target, string? value)
    {
        foreach (var token in SplitRomIdentityTokens(value))
        {
            AddEsDisplayRegionCandidate(target, token);
        }
    }

    private static void AddEsDisplayLanguageCandidates(List<string> target, string? value)
    {
        foreach (var token in SplitRomIdentityTokens(value))
        {
            AddEsDisplayLanguageCandidate(target, token);
        }
    }

    private static void AddEsDisplayRegionCandidate(List<string> target, string? value)
    {
        var normalized = NormalizeRegionIdentityKey(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized is "auto" or "automatic" or "ss" or "screenscraper" or "screen_scraper")
        {
            return;
        }

        switch (normalized)
        {
            case "fr" or "fra" or "fre" or "french" or "france" or "fr_fr":
                AddDistinctRegion(target, "fr");
                AddDistinctRegion(target, "eu");
                break;
            case "de" or "ger" or "deu" or "german" or "germany" or "de_de":
                AddDistinctRegion(target, "de");
                AddDistinctRegion(target, "eu");
                break;
            case "es" or "spa" or "spanish" or "spain" or "es_es":
                AddDistinctRegion(target, "sp");
                AddDistinctRegion(target, "es");
                AddDistinctRegion(target, "eu");
                break;
            case "it" or "ita" or "italian" or "italy" or "it_it":
                AddDistinctRegion(target, "it");
                AddDistinctRegion(target, "eu");
                break;
            case "nl" or "dut" or "dutch" or "netherlands" or "nl_nl":
                AddDistinctRegion(target, "nl");
                AddDistinctRegion(target, "eu");
                break;
            case "uk" or "gb" or "en_gb" or "united_kingdom" or "england":
                AddDistinctRegion(target, "uk");
                AddDistinctRegion(target, "eu");
                break;
            case "se" or "sv" or "swe" or "swedish" or "sweden":
            case "ru" or "rus" or "russian" or "russia":
            case "pl" or "pol" or "polish" or "da" or "danish" or "fi" or "finnish":
                AddDistinctRegion(target, "eu");
                break;
            case "eu" or "eur" or "europe":
                AddDistinctRegion(target, "eu");
                break;
            case "us" or "u" or "usa" or "america" or "united_states" or "english" or "en" or "en_us":
                AddDistinctRegion(target, "us");
                break;
            case "jp" or "j" or "jpn" or "japan" or "japanese" or "ja" or "ja_jp":
                AddDistinctRegion(target, "jp");
                break;
            case "br" or "bra" or "brazil" or "portuguese" or "pt" or "pt_br":
                AddDistinctRegion(target, "br");
                break;
            case "kr" or "ko" or "kor" or "korea" or "south_korea" or "korean":
                AddDistinctRegion(target, "kr");
                break;
            case "cn" or "zh" or "china" or "chinese" or "tw" or "taiwan":
                AddDistinctRegion(target, "cn");
                break;
            case "au" or "aus" or "australia" or "oceania":
                AddDistinctRegion(target, "au");
                break;
            case "asi" or "asia":
                AddDistinctRegion(target, "asi");
                break;
            case "world" or "w" or "ww" or "wr" or "wor" or "global" or "multi" or "multilingual":
                AddDistinctRegion(target, "wr");
                break;
            default:
                AddDistinctRegion(target, NormalizeEsDisplayRegionCode(normalized));
                break;
        }
    }

    private static void AddEsDisplayLanguageCandidate(List<string> target, string? value)
    {
        var normalized = NormalizeRegionIdentityKey(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized is "auto" or "automatic" or "ss" or "screenscraper" or "screen_scraper")
        {
            return;
        }

        AddDistinctLanguage(target, NormalizeEsDisplayLanguageCode(normalized));
    }

    private static string NormalizeEsDisplayRegionCode(string? value)
    {
        return NormalizeRegionIdentityKey(value) switch
        {
            "world" or "w" or "ww" or "wr" or "wor" or "global" => "wr",
            "europe" or "eur" or "e" => "eu",
            "usa" or "u" or "america" or "united_states" => "us",
            "japan" or "j" or "jpn" => "jp",
            "france" or "fra" or "fre" or "french" => "fr",
            "germany" or "ger" or "deu" or "german" => "de",
            "spain" or "spa" or "spanish" or "sp" => "es",
            "italy" or "ita" or "italian" => "it",
            "netherlands" or "dut" or "dutch" => "nl",
            "united_kingdom" or "gb" or "england" => "uk",
            "brazil" or "bra" => "br",
            "korea" or "south_korea" or "ko" => "kr",
            "china" or "zh" or "taiwan" or "tw" => "cn",
            "australia" or "aus" or "oceania" => "au",
            "asia" => "asi",
            var normalized => normalized
        };
    }

    private static string NormalizeEsDisplayLanguageCode(string? value)
    {
        return NormalizeRegionIdentityKey(value) switch
        {
            "english" or "eng" or "en_us" or "en_gb" or "uk" or "united_kingdom" => "en",
            "french" or "fre" or "fra" or "fr_fr" or "france" => "fr",
            "japanese" or "ja" or "jpn" or "ja_jp" or "japan" => "jp",
            "german" or "ger" or "deu" or "de_de" or "germany" => "de",
            "spanish" or "spa" or "es_es" or "spain" => "es",
            "italian" or "ita" or "it_it" or "italy" => "it",
            "portuguese" or "por" or "pt_pt" or "portugal" => "pt",
            "brazilian" or "brazilian_portuguese" or "pt_br" or "brazil" => "pt",
            "korean" or "ko" or "kor" or "ko_kr" or "korea" => "kr",
            "chinese" or "zh" or "chi" or "zho" or "zh_cn" or "zh_tw" or "china" or "taiwan" => "cn",
            "russian" or "rus" or "ru_ru" or "russia" => "ru",
            "dutch" or "dut" or "nl_nl" or "netherlands" => "nl",
            "swedish" or "swe" or "sv" or "sv_se" or "sweden" => "sw",
            "polish" or "pol" or "pl_pl" or "poland" => "pl",
            "norwegian" or "nor" or "nb" or "nn" or "nb_no" or "nn_no" or "norway" => "no",
            "greek" or "el" or "gr" or "el_gr" or "greece" => "gr",
            "multi" or "multilingual" => "multi",
            var normalized when normalized.Length >= 2 => normalized[..2] switch
            {
                "ja" => "jp",
                "ko" => "kr",
                "sv" => "sw",
                "el" => "gr",
                _ => normalized[..2]
            },
            _ => string.Empty
        };
    }

    private static IEnumerable<string> SplitRegionIdentityTokens(string? value)
    {
        return SplitRomIdentityTokens(value);
    }

    private static IEnumerable<string> SplitRomIdentityTokens(string? value)
    {
        return (value ?? string.Empty)
            .Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token));
    }

    private static string NormalizeRegionIdentityKey(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');
    }

    private static void AddDistinctRegion(List<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !target.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }
    }

    private static void AddDistinctLanguage(List<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !target.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }
    }

    private static bool TrySetLocalizedSelectionElement(
        XElement gameNode,
        string tagName,
        LocalizedTextBundle? bundle,
        string targetLanguage)
    {
        var value = ResolveBundleField(bundle, tagName, targetLanguage);
        if (TrySetSelectionElement(gameNode, tagName, value))
        {
            return true;
        }

        if (NormalizeLocalizedElement(gameNode, tagName, targetLanguage))
        {
            return true;
        }

        return RemoveWrongLanguageElement(gameNode, tagName, targetLanguage);
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

    private static string ResolveBundleField(LocalizedTextBundle? bundle, string fieldName, string requestedLanguage = "")
    {
        if (bundle == null)
        {
            return string.Empty;
        }

        if (!bundle.Fields.TryGetValue(fieldName, out var value))
        {
            return string.Empty;
        }

        var sanitized = LocalizedMetadataSanitizer.SanitizeField(fieldName, value, bundle.Language);
        var normalized = string.Equals(fieldName, "rating", StringComparison.OrdinalIgnoreCase)
            ? NormalizeGamelistRating(sanitized)
            : NormalizeGamelistText(sanitized);
        return IsLikelyWrongLanguageForTarget(fieldName, normalized, ResolveTargetLanguage(bundle, requestedLanguage, gameNode: null))
            ? string.Empty
            : normalized;
    }

    private static string ResolveTargetLanguage(LocalizedTextBundle? bundle, string requestedLanguage, XElement? gameNode)
    {
        var targetLanguage = NormalizeLanguageCode(bundle?.Language);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            targetLanguage = NormalizeLanguageCode(requestedLanguage);
        }

        if (string.IsNullOrWhiteSpace(targetLanguage) && gameNode != null)
        {
            targetLanguage = NormalizeLanguageCode(gameNode.Element("lang")?.Value);
        }

        return targetLanguage;
    }

    private static bool RemoveWrongLanguageElement(XElement parent, string name, string targetLanguage)
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

    private static bool NormalizeLocalizedElement(XElement parent, string name, string targetLanguage)
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

    private static string NormalizeGamelistRating(string? value)
    {
        var normalized = NormalizeGamelistText(value).Replace(',', '.');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!double.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rating))
        {
            return string.Empty;
        }

        if (rating > 1)
        {
            rating /= 20.0;
        }

        rating = Math.Clamp(rating, 0, 1);
        return rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
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

    private static Dictionary<string, string> ResolveProjectedKindPaths(ProjectedMediaIndex index, string projectionBaseName)
    {
        var kindPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-image", MediaKinds.Image);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-thumb", MediaKinds.Thumbnail);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-logo", MediaKinds.Logo);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-wheel", MediaKinds.Wheel);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-wheelcarbon", MediaKinds.WheelCarbon);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-wheelsteel", MediaKinds.WheelSteel);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-marquee", MediaKinds.Marquee);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-screenmarquee", MediaKinds.ScreenMarquee);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-screenmarqueesmall", MediaKinds.ScreenMarqueeSmall);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-steamgrid", MediaKinds.SteamGrid);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-mixrbv1", MediaKinds.MixRbv1);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-mixrbv2", MediaKinds.MixRbv2);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-box2d", MediaKinds.BoxFront);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-boxside", MediaKinds.BoxSide);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-boxtexture", MediaKinds.BoxTexture);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-box3d", MediaKinds.Box3d);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-cartridge", MediaKinds.Cartridge);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-label", MediaKinds.Label);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-supporttexture", MediaKinds.Label);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-support-texture", MediaKinds.Label);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-fanart", MediaKinds.Fanart);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-flyer", MediaKinds.Flyer);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-figurine", MediaKinds.Figurine);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-bezel", MediaKinds.Bezel);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-boxback", MediaKinds.BoxBack);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "images", "-map", MediaKinds.Map);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "manuals", "-manual", MediaKinds.Manual);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "manuals", "-magazine", MediaKinds.Magazine);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "videos", "-video", MediaKinds.Video);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "videos", "-video-normalized", MediaKinds.VideoNormalized);
        TryAddProjectedKind(kindPaths, index, projectionBaseName, "themes", "-themehb", MediaKinds.ThemeHb);
        RemoveProjectedMarqueeIfPollutedByWheel(kindPaths, index.FrontendSystemRoot);
        AddLogoAliasForSimpleWheel(kindPaths);
        return kindPaths;
    }

    private static void AddLogoAliasForSimpleWheel(IDictionary<string, string> kindPaths)
    {
        if (!kindPaths.ContainsKey(MediaKinds.Logo) &&
            kindPaths.TryGetValue(MediaKinds.Wheel, out var wheelPath) &&
            !string.IsNullOrWhiteSpace(wheelPath))
        {
            kindPaths[MediaKinds.Logo] = wheelPath;
        }
    }

    private static void AddVerifiedCanonicalPathsFromExistingTags(
        IDictionary<string, string> canonicalKindPaths,
        IReadOnlyDictionary<string, string> existingKindPaths,
        string systemRoot)
    {
        foreach (var pair in existingKindPaths)
        {
            var kind = MediaKinds.Normalize(pair.Key);
            if (string.IsNullOrWhiteSpace(kind) ||
                canonicalKindPaths.ContainsKey(kind) ||
                string.IsNullOrWhiteSpace(pair.Value) ||
                !IsVerifiedCanonicalMediaPath(systemRoot, pair.Value) ||
                !IsMediaPathCompatibleWithKind(pair.Value, kind, null))
            {
                continue;
            }

            canonicalKindPaths[kind] = pair.Value;
        }

        AddLogoAliasForSimpleWheel(canonicalKindPaths);
    }

    private static bool IsVerifiedCanonicalMediaPath(string systemRoot, string mediaPath)
    {
        var diskPath = ResolveEsRelativePath(systemRoot, mediaPath);
        if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
        {
            return false;
        }

        return IsPathInside(diskPath, RetroBatPaths.MediaSystemsRoot) ||
            IsPathInside(diskPath, RetroBatPaths.MediaUserSystemsRoot);
    }

    private static bool IsPathInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveProjectedMarqueeIfPollutedByWheel(IDictionary<string, string> kindPaths, string frontendSystemRoot)
    {
        if (!kindPaths.TryGetValue(MediaKinds.Marquee, out var marqueePath) ||
            !kindPaths.TryGetValue(MediaKinds.Wheel, out var wheelPath))
        {
            return;
        }

        var marqueeDiskPath = ResolveEsRelativePath(frontendSystemRoot, marqueePath);
        var wheelDiskPath = ResolveEsRelativePath(frontendSystemRoot, wheelPath);
        if (!string.IsNullOrWhiteSpace(marqueeDiskPath) &&
            !string.IsNullOrWhiteSpace(wheelDiskPath) &&
            HaveSameContent(marqueeDiskPath, wheelDiskPath))
        {
            kindPaths.Remove(MediaKinds.Marquee);
        }
    }

    private LocalizedTextBundle? ResolvePreferredBundle(string systemId, string gameSlug, string requestedLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(gameSlug))
        {
            return null;
        }

        var bundle = _localizedTextStore.LoadPreferredBundleAsync(
                systemId,
                gameSlug,
                requestedLanguage,
                cancellationToken,
                allowAnyLanguageFallback: false,
                allowEnglishFallback: false)
            .GetAwaiter()
            .GetResult();
        if (HasUsefulLocalizedBundleText(bundle))
        {
            return bundle;
        }

        if (_rawCacheMetadataService.TryRebuildLanguageAsync(systemId, gameSlug, requestedLanguage, cancellationToken)
            .GetAwaiter()
            .GetResult())
        {
            bundle = _localizedTextStore.LoadPreferredBundleAsync(
                    systemId,
                    gameSlug,
                    requestedLanguage,
                    cancellationToken,
                    allowAnyLanguageFallback: false,
                    allowEnglishFallback: false)
                .GetAwaiter()
                .GetResult();
        }

        return bundle;
    }

    private static bool HasUsefulLocalizedBundleText(LocalizedTextBundle? bundle)
    {
        if (bundle == null)
        {
            return false;
        }

        return HasBundleField(bundle, "desc") ||
            HasBundleField(bundle, "genre") ||
            HasBundleField(bundle, "family");
    }

    private static bool HasBundleField(LocalizedTextBundle bundle, string fieldName)
    {
        return bundle.Fields.TryGetValue(fieldName, out var value) &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static string ResolveTextLookupSlug(MediaProjectionPlan plan)
    {
        return string.IsNullOrWhiteSpace(plan.TextSourceGameSlug)
            ? plan.GameSlug
            : plan.TextSourceGameSlug;
    }

    private static void TryAddProjectedKind(
        IDictionary<string, string> kindPaths,
        ProjectedMediaIndex index,
        string projectionBaseName,
        string folderName,
        string suffix,
        string kind)
    {
        var relativePath = index.Resolve(folderName, projectionBaseName + suffix);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        kindPaths[kind] = relativePath;
    }

    private ProjectedMediaIndex BuildProjectedMediaIndex(string frontendSystemRoot, string projectionStorageRoot)
    {
        return new ProjectedMediaIndex(
            frontendSystemRoot,
            BuildProjectedFolderIndex(frontendSystemRoot, projectionStorageRoot, "images"),
            BuildProjectedFolderIndex(frontendSystemRoot, projectionStorageRoot, "manuals"),
            BuildProjectedFolderIndex(frontendSystemRoot, projectionStorageRoot, "videos"));
    }

    private Dictionary<string, string> BuildProjectedFolderIndex(string frontendSystemRoot, string projectionStorageRoot, string folderName)
    {
        var folderPath = Path.Combine(projectionStorageRoot, folderName);
        if (!Directory.Exists(folderPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var cacheEnabled = _options.CurrentValue.Scraping.ProjectedMediaIndexCacheEnabled;
        var folderStampUtc = Directory.GetLastWriteTimeUtc(folderPath);
        var cacheKey = BuildProjectedFolderIndexCacheKey(frontendSystemRoot, projectionStorageRoot, folderName);
        if (cacheEnabled &&
            ProjectedFolderIndexCache.TryGetValue(cacheKey, out var cached) &&
            cached.FolderStampUtc == folderStampUtc)
        {
            return cached.Index.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName) || index.ContainsKey(fileName))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(frontendSystemRoot, filePath).Replace('\\', '/');
            index[fileName] = EnsureEsRelativePrefix(relativePath);
        }

        if (cacheEnabled)
        {
            ProjectedFolderIndexCache[cacheKey] = new CachedProjectedFolderIndex(
                folderStampUtc,
                DateTime.UtcNow,
                new Dictionary<string, string>(index, StringComparer.OrdinalIgnoreCase));
        }

        return index;
    }

    private static string BuildProjectedFolderIndexCacheKey(string frontendSystemRoot, string projectionStorageRoot, string folderName)
    {
        return string.Join(
            "|",
            Path.GetFullPath(frontendSystemRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(projectionStorageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            folderName.Trim().ToLowerInvariant());
    }

    private static string ToGameRelativePath(string gamePath, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        if (!Path.IsPathRooted(gamePath))
        {
            return EnsureEsRelativePrefix(gamePath);
        }

        var relative = Path.GetRelativePath(systemRoot, gamePath).Replace('\\', '/');
        return EnsureEsRelativePrefix(relative);
    }

    private static string ToMediaRelativePath(string mediaPath, string systemRoot)
    {
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

    private static string ResolveEsRelativePath(string frontendSystemRoot, string relativePath)
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

        return Path.GetFullPath(Path.Combine(frontendSystemRoot, normalized));
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
            var leftHash = sha.ComputeHash(leftStream);
            var rightHash = sha.ComputeHash(rightStream);
            return leftHash.SequenceEqual(rightHash);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeForCompare(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }

    private static string GetPortableFileName(string? path)
    {
        return Path.GetFileName((path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        SaveSelectionNormalizationStateIfDirty();
    }

    private enum MediaSelectionTarget
    {
        Image,
        Logo,
        Thumbnail
    }

    private sealed class BootstrapPlaceholderState
    {
        public Dictionary<string, BootstrapPlaceholderSystemState> Systems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BootstrapPlaceholderSystemState
    {
        public int StateVersion { get; set; }
        public long GamelistWriteTicksUtc { get; set; }
        public long ImagesWriteTicksUtc { get; set; }
        public long PlaceholderSourceWriteTicksUtc { get; set; }
        public string GamelistFingerprint { get; set; } = string.Empty;
        public string ImagesFingerprint { get; set; } = string.Empty;
    }

    private sealed class GamelistSelectionNormalizationState
    {
        public Dictionary<string, GamelistSelectionNormalizationSystemState> Systems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GamelistSelectionNormalizationSystemState
    {
        public int StateVersion { get; set; }
        public long GamelistWriteTicksUtc { get; set; }
        public long GamelistByteLength { get; set; }
        public string GamelistFingerprint { get; set; } = string.Empty;
        public string MediaFingerprint { get; set; } = string.Empty;
        public string SettingsSignature { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed record GamelistSaveMetrics(
        long ByteLength,
        int GameCount,
        int GamesWithAnyMedia,
        int MediaTagCount,
        long WriteTicksUtc);

    private sealed record RichGamelistCandidate(
        string Path,
        XDocument Document,
        GamelistSaveMetrics Metrics);

    private sealed class EsGameIdentityEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string ScraperId { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
    }

    private sealed class EsGameIdentityIndex
    {
        public Dictionary<string, EsGameIdentityEntry> ByAbsolutePath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, EsGameIdentityEntry> ByRelativePath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, EsGameIdentityEntry> ByFileName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousAbsolutePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AmbiguousFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record DirtyLiveGamelistPlan(
        string Key,
        string FrontendSystemId,
        string GameSlug,
        string GamePath,
        MediaProjectionPlan Plan,
        DateTime LastUpdatedUtc);

    private sealed record LiveAddGamesPayloadTrace(
        string? RelativePath,
        string? FullPath,
        bool XmlWritten,
        int XmlLengthBytes,
        string XmlSha256,
        int GameNodeCount,
        int DirtyBatchCount,
        int RelatedBatchCount,
        string ThemeSet,
        string ImageSource,
        string LogoSource,
        string ThumbSource,
        string WheelStyle,
        bool LiveEsMediaPushEnabled,
        bool LiveEsMetadataPushEnabled,
        DateTime? GamelistLastWriteUtc,
        long? GamelistLength,
        DateTime? EsSettingsLastWriteUtc,
        DateTime? EsLogLastWriteUtc,
        IReadOnlyList<LiveAddGamesPayloadNodeTrace> Nodes,
        IReadOnlyList<LiveAddGamesBatchTrace> DirtyBatch,
        IReadOnlyList<string> RelatedBatchPaths);

    private sealed record LiveAddGamesPayloadNodeTrace(
        string Id,
        string Path,
        string Name,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> VisibleMediaTags,
        string Md5);

    private sealed record LiveAddGamesBatchTrace(
        string FrontendSystemId,
        string GameSlug,
        string GamePath);

    private sealed record PendingExtendedGameIdentity(
        IReadOnlySet<string> AllowedMediaTokens,
        IReadOnlyList<string> RomRegions,
        IReadOnlyList<string> RomLanguages);

    private sealed record RelatedGamelistInfo(
        string GamePath,
        string EsGameId,
        string DisplayName,
        IReadOnlyList<string> RomRegions,
        IReadOnlyList<string> RomLanguages)
    {
        public static RelatedGamelistInfo Empty(string gamePath)
        {
            return new RelatedGamelistInfo(
                gamePath,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>());
        }
    }

    private sealed record FileTraceInfo(
        DateTime? LastWriteUtc,
        long? Length);

    private sealed record GamelistEntryContentSignature(
        string NonMediaXml,
        IReadOnlyDictionary<string, string> MediaHashesByTag,
        IReadOnlySet<string> AllMediaHashes);

    private sealed record CachedProjectedFolderIndex(
        DateTime FolderStampUtc,
        DateTime IndexedAtUtc,
        IReadOnlyDictionary<string, string> Index);

    private sealed class ProjectedMediaIndex
    {
        private static readonly string[] PreferredImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
        private static readonly string[] PreferredManualExtensions = [".pdf"];
        private static readonly string[] PreferredVideoExtensions = [".mp4"];
        private readonly Dictionary<string, string> _images;
        private readonly Dictionary<string, string> _manuals;
        private readonly Dictionary<string, string> _videos;

        public ProjectedMediaIndex(
            string frontendSystemRoot,
            Dictionary<string, string> images,
            Dictionary<string, string> manuals,
            Dictionary<string, string> videos)
        {
            FrontendSystemRoot = frontendSystemRoot;
            _images = images;
            _manuals = manuals;
            _videos = videos;
        }

        public string FrontendSystemRoot { get; }

        public string Resolve(string folderName, string fileStem)
        {
            var extensions = folderName switch
            {
                "manuals" => PreferredManualExtensions,
                "videos" => PreferredVideoExtensions,
                _ => PreferredImageExtensions
            };

            var folderIndex = folderName switch
            {
                "manuals" => _manuals,
                "videos" => _videos,
                _ => _images
            };

            foreach (var extension in extensions)
            {
                var candidate = fileStem + extension;
                if (folderIndex.TryGetValue(candidate, out var relativePath) && !string.IsNullOrWhiteSpace(relativePath))
                {
                    return relativePath;
                }
            }

            return string.Empty;
        }
    }
}
