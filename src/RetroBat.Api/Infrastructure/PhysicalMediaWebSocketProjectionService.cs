using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Media;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class PhysicalMediaWebSocketProjectionService : IHostedService, IDisposable
{
    // v5: SVG filter references are stripped before rasterisation (librsvg drops filtered
    // elements - the gx4000 "GX 4000" lettering vanished), and arcade sub-systems now key
    // their wheel on the frontend id. Bumping the version regenerates every cached logo so
    // both land without a manual purge.
    private const string SystemLogoCacheVersion = "system-logo-cache-v5-png32-srgb";
    private readonly ILocalizedTextStore _localizedText;
    private readonly IEsSettingsStore _esSettings;
    private const int SelectionSnapshotDebounceMs = 35;

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly IMediaAliasStore _mediaAliasStore;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly GamelistMediaCatalogReader _gamelistReader;
    private readonly MediaResolver _mediaResolver;
    private readonly ILogger<PhysicalMediaWebSocketProjectionService>? _logger;
    private readonly HttpClient _esHttpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:1234"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    private readonly object _latestSelectionLock = new();
    private long _latestSelectionSequence;
    private IDisposable? _subscription;
    private IDisposable? _optionsChange;

    /// <summary>HP3 - cross-publication directory-listing cache. Static because the enumeration
    /// helpers it backs are static; a single hosted-service instance configures it from options.
    /// Initialised <see cref="MediaDirectoryListingCache.Disabled"/> and configured by the
    /// constructor from options (on by default since 1.6.4); DirectoryCacheEnabled=false restores
    /// the byte-for-byte HP1/HP2 path.</summary>
    internal static readonly MediaDirectoryListingCache DirectoryCache = new();

    /// <summary>HP5 - whether CreateAsset stamps PathRoot on each asset. Static for the same
    /// reason as DirectoryCache (CreateAsset is static); off until MediaDiscovery.EmitPathRoot.</summary>
    private static volatile bool _emitPathRoot;

    /// <summary>LOT 7 - whether the game snapshots FILL missing kinds from the user gamelist (roms/
    /// media referenced but not in the canonical store). Off until MediaDiscovery.GamelistMediaEnabled;
    /// additive - it only ADDS a kind the canonical table lacks, never overrides a canonical asset.</summary>
    private static volatile bool _gamelistMediaEnabled;

    public PhysicalMediaWebSocketProjectionService(
        IEventBus eventBus,
        ApiContext context,
        SystemIdNormalizer systemIdNormalizer,
        GameNameNormalizer gameNameNormalizer,
        IMediaAliasStore mediaAliasStore,
        ApiExposeRuntimeOptionsService runtimeOptions,
        GamelistMediaCatalogReader gamelistReader,
        MediaResolver mediaResolver,
        ILocalizedTextStore localizedText,
        IEsSettingsStore esSettings,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<PhysicalMediaWebSocketProjectionService>? logger = null)
    {
        _localizedText = localizedText;
        _esSettings = esSettings;
        _eventBus = eventBus;
        _context = context;
        _systemIdNormalizer = systemIdNormalizer;
        _gameNameNormalizer = gameNameNormalizer;
        _mediaAliasStore = mediaAliasStore;
        _runtimeOptions = runtimeOptions;
        _gamelistReader = gamelistReader;
        _mediaResolver = mediaResolver;
        _logger = logger;

        ApplyDiscoveryOptions(options.CurrentValue);
        _optionsChange = options.OnChange(ApplyDiscoveryOptions);
    }

    private static void ApplyDiscoveryOptions(ApiExposeOptions options)
    {
        var media = options.MediaDiscovery ?? new ApiExposeOptions.MediaDiscoveryOptions();
        DirectoryCache.Configure(new MediaDirectoryListingCache.Config(
            Enabled: media.DirectoryCacheEnabled,
            SafetyTtlSeconds: media.SafetyTtlSeconds,
            NegativeTtlSeconds: media.NegativeTtlSeconds,
            MaxDirectories: media.MaxCachedDirectories));
        _emitPathRoot = media.EmitPathRoot;
        _gamelistMediaEnabled = media.GamelistMediaEnabled;
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
        _optionsChange?.Dispose();
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
            using var inventoryScope = BeginInventoryScope();
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
                    // Additive: the legacy Media fields keep their shape and their
                    // fallbacks, Assets says exactly what THIS entry owns.
                    // The gabarit's logo layer resolves the "wheel" kind from the PLAIN
                    // asset table (its layer key is "wheel", not "systemwheel"), so the
                    // per-frontend pin must sit on BOTH tables - otherwise fbneo's gabarit
                    // reads the shared arcade wheel and overwrites the correct logo.
                    Assets = BuildSystemAssetTable(frontendSystemId, systemId, roots, DisplayableMediaKinds),
                    SystemAssets = BuildSystemAssetTable(frontendSystemId, systemId, roots, DisplayableMediaKinds),
                    Generation = ResolveSystemGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "projection")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "projection", roots);

            // A topper stayed dark for the whole system navigation: this snapshot was
            // published on the game path only, so a consumer subscribed to /ws/topper
            // alone had nothing until a game was picked. The marquee always spoke at
            // both scopes; the topper now does too.
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
                        marquee.Topper
                    },
                    Assets = BuildSystemAssetTable(frontendSystemId, systemId, roots, DisplayableMediaKinds),
                    SystemAssets = BuildSystemAssetTable(frontendSystemId, systemId, roots, DisplayableMediaKinds),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "projection")
                }
            });

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
            using var inventoryScope = BeginInventoryScope();
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

            var marquee = BuildGameMarqueeMedia(frontendSystemId, systemId, roots, fallbackSystemRoots);

            // LOT 7 - one merged game asset table for all three surfaces: canonical store, plus a
            // fill from the user gamelist when the flag is on (off by default = the old table).
            var gameAssets = BuildGameAssets(systemId, gameSlug, selected.GamePath, roots);

            // Built once for the whole batch, and BEFORE the first publish: a marquee
            // prints the game's name, its genre, its description. Computing it after
            // meant the marquee snapshot never carried it, and a template that asked for
            // {desc} drew the tag itself.
            var text = await BuildTextBlockAsync(systemId, gameSlug, selected.Details, roots);

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
                    // Additive, and SCOPED. Media.Fanart silently falls back to the
                    // system's (`game.Fanart ?? system.Fanart`), so a consumer holding a
                    // path cannot tell whose art it is. These two tables can: each holds
                    // only what that scope really owns, and a key is absent when the file
                    // is. The legacy fields are untouched.
                    Assets = gameAssets,
                    SystemAssets = BuildSystemAssetTable(frontendSystemId, systemId, fallbackSystemRoots, DisplayableMediaKinds),
                    Text = text,
                    Generation = ResolveGameGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "projection")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "projection", roots, fallbackSystemRoots);

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
                    // A topper shows more than a topper: the game's identity and the
                    // printed matter that used to sit above the cabinet. Self-sufficient
                    // stream - no second subscription to dress this surface.
                    Assets = gameAssets,
                    SystemAssets = BuildSystemAssetTable(frontendSystemId, systemId, fallbackSystemRoots, DisplayableMediaKinds),
                    Text = text,
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
                    // What this game HAS to show, without a consumer having to walk the
                    // card list to find out. Empty role = the cards at the root, kept
                    // out of the list because it is not a name.
                    Roles = cards.Select(card => card.Role)
                        .Where(role => role.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    Text = text,
                    // Composing a card takes more than the cards: the logo, the box, a
                    // fanart to sit behind them, and what the buttons DO.
                    Assets = gameAssets,
                    SystemAssets = BuildAssetTable(fallbackSystemRoots, DisplayableMediaKinds),
                    // keyed by ROM NAME, not by the media slug: dynpanels are named after
                    // the rom file ("1943.json"), and a media folder is a slug that often
                    // differs ("sonic_the_hedgehog" for "Sonic The Hedgehog (USA)")
                    Controls = BuildControlsBlock(frontendSystemId, Path.GetFileNameWithoutExtension(selected.GamePath ?? string.Empty)),
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

            if (DirectoryCache.Current.Enabled)
            {
                // §14 observability, at Debug so the on-by-default cache adds no per-navigation
                // noise in production: a cabinet that wants a look raises the level and sees hits
                // climb while enum stays flat - the "0 EnumerateFiles on revisit" criterion.
                var cache = DirectoryCache.Metrics();
                _logger?.LogDebug(
                    "media discovery cache: hits={Hits}, misses={Misses}, enum={Enum}, invalidations={Inval}, entries={Entries}",
                    cache.Hits, cache.Misses, cache.Enumerations, cache.Invalidations, cache.Entries);
            }

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
        string frontendSystemId,
        string systemId,
        IReadOnlyList<string> gameRoots,
        IReadOnlyList<string> fallbackSystemRoots)
    {
        var game = BuildMarqueeMedia(gameRoots);
        // The system fallback must stay FRONTEND-SCOPED: the collapsed "arcade" systemId holds
        // generated-system-marquee.<frontend>.png (and wheel.<frontend>.png) for mame/fbneo/fba/…,
        // and the plain glob in BuildMarqueeMedia returns the alphabetically-first sibling (fbneo).
        // Pin the same frontend-scoped assets the system snapshot uses, so a mame/arcade game with no
        // marquee of its own no longer falls back to fbneo's system marquee/logo.
        var system = BuildMarqueeMedia(fallbackSystemRoots) with
        {
            GeneratedMarquee = ResolveSystemGeneratedMarqueeAsset(frontendSystemId, systemId, fallbackSystemRoots),
            Logo = ResolveSystemLogoAsset(frontendSystemId, systemId, fallbackSystemRoots),
            Fanart = ResolveSystemFanartAsset(frontendSystemId, systemId, fallbackSystemRoots),
        };
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

    /// <param name="roots">Roots of the entry being shown. A screen serves screen-shaped
    /// media of its own, so it carries its own asset table rather than borrowing the
    /// marquee's: a consumer subscribed to this stream alone must not need a second one.</param>
    /// <param name="systemRoots">Null in system scope - there is no wider scope to
    /// distinguish it from.</param>
    /// <summary>
    /// The entry's TEXT, in the language EmulationStation is set to. Surfaces that print
    /// something about a game - a topper, an instruction card - needed the description,
    /// the genre, the number of players, and had no way to get them.
    ///
    /// Only text is published. The metadata bundles also carry media pointers
    /// (boxart, wheel, mix...) written when they were scraped: they are stale by
    /// design and would compete with the asset tables, which are resolved fresh.
    /// Fields the bundle does not carry are completed from the in-memory details.
    /// Null when nothing is known - an empty block would say "nothing to print" where
    /// the truth is "we never looked".
    /// </summary>
    /// <summary>
    /// What the buttons DO, compact enough to travel on every selection. A composed
    /// instruction card needs the meaning and the colour of each control; the full
    /// geometry, the MAME masks and the export plan stay on /ws/panel, which is built
    /// for that. A consumer subscribed to this stream alone can draw a panel without a
    /// second subscription.
    ///
    /// Source: resources/dynpanels, game file then system file - the same precedence
    /// the panel export uses. Null when the entry has no panel: an empty block would
    /// claim the buttons mean nothing.
    /// </summary>
    private object? BuildControlsBlock(string systemId, string rom)
    {
        JsonElement root;
        foreach (var candidate in new[]
                 {
                     Path.Combine(RetroBatPaths.DynPanelsRoot, "games", rom + ".json"),
                     Path.Combine(RetroBatPaths.DynPanelsRoot, "systems", systemId.ToLowerInvariant() + ".json")
                 })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                root = document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Dynpanel unreadable: {Path}", candidate);
                continue;
            }

            var buttons = new List<object>();
            var devices = new List<object>();
            var system = new List<object>();

            if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
            {
                foreach (var player in players.EnumerateObject())
                {
                    if (player.Value.ValueKind != JsonValueKind.Object) continue;

                    if (player.Value.TryGetProperty("buttons", out var playerButtons)
                        && playerButtons.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var button in playerButtons.EnumerateObject())
                        {
                            var function = Text(button.Value, "function");
                            if (string.IsNullOrWhiteSpace(function)) continue; // unused button: says nothing
                            buttons.Add(new
                            {
                                Player = player.Name,
                                Id = button.Name,
                                Function = function,
                                Color = Text(button.Value, "color"),
                                Output = Text(button.Value, "output")
                            });
                        }
                    }

                    if (player.Value.TryGetProperty("devices", out var playerDevices)
                        && playerDevices.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var device in playerDevices.EnumerateArray())
                        {
                            devices.Add(new
                            {
                                Player = player.Name,
                                Label = Text(device, "label"),
                                Type = Text(device, "type"),
                                Color = Text(device, "color")
                            });
                        }
                    }

                    if (player.Value.TryGetProperty("system_inputs", out var systemInputs)
                        && systemInputs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var input in systemInputs.EnumerateObject())
                        {
                            system.Add(new
                            {
                                Player = player.Name,
                                Id = input.Name,
                                Label = Text(input.Value, "label"),
                                Color = Text(input.Value, "color")
                            });
                        }
                    }
                }
            }

            if (buttons.Count == 0 && devices.Count == 0) return null;

            root.TryGetProperty("meta", out var meta);
            return new
            {
                Source = Path.GetFileName(candidate),
                Scope = Text(root, "scope"),
                Players = Number(meta, "players"),
                Alternating = Number(meta, "alternating") == 1,
                Notes = Text(meta, "misc_details"),
                Buttons = buttons,
                Devices = devices,
                System = system
            };
        }

        return null;

        static string Text(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        static int? Number(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var parsed)
                ? parsed
                : null;
    }

    private async Task<object?> BuildTextBlockAsync(
        string systemId,
        string gameSlug,
        GameDetails? details,
        IReadOnlyList<string> roots)
    {
        var requested = ResolveEsLanguage();
        LocalizedTextBundle? bundle = null;
        try
        {
            bundle = await _localizedText.LoadPreferredBundleAsync(systemId, gameSlug, requested);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Localized text bundle unavailable for {System}/{Game}", systemId, gameSlug);
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bundle?.Fields is { Count: > 0 })
        {
            foreach (var (key, value) in bundle.Fields)
            {
                if (TextFieldKeys.Contains(key) && !string.IsNullOrWhiteSpace(value))
                {
                    fields[key] = value.Trim();
                }
            }
        }

        void Complete(string key, string? value)
        {
            if (!fields.ContainsKey(key) && !string.IsNullOrWhiteSpace(value)) fields[key] = value!.Trim();
        }

        Complete("name", details?.Name);
        Complete("desc", details?.Desc);
        Complete("developer", details?.Developer);
        Complete("publisher", details?.Publisher);
        Complete("genre", details?.Genre);
        Complete("genres", details?.Genres);
        Complete("players", details?.Players);
        Complete("rating", details?.Rating);
        Complete("releasedate", details?.Releasedate);
        Complete("region", details?.Region);
        Complete("family", details?.Family);
        Complete("arcadesystemname", details?.Arcadesystemname);
        Complete("emulator", details?.Emulator);
        Complete("md5", details?.Md5);

        if (fields.Count == 0) return null;

        return new
        {
            Language = string.IsNullOrWhiteSpace(bundle?.Language) ? requested : bundle!.Language,
            Requested = requested,
            Available = ListTextLanguages(roots),
            Fields = fields
        };
    }

    /// <summary>Which languages this entry has been written in - so a consumer that
    /// wants another one knows it exists before asking for it.</summary>
    private static IReadOnlyList<string> ListTextLanguages(IReadOnlyList<string> roots)
    {
        var languages = new List<string>();
        foreach (var root in roots)
        {
            var textRoot = Path.Combine(root, "texts");
            if (!Directory.Exists(textRoot)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(textRoot, "metadata-*.json", SearchOption.TopDirectoryOnly))
                {
                    var language = Path.GetFileNameWithoutExtension(file)["metadata-".Length..];
                    // "text" and "langue" are working files of the scraper, not languages
                    if (language is "text" or "langue" || languages.Contains(language, StringComparer.OrdinalIgnoreCase)) continue;
                    languages.Add(language);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable: the next root may still answer
            }
        }

        languages.Sort(StringComparer.OrdinalIgnoreCase);
        return languages;
    }

    /// <summary>The language EmulationStation is set to (es_settings "Language").</summary>
    private string ResolveEsLanguage()
    {
        try
        {
            if (_esSettings.ReadAllSettings().TryGetValue("Language", out var language)
                && !string.IsNullOrWhiteSpace(language))
            {
                var parts = language.Trim().Replace('-', '_')
                    .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 1) return parts[0].ToLowerInvariant();
                if (parts.Length > 1) return $"{parts[0].ToLowerInvariant()}_{parts[1].ToUpperInvariant()}";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ES language unreadable, falling back to en");
        }

        return "en";
    }

    /// <summary>What of a metadata bundle is TEXT. Everything else in there is a media
    /// pointer frozen at scrape time.</summary>
    private static readonly IReadOnlySet<string> TextFieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "name", "desc", "developer", "publisher", "players", "genre", "genres",
        "releasedate", "rating", "region", "family", "arcadesystemname", "emulator",
        "source", "md5", "crc32", "gameid", "system"
    };

    private async Task PublishScreenSnapshotAsync(
        EventEnvelope trigger,
        PhysicalMediaSelectionSnapshot selection,
        MarqueeMediaSnapshot marquee,
        Stopwatch perf,
        string phase,
        IReadOnlyList<string> roots,
        IReadOnlyList<string>? systemRoots = null)
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
                Assets = BuildAssetTable(roots, DisplayableMediaKinds),
                SystemAssets = systemRoots is null
                    ? null
                    : BuildAssetTable(systemRoots, DisplayableMediaKinds),
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
                    // Additive: the legacy Media fields keep their shape and their
                    // fallbacks, Assets says exactly what THIS entry owns.
                    // The gabarit's logo layer resolves the "wheel" kind from the PLAIN
                    // asset table (its layer key is "wheel", not "systemwheel"), so the
                    // per-frontend pin must sit on BOTH tables - otherwise fbneo's gabarit
                    // reads the shared arcade wheel and overwrites the correct logo.
                    Assets = BuildSystemAssetTable(frontendSystemId, systemId, roots, DisplayableMediaKinds),
                    SystemAssets = BuildSystemAssetTable(frontendSystemId, systemId, roots, DisplayableMediaKinds),
                    Generation = ResolveSystemGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "background-generation")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "background-generation", roots);

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
            var marquee = BuildGameMarqueeMedia(frontendSystemId, systemId, roots, fallbackSystemRoots);
            var gameAssets = BuildGameAssets(systemId, gameSlug, selected.GamePath, roots); // LOT 7
            var text = await BuildTextBlockAsync(systemId, gameSlug, selected.Details, roots);

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
                    // Additive, and SCOPED. Media.Fanart silently falls back to the
                    // system's (`game.Fanart ?? system.Fanart`), so a consumer holding a
                    // path cannot tell whose art it is. These two tables can: each holds
                    // only what that scope really owns, and a key is absent when the file
                    // is. The legacy fields are untouched.
                    Assets = gameAssets,
                    SystemAssets = BuildSystemAssetTable(frontendSystemId, systemId, fallbackSystemRoots, DisplayableMediaKinds),
                    Text = text,
                    Generation = ResolveGameGenerationState(roots),
                    Latency = BuildSnapshotLatency(trigger, selectionSequence, selection.SelectionKey, ResolveReceivedAtUtc(trigger), DateTime.UtcNow, perf, "background-generation")
                }
            });
            await PublishScreenSnapshotAsync(trigger, selection, marquee, perf, "background-generation", roots, fallbackSystemRoots);

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
        var fanart = ResolveSystemFanartAsset(frontendSystemId, systemId, roots);
        var logo = ResolveSystemLogoAsset(frontendSystemId, systemId, roots);
        var generatedMarquee = ResolveSystemGeneratedMarqueeAsset(frontendSystemId, systemId, roots);

        return new MarqueeMediaSnapshot(
            media.Marquee,
            generatedMarquee,
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

        var fanartPath = ResolveSystemFanartPath(frontendSystemId, systemId, roots);
        var useThemeBackground = _runtimeOptions.ShouldUseThemeBackgroundForSystemMarquee() &&
            !string.IsNullOrWhiteSpace(fanartPath) &&
            File.Exists(fanartPath);

        var logoPath = await EnsureSystemLogoCachedAsync(frontendSystemId, systemId, selectedSystem, cancellationToken) ??
            ResolveSystemLogoPath(frontendSystemId, systemId, selectedSystem, roots);
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
        var outputPath = Path.Combine(destinationDirectory, FrontendScopedFileName("generated-system-marquee", ".png", frontendSystemId, systemId));
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

        var fanartPath = ResolveSystemFanartPath(frontendSystemId, systemId, roots);
        var useThemeBackground = _runtimeOptions.ShouldUseThemeBackgroundForSystemMarquee() &&
            !string.IsNullOrWhiteSpace(fanartPath) &&
            File.Exists(fanartPath);

        var logoPath = await EnsureSystemLogoCachedAsync(frontendSystemId, systemId, selectedSystem, cancellationToken) ??
            ResolveSystemLogoPath(frontendSystemId, systemId, selectedSystem, roots);
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
        var destinationPath = Path.Combine(destinationDirectory, FrontendScopedFileName("wheel", ".png", frontendSystemId, systemId));
        var markerPath = destinationPath + ".apiexpose-cache";

        var roots = ResolveSystemRoots(systemId).ToList();
        var sourcePath = ResolveSystemLogoPath(frontendSystemId, systemId, selectedSystem, roots);
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
                var conversionSource = PrepareLogoConversionSource(sourcePath, out var strippedTemp);
                try
                {
                    await RunConvertAsync(
                        convertPath,
                        BuildSystemLogoConvertArguments(conversionSource, destinationPath),
                        cancellationToken);
                }
                finally
                {
                    if (strippedTemp != null)
                    {
                        TryDelete(strippedTemp);
                    }
                }
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

    // filter:url(#id) inside a style attribute (with an optional trailing ;), or
    // filter="url(#id)" as its own attribute.
    private static readonly Regex SvgFilterReference = new(
        @"filter\s*:\s*url\(#[^)]*\)\s*;?|filter\s*=\s*""url\(#[^""]*\)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// imagemagick's SVG rasteriser (librsvg) silently DROPS any element carrying an SVG
    /// filter it does not implement - feConvolveMatrix among them - so a themed logo like
    /// gx4000, whose "GX 4000" lettering sits under such a filter, loses its text entirely
    /// and only the surrounding artwork survives. The filter is a cosmetic sharpen; removing
    /// only the REFERENCE (never the geometry, never the &lt;defs&gt;) lets the letters render.
    /// A throwaway copy is written so the theme file is never touched. Non-SVG sources, and
    /// SVGs without a filter, are converted in place. Best effort: any read/write trouble
    /// falls back to the original source.
    /// </summary>
    private static string PrepareLogoConversionSource(string sourcePath, out string? tempPath)
    {
        tempPath = null;
        if (!Path.GetExtension(sourcePath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        try
        {
            var svg = File.ReadAllText(sourcePath);
            var stripped = SvgFilterReference.Replace(svg, string.Empty);
            if (string.Equals(stripped, svg, StringComparison.Ordinal))
            {
                return sourcePath;
            }

            Directory.CreateDirectory(RetroBatPaths.RuntimeTempRoot);
            var temp = Path.Combine(RetroBatPaths.RuntimeTempRoot, "logo-nofilter-" + Guid.NewGuid().ToString("N") + ".svg");
            File.WriteAllText(temp, stripped);
            tempPath = temp;
            return temp;
        }
        catch
        {
            tempPath = null;
            return sourcePath;
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

    /// <summary>
    /// Every instruction card of a game, each stamped with the ROLE it belongs to.
    ///
    /// Cards used to live flat under `artwork/ic`; they are now grouped one level
    /// deeper, one folder per role - `cody`, `items-and-weaponry`, `stage-3`. Both
    /// layouts are read: a cabinet cartographed before the change keeps working, and
    /// its cards simply carry the empty role.
    ///
    /// One level, never more: past that it is a tree to browse, not a role.
    /// </summary>
    private IEnumerable<MediaStreamAsset> FindInstructionCards(IReadOnlyList<string> roots)
    {
        // An image, and only an image: each card now sits next to its `.json` companion,
        // which the `ic*.*` pattern happily matches too - every card would be published
        // twice, once as a picture and once as its own description.
        static bool IsCard(MediaStreamAsset asset)
            => (asset.Stem.Equals("ic", StringComparison.OrdinalIgnoreCase)
                || asset.Stem.StartsWith("ic-", StringComparison.OrdinalIgnoreCase))
               && CardImageExtensions.Contains(asset.Extension);

        var cards = FindAssets(roots, "artwork", "ic", "ic*.*")
            .Where(IsCard)
            .Select(asset => asset with { Role = string.Empty, Panels = ReadCardPanels(asset.Path) })
            .ToList();

        foreach (var role in FindCardRoles(roots))
        {
            cards.AddRange(FindAssets(roots, Path.Combine("artwork", "ic", role), "ic*.*")
                .Where(IsCard)
                .Select(asset => asset with { Role = role, Panels = ReadCardPanels(asset.Path) }));
        }

        // the default role first, then the others in alphabetical order; inside a role,
        // the page order the file names declare
        return cards
            .OrderBy(asset => asset.Role.Length == 0 ? 0 : 1)
            .ThenBy(asset => asset.Role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => InstructionCardOrder(asset.Stem))
            .ThenBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase);
    }

    // sans le point : l'asset stocke l'extension telle que CreateAsset la normalise
    private static readonly HashSet<string> CardImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp", "bmp" };

    /// <summary>The role folders of a game, merged across the media roots so a user
    /// folder can add a role the pack does not carry.</summary>
    private static IEnumerable<string> FindCardRoles(IReadOnlyList<string> roots)
    {
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var directory = CombineRelative(root, Path.Combine("artwork", "ic"));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var folder in Directory.EnumerateDirectories(directory))
            {
                seen.Add(Path.GetFileName(folder));
            }
        }

        return seen;
    }

    // A card's companion is re-read on every selection, for every card of the game.
    // Keyed by path AND timestamp: a file edited on disk is picked up, an unchanged one
    // is not parsed again.
    private static readonly Dictionary<string, IReadOnlyList<InstructionCardPanel>?> PanelCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PanelCacheLock = new();

    /// <summary>
    /// Where each entry sits inside a card, from the `.json` written next to it. Null
    /// when there is none - a card without a companion is still a card, it just cannot
    /// have one of its entries framed.
    /// </summary>
    private static IReadOnlyList<InstructionCardPanel>? ReadCardPanels(string cardPath)
    {
        try
        {
            var companion = Path.ChangeExtension(cardPath, ".json");
            if (!File.Exists(companion))
            {
                return null;
            }

            var key = companion + "|" + File.GetLastWriteTimeUtc(companion).Ticks;
            lock (PanelCacheLock)
            {
                if (PanelCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var panels = new List<InstructionCardPanel>();
            using var document = JsonDocument.Parse(File.ReadAllText(companion));
            if (document.RootElement.TryGetProperty("panels", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var panel in list.EnumerateArray())
                {
                    var rect = new List<double>();
                    if (panel.TryGetProperty("rect", out var bounds) && bounds.ValueKind == JsonValueKind.Array)
                    {
                        rect.AddRange(bounds.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.Number)
                            .Select(x => x.GetDouble()));
                    }

                    if (rect.Count != 4)
                    {
                        continue; // a panel without a rectangle cannot be framed
                    }

                    panels.Add(new InstructionCardPanel(
                        panel.TryGetProperty("role", out var role) ? role.GetString() ?? string.Empty : string.Empty,
                        panel.TryGetProperty("kind", out var kind) ? kind.GetString() ?? string.Empty : string.Empty,
                        panel.TryGetProperty("named", out var named) && named.ValueKind == JsonValueKind.True,
                        panel.TryGetProperty("label", out var label) ? label.GetString() : null,
                        rect));
                }
            }

            IReadOnlyList<InstructionCardPanel>? result = panels.Count > 0 ? panels : null;
            lock (PanelCacheLock)
            {
                if (PanelCache.Count > 512)
                {
                    PanelCache.Clear();
                }

                PanelCache[key] = result;
            }

            return result;
        }
        catch
        {
            // a malformed companion must never cost the card itself
            return null;
        }
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

    internal static MediaStreamAsset? FindFirstAsset(IReadOnlyList<string> roots, string directory1, string directory2, string pattern)
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

    internal static IEnumerable<MediaStreamAsset> FindAssets(IReadOnlyList<string> roots, string relativeDirectory, string pattern)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metrics = _metrics.Value;
        foreach (var root in roots)
        {
            var directory = CombineRelative(root, relativeDirectory);

            // HP3: filter the cached full listing by the search pattern in memory.
            // FileSystemName.MatchesSimpleExpression is exactly what EnumerateFiles(dir, pattern)
            // applies under its default MatchType.Simple, so the set of matched files is identical.
            foreach (var path in DirectoryCache
                .List(directory, metrics != null ? () => metrics.DirectoryEnumerations++ : null)
                .Where(candidate => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(candidate)))
                .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase))
            {
                if (metrics != null) metrics.PatternFilesVisited++;
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

                // HP3: same cached-listing + in-memory pattern filter as FindAssets.
                var path = DirectoryCache.List(directory)
                    .Where(candidate => FileSystemName.MatchesSimpleExpression(search.Pattern, Path.GetFileName(candidate)))
                    .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
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

    // ── HP0 baseline instrumentation ─────────────────────────────────────────────
    // The recursive AllDirectories scan of a system root is the dominant cost this
    // patch removes; these counters make the "before" measurable and testable. The
    // accumulator is null unless a scope is opened, so production pays nothing until
    // asked. HP1/HP2 replace the scan and these numbers collapse - this block goes
    // with it.
    internal sealed class MediaDiscoveryMetrics
    {
        public int RecursiveScans;         // BuildAssetTable roots actually walked recursively
        public long RecursiveFilesVisited; // files those recursive walks enumerate
        public int DirectoryEnumerations;  // FindAssets TopDirectoryOnly enumerations
        public long PatternFilesVisited;   // files those pattern searches enumerate
    }

    private static readonly AsyncLocal<MediaDiscoveryMetrics?> _metrics = new();

    /// <summary>Begins a per-publication discovery-metrics scope for the current async
    /// flow. Tests read the accumulator to assert the enumeration cost before/after the
    /// refactor; production leaves it unset, so the counters stay no-ops.</summary>
    internal static MediaDiscoveryMetrics BeginMetricsScope()
    {
        var metrics = new MediaDiscoveryMetrics();
        _metrics.Value = metrics;
        return metrics;
    }

    // ── HP2 shared inventory ─────────────────────────────────────────────────────
    // A one-shot listing of the recognised media directories under a set of roots, built
    // once and reused by every surface of a publication (marquee, topper, card, screen)
    // so a game selection enumerates each directory ONCE instead of a dozen times.
    // Purely physical: qualification stays in FromRelativePath / BuildAssetTable.
    internal sealed class MediaInventory
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _byDirectory;

        internal MediaInventory(IReadOnlyDictionary<string, IReadOnlyList<string>> byDirectory)
            => _byDirectory = byDirectory;

        /// <summary>Full file paths in a recognised directory - roots in priority order,
        /// then top-directory enumeration order. Empty when the directory is absent.</summary>
        public IReadOnlyList<string> Files(string relativeDirectory)
            => _byDirectory.TryGetValue(relativeDirectory, out var files) ? files : Array.Empty<string>();

        public IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> Directories => _byDirectory;
    }

    private static readonly AsyncLocal<System.Collections.Concurrent.ConcurrentDictionary<string, MediaInventory>?> _inventoryScope = new();

    /// <summary>Begins a per-publication inventory scope: within it, repeated inventory
    /// requests for the same roots reuse a single disk listing. Dispose ends the scope -
    /// each publication is fresh, nothing is kept across events (that is HP3).</summary>
    internal static IDisposable BeginInventoryScope()
    {
        _inventoryScope.Value = new System.Collections.Concurrent.ConcurrentDictionary<string, MediaInventory>(StringComparer.Ordinal);
        return new InventoryScope();
    }

    private sealed class InventoryScope : IDisposable
    {
        public void Dispose() => _inventoryScope.Value = null;
    }

    /// <summary>The recognised-directory listing for these roots, memoised within the
    /// current publication scope when one is open, built fresh otherwise.</summary>
    internal static MediaInventory BuildInventory(IReadOnlyList<string> roots)
    {
        var scope = _inventoryScope.Value;
        if (scope == null) return BuildInventoryUncached(roots);
        return scope.GetOrAdd(string.Join("\0", roots), _ => BuildInventoryUncached(roots));
    }

    private static MediaInventory BuildInventoryUncached(IReadOnlyList<string> roots)
    {
        var metrics = _metrics.Value;
        var byDirectory = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        // Enumerate ONLY the directories the qualifier can recognise (never the whole
        // subtree, and never games/), each once and top-level only. First root wins, so
        // the media/user override keeps its priority.
        foreach (var relativeDirectory in MediaKinds.RecognisedMediaDirectories)
        {
            List<string>? files = null;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                var directory = CombineRelative(root, relativeDirectory);

                // HP3: the listing comes from the cross-publication cache - a straight
                // pass-through enumeration when the cache is off, a validated hit otherwise. The
                // onEnumerate callback fires only on a real disk read, so the baseline metric
                // still counts enumerations and not cache hits.
                var found = DirectoryCache.List(
                    directory,
                    metrics != null ? () => metrics.DirectoryEnumerations++ : null);
                foreach (var path in found)
                {
                    if (metrics != null) metrics.PatternFilesVisited++;
                    (files ??= new List<string>()).Add(path);
                }
            }

            if (files != null) byDirectory[relativeDirectory] = files;
        }

        return new MediaInventory(byDirectory);
    }

    /// <summary>
    /// Every recognised medium under these roots, keyed by canonical
    /// <see cref="MediaKinds"/>. Backed by the shared inventory (one enumeration per
    /// directory), and the first root wins so the media/user override keeps its priority.
    ///
    /// <paramref name="allowed"/> curates the table for the surface being served: a
    /// stream carries what its surface can display, not everything on disk. A key only
    /// exists when the file exists; absence IS the answer.
    /// </summary>
    internal static IReadOnlyDictionary<string, MediaStreamAsset> BuildAssetTable(
        IReadOnlyList<string> roots,
        IReadOnlySet<string> allowed)
        => BuildAssetTable(BuildInventory(roots), allowed);

    /// <summary>Same table, from an already-built inventory: no disk I/O, so every surface
    /// of a publication filters the one listing in memory.</summary>
    internal static IReadOnlyDictionary<string, MediaStreamAsset> BuildAssetTable(
        MediaInventory inventory,
        IReadOnlySet<string> allowed)
    {
        var table = new Dictionary<string, MediaStreamAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativeDirectory, files) in inventory.Directories)
        {
            foreach (var path in files)
            {
                var relative = relativeDirectory.Length == 0
                    ? Path.GetFileName(path)
                    : relativeDirectory + "/" + Path.GetFileName(path);

                if (MediaKinds.FromRelativePath(relative) is not { Length: > 0 } kind) continue;
                if (!allowed.Contains(kind) || table.ContainsKey(kind)) continue;
                table[kind] = CreateAsset(path) with { Kind = kind };
            }
        }

        return table;
    }

    /// <summary>
    /// Every media kind a surface may pull into a composition. DECISION (2026-08): all four
    /// surfaces expose the SAME set - a surface only shows what its composition references,
    /// so curating per surface added maintenance without ever changing a display. Cheap: the
    /// table is filtered from the ALREADY-BUILT inventory (no extra disk work), and a key
    /// exists only when the file does, so a media-poor game stays small.
    ///
    /// Excluded on purpose: <c>manual</c>/<c>magazine</c> (documents, never shown on a
    /// surface), <c>themehb</c> (a theme archive), <c>video</c>/<c>video-normalized</c>
    /// (playback, served by <c>Media.Video</c>), and <c>dmd-animation</c> (a LIST served by
    /// <c>Media.Dmd</c> - a one-per-kind table would keep only one frame).
    /// </summary>
    internal static readonly IReadOnlySet<string> DisplayableMediaKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        MediaKinds.Wheel, MediaKinds.WheelCarbon, MediaKinds.WheelSteel, MediaKinds.Logo, MediaKinds.SteamGrid,
        MediaKinds.Marquee, MediaKinds.GeneratedMarquee, MediaKinds.ScreenMarquee, MediaKinds.ScreenMarqueeSmall,
        MediaKinds.Topper, MediaKinds.Dmd, MediaKinds.GeneratedDmd,
        MediaKinds.BoxFront, MediaKinds.Box3d, MediaKinds.BoxBack, MediaKinds.BoxSide, MediaKinds.BoxTexture,
        MediaKinds.MixRbv1, MediaKinds.MixRbv2,
        MediaKinds.Thumbnail, MediaKinds.Image,
        MediaKinds.Fanart, MediaKinds.Flyer,
        MediaKinds.Cartridge, MediaKinds.Label, MediaKinds.Figurine, MediaKinds.Bezel,
        MediaKinds.InstructionCard, MediaKinds.Map
    };

    /// <summary>
    /// LOT 7 - the game's canonical asset table, plus (only when MediaDiscovery.GamelistMediaEnabled
    /// is on) any kind the canonical store LACKS, filled from the user gamelist. So a roms/ medium the
    /// gamelist references but that was never migrated becomes visible, WITHOUT ever overriding a
    /// canonical asset. Off by default → the table is byte-for-byte the old one. Uses the LOT 3 reader
    /// and the LOT 4 resolver; the filled asset carries PathRoot "retrobat", so MarqueeManager resolves
    /// it against the RetroBat root (HP5).
    /// </summary>
    private IReadOnlyDictionary<string, MediaStreamAsset> BuildGameAssets(
        string systemId, string gameSlug, string? gamePath, IReadOnlyList<string> roots)
    {
        var canonical = BuildAssetTable(roots, DisplayableMediaKinds);
        if (!_gamelistMediaEnabled || string.IsNullOrWhiteSpace(gamePath))
        {
            return canonical;
        }

        GamelistGameMedia? gamelist;
        try
        {
            gamelist = _gamelistReader.GetGameMedia(systemId, gamePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Gamelist media fill skipped for {System}/{Game}.", systemId, gameSlug);
            return canonical;
        }

        if (gamelist == null || gamelist.Candidates.Count == 0)
        {
            return canonical;
        }

        var merged = new Dictionary<string, MediaStreamAsset>(canonical, StringComparer.OrdinalIgnoreCase);
        var catalog = new GameMediaCatalog(gamelist.Bindings, gamelist.Candidates);
        foreach (var kind in DisplayableMediaKinds)
        {
            if (merged.ContainsKey(kind)) continue; // fill gaps only: a canonical asset always wins

            var result = _mediaResolver.Resolve(catalog, new MediaResolveRequest(systemId, gameSlug, kind));
            if (result.State != MediaResolveState.Missing && result.Asset is { } asset)
            {
                merged[kind] = ToStreamAsset(kind, asset);
            }
        }

        return merged;
    }

    /// <summary>A resolved catalog asset (LOT 4 <see cref="MediaAssetRef"/>) turned into the
    /// projection DTO, carrying its PathRoot so the consumer resolves it against the right root (HP5).</summary>
    private static MediaStreamAsset ToStreamAsset(string kind, MediaAssetRef asset)
    {
        var fileName = Path.GetFileName(asset.Path);
        return new MediaStreamAsset(
            Kind: kind,
            Origin: asset.Origin,
            Path: asset.Path,
            FileName: fileName,
            Stem: Path.GetFileNameWithoutExtension(fileName),
            Extension: Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant(),
            Length: asset.Length ?? 0,
            LastWriteTimeUtc: asset.LastWriteTimeUtc ?? default,
            Url: asset.Url ?? string.Empty)
        {
            PathRoot = asset.PathRoot
        };
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

        // HP5: name the root Path is relative to, so a consumer stops guessing. Mirrors the
        // relative-path computation above (same order), so Path never changes - off by default.
        var pathRoot = !_emitPathRoot
            ? null
            : IsUnderRoot(fullPath, RetroBatPaths.PluginRoot)
                ? "apiexpose"
                : IsUnderRoot(fullPath, RetroBatPaths.RetroBatRoot)
                    ? "retrobat"
                    : "external-local";

        return new MediaStreamAsset(
            Kind: ResolveKind(path),
            Origin: origin,
            Path: relative,
            FileName: info.Name,
            Stem: Path.GetFileNameWithoutExtension(info.Name),
            Extension: info.Extension.TrimStart('.').ToLowerInvariant(),
            Length: info.Length,
            LastWriteTimeUtc: info.LastWriteTimeUtc,
            Url: url)
        {
            PathRoot = pathRoot
        };
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

    /// <summary>System logo/fanart shipped by the themes (active theme, then es-theme-carbon),
    /// as an O(1) in-memory lookup built once from the theme-set roots - replaces the
    /// per-selection theme glob. Built lazily on first system fanart/logo resolution, cached
    /// for the process. See docs §27.</summary>
    private static readonly object _themeArtLock = new();
    private static ThemeSystemArtIndex? _themeArtIndex;

    private static ThemeSystemArtIndex ThemeArt
    {
        get
        {
            lock (_themeArtLock)
            {
                return _themeArtIndex ??= ThemeSystemArtIndex.Build(ResolveThemeSetRoots().ToList());
            }
        }
    }

    /// <summary>A appeler apres avoir depose un logo ou un fond dans un theme : sans cela,
    /// l'index garderait en memoire l'absence du fichier jusqu'au prochain demarrage.</summary>
    internal static void InvalidateThemeArt()
    {
        lock (_themeArtLock)
        {
            _themeArtIndex = null;
        }
    }

    private MediaStreamAsset? ResolveSystemFanartAsset(string frontendSystemId, string systemId, IReadOnlyList<string> roots)
    {
        return FindFirstAsset(roots, SystemFanartSearches)
            ?? (ThemeArt.ResolveFanartPath(SystemFanartNames(frontendSystemId, systemId)) is { } path ? CreateAsset(path) : null);
    }

    private string? ResolveSystemFanartPath(string frontendSystemId, string systemId, IReadOnlyList<string> roots)
    {
        return FindFirstPhysicalPath(roots, SystemFanartSearches)
            ?? ThemeArt.ResolveFanartPath(SystemFanartNames(frontendSystemId, systemId));
    }

    // The system logo the marquee SNAPSHOT carries must be a raster - MarqueeManager renders
    // SVG only for panels, not for media - so it is the media-store PNG. That PNG IS the cache
    // produced from the source resolved below and refreshed by EnsureSystemLogoCachedAsync,
    // then republished; a first visit before the cache exists simply carries no system logo.
    private MediaStreamAsset? ResolveSystemLogoAsset(string frontendSystemId, string systemId, IReadOnlyList<string> roots)
    {
        // Resolve the EXACT file EnsureSystemLogoCached writes: wheel.png for 1:1 systems
        // (snes, and the arcade AGGREGATE) and wheel.<frontend>.png for the collapsed arcade
        // sub-systems. Using the exact name - never the "wheel.*" glob - is what stops the
        // arcade aggregate from grabbing an alphabetically-earlier sibling (wheel.fbneo.png),
        // and stops fbneo from grabbing the shared wheel. roots put media/user first, so this
        // already honours an operator override.
        var scoped = FindFirstAsset(roots, "ui", "wheels", FrontendScopedFileName("wheel", ".png", frontendSystemId, systemId));
        if (scoped != null)
        {
            return scoped;
        }

        // No generated wheel yet: a 1:1 system may still own a logo elsewhere (ui/logos,
        // artwork/logo, a scraped wheel.jpg); a collapsed sub-system accepts only an operator
        // override, never a sibling frontend's shared file.
        return string.Equals(frontendSystemId, systemId, StringComparison.OrdinalIgnoreCase)
            ? FindFirstAsset(roots, SystemLogoSearches)
            : FindFirstAsset(UserRootsOf(roots), SystemLogoSearches);
    }

    /// <summary>The composited system marquee for the snapshot: frontend-scoped for the
    /// collapsed arcade sub-systems (so fbneo and mame keep distinct compositions, null
    /// until the first background generation), the shared file for 1:1 systems.</summary>
    private MediaStreamAsset? ResolveSystemGeneratedMarqueeAsset(string frontendSystemId, string systemId, IReadOnlyList<string> roots)
    {
        // Exact frontend-scoped name, same reason as the wheel: the arcade aggregate must
        // not grab a sibling's generated-system-marquee.<frontend>.png via a glob.
        var scoped = FindFirstAsset(roots, "artwork", "marquee", FrontendScopedFileName("generated-system-marquee", ".png", frontendSystemId, systemId));
        return scoped
            ?? (string.Equals(frontendSystemId, systemId, StringComparison.OrdinalIgnoreCase)
                ? FindFirstAsset(roots, "artwork", "marquee", "generated-marquee.*")
                : null);
    }

    /// <summary>
    /// The system asset table the GABARIT reads - published as <c>SystemAssets</c>, which
    /// MarqueeManager exposes under the snapshot's "system:*" keys and a gabarit's
    /// "systemwheel"/"systemmarquee" layers resolve against (never Media.Logo). The generic
    /// table enumerates the shared media dir, where arcade collapses several frontends'
    /// wheels and marquees together and the first-wins pick is arbitrary. Pinning the wheel
    /// and the generated marquee to the per-frontend files is what makes a gabarit show
    /// fbneo on fbneo and mame on mame.
    /// </summary>
    private IReadOnlyDictionary<string, MediaStreamAsset> BuildSystemAssetTable(
        string frontendSystemId, string systemId, IReadOnlyList<string> roots, IReadOnlySet<string> allowed)
    {
        var table = new Dictionary<string, MediaStreamAsset>(BuildAssetTable(roots, allowed), StringComparer.OrdinalIgnoreCase);
        PinAsset(table, MediaKinds.Wheel, ResolveSystemLogoAsset(frontendSystemId, systemId, roots), allowed);
        PinAsset(table, MediaKinds.GeneratedMarquee, ResolveSystemGeneratedMarqueeAsset(frontendSystemId, systemId, roots), allowed);
        return table;
    }

    /// <summary>Force one kind to a specific asset (or drop it when there is none), so the
    /// generic first-wins enumeration cannot leak a sibling frontend's file.</summary>
    private static void PinAsset(Dictionary<string, MediaStreamAsset> table, string kind, MediaStreamAsset? asset, IReadOnlySet<string> allowed)
    {
        if (!allowed.Contains(kind))
        {
            return;
        }

        if (asset != null)
        {
            table[kind] = asset with { Kind = kind };
        }
        else
        {
            table.Remove(kind);
        }
    }

    // The logo SOURCE to convert/compose (svg is fine here - imagemagick handles it):
    //  1. a logo the operator dropped under media/user ALWAYS wins - it is a deliberate
    //     per-cabinet override, and it must never be shadowed by a theme;
    //  2. then the theme's per-system logo (canonical, and it keeps fbneo/mame distinct via
    //     the specific frontend id, where the media store is keyed by the collapsed "arcade"
    //     id - this is what beats the wrong "arcade" logo the scraped store carries);
    //  3. then the rest of the media store, for systems no theme ships.
    private string? ResolveSystemLogoPath(string frontendSystemId, string systemId, SystemDetails? selectedSystem, IReadOnlyList<string> roots)
    {
        var userRoots = UserRootsOf(roots);
        return (userRoots.Count > 0 ? FindFirstPhysicalPath(userRoots, SystemLogoSearches) : null)
            ?? ThemeArt.ResolveLogoPath(SystemLogoNames(frontendSystemId, systemId, selectedSystem), ResolveEsLanguage())
            ?? FindFirstPhysicalPath(roots, SystemLogoSearches);
    }

    /// <summary>The operator-override roots (media/user/systems/…) among a system's roots,
    /// identified by prefix so it does not depend on their order.</summary>
    private static List<string> UserRootsOf(IReadOnlyList<string> roots)
        => roots
            .Where(r => r.StartsWith(RetroBatPaths.MediaUserSystemsRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Arcade collapses mame/fbneo/fba/hbmame onto ONE systemId ("arcade"), yet each ES
    /// system carries its OWN logo. The GENERATED caches (the rasterised wheel, the
    /// composited marquee) therefore key on the FRONTEND id, so fbneo and mame no longer
    /// overwrite each other's single file under media/systems/arcade. A system whose
    /// frontend id already equals its systemId (snes, and the jaguar-style 1:1 renames)
    /// keeps the plain name - no new files, no migration, no behaviour change.
    /// </summary>
    internal static string FrontendScopedFileName(string baseName, string extension, string frontendSystemId, string systemId)
        => string.Equals(frontendSystemId, systemId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(frontendSystemId)
            ? baseName + extension
            : $"{baseName}.{frontendSystemId}{extension}";

    /// <summary>Logo candidate names, most specific first: the frontend id (keeps arcade
    /// sub-systems distinct), the es_systems theme, the normalised id, the display name.</summary>
    private static IReadOnlyList<string?> SystemLogoNames(string frontendSystemId, string systemId, SystemDetails? selectedSystem)
        => new[] { frontendSystemId, selectedSystem?.Theme, systemId, selectedSystem?.Name };

    private static IReadOnlyList<string?> SystemFanartNames(string frontendSystemId, string systemId)
        => new string?[] { frontendSystemId, systemId };

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

    internal static IEnumerable<string> ResolveGameRoots(string systemId, string gameSlug)
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
            // Honor the TRUE events.ini order carried in the payload. Raw events
            // are processed on concurrent Task.Run tasks, so an OLDER selection
            // (e.g. an intermediate hovered on the way to the destination) can
            // reach us AFTER a newer one. Advance the high-water mark only on a
            // strictly greater sequence; never promote an out-of-order arrival.
            // A late lower sequence keeps its real value so IsStaleSelectionSequence
            // drops it - the intermediate is skipped by order, the destination wins.
            // (Equal sequence = the enriched second pass of the same selection: it
            // is not advanced and not dropped, so genre/year details still publish.)
            if (sequence > _latestSelectionSequence)
            {
                _latestSelectionSequence = sequence;
            }

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

    internal sealed record MediaStreamAsset(
        string Kind,
        string Origin,
        string Path,
        string FileName,
        string Stem,
        string Extension,
        long Length,
        DateTime LastWriteTimeUtc,
        string Url)
    {
        /// <summary>
        /// For an instruction card: the folder it sits in, which IS what the card is
        /// about - a character (`cody`), a topic (`items-and-weaponry`) or a stage
        /// (`stage-3`). Empty for cards at the root of `artwork/ic`, the default role.
        ///
        /// Added rather than folded into `Kind`: a consumer that only knows the flat
        /// list keeps working, and the role is what lets a viewer follow the character
        /// a player just chose.
        /// </summary>
        public string Role { get; init; } = string.Empty;

        /// <summary>Where each entry sits INSIDE the card, when a companion file says
        /// so. Null when the card has none.</summary>
        public IReadOnlyList<InstructionCardPanel>? Panels { get; init; }

        /// <summary>
        /// HP5 - which root <see cref="Path"/> is relative to, so a consumer resolves the file
        /// deterministically instead of guessing: "apiexpose" (under the plugin), "retrobat"
        /// (under RetroBat but outside the plugin - an EmulationStation theme lands here), or
        /// "external-local" (a local file outside both roots). Null - and omitted from the JSON
        /// (WhenWritingNull) - unless MediaDiscovery.EmitPathRoot is on: an older consumer never
        /// sees the field, a newer one uses it when present and falls back to its two-root guess
        /// when absent. The value mirrors the root Path is already relative to, so Path is
        /// unchanged and the addition is purely additive (SnapshotVersion stays 2).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? PathRoot { get; init; }
    }

    /// <summary>
    /// One entry of an instruction card - a weapon, a stage, a score table - and the
    /// rectangle it occupies, in FRACTIONS of the image so a frame drawn over it
    /// survives any scaling.
    ///
    /// <paramref name="Named"/> tells a name from a rank: `false` means the panel was
    /// only measured and carries a position (`panel-2`), not an identity. Without it a
    /// consumer would match an event against a placeholder.
    /// </summary>
    internal sealed record InstructionCardPanel(
        string Role,
        string Kind,
        bool Named,
        string? Label,
        IReadOnlyList<double> Rect);
}
