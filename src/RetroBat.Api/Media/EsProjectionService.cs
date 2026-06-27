using System.IO.Compression;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Services;
using RetroBat.Api.Infrastructure;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RetroBat.Api.Media;

public class EsProjectionService
{
    private const int AtomicWriteMaxAttempts = 10;
    private static readonly TimeSpan AtomicWriteRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ThemeHbRefreshDebounceWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ThemeHbGlobalF5DebounceWindow = TimeSpan.FromSeconds(5);
    private static readonly object ThemeHbRefreshLock = new();
    private static readonly object ThemeHbInstallIndexLock = new();
    private static readonly JsonSerializerOptions ThemeHbInstallIndexJsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, DateTime> RecentThemeHbRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? ThemeHbInstallIndex;
    private static DateTime LastThemeHbF5AtUtc = DateTime.MinValue;
    private readonly IMediaAliasStore _aliasStore;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly LocalMediaIndexService _localMediaIndexService;
    private readonly EsControllerService _esControllerService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly ApiContext _context;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ILogger<EsProjectionService>? _logger;
    private readonly HttpClient _esHttpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:1234"),
        Timeout = TimeSpan.FromSeconds(2)
    };

    public EsProjectionService(
        IMediaAliasStore aliasStore,
        IOptionsMonitor<ApiExposeOptions> options,
        EmulationStationSettingsService settingsService,
        ApiExposeRuntimeOptionsService runtimeOptions,
        LocalMediaIndexService localMediaIndexService,
        EsControllerService esControllerService,
        MediaRuntimeState runtimeState,
        ApiContext context,
        IEmulationStationNotificationService notificationService,
        InterfaceTextService interfaceTextService,
        ILogger<EsProjectionService>? logger = null)
    {
        _aliasStore = aliasStore;
        _options = options;
        _settingsService = settingsService;
        _runtimeOptions = runtimeOptions;
        _localMediaIndexService = localMediaIndexService;
        _esControllerService = esControllerService;
        _runtimeState = runtimeState;
        _context = context;
        _notificationService = notificationService;
        _interfaceTextService = interfaceTextService;
        _logger = logger;
    }

    public async Task ApplyCanonicalImportAsync(MediaProjectionPlan plan, CancellationToken cancellationToken = default)
    {
        foreach (var need in plan.Needs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = ResolveSourcePath(plan.SystemId, plan.FrontendSystemId, need, plan.GameSlug);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                continue;
            }

            if (IsInvalidNoMediaFile(sourcePath))
            {
                CleanupInvalidNoMediaFile(sourcePath);
                continue;
            }

            await ImportCanonicalAsync(
                plan.SystemId,
                plan.GameSlug,
                need,
                sourcePath,
                cancellationToken,
                plan.FrontendSystemId,
                plan.GamePath,
                plan.EsGameId);
        }
    }

    public async Task<string?> ImportCanonicalAsync(
        string systemId,
        string gameSlug,
        MediaNeed need,
        string sourcePath,
        CancellationToken cancellationToken = default,
        string? frontendSystemId = null,
        string? gamePath = null,
        string? esGameId = null,
        bool notifyThemeHbScrape = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        if (IsInvalidNoMediaFile(sourcePath))
        {
            CleanupInvalidNoMediaFile(sourcePath);
            return null;
        }

        if (IsUnderRoot(sourcePath, RetroBatPaths.MediaUserRoot))
        {
            need.ImportedPath = sourcePath;
            need.ExistingPath = sourcePath;
            need.IsMissing = false;
            await ApplyImportedMediaDeploymentsAsync(systemId, frontendSystemId ?? systemId, gameSlug, need, sourcePath, gamePath, esGameId, notifyThemeHbScrape, cancellationToken);
            return sourcePath;
        }

        var contentHash = await JsonMediaAliasStore.ComputeSha256Async(sourcePath, cancellationToken);
        var existingCanonical = await _aliasStore.ResolveMediaByHashAsync(systemId, need.Kind, contentHash, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingCanonical) && File.Exists(existingCanonical))
        {
            if (IsInvalidNoMediaFile(existingCanonical))
            {
                CleanupInvalidNoMediaFile(existingCanonical);
            }
            else if (IsCanonicalPathForGame(existingCanonical, systemId, gameSlug))
            {
                need.ImportedPath = existingCanonical;
                need.ExistingPath = existingCanonical;
                need.IsMissing = false;
                await ApplyImportedMediaDeploymentsAsync(systemId, frontendSystemId ?? systemId, gameSlug, need, existingCanonical, gamePath, esGameId, notifyThemeHbScrape, cancellationToken);
                return existingCanonical;
            }
        }

        if (IsUnderRoot(sourcePath, RetroBatPaths.MediaSystemsRoot))
        {
            var canonicalLayoutPath = GetCanonicalImportPath(systemId, gameSlug, need.Kind, sourcePath);
            var effectivePath = string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(canonicalLayoutPath), StringComparison.OrdinalIgnoreCase)
                ? sourcePath
                : await CopyExistingSystemMediaToCanonicalLayoutAsync(sourcePath, canonicalLayoutPath, cancellationToken);

            need.ImportedPath = effectivePath;
            need.ExistingPath = effectivePath;
            need.IsMissing = false;
            await _aliasStore.RecordMediaHashAsync(systemId, need.Kind, contentHash, effectivePath, cancellationToken);
            await ApplyImportedMediaDeploymentsAsync(systemId, frontendSystemId ?? systemId, gameSlug, need, effectivePath, gamePath, esGameId, notifyThemeHbScrape, cancellationToken);
            return effectivePath;
        }

        var canonicalPath = GetCanonicalImportPath(systemId, gameSlug, need.Kind, sourcePath);
        need.ImportedPath = canonicalPath;

        if (!File.Exists(canonicalPath))
        {
            var destinationDir = Path.GetDirectoryName(canonicalPath);
            if (!string.IsNullOrWhiteSpace(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            await CopyFileAtomicallyAsync(sourcePath, canonicalPath, cancellationToken);
            need.WasImported = true;
        }

        if (File.Exists(canonicalPath))
        {
            await _aliasStore.RecordMediaHashAsync(systemId, need.Kind, contentHash, canonicalPath, cancellationToken);
            need.ExistingPath = canonicalPath;
            need.IsMissing = false;
            await ApplyImportedMediaDeploymentsAsync(systemId, frontendSystemId ?? systemId, gameSlug, need, canonicalPath, gamePath, esGameId, notifyThemeHbScrape, cancellationToken);
            return canonicalPath;
        }

        return null;
    }

    private static async Task<string> CopyExistingSystemMediaToCanonicalLayoutAsync(
        string sourcePath,
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(canonicalPath))
        {
            return canonicalPath;
        }

        var destinationDir = Path.GetDirectoryName(canonicalPath);
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        await CopyFileAtomicallyAsync(sourcePath, canonicalPath, cancellationToken);
        return File.Exists(canonicalPath) ? canonicalPath : sourcePath;
    }

    private static bool IsCanonicalPathForGame(string canonicalPath, string systemId, string gameSlug)
    {
        var gameRoot = Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "games", gameSlug);
        return IsUnderRoot(canonicalPath, gameRoot);
    }

    public async Task ApplyProjectionAsync(MediaProjectionPlan plan, CancellationToken cancellationToken = default)
    {
        // Full canonical media contract:
        // APIExpose media must live in media/{systems,user}/... and gamelist/addgames links
        // must point there relatively from roms/<system>. Do not create new ES media
        // projections in roms/<system>/{images,videos,manuals}; existing projected files are
        // legacy artifacts only.
        await Task.CompletedTask;
        foreach (var need in plan.Needs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            need.ProjectedPath = string.Empty;
            need.WasProjected = false;
        }
    }

    public string GetProjectedPath(string systemId, string relativePath)
    {
        var storageSystemId = ResolveProjectionStorageSystemId(systemId);
        return Path.Combine(RetroBatPaths.RomsRoot, storageSystemId, relativePath);
    }

    public string GetProjectedPath(string systemId, string relativePath, string sourcePath)
    {
        var sourceExtension = NormalizePreferredExtension(Path.GetExtension(sourcePath));
        var normalizedRelative = string.IsNullOrWhiteSpace(sourceExtension)
            ? relativePath
            : ReplaceExtension(relativePath, sourceExtension);
        return GetProjectedPath(systemId, normalizedRelative);
    }

    public string? ResolveCanonicalSourcePath(string systemId, string gameSlug, string kind)
    {
        foreach (var root in GetCanonicalRootCandidates())
        {
            var candidate = ResolveKindPath(root, systemId, gameSlug, kind);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public IDisposable BeginCanonicalMediaIndexScope(string systemId)
    {
        return _localMediaIndexService.BeginSystemScope(systemId);
    }

    public string? ResolveUserSourcePath(string systemId, string gameSlug, string kind)
    {
        return ResolveKindPath(RetroBatPaths.MediaUserSystemsRoot, systemId, gameSlug, kind);
    }

    private static IEnumerable<string> GetCanonicalRootCandidates()
    {
        yield return RetroBatPaths.MediaUserSystemsRoot;
        yield return RetroBatPaths.MediaSystemsRoot;
    }

    private string? ResolveKindPath(string root, string systemId, string gameSlug, string kind)
    {
        var (directory, fileStem) = GetKindLayout(systemId, gameSlug, kind);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileStem))
        {
            return null;
        }

        var indexedCandidate = root.Equals(RetroBatPaths.MediaUserSystemsRoot, StringComparison.OrdinalIgnoreCase)
            ? _localMediaIndexService.ResolveActiveSourcePath("media/user", systemId, gameSlug, kind)
            : _localMediaIndexService.ResolveActiveSourcePath("media", systemId, gameSlug, kind);
        if (!string.IsNullOrWhiteSpace(indexedCandidate))
        {
            if (string.Equals(MediaKinds.Normalize(kind), MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeThemeHbArchivePath(indexedCandidate);
            }

            if (string.Equals(kind, MediaKinds.Marquee, StringComparison.OrdinalIgnoreCase) &&
                IsCanonicalMarqueePollutedByWheel(root, systemId, gameSlug, indexedCandidate))
            {
                return null;
            }

            return indexedCandidate;
        }

        var fullDirectory = Path.Combine(root, directory);
        if (!Directory.Exists(fullDirectory))
        {
            return null;
        }

        var matches = Directory.GetFiles(fullDirectory, fileStem + ".*");
        var candidate = matches
            .OrderBy(ScoreCandidatePath)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? ResolveLegacyKindPath(root, systemId, gameSlug, kind);

        if (string.Equals(MediaKinds.Normalize(kind), MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeThemeHbArchivePath(candidate);
        }

        if (string.Equals(kind, MediaKinds.Marquee, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(candidate) &&
            IsCanonicalMarqueePollutedByWheel(root, systemId, gameSlug, candidate))
        {
            return null;
        }

        return candidate;
    }

    private bool IsCanonicalMarqueePollutedByWheel(string root, string systemId, string gameSlug, string marqueePath)
    {
        var wheelPath = ResolveKindPath(root, systemId, gameSlug, MediaKinds.Wheel);
        return !string.IsNullOrWhiteSpace(wheelPath)
            && File.Exists(wheelPath)
            && File.Exists(marqueePath)
            && HaveSameContent(wheelPath, marqueePath);
    }

    private static string? ResolveLegacyKindPath(string root, string systemId, string gameSlug, string kind)
    {
        var gameRoot = Path.Combine(root, systemId, "games", gameSlug);
        if (!Directory.Exists(gameRoot))
        {
            return null;
        }

        IEnumerable<string> matches = kind switch
        {
            MediaKinds.Marquee
                => Directory.Exists(Path.Combine(gameRoot, "artwork", "marquee"))
                    ? Directory.GetFiles(Path.Combine(gameRoot, "artwork", "marquee"), "*.*", SearchOption.AllDirectories)
                    : Array.Empty<string>(),
            MediaKinds.ScreenMarquee
                => Directory.Exists(Path.Combine(gameRoot, "artwork", "screenmarquee"))
                    ? Directory.GetFiles(Path.Combine(gameRoot, "artwork", "screenmarquee"), "screenmarquee.*", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>(),
            MediaKinds.ScreenMarqueeSmall
                => Directory.Exists(Path.Combine(gameRoot, "artwork", "screenmarquee"))
                    ? Directory.GetFiles(Path.Combine(gameRoot, "artwork", "screenmarquee"), "screenmarquee-small.*", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>(),
            MediaKinds.SteamGrid
                => Directory.Exists(Path.Combine(gameRoot, "ui", "steamgrid"))
                    ? Directory.GetFiles(Path.Combine(gameRoot, "ui", "steamgrid"), "steamgrid.*", SearchOption.TopDirectoryOnly)
                    : Directory.Exists(Path.Combine(gameRoot, "artwork", "steamgrid"))
                        ? Directory.GetFiles(Path.Combine(gameRoot, "artwork", "steamgrid"), "steamgrid.*", SearchOption.TopDirectoryOnly)
                        : Array.Empty<string>(),
            MediaKinds.Figurine
                => Directory.Exists(Path.Combine(gameRoot, "artwork", "figurines"))
                    ? Directory.GetFiles(Path.Combine(gameRoot, "artwork", "figurines"), "figurine.*", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>(),
            _ => Array.Empty<string>()
        };

        return matches
            .Where(path => IsSupportedLegacyMediaExtension(path, kind))
            .Where(path => !string.Equals(kind, MediaKinds.Marquee, StringComparison.OrdinalIgnoreCase) ||
                IsTrueLegacyMarqueeCandidate(path))
            .OrderBy(ScoreCandidatePath)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsTrueLegacyMarqueeCandidate(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        if (stem.StartsWith("generated-", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("screenmarquee", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("dmd", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("topper", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public string GetCanonicalImportPath(string systemId, string gameSlug, string kind, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = kind switch
            {
                MediaKinds.Manual => ".pdf",
                MediaKinds.Video or MediaKinds.VideoNormalized => ".mp4",
                MediaKinds.ThemeHb => ".zip",
                _ => ".png"
            };
        }

        var (directory, fileStem) = GetKindLayout(systemId, gameSlug, kind);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileStem))
        {
            return Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "games", gameSlug, kind + extension);
        }

        return Path.Combine(RetroBatPaths.MediaSystemsRoot, directory, fileStem + extension);
    }

    private async Task ApplyImportedMediaDeploymentsAsync(
        string systemId,
        string frontendSystemId,
        string gameSlug,
        MediaNeed need,
        string sourcePath,
        string? gamePath,
        string? esGameId,
        bool notifyThemeHbScrape,
        CancellationToken cancellationToken)
    {
        await TryInstallThemeHbArchiveAsync(frontendSystemId, gameSlug, need, sourcePath, gamePath, esGameId, notifyThemeHbScrape, cancellationToken);
        await ApplyMediaDeploymentRulesAsync(systemId, frontendSystemId, gameSlug, need, sourcePath, gamePath, cancellationToken);
    }

    private async Task TryInstallThemeHbArchiveAsync(
        string frontendSystemId,
        string gameSlug,
        MediaNeed need,
        string zipPath,
        string? gamePath,
        string? esGameId,
        bool notifyThemeHbScrape,
        CancellationToken cancellationToken)
    {
        if (!_runtimeOptions.IsHyperBatThemeInstallEnabled())
        {
            return;
        }

        var normalizedKind = MediaKinds.Normalize(need.Kind);
        var deployments = ResolveThemeDeploymentRules(normalizedKind).ToArray();
        if (deployments.Length == 0)
        {
            return;
        }

        var normalizedZipPath = NormalizeThemeHbArchivePath(zipPath);
        if (string.IsNullOrWhiteSpace(normalizedZipPath))
        {
            return;
        }

        var settings = _settingsService.GetScrapingSettings();
        var installedAnyTheme = false;
        var notifiedLocalScrape = false;
        foreach (var deployment in deployments)
        {
            var themeSets = ResolveThemeSets(settings, deployment).ToArray();
            if (themeSets.Length == 0)
            {
                continue;
            }

            foreach (var themeSet in themeSets)
            {
                if (!IsSafeThemeSetName(themeSet))
                {
                    _logger?.LogWarning(
                        "Theme deployment skipped because ThemeSet is not a safe theme directory name: {ThemeSet}",
                        themeSet);
                    continue;
                }

                foreach (var target in deployment.InstallTargets.Where(IsEnabledDeploymentTarget))
                {
                    var destinationRoot = ResolveDeploymentPath(
                        target.Path,
                        systemId: frontendSystemId,
                        frontendSystemId,
                        gameSlug,
                        normalizedKind,
                        normalizedZipPath,
                        gamePath,
                        themeSet);
                    if (string.IsNullOrWhiteSpace(destinationRoot) ||
                        !IsDeploymentTargetPathAllowed(destinationRoot))
                    {
                        _logger?.LogWarning(
                            "Theme deployment target skipped because path is unsafe or empty: deployment={Deployment}, target={Target}",
                            deployment.Name,
                            destinationRoot);
                        continue;
                    }

                    var installKey = BuildDeploymentInstallKey(deployment.Name, frontendSystemId, gameSlug, themeSet, destinationRoot, normalizedZipPath);
                    var legacyInstallKey = BuildThemeHbInstallKey(frontendSystemId, gameSlug, themeSet, normalizedZipPath);
                    var archiveFingerprint = BuildThemeHbArchiveFingerprint(normalizedZipPath);

                    if (Directory.Exists(destinationRoot) && IsThemeHbInstallFingerprintCurrent(installKey, archiveFingerprint))
                    {
                        _logger?.LogDebug(
                            "Theme deployment skipped because archive fingerprint is already applied: deployment={Deployment}, system={SystemId}, game={GameSlug}, theme={ThemeSet}: {ZipPath}",
                            deployment.Name,
                            frontendSystemId,
                            gameSlug,
                            themeSet,
                            normalizedZipPath);
                        continue;
                    }

                    if (Directory.Exists(destinationRoot) && IsThemeHbInstallFingerprintCurrent(legacyInstallKey, archiveFingerprint))
                    {
                        RememberThemeHbInstallFingerprint(installKey, archiveFingerprint);
                        _logger?.LogInformation(
                            "Theme deployment index migrated without extraction: deployment={Deployment}, system={SystemId}, game={GameSlug}, theme={ThemeSet}, target={DestinationRoot}",
                            deployment.Name,
                            frontendSystemId,
                            gameSlug,
                            themeSet,
                            destinationRoot);
                        continue;
                    }

                    if (Directory.Exists(destinationRoot))
                    {
                        RememberThemeHbInstallFingerprint(installKey, archiveFingerprint);
                        RememberThemeHbInstallFingerprint(legacyInstallKey, archiveFingerprint);
                        _logger?.LogInformation(
                            "Theme deployment skipped because target folder already exists: deployment={Deployment}, system={SystemId}, game={GameSlug}, theme={ThemeSet}, target={DestinationRoot}",
                            deployment.Name,
                            frontendSystemId,
                            gameSlug,
                            themeSet,
                            destinationRoot);
                        continue;
                    }

                    try
                    {
                        var changed = await ApplyDeploymentTargetAsync(target, normalizedZipPath, destinationRoot, cancellationToken);
                        RememberThemeHbInstallFingerprint(installKey, archiveFingerprint);
                        installedAnyTheme |= changed;
                        if (changed &&
                            notifyThemeHbScrape &&
                            deployment.NotifyLocalScrape &&
                            !notifiedLocalScrape &&
                            IsCurrentlySelectedGame(frontendSystemId, gameSlug))
                        {
                            await _notificationService.NotifyAsync(
                                _interfaceTextService.Format(
                                    "notification.theme.local_applied",
                                    settings.Language,
                                    ("theme", deployment.Name),
                                    ("game", gameSlug)),
                                cancellationToken);
                            notifiedLocalScrape = true;
                        }

                        _logger?.LogInformation(
                            "Theme deployment applied: deployment={Deployment}, system={SystemId}, game={GameSlug}, theme={ThemeSet}, changed={Changed}, target={DestinationRoot}",
                            deployment.Name,
                            frontendSystemId,
                            gameSlug,
                            themeSet,
                            changed,
                            destinationRoot);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Theme deployment failed: deployment={Deployment}, system={SystemId}, game={GameSlug}, source={ZipPath}",
                            deployment.Name,
                            frontendSystemId,
                            gameSlug,
                            normalizedZipPath);
                    }
                }
            }
        }

        if (installedAnyTheme && !settings.IsHyperBatThemeActive)
        {
            await RefreshTrackingLog.AppendAsync(
                "f5",
                "skipped-inactive-theme",
                new
                {
                    reason = "hyperbat-theme-installed",
                    frontendSystemId,
                    gameSlug,
                    settings.ThemeSet
                },
                cancellationToken);
        }
        else if (installedAnyTheme && TryMarkThemeHbRefresh(frontendSystemId, gameSlug))
        {
            await RefreshThemeHbByF5Async(frontendSystemId, gameSlug, cancellationToken);
        }
        else if (installedAnyTheme)
        {
            await RefreshTrackingLog.AppendAsync(
                "f5",
                "skipped-debounce",
                new
                {
                    reason = "hyperbat-theme-installed",
                    frontendSystemId,
                    gameSlug
                },
                cancellationToken);
        }
    }

    private async Task ApplyMediaDeploymentRulesAsync(
        string systemId,
        string frontendSystemId,
        string gameSlug,
        MediaNeed need,
        string sourcePath,
        string? gamePath,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.MediaDeploymentRules;
        if (!options.Enabled)
        {
            return;
        }

        var normalizedKind = MediaKinds.Normalize(need.Kind);
        foreach (var rule in options.Rules.Where(rule => IsMediaDeploymentRuleMatch(rule, systemId, frontendSystemId, normalizedKind)))
        {
            var resolvedSource = ResolveDeploymentPath(
                string.IsNullOrWhiteSpace(rule.Source) ? "{source}" : rule.Source,
                systemId,
                frontendSystemId,
                gameSlug,
                normalizedKind,
                sourcePath,
                gamePath,
                themeSet: string.Empty);
            if (string.IsNullOrWhiteSpace(resolvedSource) ||
                !File.Exists(resolvedSource) ||
                !IsDeploymentSourcePathAllowed(resolvedSource))
            {
                _logger?.LogWarning(
                    "Media deployment source skipped because it is missing or unsafe: rule={Rule}, source={Source}",
                    rule.Name,
                    resolvedSource);
                continue;
            }

            foreach (var target in rule.Targets.Where(IsEnabledDeploymentTarget))
            {
                var destinationPath = ResolveDeploymentPath(
                    target.Path,
                    systemId,
                    frontendSystemId,
                    gameSlug,
                    normalizedKind,
                    resolvedSource,
                    gamePath,
                    themeSet: string.Empty);
                if (string.IsNullOrWhiteSpace(destinationPath) ||
                    !IsDeploymentTargetPathAllowed(destinationPath))
                {
                    _logger?.LogWarning(
                        "Media deployment target skipped because path is unsafe or empty: rule={Rule}, target={Target}",
                        rule.Name,
                        destinationPath);
                    continue;
                }

                try
                {
                    var changed = await ApplyDeploymentTargetAsync(target, resolvedSource, destinationPath, cancellationToken);
                    _logger?.LogInformation(
                        "Media deployment applied: rule={Rule}, system={SystemId}, game={GameSlug}, mediaKind={MediaKind}, changed={Changed}, target={Target}",
                        rule.Name,
                        frontendSystemId,
                        gameSlug,
                        normalizedKind,
                        changed,
                        destinationPath);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(
                        ex,
                        "Media deployment failed: rule={Rule}, system={SystemId}, game={GameSlug}, source={SourcePath}",
                        rule.Name,
                        frontendSystemId,
                        gameSlug,
                        resolvedSource);
                }
            }
        }
    }

    private IEnumerable<ApiExposeOptions.ThemeDeploymentRuleOptions> ResolveThemeDeploymentRules(string mediaKind)
    {
        var options = _options.CurrentValue.ThemeDeployments;
        if (!options.Enabled)
        {
            yield break;
        }

        var normalizedKind = MediaKinds.Normalize(mediaKind);
        foreach (var rule in options.Rules)
        {
            if (!rule.Enabled ||
                rule.InstallTargets.Count == 0 ||
                !string.Equals(MediaKinds.Normalize(rule.MediaKind), normalizedKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return rule;
        }
    }

    private static IEnumerable<string> ResolveThemeSets(
        EmulationStationScrapingSettings settings,
        ApiExposeOptions.ThemeDeploymentRuleOptions deployment)
    {
        var activeThemeSet = (settings.ThemeSet ?? string.Empty).Trim();
        var activeMatcher = string.IsNullOrWhiteSpace(deployment.ActiveThemeMatcher)
            ? "*"
            : deployment.ActiveThemeMatcher.Trim();

        if (!string.IsNullOrWhiteSpace(activeThemeSet) &&
            IsWildcardMatch(activeThemeSet, activeMatcher) &&
            IsInstalledThemeSet(activeThemeSet))
        {
            yield return activeThemeSet;
        }
    }

    private static bool IsMediaDeploymentRuleMatch(
        ApiExposeOptions.MediaDeploymentRuleOptions rule,
        string systemId,
        string frontendSystemId,
        string mediaKind)
    {
        if (!rule.Enabled || rule.Targets.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.MediaKind) &&
            !string.Equals(MediaKinds.Normalize(rule.MediaKind), mediaKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.System) &&
            !IsWildcardMatch(systemId, rule.System))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(rule.FrontendSystem) ||
            IsWildcardMatch(frontendSystemId, rule.FrontendSystem);
    }

    private static bool IsEnabledDeploymentTarget(ApiExposeOptions.DeploymentTargetOptions target)
    {
        return target.Enabled &&
            !string.IsNullOrWhiteSpace(target.Type) &&
            !string.IsNullOrWhiteSpace(target.Path);
    }

    private static string ResolveDeploymentPath(
        string path,
        string systemId,
        string frontendSystemId,
        string gameSlug,
        string mediaKind,
        string sourcePath,
        string? gamePath,
        string themeSet)
    {
        var sourceFileName = Path.GetFileName(sourcePath);
        var gameFileName = string.IsNullOrWhiteSpace(gamePath) ? string.Empty : Path.GetFileName(gamePath);
        var rom = string.IsNullOrWhiteSpace(gamePath) ? gameSlug : Path.GetFileNameWithoutExtension(gamePath);
        var expanded = (path ?? string.Empty).Trim()
            .Replace("{source}", sourcePath, StringComparison.OrdinalIgnoreCase)
            .Replace("{sourceFileName}", sourceFileName, StringComparison.OrdinalIgnoreCase)
            .Replace("{sourceFileNameWithoutExtension}", Path.GetFileNameWithoutExtension(sourceFileName), StringComparison.OrdinalIgnoreCase)
            .Replace("{sourceExtension}", Path.GetExtension(sourceFileName), StringComparison.OrdinalIgnoreCase)
            .Replace("{RetroBatRoot}", RetroBatPaths.RetroBatRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{PluginRoot}", RetroBatPaths.PluginRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{MediaRoot}", RetroBatPaths.MediaRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{MediaSystemsRoot}", RetroBatPaths.MediaSystemsRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{EmulationStationThemesRoot}", RetroBatPaths.EmulationStationThemesRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{EmulationStationConfigRoot}", RetroBatPaths.EmulationStationConfigRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{RomsRoot}", RetroBatPaths.RomsRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{system}", systemId, StringComparison.OrdinalIgnoreCase)
            .Replace("{frontendSystem}", frontendSystemId, StringComparison.OrdinalIgnoreCase)
            .Replace("{game}", gameSlug, StringComparison.OrdinalIgnoreCase)
            .Replace("{rom}", rom, StringComparison.OrdinalIgnoreCase)
            .Replace("{gameFileName}", gameFileName, StringComparison.OrdinalIgnoreCase)
            .Replace("{mediaKind}", mediaKind, StringComparison.OrdinalIgnoreCase)
            .Replace("{themeSet}", themeSet, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }

        expanded = expanded.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(expanded))
        {
            expanded = Path.Combine(RetroBatPaths.PluginRoot, expanded);
        }

        return Path.GetFullPath(expanded);
    }

    private static bool IsDeploymentSourcePathAllowed(string path)
    {
        return IsPathUnderKnownWritableRoot(path);
    }

    private static bool IsDeploymentTargetPathAllowed(string path)
    {
        return IsPathUnderKnownWritableRoot(path);
    }

    private static bool IsPathUnderKnownWritableRoot(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            (IsUnderRoot(path, RetroBatPaths.RetroBatRoot) ||
             IsUnderRoot(path, RetroBatPaths.PluginRoot));
    }

    private static string BuildDeploymentInstallKey(
        string deploymentName,
        string frontendSystemId,
        string gameSlug,
        string themeSet,
        string destinationPath,
        string sourcePath)
    {
        var raw = string.Join(
            "|",
            deploymentName.Trim().ToLowerInvariant(),
            frontendSystemId.Trim().ToLowerInvariant(),
            gameSlug.Trim().ToLowerInvariant(),
            themeSet.Trim().ToLowerInvariant(),
            Path.GetFullPath(destinationPath).Trim().ToLowerInvariant(),
            Path.GetFullPath(sourcePath).Trim().ToLowerInvariant());
        using var sha = SHA256.Create();
        return "deployment-" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static async Task<bool> ApplyDeploymentTargetAsync(
        ApiExposeOptions.DeploymentTargetOptions target,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var type = (target.Type ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal);
        return type switch
        {
            "extractarchive" or "extract-archive" => await ExtractZipSafelyAsync(sourcePath, destinationPath, cancellationToken),
            "copyfile" or "copy-file" => await CopyDeploymentFileAsync(sourcePath, destinationPath, target.Overwrite, cancellationToken),
            _ => false
        };
    }

    private static async Task<bool> CopyDeploymentFileAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var resolvedDestinationPath = ResolveDeploymentFileDestination(sourcePath, destinationPath);
        if (File.Exists(resolvedDestinationPath))
        {
            if (HaveSameContent(sourcePath, resolvedDestinationPath) || !overwrite)
            {
                return false;
            }
        }

        await CopyFileAtomicallyAsync(sourcePath, resolvedDestinationPath, cancellationToken);
        return true;
    }

    private static string ResolveDeploymentFileDestination(string sourcePath, string destinationPath)
    {
        var trimmed = (destinationPath ?? string.Empty).Trim();
        if (trimmed.EndsWith(Path.DirectorySeparatorChar) ||
            trimmed.EndsWith(Path.AltDirectorySeparatorChar) ||
            Directory.Exists(trimmed))
        {
            return Path.Combine(trimmed, Path.GetFileName(sourcePath));
        }

        return trimmed;
    }

    private static bool IsWildcardMatch(string value, string pattern)
    {
        var normalizedValue = (value ?? string.Empty).Trim();
        var normalizedPattern = (pattern ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPattern) ||
            string.Equals(normalizedPattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (!normalizedPattern.Contains('*'))
        {
            return string.Equals(normalizedValue, normalizedPattern, StringComparison.OrdinalIgnoreCase);
        }

        var parts = normalizedPattern
            .Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = 0;
        foreach (var part in parts)
        {
            var found = normalizedValue.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            index = found + part.Length;
        }

        return (normalizedPattern.StartsWith('*') || normalizedValue.StartsWith(parts.FirstOrDefault() ?? string.Empty, StringComparison.OrdinalIgnoreCase)) &&
            (normalizedPattern.EndsWith('*') || normalizedValue.EndsWith(parts.LastOrDefault() ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInstalledThemeSet(string themeSet)
    {
        if (!IsSafeThemeSetName(themeSet))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(RetroBatPaths.EmulationStationThemesRoot, themeSet.Trim()));
    }

    private async Task<string?> TryRefreshThemeHbByPlaycountAsync(
        string frontendSystemId,
        string gameSlug,
        string? gamePath,
        string? esGameId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(frontendSystemId) ||
            string.IsNullOrWhiteSpace(gameSlug) ||
            string.IsNullOrWhiteSpace(gamePath) ||
            string.IsNullOrWhiteSpace(esGameId))
        {
            return null;
        }

        var currentPlayCount = await TryReadEsGameMetadataStringAsync(
            frontendSystemId,
            esGameId,
            "playcount",
            cancellationToken);
        if (!int.TryParse(currentPlayCount, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var playCount) ||
            playCount <= 0)
        {
            await RefreshTrackingLog.AppendAsync(
                "metadata",
                "skipped-playcount-default",
                new
                {
                    reason = "hyperbat-theme-installed",
                    frontendSystemId,
                    gameSlug,
                    esGameId,
                    playcount = currentPlayCount ?? string.Empty
                },
                cancellationToken);
            return null;
        }

        var nextPlayCount = (playCount + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!await PostEsGameMetadataAsync(frontendSystemId, esGameId, "playcount", nextPlayCount, cancellationToken))
        {
            return null;
        }

        _runtimeState.QueuePendingLiveMetadataRestore(
            frontendSystemId,
            gameSlug,
            gamePath,
            "playcount",
            playCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "playcount";
    }

    private async Task<string?> TryRefreshThemeHbByTransientGenreAsync(
        string frontendSystemId,
        string gameSlug,
        string? esGameId,
        string zipPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(frontendSystemId) ||
            string.IsNullOrWhiteSpace(gameSlug) ||
            string.IsNullOrWhiteSpace(esGameId))
        {
            return null;
        }

        try
        {
            var marker = BuildThemeHbTransientGenreMarker(frontendSystemId, gameSlug, zipPath);
            if (await PostEsGameMetadataAsync(frontendSystemId, esGameId, "genres", marker, cancellationToken))
            {
                return "genres";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger?.LogInformation(
                ex,
                "ThemeHb metadata refresh skipped because EmulationStation API is unavailable.");
        }

        return null;
    }

    private async Task<bool> PostEsGameMetadataAsync(
        string frontendSystemId,
        string esGameId,
        string fieldName,
        string value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [fieldName] = value
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _esHttpClient.PostAsync(
            $"/systems/{Uri.EscapeDataString(frontendSystemId)}/games/{Uri.EscapeDataString(esGameId.Trim())}",
            content,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        _logger?.LogWarning(
            "ThemeHb metadata refresh returned HTTP {StatusCode} for system={SystemId}, gameid={EsGameId}, field={FieldName}.",
            (int)response.StatusCode,
            frontendSystemId,
            esGameId,
            fieldName);
        await RefreshTrackingLog.AppendAsync(
            "metadata",
            "failed",
            new
            {
                reason = "hyperbat-theme-installed",
                frontendSystemId,
                esGameId,
                statusCode = (int)response.StatusCode,
                field = fieldName
            },
            cancellationToken);
        return false;
    }

    private async Task<string?> TryReadEsGameMetadataStringAsync(
        string frontendSystemId,
        string esGameId,
        string fieldName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _esHttpClient.GetAsync(
                $"/systems/{Uri.EscapeDataString(frontendSystemId)}/games/{Uri.EscapeDataString(esGameId.Trim())}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty(fieldName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger?.LogDebug(
                ex,
                "ThemeHb metadata preflight skipped because EmulationStation API is unavailable.");
        }

        return null;
    }

    private async Task RefreshThemeHbByF5Async(
        string frontendSystemId,
        string gameSlug,
        CancellationToken cancellationToken)
    {
        var refreshResult = await _esControllerService.TapAsync(
            new EsControllerTapRequest
            {
                Input = "f5",
                Count = 1
            },
            cancellationToken);

        if (!refreshResult.Success)
        {
            _logger?.LogWarning(
                "ThemeHb F5 refresh failed for system={SystemId}, game={GameSlug}: {Reason} - {Message}",
                frontendSystemId,
                gameSlug,
                refreshResult.Reason,
                refreshResult.Message);
            await RefreshTrackingLog.AppendAsync(
                "f5",
                "failed",
                new
                {
                    reason = "hyperbat-theme-installed",
                    frontendSystemId,
                    gameSlug,
                    refreshResult.Reason,
                    refreshResult.Message
                },
                cancellationToken);
            return;
        }

        _runtimeState.SuppressLiveAddGamesAfterThemeRefresh(
            frontendSystemId,
            gameSlug);
        await RefreshTrackingLog.AppendAsync(
            "f5",
            "success",
            new
            {
                reason = "hyperbat-theme-installed",
                frontendSystemId,
                gameSlug,
                refreshResult.Message
            },
            cancellationToken);
    }

    public async Task RefreshThemeHbAfterExternalInstallAsync(
        string frontendSystemId,
        string gameSlug,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetScrapingSettings();
        if (!settings.IsHyperBatThemeActive)
        {
            await RefreshTrackingLog.AppendAsync(
                "f5",
                "skipped-inactive-theme",
                new
                {
                    reason = "hyperbat-external-theme-installed",
                    frontendSystemId,
                    gameSlug,
                    settings.ThemeSet
                },
                cancellationToken);
            return;
        }

        if (TryMarkThemeHbRefresh(frontendSystemId, gameSlug))
        {
            await RefreshThemeHbByF5Async(frontendSystemId, gameSlug, cancellationToken);
            return;
        }

        await RefreshTrackingLog.AppendAsync(
            "f5",
            "skipped-debounce",
            new
            {
                reason = "hyperbat-external-theme-installed",
                frontendSystemId,
                gameSlug
            },
            cancellationToken);
    }

    private static string BuildThemeHbTransientGenreMarker(
        string frontendSystemId,
        string gameSlug,
        string zipPath)
    {
        var source = string.Join(
            "|",
            "apiexpose-themehb",
            frontendSystemId.Trim().ToLowerInvariant(),
            gameSlug.Trim().ToLowerInvariant(),
            Path.GetFullPath(zipPath).Trim().ToLowerInvariant(),
            BuildThemeHbArchiveFingerprint(zipPath),
            DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return "apiexpose-themehb-" + Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    private bool IsCurrentlySelectedGame(string frontendSystemId, string gameSlug)
    {
        var selected = _context.Ui.Selected;
        if (selected == null)
        {
            return false;
        }

        var selectedSystem = selected.SystemId;
        if (string.IsNullOrWhiteSpace(selectedSystem))
        {
            selectedSystem = _context.Ui.SelectedSystem?.Name ?? string.Empty;
        }

        if (!string.Equals(
                NormalizeThemeHbSelectionValue(selectedSystem),
                NormalizeThemeHbSelectionValue(frontendSystemId),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var selectedPathSlug = NormalizeThemeHbSelectionValue(Path.GetFileNameWithoutExtension(selected.GamePath));
        var selectedNameSlug = NormalizeThemeHbSelectionValue(selected.GameName);
        var targetSlug = NormalizeThemeHbSelectionValue(gameSlug);
        return string.Equals(selectedPathSlug, targetSlug, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectedNameSlug, targetSlug, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeThemeHbSelectionValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string BuildThemeHbInstallKey(
        string frontendSystemId,
        string gameSlug,
        string themeSet,
        string archivePath)
    {
        return string.Join(
            "|",
            (frontendSystemId ?? string.Empty).Trim().ToLowerInvariant(),
            (gameSlug ?? string.Empty).Trim().ToLowerInvariant(),
            (themeSet ?? string.Empty).Trim().ToLowerInvariant(),
            Path.GetFullPath(archivePath).Trim().ToLowerInvariant());
    }

    private static string BuildThemeHbArchiveFingerprint(string archivePath)
    {
        var file = new FileInfo(archivePath);
        return string.Join(
            "|",
            file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool IsThemeHbInstallFingerprintCurrent(string key, string fingerprint)
    {
        lock (ThemeHbInstallIndexLock)
        {
            var index = LoadThemeHbInstallIndex();
            return index.TryGetValue(key, out var current) &&
                string.Equals(current, fingerprint, StringComparison.Ordinal);
        }
    }

    private static void RememberThemeHbInstallFingerprint(string key, string fingerprint)
    {
        lock (ThemeHbInstallIndexLock)
        {
            var index = LoadThemeHbInstallIndex();
            index[key] = fingerprint;
            SaveThemeHbInstallIndex(index);
        }
    }

    private static Dictionary<string, string> LoadThemeHbInstallIndex()
    {
        if (ThemeHbInstallIndex != null)
        {
            return ThemeHbInstallIndex;
        }

        var path = GetThemeHbInstallIndexPath();
        if (!File.Exists(path))
        {
            ThemeHbInstallIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return ThemeHbInstallIndex;
        }

        try
        {
            var json = File.ReadAllText(path);
            ThemeHbInstallIndex = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ThemeHbInstallIndex = new Dictionary<string, string>(ThemeHbInstallIndex, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            ThemeHbInstallIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return ThemeHbInstallIndex;
    }

    private static void SaveThemeHbInstallIndex(Dictionary<string, string> index)
    {
        var path = GetThemeHbInstallIndexPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(index, ThemeHbInstallIndexJsonOptions));
    }

    private static string GetThemeHbInstallIndexPath()
    {
        return Path.Combine(RetroBatPaths.PluginRoot, "logs", "hyperbat-install-index.json");
    }

    private static bool TryMarkThemeHbRefresh(string frontendSystemId, string gameSlug)
    {
        var key = string.Join(
            "|",
            (frontendSystemId ?? string.Empty).Trim().ToLowerInvariant(),
            (gameSlug ?? string.Empty).Trim().ToLowerInvariant());
        var nowUtc = DateTime.UtcNow;
        lock (ThemeHbRefreshLock)
        {
            if (LastThemeHbF5AtUtc != DateTime.MinValue &&
                nowUtc - LastThemeHbF5AtUtc < ThemeHbGlobalF5DebounceWindow)
            {
                return false;
            }

            var thresholdUtc = nowUtc - ThemeHbRefreshDebounceWindow;
            foreach (var stale in RecentThemeHbRefreshes.Where(entry => entry.Value < thresholdUtc).Select(entry => entry.Key).ToArray())
            {
                RecentThemeHbRefreshes.TryRemove(stale, out _);
            }

            if (RecentThemeHbRefreshes.TryGetValue(key, out var previousUtc) &&
                nowUtc - previousUtc < ThemeHbRefreshDebounceWindow)
            {
                return false;
            }

            RecentThemeHbRefreshes[key] = nowUtc;
            LastThemeHbF5AtUtc = nowUtc;
            return true;
        }
    }

    private static IEnumerable<string> ResolveHyperBatThemeSets(EmulationStationScrapingSettings settings)
    {
        var activeThemeSet = settings.ThemeSet.Trim();
        if (settings.IsHyperBatThemeActive && !string.IsNullOrWhiteSpace(activeThemeSet))
        {
            yield return activeThemeSet;
            yield break;
        }

        if (!Directory.Exists(RetroBatPaths.EmulationStationThemesRoot))
        {
            yield break;
        }

        foreach (var themeDirectory in Directory.EnumerateDirectories(RetroBatPaths.EmulationStationThemesRoot, "*hyperbat*", SearchOption.TopDirectoryOnly))
        {
            var themeSet = Path.GetFileName(themeDirectory);
            if (!string.IsNullOrWhiteSpace(themeSet))
            {
                yield return themeSet;
            }
        }
    }

    private static async Task<bool> ExtractZipSafelyAsync(string zipPath, string destinationRoot, CancellationToken cancellationToken)
    {
        var fullDestinationRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(fullDestinationRoot);

        var changed = false;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var entryRelativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(fullDestinationRoot, entryRelativePath));
            if (!IsUnderRoot(destinationPath, fullDestinationRoot))
            {
                throw new InvalidDataException($"ThemeHb zip entry escapes destination root: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (File.Exists(destinationPath) &&
                new FileInfo(destinationPath).Length == entry.Length &&
                await ZipEntryContentEqualsFileAsync(entry, destinationPath, cancellationToken))
            {
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            await using var sourceStream = entry.Open();
            await using var targetStream = File.Create(destinationPath);
            await sourceStream.CopyToAsync(targetStream, cancellationToken);
            changed = true;
        }

        return changed;
    }

    private static async Task<bool> ZipEntryContentEqualsFileAsync(
        ZipArchiveEntry entry,
        string path,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 81920;
        await using var left = entry.Open();
        await using var right = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var leftBuffer = new byte[BufferSize];
        var rightBuffer = new byte[BufferSize];

        while (true)
        {
            var leftRead = await left.ReadAsync(leftBuffer.AsMemory(0, leftBuffer.Length), cancellationToken);
            var rightRead = await right.ReadAsync(rightBuffer.AsMemory(0, rightBuffer.Length), cancellationToken);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static bool IsSafeThemeSetName(string themeSet)
    {
        return !string.IsNullOrWhiteSpace(themeSet)
            && !Path.IsPathRooted(themeSet)
            && themeSet.IndexOfAny(Path.GetInvalidPathChars()) < 0
            && !themeSet.Contains(Path.DirectorySeparatorChar)
            && !themeSet.Contains(Path.AltDirectorySeparatorChar);
    }

    private static (string directory, string fileStem) GetKindLayout(string systemId, string gameSlug, string kind)
    {
        return kind switch
        {
            MediaKinds.Image => (Path.Combine(systemId, "games", gameSlug, "artwork"), "screentitle"),
            MediaKinds.Thumbnail => (Path.Combine(systemId, "games", gameSlug, "artwork"), "screenshot"),
            // `logo` is a semantic display slot; ScreenScraper usually backs it with the same
            // wheel asset, so we avoid duplicating canonical storage.
            MediaKinds.Logo => (Path.Combine(systemId, "games", gameSlug, "ui", "wheels"), "wheel"),
            MediaKinds.Wheel => (Path.Combine(systemId, "games", gameSlug, "ui", "wheels"), "wheel"),
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
            MediaKinds.Fanart => (Path.Combine(systemId, "games", gameSlug, "artwork"), "fanart"),
            MediaKinds.Flyer => (Path.Combine(systemId, "games", gameSlug, "artwork"), "flyer"),
            MediaKinds.Figurine => (Path.Combine(systemId, "games", gameSlug, "artwork"), "figurine"),
            MediaKinds.Bezel => (Path.Combine(systemId, "games", gameSlug, "artwork", "bezels"), "bezel"),
            MediaKinds.BoxBack => (Path.Combine(systemId, "games", gameSlug, "artwork", "box"), "back"),
            MediaKinds.Map => (Path.Combine(systemId, "games", gameSlug, "documents", "maps"), "map"),
            MediaKinds.Manual => (Path.Combine(systemId, "games", gameSlug, "documents"), "manual"),
            MediaKinds.Magazine => (Path.Combine(systemId, "games", gameSlug, "documents"), "magazine"),
            MediaKinds.Video => (Path.Combine(systemId, "games", gameSlug), "video"),
            MediaKinds.VideoNormalized => (Path.Combine(systemId, "games", gameSlug), "video-normalized"),
            MediaKinds.ThemeHb => (Path.Combine(systemId, "games", gameSlug, "themes"), "themehb"),
            _ => (Path.Combine(systemId, "games", gameSlug), kind)
        };
    }

    private string? ResolveSourcePath(string systemId, string frontendSystemId, MediaNeed need, string gameSlug)
    {
        var candidate = TryResolvePath(frontendSystemId, need.ExistingPath);
        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
        {
            return candidate;
        }

        return ResolveCanonicalSourcePath(systemId, gameSlug, need.Kind);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolvePath(string systemId, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var normalized = path.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (normalized.StartsWith("." + Path.DirectorySeparatorChar) || normalized.StartsWith("./") || normalized.StartsWith(".\\"))
        {
            normalized = normalized[2..];
        }

        return Path.Combine(RetroBatPaths.RomsRoot, systemId, normalized);
    }

    private static string ResolveProjectionStorageSystemId(string systemId)
    {
        return systemId switch
        {
            "mame" or "fbneo" or "fba" or "hbmame" => "arcade",
            _ => systemId
        };
    }

    private static int ScoreCandidatePath(string path)
    {
        var extension = NormalizePreferredExtension(Path.GetExtension(path));
        return extension switch
        {
            ".png" => 0,
            ".jpg" => 1,
            ".jpeg" => 2,
            ".webp" => 3,
            ".gif" => 4,
            ".mp4" => 5,
            ".pdf" => 6,
            ".zip" => 7,
            _ => 50
        };
    }

    private static bool IsSupportedLegacyMediaExtension(string path, string kind)
    {
        var extension = NormalizePreferredExtension(Path.GetExtension(path));
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return kind switch
        {
            MediaKinds.Manual => extension == ".pdf",
            MediaKinds.Video or MediaKinds.VideoNormalized => extension == ".mp4",
            MediaKinds.ThemeHb => extension == ".zip",
            _ => extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"
        };
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
            using var sha = System.Security.Cryptography.SHA256.Create();
            var leftHash = sha.ComputeHash(leftStream);
            var rightHash = sha.ComputeHash(rightStream);
            return leftHash.SequenceEqual(rightHash);
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeThemeHbArchivePath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            return null;
        }

        if (!IsZipArchive(candidate))
        {
            return null;
        }

        if (string.Equals(Path.GetExtension(candidate), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        var zipPath = Path.ChangeExtension(candidate, ".zip");
        if (!File.Exists(zipPath))
        {
            File.Move(candidate, zipPath);
        }

        return zipPath;
    }

    private static bool IsZipArchive(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> signature = stackalloc byte[4];
            if (stream.Read(signature) < signature.Length)
            {
                return false;
            }

            return signature[0] == 0x50 &&
                signature[1] == 0x4B &&
                ((signature[2] == 0x03 && signature[3] == 0x04) ||
                    (signature[2] == 0x05 && signature[3] == 0x06) ||
                    (signature[2] == 0x07 && signature[3] == 0x08));
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
            throw new InvalidOperationException($"Destination path has no directory: {destinationPath}");
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
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var targetStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await sourceStream.CopyToAsync(targetStream, cancellationToken);
                await targetStream.FlushAsync(cancellationToken);
                targetStream.Flush(flushToDisk: true);
            }

            await ReplaceWithRetryAsync(tempPath, destinationPath, cancellationToken);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static async Task ReplaceWithRetryAsync(
        string tempPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= AtomicWriteMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, destinationPath);
                }

                return;
            }
            catch (Exception ex) when (
                attempt < AtomicWriteMaxAttempts &&
                (ex is IOException || ex is UnauthorizedAccessException))
            {
                await Task.Delay(AtomicWriteRetryDelay, cancellationToken);
            }
        }
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
            // Best effort cleanup.
        }
    }

    private static string ReplaceExtension(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return path;
        }

        return Path.ChangeExtension(path, extension);
    }

    private static string NormalizePreferredExtension(string? extension)
    {
        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".mp4" or ".pdf" or ".zip" => normalized,
            _ => string.Empty
        };
    }

    private static bool IsInvalidNoMediaFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > 64)
            {
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes).Trim();
            return string.Equals(text, "NOMEDIA", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupInvalidNoMediaFile(string path)
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
            // Best effort cleanup.
        }
    }

}
