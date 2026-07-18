using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class PhysicalMediaWebSocketProjectionService : IHostedService, IDisposable
{
    private const string SystemLogoCacheVersion = "system-logo-cache-v4-png32-srgb";
    private const int SelectionSnapshotDebounceMs = 35;

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly IMediaAliasStore _mediaAliasStore;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ILogger<PhysicalMediaWebSocketProjectionService>? _logger;
    private readonly HttpClient _esHttpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:1234"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    private readonly object _latestSelectionLock = new();
    private long _latestSelectionSequence;
    private IDisposable? _subscription;

    public PhysicalMediaWebSocketProjectionService(
        IEventBus eventBus,
        ApiContext context,
        SystemIdNormalizer systemIdNormalizer,
        GameNameNormalizer gameNameNormalizer,
        IMediaAliasStore mediaAliasStore,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ILogger<PhysicalMediaWebSocketProjectionService>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _systemIdNormalizer = systemIdNormalizer;
        _gameNameNormalizer = gameNameNormalizer;
        _mediaAliasStore = mediaAliasStore;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(OnEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }

    private void OnEvent(EventEnvelope envelope)
    {
        if (string.Equals(envelope.Type, "ui.system.selected.raw", StringComparison.OrdinalIgnoreCase))
        {
            var sequence = MarkLatestSelectionSequence(envelope);
            _ = PublishSystemSnapshotAsync(envelope, sequence);
            return;
        }

        if (string.Equals(envelope.Type, "ui.game.selected.raw", StringComparison.OrdinalIgnoreCase))
        {
            var sequence = MarkLatestSelectionSequence(envelope);
            _ = PublishGameSnapshotsAsync(envelope, _context.Ui.Selected, "game-selected", sequence);
            return;
        }

        if (string.Equals(envelope.Type, "ui.game.started.raw", StringComparison.OrdinalIgnoreCase))
        {
            _ = PublishGameSnapshotsAsync(envelope, _context.Ui.Running ?? _context.Ui.Selected, "game-start");
            return;
        }

        if (string.Equals(envelope.Type, "ui.game.ended.raw", StringComparison.OrdinalIgnoreCase))
        {
            _ = PublishGameSnapshotsAsync(envelope, _context.Ui.Selected, "game-end");
        }
    }

    private async Task PublishSystemSnapshotAsync(EventEnvelope trigger, long selectionSequence)
    {
        var perf = Stopwatch.StartNew();
        try
        {
            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale system media snapshot skipped before work: sequence={Sequence}",
                    selectionSequence);
                return;
            }

            await DelayLatestSelectionSnapshotAsync(selectionSequence);

            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale system media snapshot skipped after debounce: sequence={Sequence}",
                    selectionSequence);
                return;
            }

            var selectedSystem = _context.Ui.SelectedSystem;
            var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(selectedSystem?.Name);
            var systemId = _systemIdNormalizer.Normalize(selectedSystem?.Name);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                return;
            }

            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale system media snapshot skipped before publish: sequence={Sequence}, system={SystemId}",
                    selectionSequence,
                    systemId);
                return;
            }

            var roots = ResolveSystemRoots(systemId).ToList();
            var selection = BuildSystemSelection(selectionSequence, frontendSystemId, systemId, "system-selected");
            var marquee = BuildSystemMarqueeMedia(frontendSystemId, systemId, selectedSystem, roots);

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "marquee.snapshot",
                NodeId = trigger.NodeId,
                CorrelationId = trigger.CorrelationId,
                Payload = new
                {
                    SnapshotVersion = 2,
                    Sequence = selectionSequence,
                    SelectionKey = selection.SelectionKey,
                    Stream = "marquee",
                    Selection = selection,
                    Media = marquee,
                    Generation = ResolveSystemGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "projection")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "projection");
            _logger?.LogInformation(
                "marquee system snapshot published: trigger={Trigger}, system={SystemId}, elapsedMs={ElapsedMs}",
                trigger.Type,
                systemId,
                (int)perf.Elapsed.TotalMilliseconds);

            _ = GenerateSystemMediaAndPublishAsync(trigger, frontendSystemId, systemId, selectedSystem, selectionSequence);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to publish system marquee WebSocket snapshot.");
        }
    }

    private async Task PublishGameSnapshotsAsync(
        EventEnvelope trigger,
        GameReference? selected,
        string state,
        long selectionSequence = 0)
    {
        var perf = Stopwatch.StartNew();
        try
        {
            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale game media snapshot skipped before work: state={State}, sequence={Sequence}",
                    state,
                    selectionSequence);
                return;
            }

            if (selected == null)
            {
                return;
            }

            await DelayLatestSelectionSnapshotAsync(selectionSequence);

            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale game media snapshot skipped after debounce: state={State}, sequence={Sequence}",
                    state,
                    selectionSequence);
                return;
            }

            var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(selected.SystemId);
            var systemId = _systemIdNormalizer.Normalize(selected.SystemId);
            var requestedSlug = _gameNameNormalizer.NormalizeGameSlug(selected.GameName, selected.GamePath);
            if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(requestedSlug))
            {
                return;
            }

            var gameSlug = await _mediaAliasStore.ResolveGameSlugAsync(
                systemId,
                BuildAliasKeys(selected, requestedSlug),
                requestedSlug);

            var roots = ResolveGameRoots(systemId, gameSlug).ToList();
            var fallbackSystemRoots = ResolveSystemRoots(systemId).ToList();
            var selection = BuildGameSelection(selectionSequence, frontendSystemId, systemId, gameSlug, selected, state);

            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale game media snapshot skipped before publish: state={State}, sequence={Sequence}, system={SystemId}, game={GameSlug}",
                    state,
                    selectionSequence,
                    systemId,
                    gameSlug);
                return;
            }

            var marquee = BuildGameMarqueeMedia(roots, fallbackSystemRoots);
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "marquee.snapshot",
                NodeId = trigger.NodeId,
                CorrelationId = trigger.CorrelationId,
                Payload = new
                {
                    SnapshotVersion = 2,
                    Sequence = selectionSequence,
                    SelectionKey = selection.SelectionKey,
                    Stream = "marquee",
                    Selection = selection,
                    Media = marquee,
                    Generation = ResolveGameGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "projection")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "projection");

            var topper = FindFirstAsset(roots, "artwork", "marquee", "topper.*");
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "topper.snapshot",
                NodeId = trigger.NodeId,
                CorrelationId = trigger.CorrelationId,
                Payload = new
                {
                    SnapshotVersion = 2,
                    Sequence = selectionSequence,
                    SelectionKey = selection.SelectionKey,
                    Stream = "topper",
                    Selection = selection,
                    Media = new
                    {
                        Topper = topper
                    },
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "projection")
                }
            });

            var cards = FindInstructionCards(roots).ToList();
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "instruction-card.snapshot",
                NodeId = trigger.NodeId,
                CorrelationId = trigger.CorrelationId,
                Payload = new
                {
                    SnapshotVersion = 2,
                    Sequence = selectionSequence,
                    SelectionKey = selection.SelectionKey,
                    Stream = "instruction-card",
                    Selection = selection,
                    Cards = cards,
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "projection")
                }
            });
            _logger?.LogInformation(
                "physical media snapshots published: trigger={Trigger}, state={State}, system={SystemId}, game={GameSlug}, elapsedMs={ElapsedMs}",
                trigger.Type,
                state,
                systemId,
                gameSlug,
                (int)perf.Elapsed.TotalMilliseconds);

            _ = GenerateGameMediaAndPublishAsync(trigger, selected, frontendSystemId, systemId, gameSlug, state, selectionSequence);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to publish physical media WebSocket snapshots.");
        }
    }

    private MarqueeMediaSnapshot BuildMarqueeMedia(IReadOnlyList<string> roots)
    {
        var dmdStill = FindFirstAsset(roots, "artwork", "marquee", "dmd.png");
        var dmdGenerated = FindFirstAsset(roots, "artwork", "marquee", "generated-system-dmd.*") ??
            FindFirstAsset(roots, "artwork", "marquee", "generated-dmd.*");
        var dmdAnimations = FindAssets(roots, "artwork", "marquee", "dmd*.gif")
            .OrderBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var generatedMarquee = FindFirstAsset(roots, "artwork", "marquee", "generated-system-marquee.*") ??
            FindFirstAsset(roots, "artwork", "marquee", "generated-marquee.*");

        return new MarqueeMediaSnapshot(
            Marquee: FindFirstAsset(roots, "artwork", "marquee", "marquee.*"),
            GeneratedMarquee: generatedMarquee,
            ScreenMarquee: FindFirstAsset(roots, "artwork", "marquee", "screenmarquee.*"),
            ScreenMarqueeSmall: FindFirstAsset(roots, "artwork", "marquee", "screenmarquee-small.*"),
            Dmd: new DmdMediaSnapshot(
                Kind: "dmd",
                Still: dmdStill,
                Generated: dmdGenerated,
                Animations: dmdAnimations),
            Topper: FindFirstAsset(roots, "artwork", "marquee", "topper.*"),
            Fanart: FindFirstAsset(roots, GameFanartSearches),
            Logo: FindFirstAsset(roots, GameLogoSearches),
            Video: FindAssets(roots, string.Empty, "video.*").FirstOrDefault());
    }

    private MarqueeMediaSnapshot BuildGameMarqueeMedia(
        IReadOnlyList<string> gameRoots,
        IReadOnlyList<string> fallbackSystemRoots)
    {
        var game = BuildMarqueeMedia(gameRoots);
        var system = BuildMarqueeMedia(fallbackSystemRoots);
        var gameDmd = (DmdMediaSnapshot)game.Dmd;
        var systemDmd = (DmdMediaSnapshot)system.Dmd;

        return new MarqueeMediaSnapshot(
            Marquee: game.Marquee ?? system.Marquee,
            GeneratedMarquee: game.GeneratedMarquee ?? system.GeneratedMarquee,
            ScreenMarquee: game.ScreenMarquee ?? system.ScreenMarquee,
            ScreenMarqueeSmall: game.ScreenMarqueeSmall ?? system.ScreenMarqueeSmall,
            Dmd: HasDmdMedia(gameDmd) ? gameDmd : systemDmd,
            Topper: game.Topper ?? system.Topper,
            Fanart: game.Fanart ?? system.Fanart,
            Logo: game.Logo ?? system.Logo,
            // Game video only: falling back to the system video would loop an
            // unrelated clip on every game of the system.
            Video: game.Video);
    }

    private static bool HasDmdMedia(DmdMediaSnapshot dmd)
    {
        return dmd.Still != null ||
            dmd.Generated != null ||
            dmd.Animations.Count > 0;
    }

    private async Task PublishScreenSnapshotAsync(
        EventEnvelope trigger,
        PhysicalMediaSelectionSnapshot selection,
        MarqueeMediaSnapshot marquee,
        Stopwatch perf,
        string phase)
    {
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "screen.snapshot",
            NodeId = trigger.NodeId,
            CorrelationId = trigger.CorrelationId,
            Payload = new
            {
                SnapshotVersion = 2,
                Sequence = selection.Sequence,
                SelectionKey = selection.SelectionKey,
                Stream = "screen",
                Selection = selection,
                Media = new
                {
                    marquee.ScreenMarquee,
                    marquee.ScreenMarqueeSmall,
                    marquee.Fanart
                },
                Latency = BuildSnapshotLatency(trigger, selection.Sequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, phase)
            }
        });
    }

    private PhysicalMediaSelectionSnapshot BuildSystemSelection(
        long sequence,
        string frontendSystemId,
        string systemId,
        string state)
        => new(
            Sequence: sequence,
            SelectionKey: BuildSelectionKey(systemId, string.Empty),
            Scope: "system",
            FrontendSystem: frontendSystemId,
            System: systemId,
            Game: string.Empty,
            GameId: string.Empty,
            GameName: string.Empty,
            GamePath: string.Empty,
            Name: string.Empty,
            Releasedate: string.Empty,
            Developer: string.Empty,
            Publisher: string.Empty,
            Marquee: string.Empty,
            Image: string.Empty,
            Fanart: string.Empty,
            State: state);

    private PhysicalMediaSelectionSnapshot BuildGameSelection(
        long sequence,
        string frontendSystemId,
        string systemId,
        string gameSlug,
        GameReference game,
        string state)
    {
        var details = game.Details;
        return new PhysicalMediaSelectionSnapshot(
            Sequence: sequence,
            SelectionKey: BuildSelectionKey(systemId, gameSlug),
            Scope: "game",
            FrontendSystem: frontendSystemId,
            System: systemId,
            Game: gameSlug,
            GameId: FirstNonEmpty(game.GameId, details?.Id, details?.Md5),
            GameName: game.GameName,
            GamePath: game.GamePath,
            Name: FirstNonEmpty(details?.Name, game.GameName),
            Releasedate: details?.Releasedate ?? string.Empty,
            Developer: details?.Developer ?? string.Empty,
            Publisher: details?.Publisher ?? string.Empty,
            Marquee: details?.Marquee ?? string.Empty,
            Image: details?.Image ?? string.Empty,
            Fanart: details?.Fanart ?? string.Empty,
            State: state);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private async Task GenerateSystemMediaAndPublishAsync(
        EventEnvelope trigger,
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        long selectionSequence)
    {
        var perf = Stopwatch.StartNew();
        try
        {
            if (IsStaleSelectionSequence(selectionSequence))
            {
                return;
            }

            var roots = ResolveSystemRoots(systemId).ToList();
            await EnsureSystemLogoCachedAsync(frontendSystemId, systemId, selectedSystem);
            await EnsureSystemMarqueeGeneratedAsync(frontendSystemId, systemId, selectedSystem, roots);
            await EnsureSystemDmdGeneratedAsync(frontendSystemId, systemId, selectedSystem, roots);

            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale generated system media update skipped: sequence={Sequence}, system={SystemId}",
                    selectionSequence,
                    systemId);
                return;
            }

            var selection = BuildSystemSelection(selectionSequence, frontendSystemId, systemId, "system-selected");
            var marquee = BuildSystemMarqueeMedia(frontendSystemId, systemId, selectedSystem, roots);

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "marquee.snapshot.updated",
                NodeId = trigger.NodeId,
                CorrelationId = trigger.CorrelationId,
                Payload = new
                {
                    SnapshotVersion = 2,
                    Sequence = selectionSequence,
                    SelectionKey = selection.SelectionKey,
                    Stream = "marquee",
                    Selection = selection,
                    Media = marquee,
                    Generation = ResolveSystemGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "background-generation")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "background-generation");

            _logger?.LogInformation(
                "marquee system background generation completed: system={SystemId}, elapsedMs={ElapsedMs}",
                systemId,
                (int)perf.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to publish generated system marquee update for system={SystemId}.", systemId);
        }
    }

    private async Task GenerateGameMediaAndPublishAsync(
        EventEnvelope trigger,
        GameReference selected,
        string frontendSystemId,
        string systemId,
        string gameSlug,
        string state,
        long selectionSequence)
    {
        var perf = Stopwatch.StartNew();
        try
        {
            if (IsStaleSelectionSequence(selectionSequence))
            {
                return;
            }

            var roots = ResolveGameRoots(systemId, gameSlug).ToList();
            var fallbackSystemRoots = ResolveSystemRoots(systemId).ToList();
            await EnsureGameDmdGeneratedAsync(systemId, gameSlug, roots);

            if (IsStaleSelectionSequence(selectionSequence))
            {
                _logger?.LogDebug(
                    "stale generated game media update skipped: state={State}, sequence={Sequence}, system={SystemId}, game={GameSlug}",
                    state,
                    selectionSequence,
                    systemId,
                    gameSlug);
                return;
            }

            var selection = BuildGameSelection(selectionSequence, frontendSystemId, systemId, gameSlug, selected, state);
            var marquee = BuildGameMarqueeMedia(roots, fallbackSystemRoots);

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "marquee.snapshot.updated",
                NodeId = trigger.NodeId,
                CorrelationId = trigger.CorrelationId,
                Payload = new
                {
                    SnapshotVersion = 2,
                    Sequence = selectionSequence,
                    SelectionKey = selection.SelectionKey,
                    Stream = "marquee",
                    Selection = selection,
                    Media = marquee,
                    Generation = ResolveGameGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "background-generation")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "background-generation");

            _logger?.LogInformation(
                "marquee game background generation completed: state={State}, system={SystemId}, game={GameSlug}, elapsedMs={ElapsedMs}",
                state,
                systemId,
                gameSlug,
                (int)perf.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to publish generated game marquee update for system={SystemId}, game={GameSlug}.", systemId, gameSlug);
        }
    }

    private object ResolveSystemGenerationState(IReadOnlyList<string> roots)
    {
        return new
        {
            Marquee = ResolveGenerationState(
                ResolveProfile(_runtimeOptions.GetMarqueeManagerAutogenProfile()) != null &&
                    _runtimeOptions.IsRemoteMarqueeScrapingEnabled(),
                FindFirstAsset(roots, "artwork", "marquee", "marquee.*") != null,
                FindFirstAsset(roots, "artwork", "marquee", "generated-system-marquee.*") != null),
            Dmd = ResolveGenerationState(
                ResolveDmdProfile(_runtimeOptions.GetMarqueeManagerDmdAutogenProfile()) != null,
                FindFirstAsset(roots, "artwork", "marquee", "dmd.png") != null,
                FindFirstAsset(roots, "artwork", "marquee", "generated-system-dmd.*") != null)
        };
    }

    private object ResolveGameGenerationState(IReadOnlyList<string> roots)
    {
        return new
        {
            Marquee = ResolveGenerationState(
                ResolveProfile(_runtimeOptions.GetMarqueeManagerAutogenProfile()) != null &&
                    _runtimeOptions.IsRemoteMarqueeScrapingEnabled(),
                FindFirstAsset(roots, "artwork", "marquee", "marquee.*") != null,
                FindFirstAsset(roots, "artwork", "marquee", "generated-marquee.*") != null),
            Dmd = ResolveGenerationState(
                ResolveDmdProfile(_runtimeOptions.GetMarqueeManagerDmdAutogenProfile()) != null,
                FindFirstAsset(roots, "artwork", "marquee", "dmd.png") != null,
                FindFirstAsset(roots, "artwork", "marquee", "generated-dmd.*") != null)
        };
    }

    private static string ResolveGenerationState(bool enabled, bool sourcePresent, bool generatedPresent)
    {
        if (sourcePresent)
        {
            return "source-present";
        }

        if (generatedPresent)
        {
            return "generated-present";
        }

        return enabled ? "pending" : "disabled";
    }

    private MarqueeMediaSnapshot BuildSystemMarqueeMedia(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        IReadOnlyList<string> roots)
    {
        var media = BuildMarqueeMedia(roots);
        var themeRoots = ResolveThemeRoots(frontendSystemId, systemId, selectedSystem).ToList();
        var fanart = ResolveSystemFanartAsset(frontendSystemId, systemId, selectedSystem, roots, themeRoots);
        var logo = ResolveSystemLogoAsset(frontendSystemId, systemId, selectedSystem, roots, themeRoots);

        return new MarqueeMediaSnapshot(
            media.Marquee,
            media.GeneratedMarquee,
            media.ScreenMarquee,
            media.ScreenMarqueeSmall,
            media.Dmd,
            media.Topper,
            fanart,
            logo,
            media.Video);
    }

    private async Task EnsureSystemMarqueeGeneratedAsync(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        var profile = ResolveProfile(_runtimeOptions.GetMarqueeManagerAutogenProfile());
        if (profile == null || !_runtimeOptions.IsRemoteMarqueeScrapingEnabled())
        {
            return;
        }

        if (FindFirstAsset(roots, "artwork", "marquee", "marquee.*") != null)
        {
            return;
        }

        var themeRoots = ResolveThemeRoots(frontendSystemId, systemId, selectedSystem).ToList();
        var fanartPath = ResolveSystemFanartPath(frontendSystemId, systemId, selectedSystem, roots, themeRoots);
        var useThemeBackground = _runtimeOptions.ShouldUseThemeBackgroundForSystemMarquee() &&
            !string.IsNullOrWhiteSpace(fanartPath) &&
            File.Exists(fanartPath);

        var logoPath = await EnsureSystemLogoCachedAsync(frontendSystemId, systemId, selectedSystem, cancellationToken) ??
            ResolveSystemLogoPath(frontendSystemId, systemId, selectedSystem, roots, themeRoots);
        var deleteLogoPath = false;
        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
        {
            logoPath = await DownloadEsSystemLogoAsync(frontendSystemId, selectedSystem, cancellationToken);
            deleteLogoPath = !string.IsNullOrWhiteSpace(logoPath);
        }

        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
        {
            return;
        }

        var convertPath = Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", "convert.exe");
        if (!File.Exists(convertPath))
        {
            return;
        }

        var destinationDirectory = Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "artwork", "marquee");
        Directory.CreateDirectory(destinationDirectory);
        var outputPath = Path.Combine(destinationDirectory, "generated-system-marquee.png");
        var tempDirectory = Path.Combine(RetroBatPaths.RuntimeTempRoot, "marquee-autogen");
        Directory.CreateDirectory(tempDirectory);
        var tempBasePath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-system-base.png");
        var tempGradientPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-system-gradient.png");

        try
        {
            await CreateSystemMarqueeBaseAsync(
                convertPath,
                fanartPath,
                useThemeBackground,
                profile.Width,
                profile.Height,
                tempBasePath,
                cancellationToken);

            var finalBasePath = tempBasePath;
            var gradientPath = Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", "gradient_black.png");
            if (File.Exists(gradientPath))
            {
                await RunConvertAsync(
                    convertPath,
                    [
                        tempBasePath,
                        gradientPath,
                        "-antialias",
                        "-filter", "Lanczos",
                        "-resize", $"{profile.Width}x{profile.Height}!",
                        "-gravity", "Center",
                        "-composite",
                        "-colorspace", "sRGB",
                        "-type", "TrueColorAlpha",
                        Png32(tempGradientPath)
                    ],
                    cancellationToken);
                finalBasePath = tempGradientPath;
            }

            var logoMaxWidth = (int)Math.Round(profile.Width * 0.78);
            var logoMaxHeight = (int)Math.Round(profile.Height * 0.92);
            await RunConvertAsync(
                convertPath,
                [
                    finalBasePath,
                    "(",
                    logoPath,
                    "-auto-orient",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    "-antialias",
                    "-filter", "Lanczos",
                    "-resize", $"{logoMaxWidth}x{logoMaxHeight}",
                    ")",
                    "-gravity", "Center",
                    "-composite",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    Png32(outputPath)
                ],
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Unable to generate system marquee for system={SystemId}.", systemId);
        }
        finally
        {
            TryDelete(tempBasePath);
            TryDelete(tempGradientPath);
            if (deleteLogoPath)
            {
                TryDelete(logoPath);
            }
        }
    }

    private async Task EnsureSystemDmdGeneratedAsync(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        var profile = ResolveDmdProfile(_runtimeOptions.GetMarqueeManagerDmdAutogenProfile());
        if (profile == null)
        {
            return;
        }

        if (FindFirstAsset(roots, "artwork", "marquee", "dmd.png") != null)
        {
            return;
        }

        var themeRoots = ResolveThemeRoots(frontendSystemId, systemId, selectedSystem).ToList();
        var fanartPath = ResolveSystemFanartPath(frontendSystemId, systemId, selectedSystem, roots, themeRoots);
        var useThemeBackground = _runtimeOptions.ShouldUseThemeBackgroundForSystemMarquee() &&
            !string.IsNullOrWhiteSpace(fanartPath) &&
            File.Exists(fanartPath);

        var logoPath = await EnsureSystemLogoCachedAsync(frontendSystemId, systemId, selectedSystem, cancellationToken) ??
            ResolveSystemLogoPath(frontendSystemId, systemId, selectedSystem, roots, themeRoots);
        var deleteLogoPath = false;
        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
        {
            logoPath = await DownloadEsSystemLogoAsync(frontendSystemId, selectedSystem, cancellationToken);
            deleteLogoPath = !string.IsNullOrWhiteSpace(logoPath);
        }

        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
        {
            return;
        }

        var convertPath = Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", "convert.exe");
        if (!File.Exists(convertPath))
        {
            return;
        }

        var destinationDirectory = Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "artwork", "marquee");
        Directory.CreateDirectory(destinationDirectory);
        var outputPath = Path.Combine(destinationDirectory, "generated-system-dmd.png");
        var tempDirectory = Path.Combine(RetroBatPaths.RuntimeTempRoot, "marquee-autogen");
        Directory.CreateDirectory(tempDirectory);
        var tempBasePath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-system-dmd-base.png");

        try
        {
            await CreateSystemMarqueeBaseAsync(
                convertPath,
                fanartPath,
                useThemeBackground,
                profile.Width,
                profile.Height,
                tempBasePath,
                cancellationToken);

            var logoMaxWidth = (int)Math.Round(profile.Width * 0.92);
            var logoMaxHeight = (int)Math.Round(profile.Height * 0.82);
            await RunConvertAsync(
                convertPath,
                [
                    tempBasePath,
                    "(",
                    logoPath,
                    "-auto-orient",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    "-antialias",
                    "-filter", "Lanczos",
                    "-resize", $"{logoMaxWidth}x{logoMaxHeight}",
                    ")",
                    "-gravity", "Center",
                    "-composite",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    Png32(outputPath)
                ],
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Unable to generate system DMD marquee for system={SystemId}.", systemId);
        }
        finally
        {
            TryDelete(tempBasePath);
            if (deleteLogoPath)
            {
                TryDelete(logoPath);
            }
        }
    }

    private async Task EnsureGameDmdGeneratedAsync(
        string systemId,
        string gameSlug,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        var profile = ResolveDmdProfile(_runtimeOptions.GetMarqueeManagerDmdAutogenProfile());
        if (profile == null)
        {
            return;
        }

        if (FindFirstAsset(roots, "artwork", "marquee", "dmd.png") != null)
        {
            return;
        }

        if (FindFirstAsset(roots, "artwork", "marquee", "generated-dmd.*") != null)
        {
            return;
        }

        var fanartPath = FindFirstPhysicalPath(roots, GameFanartSearches);
        var logoPath = FindFirstPhysicalPath(roots, GameLogoSearches);
        if (string.IsNullOrWhiteSpace(fanartPath) ||
            string.IsNullOrWhiteSpace(logoPath) ||
            !File.Exists(fanartPath) ||
            !File.Exists(logoPath))
        {
            return;
        }

        var convertPath = Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", "convert.exe");
        if (!File.Exists(convertPath))
        {
            return;
        }

        var destinationDirectory = Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "games", gameSlug, "artwork", "marquee");
        Directory.CreateDirectory(destinationDirectory);
        var outputPath = Path.Combine(destinationDirectory, "generated-dmd.png");
        var tempDirectory = Path.Combine(RetroBatPaths.RuntimeTempRoot, "marquee-autogen");
        Directory.CreateDirectory(tempDirectory);
        var tempBasePath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-game-dmd-base.png");

        try
        {
            await CreateSystemMarqueeBaseAsync(
                convertPath,
                fanartPath,
                useThemeBackground: true,
                profile.Width,
                profile.Height,
                tempBasePath,
                cancellationToken);

            var logoMaxWidth = (int)Math.Round(profile.Width * 0.92);
            var logoMaxHeight = (int)Math.Round(profile.Height * 0.82);
            await RunConvertAsync(
                convertPath,
                [
                    tempBasePath,
                    "(",
                    logoPath,
                    "-auto-orient",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    "-antialias",
                    "-filter", "Lanczos",
                    "-resize", $"{logoMaxWidth}x{logoMaxHeight}",
                    ")",
                    "-gravity", "Center",
                    "-composite",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    Png32(outputPath)
                ],
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Unable to generate game DMD marquee for system={SystemId}, game={GameSlug}.", systemId, gameSlug);
        }
        finally
        {
            TryDelete(tempBasePath);
        }
    }

    private static async Task CreateSystemMarqueeBaseAsync(
        string convertPath,
        string? fanartPath,
        bool useThemeBackground,
        int width,
        int height,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (useThemeBackground && !string.IsNullOrWhiteSpace(fanartPath) && File.Exists(fanartPath))
        {
            await RunConvertAsync(
                convertPath,
                [
                    fanartPath,
                    "-auto-orient",
                    "-resize", $"{width}x{height}^",
                    "-gravity", "Center",
                    "-extent", $"{width}x{height}",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    Png32(outputPath)
                ],
                cancellationToken);
            return;
        }

        await RunConvertAsync(
            convertPath,
            [
                "-size", $"{width}x{height}",
                "xc:black",
                "-colorspace", "sRGB",
                "-type", "TrueColorAlpha",
                Png32(outputPath)
            ],
            cancellationToken);
    }

    private async Task<string?> EnsureSystemLogoCachedAsync(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        CancellationToken cancellationToken = default)
    {
        var destinationDirectory = Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "ui", "wheels");
        var destinationPath = Path.Combine(destinationDirectory, "wheel.png");
        var markerPath = destinationPath + ".apiexpose-cache";

        var roots = ResolveSystemRoots(systemId).ToList();
        var themeRoots = ResolveThemeRoots(frontendSystemId, systemId, selectedSystem).ToList();
        var sourcePath = ResolveSystemLogoPath(frontendSystemId, systemId, selectedSystem, roots, themeRoots);
        var deleteSourcePath = false;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            sourcePath = await DownloadEsSystemLogoAsync(frontendSystemId, selectedSystem, cancellationToken);
            deleteSourcePath = !string.IsNullOrWhiteSpace(sourcePath);
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return File.Exists(destinationPath) ? destinationPath : null;
        }

        if (IsSystemLogoCacheCurrent(destinationPath, markerPath, sourcePath))
        {
            return destinationPath;
        }

        Directory.CreateDirectory(destinationDirectory);
        try
        {
            var convertPath = Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", "convert.exe");
            if (File.Exists(convertPath))
            {
                await RunConvertAsync(
                    convertPath,
                    BuildSystemLogoConvertArguments(sourcePath, destinationPath),
                    cancellationToken);
            }
            else if (Path.GetExtension(sourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            if (!File.Exists(destinationPath))
            {
                return null;
            }

            WriteSystemLogoCacheMarker(markerPath, sourcePath);
            return destinationPath;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Unable to cache system logo for system={SystemId}.", systemId);
            return null;
        }
        finally
        {
            if (deleteSourcePath)
            {
                TryDelete(sourcePath);
            }
        }
    }

    private static IReadOnlyList<string> BuildSystemLogoConvertArguments(string sourcePath, string destinationPath)
    {
        var outputPath = "png32:" + destinationPath;
        if (Path.GetExtension(sourcePath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "-background", "none",
                "-density", "384",
                sourcePath,
                "-alpha", "on",
                "-strip",
                "-trim",
                "+repage",
                "-colorspace", "sRGB",
                "-type", "TrueColorAlpha",
                outputPath
            ];
        }

        return
        [
            sourcePath,
            "-auto-orient",
            "-alpha", "on",
            "-background", "none",
            "-colorspace", "sRGB",
            "-type", "TrueColorAlpha",
            outputPath
        ];
    }

    private static bool IsSystemLogoCacheCurrent(string destinationPath, string markerPath, string sourcePath)
    {
        if (!File.Exists(destinationPath) || !File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var expected = BuildSystemLogoCacheMarker(sourceInfo);
            var actual = File.ReadAllText(markerPath).Trim();
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteSystemLogoCacheMarker(string markerPath, string sourcePath)
    {
        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            File.WriteAllText(markerPath, BuildSystemLogoCacheMarker(sourceInfo));
        }
        catch
        {
            // Cache marker is best effort; the PNG itself remains usable.
        }
    }

    private static string BuildSystemLogoCacheMarker(FileInfo sourceInfo)
    {
        return string.Join(
            "|",
            SystemLogoCacheVersion,
            sourceInfo.FullName,
            sourceInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceInfo.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private IEnumerable<MediaStreamAsset> FindInstructionCards(IReadOnlyList<string> roots)
    {
        return FindAssets(roots, "artwork", "ic", "ic*.*")
            .Where(asset => asset.Stem.Equals("ic", StringComparison.OrdinalIgnoreCase) ||
                asset.Stem.StartsWith("ic-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => InstructionCardOrder(asset.Stem))
            .ThenBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase);
    }

    private static int InstructionCardOrder(string stem)
    {
        if (stem.Equals("ic", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (stem.StartsWith("ic-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(stem[3..], out var index))
        {
            return Math.Max(index, 2);
        }

        return int.MaxValue;
    }

    private static MediaStreamAsset? FindFirstAsset(IReadOnlyList<string> roots, string directory1, string directory2, string pattern)
    {
        return FindAssets(roots, directory1, directory2, pattern).FirstOrDefault();
    }

    private static MediaStreamAsset? FindFirstAsset(IReadOnlyList<string> roots, params AssetSearch[] searches)
    {
        foreach (var search in searches)
        {
            var asset = FindAssets(roots, search.RelativeDirectory, search.Pattern).FirstOrDefault();
            if (asset != null)
            {
                return asset;
            }
        }

        return null;
    }

    private static IEnumerable<MediaStreamAsset> FindAssets(IReadOnlyList<string> roots, string directory1, string directory2, string pattern)
    {
        return FindAssets(roots, Path.Combine(directory1, directory2), pattern);
    }

    private static IEnumerable<MediaStreamAsset> FindAssets(IReadOnlyList<string> roots, string relativeDirectory, string pattern)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var directory = CombineRelative(root, relativeDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(path);
                if (!seen.Add(fullPath))
                {
                    continue;
                }

                yield return CreateAsset(fullPath);
            }
        }
    }

    private static string? FindFirstPhysicalPath(IReadOnlyList<string> roots, params AssetSearch[] searches)
    {
        foreach (var search in searches)
        {
            foreach (var root in roots)
            {
                var directory = CombineRelative(root, search.RelativeDirectory);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var path = Directory.EnumerateFiles(directory, search.Pattern, SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return Path.GetFullPath(path);
                }
            }
        }

        return null;
    }

    private static string CombineRelative(string root, string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return root;
        }

        var current = root;
        foreach (var segment in relativeDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = Path.Combine(current, segment);
        }

        return current;
    }

    private static MediaStreamAsset CreateAsset(string path)
    {
        var info = new FileInfo(path);
        var fullPath = Path.GetFullPath(path);
        var relative = IsUnderRoot(fullPath, RetroBatPaths.PluginRoot)
            ? Path.GetRelativePath(RetroBatPaths.PluginRoot, fullPath).Replace('\\', '/')
            : IsUnderRoot(fullPath, RetroBatPaths.RetroBatRoot)
                ? Path.GetRelativePath(RetroBatPaths.RetroBatRoot, fullPath).Replace('\\', '/')
                : Path.GetFileName(fullPath);
        var origin = relative.StartsWith("media/user/", StringComparison.OrdinalIgnoreCase)
            ? "user"
            : Path.GetFileNameWithoutExtension(path).StartsWith("generated-", StringComparison.OrdinalIgnoreCase)
                ? "generated"
                : IsUnderRoot(fullPath, RetroBatPaths.EmulationStationThemesRoot)
                    ? "emulationstation-theme"
                : "local";

        // Assets under the canonical media store are reachable over HTTP: give
        // consumers a ready /api/v1/media URL so they no longer have to resolve
        // the plugin folder on disk.
        var url = relative.StartsWith("media/", StringComparison.OrdinalIgnoreCase)
            ? "/api/v1/media/" + relative["media/".Length..]
            : string.Empty;

        return new MediaStreamAsset(
            Kind: ResolveKind(path),
            Origin: origin,
            Path: relative,
            FileName: info.Name,
            Stem: Path.GetFileNameWithoutExtension(info.Name),
            Extension: info.Extension.TrimStart('.').ToLowerInvariant(),
            Length: info.Length,
            LastWriteTimeUtc: info.LastWriteTimeUtc,
            Url: url);
    }

    private static MediaStreamAsset CreateExternalAsset(string kind, string origin, string path, string url, string extension)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = kind + "." + extension;
        }

        return new MediaStreamAsset(
            Kind: kind,
            Origin: origin,
            Path: path,
            FileName: fileName,
            Stem: Path.GetFileNameWithoutExtension(fileName),
            Extension: extension,
            Length: 0,
            LastWriteTimeUtc: DateTime.MinValue,
            Url: url);
    }

    private static string ResolveKind(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return stem switch
        {
            "marquee" => "marquee",
            "screenmarquee" => "screenmarquee",
            "screenmarquee-small" => "screenmarquee-small",
            "topper" => "topper",
            "dmd" => "dmd",
            "fanart" => "fanart",
            "wheel" => "wheel",
            "ic" => "instruction-card",
            "generated-system-dmd" or "generated-dmd" => "dmd",
            _ when stem.StartsWith("dmd", StringComparison.OrdinalIgnoreCase) => "dmd-animation",
            _ when stem.StartsWith("ic-", StringComparison.OrdinalIgnoreCase) => "instruction-card",
            _ when stem.StartsWith("generated-", StringComparison.OrdinalIgnoreCase) &&
                stem.Contains("dmd", StringComparison.OrdinalIgnoreCase) => "dmd",
            _ when stem.StartsWith("generated-", StringComparison.OrdinalIgnoreCase) => "marquee",
            _ => stem
        };
    }

    private async Task<MediaStreamAsset?> TryBuildEsSystemLogoAssetAsync(string frontendSystemId, SystemDetails? selectedSystem)
    {
        var path = ResolveEsSystemLogoPath(frontendSystemId, selectedSystem);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var response = await _esHttpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var extension = ResolveExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);
            return CreateExternalAsset("wheel", "emulationstation", path, path, extension);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private async Task<string?> DownloadEsSystemLogoAsync(string frontendSystemId, SystemDetails? selectedSystem, CancellationToken cancellationToken)
    {
        var path = ResolveEsSystemLogoPath(frontendSystemId, selectedSystem);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var response = await _esHttpClient.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var extension = ResolveExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);
            var tempDirectory = Path.Combine(RetroBatPaths.RuntimeTempRoot, "marquee-autogen");
            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-system-logo." + extension);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(tempPath);
            await stream.CopyToAsync(output, cancellationToken);
            return tempPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private static string ResolveEsSystemLogoPath(string frontendSystemId, SystemDetails? selectedSystem)
    {
        var logo = selectedSystem?.Logo?.Trim() ?? string.Empty;
        if (logo.StartsWith("/systems/", StringComparison.OrdinalIgnoreCase))
        {
            return logo;
        }

        return string.IsNullOrWhiteSpace(frontendSystemId)
            ? string.Empty
            : $"/systems/{Uri.EscapeDataString(frontendSystemId)}/logo";
    }

    private static string ResolveExtensionFromContentType(string? contentType)
    {
        return (contentType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/svg+xml" => "svg",
            _ => "png"
        };
    }

    private static MediaStreamAsset? ResolveSystemFanartAsset(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> themeRoots)
    {
        return FindFirstAsset(roots, SystemFanartSearches) ??
            FindFirstAsset(themeRoots, BuildThemeFanartSearches(frontendSystemId, systemId, selectedSystem));
    }

    private static string? ResolveSystemFanartPath(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> themeRoots)
    {
        return FindFirstPhysicalPath(roots, SystemFanartSearches) ??
            FindFirstPhysicalPath(themeRoots, BuildThemeFanartSearches(frontendSystemId, systemId, selectedSystem));
    }

    private static MediaStreamAsset? ResolveSystemLogoAsset(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> themeRoots)
    {
        return FindFirstAsset(roots, SystemLogoSearches) ??
            FindFirstAsset(themeRoots, BuildThemeLogoSearches(frontendSystemId, systemId, selectedSystem));
    }

    private static string? ResolveSystemLogoPath(
        string frontendSystemId,
        string systemId,
        SystemDetails? selectedSystem,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> themeRoots)
    {
        return FindFirstPhysicalPath(roots, SystemLogoSearches) ??
            FindFirstPhysicalPath(themeRoots, BuildThemeLogoSearches(frontendSystemId, systemId, selectedSystem));
    }

    private static AssetSearch[] BuildThemeFanartSearches(string frontendSystemId, string systemId, SystemDetails? selectedSystem)
    {
        return BuildSystemNames(frontendSystemId, systemId, selectedSystem)
            .SelectMany(name => new[]
            {
                new AssetSearch("art/background", name + ".*"),
                new AssetSearch("background", name + ".*"),
                new AssetSearch("_systemmedia/fanartsyst", name + ".*"),
                new AssetSearch("_systemmedia/background", name + ".*")
            })
            .ToArray();
    }

    private static AssetSearch[] BuildThemeLogoSearches(string frontendSystemId, string systemId, SystemDetails? selectedSystem)
    {
        return BuildSystemNames(frontendSystemId, systemId, selectedSystem)
            .SelectMany(name => new[]
            {
                new AssetSearch("_systemmedia/_logosyst/clearlogos", name + "-w.*"),
                new AssetSearch("_systemmedia/_logosyst/clearlogos", name + ".*"),
                new AssetSearch("_systemmedia/_logosyst", name + "-w.*"),
                new AssetSearch("_systemmedia/_logosyst", name + ".*"),
                new AssetSearch("art/logos", name + "-w.*"),
                new AssetSearch("art/logos", name + ".*"),
                new AssetSearch("art/logo", name + "-w.*"),
                new AssetSearch("art/logo", name + ".*"),
                new AssetSearch("art/wheels", name + "-w.*"),
                new AssetSearch("art/wheels", name + ".*"),
                new AssetSearch("logos", name + "-w.*"),
                new AssetSearch("logos", name + ".*"),
                new AssetSearch("wheels", name + "-w.*"),
                new AssetSearch("wheels", name + ".*"),
                new AssetSearch("_systemmedia/logos", name + "-w.*"),
                new AssetSearch("_systemmedia/logos", name + ".*"),
                new AssetSearch("_systemmedia/wheels", name + "-w.*"),
                new AssetSearch("_systemmedia/wheels", name + ".*")
            })
            .ToArray();
    }

    private static IEnumerable<string> BuildSystemNames(string frontendSystemId, string systemId, SystemDetails? selectedSystem)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { selectedSystem?.Theme, frontendSystemId, systemId, selectedSystem?.Name })
        {
            var normalized = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string> ResolveThemeRoots(string frontendSystemId, string systemId, SystemDetails? selectedSystem)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var themeRoot in ResolveThemeSetRoots())
        {
            foreach (var systemFolder in new[] { selectedSystem?.Theme, frontendSystemId, systemId, string.Empty })
            {
                var root = string.IsNullOrWhiteSpace(systemFolder)
                    ? themeRoot
                    : Path.Combine(themeRoot, systemFolder.Trim());
                if (Directory.Exists(root) && seen.Add(Path.GetFullPath(root)))
                {
                    yield return root;
                }
            }
        }
    }

    private static IEnumerable<string> ResolveThemeSetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var themeSet = ReadEsThemeSet();
        if (!string.IsNullOrWhiteSpace(themeSet))
        {
            var activeRoot = Path.Combine(RetroBatPaths.EmulationStationThemesRoot, themeSet);
            if (Directory.Exists(activeRoot) && seen.Add(Path.GetFullPath(activeRoot)))
            {
                yield return activeRoot;
            }
        }

        var carbonRoot = Path.Combine(RetroBatPaths.EmulationStationThemesRoot, "es-theme-carbon");
        if (Directory.Exists(carbonRoot) && seen.Add(Path.GetFullPath(carbonRoot)))
        {
            yield return carbonRoot;
        }
    }

    private static string ReadEsThemeSet()
    {
        try
        {
            if (!File.Exists(RetroBatPaths.EmulationStationSettingsPath))
            {
                return string.Empty;
            }

            var document = XDocument.Load(RetroBatPaths.EmulationStationSettingsPath);
            return document.Descendants("string")
                .FirstOrDefault(element => string.Equals((string?)element.Attribute("name"), "ThemeSet", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")
                ?.Value
                ?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static MarqueeAutogenProfile? ResolveProfile(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "xl-1920x360" => new MarqueeAutogenProfile(1920, 360),
            "l-1280x400" => new MarqueeAutogenProfile(1280, 400),
            "m-920x360" => new MarqueeAutogenProfile(920, 360),
            _ => null
        };
    }

    private static MarqueeAutogenProfile? ResolveDmdProfile(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "64x32" => new MarqueeAutogenProfile(64, 32),
            "128x32" => new MarqueeAutogenProfile(128, 32),
            "128x64" => new MarqueeAutogenProfile(128, 64),
            "256x64" => new MarqueeAutogenProfile(256, 64),
            _ => null
        };
    }

    private static async Task RunConvertAsync(string convertPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = convertPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("ImageMagick convert.exe could not be started.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        var stdout = await stdoutTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ImageMagick convert.exe failed with exit code {process.ExitCode}: {stderr}{stdout}");
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup best effort only.
        }
    }

    private static string Png32(string path) => "png32:" + path;

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ResolveGameRoots(string systemId, string gameSlug)
    {
        yield return Path.Combine(RetroBatPaths.MediaUserSystemsRoot, systemId, "games", gameSlug);
        yield return Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId, "games", gameSlug);
    }

    private static IEnumerable<string> ResolveSystemRoots(string systemId)
    {
        yield return Path.Combine(RetroBatPaths.MediaUserSystemsRoot, systemId);
        yield return Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId);
    }

    private IEnumerable<string> BuildAliasKeys(GameReference game, string requestedSlug)
    {
        yield return "slug:" + requestedSlug;
        yield return "name:" + _gameNameNormalizer.NormalizeGameSlug(game.GameName, game.GamePath);
        yield return "rom:" + Path.GetFileNameWithoutExtension(game.GamePath ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(game.GamePath))
        {
            yield return "path:" + game.GamePath.Trim().ToLowerInvariant();
        }
    }

    private long MarkLatestSelectionSequence(EventEnvelope envelope)
    {
        var sequence = ReadLongProperty(envelope.Payload, "Sequence");
        lock (_latestSelectionLock)
        {
            if (sequence <= _latestSelectionSequence)
            {
                sequence = _latestSelectionSequence + 1;
            }

            _latestSelectionSequence = sequence;
            return sequence;
        }
    }

    private bool IsStaleSelectionSequence(long sequence)
    {
        if (sequence <= 0)
        {
            return false;
        }

        lock (_latestSelectionLock)
        {
            return sequence < _latestSelectionSequence;
        }
    }

    private static async Task DelayLatestSelectionSnapshotAsync(long selectionSequence)
    {
        if (selectionSequence <= 0)
        {
            return;
        }

        await Task.Delay(SelectionSnapshotDebounceMs);
    }

    private static string BuildSelectionKey(string systemId, string gameSlug)
    {
        var normalizedSystem = (systemId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGame = (gameSlug ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedGame)
            ? $"{normalizedSystem}|"
            : $"{normalizedSystem}|{normalizedGame}";
    }

    private static DateTime ResolveReceivedAtUtc(EventEnvelope trigger)
    {
        return ReadDateTimeProperty(trigger.Payload, "ReceivedAtUtc") ?? NormalizeUtc(trigger.Ts);
    }

    private static object BuildSnapshotLatency(
        EventEnvelope trigger,
        long sequence,
        string selectionKey,
        DateTime receivedAtUtc,
        DateTime publishedAtUtc,
        Stopwatch perf,
        string source)
    {
        return new
        {
            Source = source,
            Trigger = trigger.Type,
            Sequence = sequence,
            SelectionKey = selectionKey,
            ReceivedAtUtc = receivedAtUtc,
            PublishedAtUtc = publishedAtUtc,
            AgeMs = Math.Max(0, (int)(publishedAtUtc - receivedAtUtc).TotalMilliseconds),
            ElapsedMs = (int)perf.Elapsed.TotalMilliseconds
        };
    }

    private static long ReadLongProperty(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            null => 0,
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when long.TryParse(element.GetString(), out var number) => number,
            string text when long.TryParse(text, out var number) => number,
            _ => 0
        };
    }

    private static DateTime? ReadDateTimeProperty(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            DateTime date => NormalizeUtc(date),
            DateTimeOffset date => date.UtcDateTime,
            JsonElement { ValueKind: JsonValueKind.String } element when DateTime.TryParse(element.GetString(), out var date) => NormalizeUtc(date),
            string text when DateTime.TryParse(text, out var date) => NormalizeUtc(date),
            _ => null
        };
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static object? ReadProperty(object? source, string propertyName)
    {
        if (source == null)
        {
            return null;
        }

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) ||
                    property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        return source
            .GetType()
            .GetProperties()
            .FirstOrDefault(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            ?.GetValue(source);
    }

    private static readonly AssetSearch[] SystemFanartSearches =
    [
        new("artwork", "fanart.*"),
        new("artwork", "background.*"),
        new("artwork/fanart", "fanart.*"),
        new(string.Empty, "fanart.*"),
        new(string.Empty, "background.*")
    ];

    private static readonly AssetSearch[] SystemLogoSearches =
    [
        new("ui/wheels", "wheel.*"),
        new("ui/logos", "logo.*"),
        new("ui", "wheel.*"),
        new("ui", "logo.*"),
        new("artwork", "logo.*"),
        new(string.Empty, "wheel.*"),
        new(string.Empty, "logo.*")
    ];

    private static readonly AssetSearch[] GameFanartSearches =
    [
        new("artwork", "fanart.*"),
        new("artwork/fanart", "fanart.*")
    ];

    private static readonly AssetSearch[] GameLogoSearches =
    [
        new("ui/wheels", "wheel.*")
    ];

    private sealed record AssetSearch(string RelativeDirectory, string Pattern);
    private sealed record MarqueeAutogenProfile(int Width, int Height);
    private sealed record DmdMediaSnapshot(
        string Kind,
        MediaStreamAsset? Still,
        MediaStreamAsset? Generated,
        IReadOnlyList<MediaStreamAsset> Animations);

    private sealed record MarqueeMediaSnapshot(
        MediaStreamAsset? Marquee,
        MediaStreamAsset? GeneratedMarquee,
        MediaStreamAsset? ScreenMarquee,
        MediaStreamAsset? ScreenMarqueeSmall,
        object Dmd,
        MediaStreamAsset? Topper,
        MediaStreamAsset? Fanart,
        MediaStreamAsset? Logo,
        MediaStreamAsset? Video);

    private sealed record PhysicalMediaSelectionSnapshot(
        long Sequence,
        string SelectionKey,
        string Scope,
        string FrontendSystem,
        string System,
        string Game,
        string GameId,
        string GameName,
        string GamePath,
        string Name,
        string Releasedate,
        string Developer,
        string Publisher,
        string Marquee,
        string Image,
        string Fanart,
        string State);

    private sealed record MediaStreamAsset(
        string Kind,
        string Origin,
        string Path,
        string FileName,
        string Stem,
        string Extension,
        long Length,
        DateTime LastWriteTimeUtc,
        string Url);
}
