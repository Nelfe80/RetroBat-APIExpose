using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class CollectionPackInstallerService : IHostedService, IDisposable
{
    private const string ImporterVersion = "20260515-collections-pack-v5";
    private const string CollectionGameThemeGeneratorVersion = "20260515-collection-game-theme-v3";
    private const string CollectionGameThemeMarkerFileName = ".apiexpose-collection-theme.json";
    private const string CanonicalGameThemeLinkGeneratorVersion = "20260515-canonical-game-theme-link-v2";
    private const string CanonicalGameThemeLinkMarkerFileName = ".apiexpose-canonical-theme-link.json";
    private const string CanonicalGameThemeSourceMarkerFileName = ".apiexpose-canonical-theme-source.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new();
    private static readonly HashSet<string> PackExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };
    private static readonly HashSet<string> ThemeComponentElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "badges",
        "carousel",
        "control",
        "datetime",
        "gamecarousel",
        "gameextras",
        "grid",
        "helpsystem",
        "image",
        "ninepatch",
        "rating",
        "sound",
        "text",
        "textlist",
        "video"
    };

    private static readonly HashSet<string> ThemeLayoutPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "alignment",
        "autoFade",
        "color",
        "colorEnd",
        "delay",
        "effect",
        "fit",
        "flipX",
        "flipY",
        "fontPath",
        "fontSize",
        "gradientType",
        "glowColor",
        "glowOffset",
        "glowSize",
        "horizontalAlignment",
        "lineSpacing",
        "linearSmooth",
        "maxSize",
        "minSize",
        "multiLine",
        "opacity",
        "origin",
        "pos",
        "rotation",
        "rotationOrigin",
        "scale",
        "scaleOrigin",
        "size",
        "verticalAlignment",
        "visible",
        "zIndex"
    };

    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly IEmulationStationNotificationService _notifications;
    private readonly MediaRuntimeState _runtimeState;
    private readonly MameGamelistGroupIndex _mameGamelistGroupIndex;
    private readonly ILogger<CollectionPackInstallerService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDisposable? _settingsSubscription;
    private string _lastSettingsSignature = string.Empty;
    private Dictionary<string, string> _lastInstallerSettings = new(StringComparer.OrdinalIgnoreCase);
    private CollectionPackInstallerIndex _index = new();
    private Dictionary<string, CollectionFamilyIndexEntry> _familyIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _warnedCollectionModeConflict;

    public CollectionPackInstallerService(
        ApiExposeRuntimeOptionsService runtimeOptions,
        IOptionsMonitor<ApiExposeOptions> options,
        EmulationStationSettingsService settingsService,
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        IEmulationStationNotificationService notifications,
        MediaRuntimeState runtimeState,
        MameGamelistGroupIndex mameGamelistGroupIndex,
        ILogger<CollectionPackInstallerService>? logger = null)
    {
        _runtimeOptions = runtimeOptions;
        _options = options;
        _settingsService = settingsService;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _notifications = notifications;
        _runtimeState = runtimeState;
        _mameGamelistGroupIndex = mameGamelistGroupIndex;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _index = LoadIndex();
        _lastInstallerSettings = ReadInstallerSettings();
        _lastSettingsSignature = ComputeInstallerSettingsSignature(_lastInstallerSettings);
        _settingsSubscription = _settingsChangeBus.Subscribe((_, token) => HandleInstallerSettingsChangedAsync(token));
        return RunConfiguredImportAsync("startup", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settingsSubscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _settingsSubscription?.Dispose();
        _gate.Dispose();
    }

    private async Task RunConfiguredImportAsync(string trigger, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _familyIndex = BuildAndSaveFamilyIndex();

        var managerEnabled = _runtimeOptions.IsCollectionPackManagerEnabled();
        var installerEnabled = _runtimeOptions.IsCollectionPackInstallerEnabled();
        var applyCollectionThemeToGamesEnabled = _runtimeOptions.IsCollectionPackApplyCollectionThemeToGamesEnabled();
        if (!managerEnabled)
        {
            _logger?.LogInformation("Collection Pack Installer disabled; package scan skipped. Trigger={Trigger}", trigger);
            await StartupGamelistPreparationLog.AppendAsync(
                "collection-pack-installer",
                "skipped",
                new { trigger, reason = "disabled", elapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
            return;
        }

        var changed = false;
        if (installerEnabled)
        {
            changed |= await ScanAndInstallAsync(cancellationToken);
        }

        if (applyCollectionThemeToGamesEnabled)
        {
            changed |= await ApplyCollectionThemesToGamesAsync(cancellationToken);
        }
        else
        {
            changed |= RemoveGeneratedCollectionGameThemes();
        }

        if (changed)
        {
            _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
        }

        await StartupGamelistPreparationLog.AppendAsync(
            "collection-pack-installer",
            "completed",
            new
            {
                trigger,
                installerEnabled,
                applyCollectionThemeToGamesEnabled,
                changed,
                elapsedMs = stopwatch.ElapsedMilliseconds
            },
            cancellationToken);
    }

    private async Task HandleInstallerSettingsChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
            await _settingsStore.WaitForStableFileAsync(cancellationToken);
            var currentSettings = ReadInstallerSettings();
            var signature = ComputeInstallerSettingsSignature(currentSettings);
            if (string.Equals(signature, _lastSettingsSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastInstallerSettings = currentSettings;
            _lastSettingsSignature = signature;
            await RunConfiguredImportAsync("settings-change", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer settings write.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Collection Pack Installer settings change handling failed.");
        }
    }

    private async Task<bool> ScanAndInstallAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var changed = false;
        try
        {
            var packageRoot = ResolvePackageRoot();
            Directory.CreateDirectory(packageRoot);
            var packages = EnumerateCollectionPackages().ToList();
            if (packages.Count == 0)
            {
                _logger?.LogInformation("No collection pack found in {PackageRoot}.", packageRoot);
                return false;
            }

            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await InstallPackageAsync(package, cancellationToken);
                    changed |= result.Changed;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Collection pack installation failed: {PackagePath}", package.Path);
                    await NotifyAsync($"Echec installation pack collection : {Path.GetFileName(package.Path)}", cancellationToken);
                }
            }

            PruneMissingPackages(packages.Select(package => package.Path));
            SaveIndex();
        }
        finally
        {
            _gate.Release();
        }

        return changed;
    }

    private async Task<CollectionPackInstallResult> InstallPackageAsync(CollectionPackage package, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(package.Path);
        if (!fileInfo.Exists)
        {
            return CollectionPackInstallResult.NoChange;
        }

        var sha256 = ComputeSha256(package.Path);
        var collectionName = NormalizeCollectionName(Path.GetFileNameWithoutExtension(package.Path));
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            return CollectionPackInstallResult.NoChange;
        }

        var installTargets = ResolveCollectionPackInstallTargets(package.Kind, collectionName).ToList();
        if (installTargets.Count == 0)
        {
            _logger?.LogWarning("Collection pack skipped because no theme installation target was found: {PackagePath}", package.Path);
            return CollectionPackInstallResult.NoChange;
        }

        var familyNames = ResolveFamilyNames(collectionName);
        var generationMode = ResolveCollectionGenerationMode();
        var existing = FindReusablePack(package.Path, sha256);
        if (existing != null && IsPackCurrent(existing, package, collectionName, sha256, installTargets, familyNames, generationMode))
        {
            var repaired = EnsureCollectionSettings(collectionName);
            repaired |= EnsureCollectionConfig(collectionName, familyNames, generationMode);
            return new CollectionPackInstallResult(repaired);
        }

        await NotifyAsync($"Installation pack collection : {collectionName} ({package.Kind})", cancellationToken);
        var tempRoot = Path.Combine(TempRoot, Path.GetFileNameWithoutExtension(package.Path) + "-" + Guid.NewGuid().ToString("N"));
        TryDeleteDirectory(tempRoot);
        Directory.CreateDirectory(tempRoot);

        var targets = new List<CollectionPackInstallTarget>();
        var changed = false;
        try
        {
            await ExtractArchiveAsync(package.Path, tempRoot, cancellationToken);
            var contentRoot = ResolveContentRoot(tempRoot, collectionName);
            foreach (var target in installTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationRoot = target.Path;
                if (string.IsNullOrWhiteSpace(destinationRoot) || !IsSafeSubPath(RetroBatPaths.EmulationStationThemesRoot, destinationRoot))
                {
                    _logger?.LogWarning(
                        "Collection pack target skipped because destination is unsafe: kind={Kind}, theme={Theme}, collection={Collection}",
                        package.Kind,
                        target.ThemeSet,
                        collectionName);
                    continue;
                }

                TryDeleteDirectory(destinationRoot);
                CopyDirectory(contentRoot, destinationRoot);
                targets.Add(target.ToIndexTarget());
                changed = true;
            }

            changed |= EnsureCollectionSettings(collectionName);
            changed |= EnsureCollectionConfig(collectionName, familyNames, generationMode);

            UpsertPack(new CollectionPackIndexEntry
            {
                PackagePath = package.Path,
                Sha256 = sha256,
                Kind = package.Kind,
                CollectionName = collectionName,
                ImporterVersion = ImporterVersion,
                InstalledAtUtc = DateTime.UtcNow,
                Targets = targets
            });
            SaveIndex();

            await NotifyAsync($"Collection installee : {collectionName}", cancellationToken);
            return new CollectionPackInstallResult(changed);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<bool> ApplyCollectionThemesToGamesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packs = _index.Packs
                .Where(pack => !string.IsNullOrWhiteSpace(pack.CollectionName) && pack.Targets.Count > 0)
                .OrderBy(pack => pack.CollectionName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (packs.Count == 0)
            {
                _logger?.LogInformation("Collection game theme generation skipped because no installed collection pack is indexed.");
                return false;
            }

            var changed = false;
            var generated = 0;
            foreach (var pack in packs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var familyNames = ResolveFamilyNames(pack.CollectionName);
                var games = ResolveCollectionGames(pack.CollectionName, familyNames);
                if (games.Count == 0)
                {
                    continue;
                }

                foreach (var target in pack.Targets.Where(target => !string.IsNullOrWhiteSpace(target.ThemeSet) && !string.IsNullOrWhiteSpace(target.Path)))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var sourceThemePath = ResolveCollectionThemeSourcePath(target.Path);
                    if (string.IsNullOrWhiteSpace(sourceThemePath))
                    {
                        _logger?.LogDebug(
                            "Collection game theme generation skipped because no systheme/theme xml was found: collection={Collection}, target={Target}",
                            pack.CollectionName,
                            target.Path);
                        continue;
                    }

                    var themeRoot = Path.Combine(RetroBatPaths.EmulationStationThemesRoot, target.ThemeSet);
                    var sourceDirectory = Path.GetDirectoryName(sourceThemePath) ?? target.Path;
                    if (!IsSafeSubPath(themeRoot, sourceDirectory))
                    {
                        continue;
                    }

                    var sourceRelativeRoot = ToEsGenericPath(Path.GetRelativePath(themeRoot, sourceDirectory));
                    var sourceHash = ComputeSha256(sourceThemePath);
                    var themeXml = BuildCollectionGameThemeXml(sourceThemePath, sourceRelativeRoot, themeRoot);
                    foreach (var game in games)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var canonicalResult = await TryApplyCanonicalEquivalentThemeToGameAsync(target, game, cancellationToken);
                        if (canonicalResult.Handled)
                        {
                            changed |= canonicalResult.Changed;
                            continue;
                        }

                        var destinationRoot = ResolveGameThemeDestinationRoot(target, game);
                        if (!IsSafeSubPath(themeRoot, destinationRoot))
                        {
                            _logger?.LogWarning(
                                "Collection game theme target skipped because destination is unsafe: collection={Collection}, system={System}, rom={RomStem}",
                                pack.CollectionName,
                                game.SystemId,
                                game.RomStem);
                            continue;
                        }

                        if (!WriteCollectionGameThemeIfNeeded(
                                destinationRoot,
                                themeXml,
                                pack.CollectionName,
                                target.ThemeSet,
                                game,
                                sourceThemePath,
                                sourceHash,
                                familyNames))
                        {
                            continue;
                        }

                        changed = true;
                        generated++;
                    }
                }
            }

            if (generated > 0)
            {
                _logger?.LogInformation("Collection game themes generated: {Count}", generated);
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryApplyCollectionThemeToGameAsync(
        MediaProjectionPlan plan,
        CancellationToken cancellationToken)
    {
        if (!_runtimeOptions.IsCollectionPackApplyCollectionThemeToGamesEnabled())
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_familyIndex.Count == 0)
            {
                _familyIndex = BuildAndSaveFamilyIndex();
            }

            var game = ResolveCollectionGame(plan.FrontendSystemId, plan.GamePath);
            if (game == null)
            {
                return false;
            }

            foreach (var pack in _index.Packs
                         .Where(pack => !string.IsNullOrWhiteSpace(pack.CollectionName) && pack.Targets.Count > 0)
                         .OrderBy(pack => pack.CollectionName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var familyNames = ResolveFamilyNames(pack.CollectionName);
                var comparables = new HashSet<string>(
                    familyNames
                        .Select(NormalizeComparable)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                if (!comparables.Contains(NormalizeComparable(game.Family)))
                {
                    continue;
                }

                foreach (var target in pack.Targets.Where(target => !string.IsNullOrWhiteSpace(target.ThemeSet) && !string.IsNullOrWhiteSpace(target.Path)))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var canonicalResult = await TryApplyCanonicalEquivalentThemeToGameAsync(target, game, cancellationToken);
                    if (canonicalResult.Handled)
                    {
                        return canonicalResult.Changed;
                    }

                    if (TryApplyCollectionThemeToGame(pack.CollectionName, target, familyNames, game))
                    {
                        await NotifyAsync($"Theme collection applique : {pack.CollectionName}", cancellationToken);
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<CollectionThemeApplyResult> TryApplyCanonicalEquivalentThemeToGameAsync(
        MediaProjectionPlan plan,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = ResolveCollectionGame(plan.FrontendSystemId, plan.GamePath);
            if (game == null)
            {
                var romStem = ResolveRomStem(plan.GamePath ?? string.Empty);
                if (string.IsNullOrWhiteSpace(plan.FrontendSystemId) || string.IsNullOrWhiteSpace(romStem))
                {
                    return CollectionThemeApplyResult.NotHandled;
                }

                game = new CollectionGameCandidate
                {
                    SystemId = plan.FrontendSystemId,
                    RomStem = romStem,
                    GamePath = plan.GamePath ?? string.Empty,
                    Family = string.Empty
                };
            }

            foreach (var target in ResolveRuntimeGameThemeTargets())
            {
                var result = await TryApplyCanonicalEquivalentThemeToGameAsync(target, game, cancellationToken);
                if (result.Handled)
                {
                    return result;
                }
            }

            return CollectionThemeApplyResult.NotHandled;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryApplyCollectionThemeToGame(
        string collectionName,
        CollectionPackInstallTarget target,
        IReadOnlyList<string> familyNames,
        CollectionGameCandidate game)
    {
        var sourceThemePath = ResolveCollectionThemeSourcePath(target.Path);
        if (string.IsNullOrWhiteSpace(sourceThemePath))
        {
            return false;
        }

        var themeRoot = Path.Combine(RetroBatPaths.EmulationStationThemesRoot, target.ThemeSet);
        var sourceDirectory = Path.GetDirectoryName(sourceThemePath) ?? target.Path;
        if (!IsSafeSubPath(themeRoot, sourceDirectory))
        {
            return false;
        }

        var destinationRoot = ResolveGameThemeDestinationRoot(target, game);
        if (!IsSafeSubPath(themeRoot, destinationRoot))
        {
            return false;
        }

        var sourceRelativeRoot = ToEsGenericPath(Path.GetRelativePath(themeRoot, sourceDirectory));
        var sourceHash = ComputeSha256(sourceThemePath);
        var themeXml = BuildCollectionGameThemeXml(sourceThemePath, sourceRelativeRoot, themeRoot);
        return WriteCollectionGameThemeIfNeeded(
            destinationRoot,
            themeXml,
            collectionName,
            target.ThemeSet,
            game,
            sourceThemePath,
            sourceHash,
            familyNames);
    }

    private async Task<CollectionThemeApplyResult> TryApplyCanonicalEquivalentThemeToGameAsync(
        CollectionPackInstallTarget target,
        CollectionGameCandidate game,
        CancellationToken cancellationToken)
    {
        var themeSet = target.ThemeSet;
        if (string.IsNullOrWhiteSpace(themeSet) || string.IsNullOrWhiteSpace(game.SystemId) || string.IsNullOrWhiteSpace(game.RomStem))
        {
            return CollectionThemeApplyResult.NotHandled;
        }

        var themeRoot = Path.Combine(RetroBatPaths.EmulationStationThemesRoot, themeSet);
        var destinationRoot = ResolveGameThemeDestinationRoot(target, game);
        var themePath = Path.Combine(destinationRoot, "theme.xml");
        var markerPath = Path.Combine(destinationRoot, CollectionGameThemeMarkerFileName);
        if (File.Exists(themePath) && !File.Exists(markerPath))
        {
            return CollectionThemeApplyResult.HandledNoChange;
        }

        var currentRom = NormalizeRomName(game.RomStem);
        foreach (var relatedRom in _mameGamelistGroupIndex.GetRelatedRoms(game.SystemId, game.GamePath, game.RomStem))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relatedSlug = NormalizeRomName(relatedRom);
            if (string.IsNullOrWhiteSpace(relatedSlug) ||
                string.Equals(relatedSlug, currentRom, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceArchive = ResolveCanonicalThemeHbArchivePath(game.SystemId, relatedSlug);
            if (string.IsNullOrWhiteSpace(sourceArchive))
            {
                continue;
            }

            var canonicalRoot = ResolveCanonicalThemeDestinationRoot(target, game.SystemId, relatedSlug);
            if (!IsSafeSubPath(themeRoot, destinationRoot) || !IsSafeSubPath(themeRoot, canonicalRoot))
            {
                return CollectionThemeApplyResult.NotHandled;
            }

            var canonicalChanged = await EnsureCanonicalThemeSourceAsync(canonicalRoot, sourceArchive, cancellationToken);
            var canonicalThemePath = Path.Combine(canonicalRoot, "theme.xml");
            if (!File.Exists(canonicalThemePath))
            {
                continue;
            }

            var sourceRelativeRoot = ToEsGenericPath(Path.GetRelativePath(themeRoot, canonicalRoot));
            var linkXml = BuildCanonicalGameThemeLinkXml(canonicalThemePath, sourceRelativeRoot);
            var linkChanged = WriteCanonicalGameThemeLinkIfNeeded(
                destinationRoot,
                linkXml,
                themeSet,
                game,
                relatedSlug,
                canonicalThemePath,
                ComputeSha256(sourceArchive));

            _logger?.LogInformation(
                "Collection game theme linked to canonical equivalent: system={SystemId}, game={RomStem}, related={RelatedSlug}, source={SourceArchive}",
                game.SystemId,
                game.RomStem,
                relatedSlug,
                sourceArchive);
            return new CollectionThemeApplyResult(Handled: true, Changed: canonicalChanged || linkChanged);
        }

        return CollectionThemeApplyResult.NotHandled;
    }

    private static async Task<bool> EnsureCanonicalThemeSourceAsync(
        string canonicalRoot,
        string sourceArchive,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(canonicalRoot, CanonicalGameThemeSourceMarkerFileName);
        var archiveHash = ComputeSha256(sourceArchive);
        if (File.Exists(Path.Combine(canonicalRoot, "theme.xml")) &&
            TryReadCanonicalGameThemeSourceMarker(markerPath, out var marker) &&
            marker != null &&
            string.Equals(marker.SourceArchiveSha256, archiveHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(marker.GeneratorVersion, CanonicalGameThemeLinkGeneratorVersion, StringComparison.Ordinal))
        {
            return false;
        }

        TryDeleteDirectory(canonicalRoot);
        Directory.CreateDirectory(canonicalRoot);
        await ExtractArchiveAsync(sourceArchive, canonicalRoot, cancellationToken);
        var sourceMarker = new CanonicalGameThemeSourceMarker
        {
            GeneratorVersion = CanonicalGameThemeLinkGeneratorVersion,
            SourceArchivePath = sourceArchive,
            SourceArchiveSha256 = archiveHash,
            ExtractedAtUtc = DateTime.UtcNow
        };
        File.WriteAllText(markerPath, JsonSerializer.Serialize(sourceMarker, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static bool TryReadCanonicalGameThemeSourceMarker(string markerPath, out CanonicalGameThemeSourceMarker? marker)
    {
        marker = null;
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            marker = JsonSerializer.Deserialize<CanonicalGameThemeSourceMarker>(File.ReadAllText(markerPath), JsonOptions);
            return marker != null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCanonicalGameThemeLinkXml(string canonicalThemePath, string sourceRelativeRoot)
    {
        var document = XDocument.Load(canonicalThemePath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? new XElement("theme");
        if (document.Root == null)
        {
            document.Add(root);
        }

        foreach (var pathElement in root.Descendants("path").ToList())
        {
            pathElement.Value = RewriteCollectionGameThemePath(pathElement.Value, sourceRelativeRoot);
        }

        document.Declaration ??= new XDeclaration("1.0", "UTF-8", null);
        return $"{document.Declaration}{Environment.NewLine}{document}";
    }

    private static bool WriteCanonicalGameThemeLinkIfNeeded(
        string destinationRoot,
        string themeXml,
        string themeSet,
        CollectionGameCandidate game,
        string relatedSlug,
        string canonicalThemePath,
        string sourceArchiveHash)
    {
        var themePath = Path.Combine(destinationRoot, "theme.xml");
        var linkMarkerPath = Path.Combine(destinationRoot, CanonicalGameThemeLinkMarkerFileName);
        var collectionMarkerPath = Path.Combine(destinationRoot, CollectionGameThemeMarkerFileName);
        if (File.Exists(themePath) && !File.Exists(linkMarkerPath) && !File.Exists(collectionMarkerPath))
        {
            return false;
        }

        var generatedThemeSha256 = ComputeSha256FromText(themeXml);
        if (File.Exists(themePath) &&
            File.Exists(linkMarkerPath) &&
            TryReadCanonicalGameThemeLinkMarker(linkMarkerPath, out var previousMarker) &&
            previousMarker != null &&
            !string.Equals(ComputeSha256FromText(File.ReadAllText(themePath)), previousMarker.GeneratedThemeSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(linkMarkerPath);
            return false;
        }

        var marker = new CanonicalGameThemeLinkMarker
        {
            GeneratorVersion = CanonicalGameThemeLinkGeneratorVersion,
            ThemeSet = themeSet,
            SystemId = game.SystemId,
            RomStem = game.RomStem,
            GamePath = game.GamePath,
            RelatedSlug = relatedSlug,
            CanonicalThemePath = canonicalThemePath,
            SourceArchiveSha256 = sourceArchiveHash,
            GeneratedThemeSha256 = generatedThemeSha256,
            GeneratedAtUtc = DateTime.UtcNow
        };

        if (File.Exists(themePath) &&
            TryReadCanonicalGameThemeLinkMarker(linkMarkerPath, out var existingMarker) &&
            existingMarker != null &&
            string.Equals(existingMarker.GeneratorVersion, marker.GeneratorVersion, StringComparison.Ordinal) &&
            string.Equals(existingMarker.RelatedSlug, marker.RelatedSlug, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingMarker.SourceArchiveSha256, marker.SourceArchiveSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingMarker.GeneratedThemeSha256, marker.GeneratedThemeSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Directory.CreateDirectory(destinationRoot);
        File.WriteAllText(themePath, themeXml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(linkMarkerPath, JsonSerializer.Serialize(marker, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(collectionMarkerPath))
        {
            File.Delete(collectionMarkerPath);
        }

        return true;
    }

    private static bool TryReadCanonicalGameThemeLinkMarker(string markerPath, out CanonicalGameThemeLinkMarker? marker)
    {
        marker = null;
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            marker = JsonSerializer.Deserialize<CanonicalGameThemeLinkMarker>(File.ReadAllText(markerPath), JsonOptions);
            return marker != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveCanonicalThemeHbArchivePath(string systemId, string relatedSlug)
    {
        foreach (var candidateSystem in ResolveCanonicalThemeSystemCandidates(systemId))
        {
            foreach (var root in new[] { RetroBatPaths.MediaUserSystemsRoot, RetroBatPaths.MediaSystemsRoot })
            {
                var directory = Path.Combine(root, candidateSystem, "games", relatedSlug, "themes");
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var candidate = Directory.EnumerateFiles(directory, "themehb.*", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(IsZipArchive);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> ResolveCanonicalThemeSystemCandidates(string systemId)
    {
        if (!string.IsNullOrWhiteSpace(systemId))
        {
            yield return systemId;
        }

        if (!string.Equals(systemId, "arcade", StringComparison.OrdinalIgnoreCase))
        {
            yield return "arcade";
        }

        if (!string.Equals(systemId, "mame", StringComparison.OrdinalIgnoreCase))
        {
            yield return "mame";
        }
    }

    private static string? ResolveCollectionThemeSourcePath(string collectionTargetPath)
    {
        var systemTheme = Path.Combine(collectionTargetPath, "systheme.xml");
        if (File.Exists(systemTheme))
        {
            return systemTheme;
        }

        var gameTheme = Path.Combine(collectionTargetPath, "theme.xml");
        return File.Exists(gameTheme) ? gameTheme : null;
    }

    private static string BuildCollectionGameThemeXml(string sourceThemePath, string sourceRelativeRoot, string themeRoot)
    {
        var document = XDocument.Load(sourceThemePath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? new XElement("theme");
        if (document.Root == null)
        {
            document.Add(root);
        }

        MaterializeSystemViewLayoutStyles(root, themeRoot, sourceThemePath);

        foreach (var view in root.Elements("view")
                     .Where(element => string.Equals(element.Attribute("name")?.Value, "system", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            var customView = new XElement(
                "customView",
                new XAttribute("name", "hyperbat"),
                new XAttribute("displayName", "${viewchoice.hyperbat}"),
                new XAttribute("inherits", "gamecarousel"),
                view.Nodes());
            view.ReplaceWith(customView);
        }

        foreach (var pathElement in root.Descendants("path").ToList())
        {
            pathElement.Value = RewriteCollectionGameThemePath(pathElement.Value, sourceRelativeRoot);
        }

        ExpandEventlessStoryboards(root);

        document.Declaration ??= new XDeclaration("1.0", "UTF-8", null);
        return $"{document.Declaration}{Environment.NewLine}{document}";
    }

    private static void MaterializeSystemViewLayoutStyles(XElement root, string themeRoot, string sourceThemePath)
    {
        if (string.IsNullOrWhiteSpace(themeRoot) || !Directory.Exists(themeRoot))
        {
            return;
        }

        var styleIndex = BuildSystemViewStyleIndex(themeRoot, sourceThemePath);
        if (styleIndex.Count == 0)
        {
            return;
        }

        foreach (var view in root.Elements("view").Where(IsSystemView).ToList())
        {
            foreach (var component in view.Elements().Where(IsThemeComponent).ToList())
            {
                foreach (var styleElement in ResolveStyleElements(styleIndex, component))
                {
                    ApplyMissingLayoutProperties(component, styleElement);
                }
            }
        }
    }

    private static Dictionary<string, List<XElement>> BuildSystemViewStyleIndex(string themeRoot, string sourceThemePath)
    {
        var styleIndex = new Dictionary<string, List<XElement>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFullPath = Path.GetFullPath(sourceThemePath);
        VisitThemeStyleDocument(Path.Combine(themeRoot, "theme.xml"), themeRoot, sourceFullPath, styleIndex, visited, 0);
        return styleIndex;
    }

    private static void VisitThemeStyleDocument(
        string path,
        string themeRoot,
        string sourceThemePath,
        Dictionary<string, List<XElement>> styleIndex,
        HashSet<string> visited,
        int depth)
    {
        if (depth > 32)
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        if (!File.Exists(fullPath) ||
            !IsSafeSubPath(themeRoot, fullPath) ||
            !visited.Add(fullPath) ||
            string.Equals(fullPath, sourceThemePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return;
        }

        var root = document.Root;
        if (root == null)
        {
            return;
        }

        foreach (var view in root.Elements("view").Where(IsSystemView))
        {
            foreach (var component in view.Elements().Where(IsThemeComponent))
            {
                AddStyleElement(styleIndex, component);
            }
        }

        var currentDirectory = Path.GetDirectoryName(fullPath) ?? themeRoot;
        foreach (var include in root.Descendants("include"))
        {
            var includePath = ResolveThemeIncludePath(include.Value, currentDirectory, themeRoot);
            if (!string.IsNullOrWhiteSpace(includePath))
            {
                VisitThemeStyleDocument(includePath, themeRoot, sourceThemePath, styleIndex, visited, depth + 1);
            }
        }
    }

    private static string? ResolveThemeIncludePath(string value, string currentDirectory, string themeRoot)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        normalized = normalized
            .Replace("${themePath}", themeRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("${bobmediapath}", themeRoot, StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);

        if (normalized.Contains("${", StringComparison.Ordinal) || normalized.Contains('{', StringComparison.Ordinal))
        {
            return null;
        }

        if (!Path.IsPathRooted(normalized))
        {
            normalized = Path.Combine(currentDirectory, normalized);
        }

        try
        {
            var fullPath = Path.GetFullPath(normalized);
            return string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddStyleElement(IDictionary<string, List<XElement>> styleIndex, XElement component)
    {
        foreach (var name in SplitThemeComponentNames(component))
        {
            var key = BuildThemeStyleKey(component.Name.LocalName, name);
            if (!styleIndex.TryGetValue(key, out var elements))
            {
                elements = new List<XElement>();
                styleIndex[key] = elements;
            }

            elements.Add(component);
        }
    }

    private static IEnumerable<XElement> ResolveStyleElements(
        IReadOnlyDictionary<string, List<XElement>> styleIndex,
        XElement component)
    {
        foreach (var name in SplitThemeComponentNames(component))
        {
            if (styleIndex.TryGetValue(BuildThemeStyleKey(component.Name.LocalName, name), out var elements))
            {
                foreach (var element in elements)
                {
                    yield return element;
                }
            }
        }
    }

    private static void ApplyMissingLayoutProperties(XElement target, XElement styleSource)
    {
        var propertiesToAdd = new List<XElement>();
        var seenSignatures = target.Elements()
            .Where(IsThemeLayoutProperty)
            .Select(BuildThemePropertySignature)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var property in styleSource.Elements().Where(IsThemeLayoutProperty))
        {
            if (!seenSignatures.Add(BuildThemePropertySignature(property)))
            {
                continue;
            }

            propertiesToAdd.Add(new XElement(property));
        }

        if (propertiesToAdd.Count == 0)
        {
            return;
        }

        var closingIndent = ExtractTrailingWhitespace(target) ?? $"{Environment.NewLine}\t\t";
        var childIndent = ResolveChildIndent(target, closingIndent);
        foreach (var property in propertiesToAdd)
        {
            target.Add(new XText(childIndent));
            target.Add(property);
        }

        target.Add(new XText(closingIndent));
    }

    private static string? ExtractTrailingWhitespace(XElement element)
    {
        var lastText = element.Nodes().LastOrDefault() as XText;
        if (lastText == null || !string.IsNullOrWhiteSpace(lastText.Value))
        {
            return null;
        }

        var value = lastText.Value;
        lastText.Remove();
        return value;
    }

    private static string ResolveChildIndent(XElement element, string closingIndent)
    {
        var existingChildIndent = element.Nodes()
            .OfType<XText>()
            .Select(text => text.Value)
            .FirstOrDefault(value => string.IsNullOrWhiteSpace(value) && value.Contains('\n', StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(existingChildIndent))
        {
            return existingChildIndent;
        }

        var newlineIndex = closingIndent.LastIndexOf('\n');
        if (newlineIndex >= 0)
        {
            return closingIndent + "\t";
        }

        return $"{Environment.NewLine}\t\t\t";
    }

    private static string BuildThemePropertySignature(XElement property)
    {
        var attributes = property.Attributes()
            .OrderBy(attribute => attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}");
        return property.Name.LocalName + "|" + string.Join("|", attributes);
    }

    private static IEnumerable<string> SplitThemeComponentNames(XElement component)
    {
        var name = component.Attribute("name")?.Value ?? string.Empty;
        return name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string BuildThemeStyleKey(string elementName, string componentName)
    {
        return elementName + "|" + componentName;
    }

    private static bool IsThemeComponent(XElement element)
    {
        return ThemeComponentElementNames.Contains(element.Name.LocalName) &&
               !string.IsNullOrWhiteSpace(element.Attribute("name")?.Value);
    }

    private static bool IsThemeLayoutProperty(XElement element)
    {
        return ThemeLayoutPropertyNames.Contains(element.Name.LocalName);
    }

    private static bool IsSystemView(XElement view)
    {
        return SplitViewNames(view.Attribute("name")?.Value)
            .Any(name => string.Equals(name, "system", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitViewNames(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string RewriteCollectionGameThemePath(string value, string sourceRelativeRoot)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return value ?? string.Empty;
        }

        if (string.Equals(trimmed, "{random}", StringComparison.OrdinalIgnoreCase))
        {
            return "{game:video}";
        }

        var sourcePrefix = "${bobmediapath}/" + sourceRelativeRoot.Trim('/').Replace('\\', '/') + "/";
        if (trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith(@".\", StringComparison.Ordinal))
        {
            return sourcePrefix + trimmed[2..].Replace('\\', '/');
        }

        return value ?? string.Empty;
    }

    private static bool RemoveGeneratedCollectionGameThemes()
    {
        if (!Directory.Exists(RetroBatPaths.EmulationStationThemesRoot))
        {
            return false;
        }

        var changed = false;
        foreach (var markerPath in Directory.EnumerateFiles(
                     RetroBatPaths.EmulationStationThemesRoot,
                     CollectionGameThemeMarkerFileName,
                     SearchOption.AllDirectories))
        {
            if (!TryReadCollectionGameThemeMarker(markerPath, out var marker) || marker == null)
            {
                continue;
            }

            var themePath = Path.Combine(Path.GetDirectoryName(markerPath) ?? string.Empty, "theme.xml");
            if (File.Exists(themePath) &&
                string.Equals(ComputeSha256FromText(File.ReadAllText(themePath)), marker.GeneratedThemeSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(themePath);
                changed = true;
            }

            File.Delete(markerPath);
            changed = true;
            TryDeleteEmptyDirectory(Path.GetDirectoryName(markerPath));
        }

        return changed;
    }

    private static void ExpandEventlessStoryboards(XElement root)
    {
        foreach (var storyboard in root.Descendants("storyboard")
                     .Where(element => element.Attribute("event") == null)
                     .ToList())
        {
            storyboard.ReplaceWith(
                CloneStoryboardForEvent(storyboard, "open"),
                CloneStoryboardForEvent(storyboard, "activateNext"),
                CloneStoryboardForEvent(storyboard, "activatePrev"));
        }
    }

    private static XElement CloneStoryboardForEvent(XElement storyboard, string eventName)
    {
        var clone = new XElement(storyboard);
        clone.SetAttributeValue("event", eventName);
        return clone;
    }

    private static bool WriteCollectionGameThemeIfNeeded(
        string destinationRoot,
        string themeXml,
        string collectionName,
        string themeSet,
        CollectionGameCandidate game,
        string sourceThemePath,
        string sourceHash,
        IReadOnlyList<string> familyNames)
    {
        var themePath = Path.Combine(destinationRoot, "theme.xml");
        var markerPath = Path.Combine(destinationRoot, CollectionGameThemeMarkerFileName);
        if (File.Exists(themePath) && !File.Exists(markerPath))
        {
            return false;
        }

        var generatedThemeSha256 = ComputeSha256FromText(themeXml);
        if (File.Exists(themePath) &&
            TryReadCollectionGameThemeMarker(markerPath, out var previousMarker) &&
            previousMarker != null &&
            !string.Equals(ComputeSha256FromText(File.ReadAllText(themePath)), previousMarker.GeneratedThemeSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(markerPath);
            return false;
        }

        var marker = new CollectionGameThemeMarker
        {
            GeneratorVersion = CollectionGameThemeGeneratorVersion,
            CollectionName = collectionName,
            ThemeSet = themeSet,
            SystemId = game.SystemId,
            RomStem = game.RomStem,
            GamePath = game.GamePath,
            Family = game.Family,
            SourceThemePath = sourceThemePath,
            SourceThemeSha256 = sourceHash,
            GeneratedThemeSha256 = generatedThemeSha256,
            Families = familyNames.ToList(),
            GeneratedAtUtc = DateTime.UtcNow
        };

        if (File.Exists(themePath) &&
            TryReadCollectionGameThemeMarker(markerPath, out var existingMarker) &&
            existingMarker != null &&
            string.Equals(existingMarker.GeneratorVersion, marker.GeneratorVersion, StringComparison.Ordinal) &&
            string.Equals(existingMarker.CollectionName, marker.CollectionName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingMarker.SourceThemeSha256, marker.SourceThemeSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingMarker.GeneratedThemeSha256, marker.GeneratedThemeSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingMarker.RomStem, marker.RomStem, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingMarker.SystemId, marker.SystemId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Directory.CreateDirectory(destinationRoot);
        File.WriteAllText(themePath, themeXml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static string ComputeSha256FromText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    private static bool TryReadCollectionGameThemeMarker(string markerPath, out CollectionGameThemeMarker? marker)
    {
        marker = null;
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            marker = JsonSerializer.Deserialize<CollectionGameThemeMarker>(File.ReadAllText(markerPath), JsonOptions);
            return marker != null;
        }
        catch
        {
            return false;
        }
    }

    private CollectionGenerationMode ResolveCollectionGenerationMode()
    {
        var dynamicEnabled = _runtimeOptions.AreCollectionPackDynamicCollectionsEnabled();
        var staticEnabled = _runtimeOptions.AreCollectionPackStaticCollectionsEnabled();
        if (dynamicEnabled && staticEnabled)
        {
            if (!_warnedCollectionModeConflict)
            {
                _warnedCollectionModeConflict = true;
                _ = NotifyAsync("Collections dynamiques et statiques actives : mode dynamique prioritaire.", CancellationToken.None);
            }

            return new CollectionGenerationMode(Dynamic: true, Static: false);
        }

        return new CollectionGenerationMode(dynamicEnabled, staticEnabled);
    }

    private IEnumerable<CollectionPackage> EnumerateCollectionPackages()
    {
        var packageRoot = ResolvePackageRoot();
        if (!Directory.Exists(packageRoot))
        {
            yield break;
        }

        foreach (var kindDirectory in Directory.EnumerateDirectories(packageRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var kind = Path.GetFileName(kindDirectory).Trim().ToLowerInvariant();
            if (!IsConfiguredCollectionKind(kind))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(kindDirectory, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(path => PackExtensions.Contains(Path.GetExtension(path)))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return new CollectionPackage(file, kind);
            }
        }
    }

    private static string ResolveContentRoot(string tempRoot, string collectionName)
    {
        var directories = Directory.EnumerateDirectories(tempRoot, "*", SearchOption.TopDirectoryOnly).ToList();
        var files = Directory.EnumerateFiles(tempRoot, "*", SearchOption.TopDirectoryOnly).ToList();
        if (files.Count == 0 && directories.Count == 1)
        {
            return directories[0];
        }

        var matchingDirectory = directories.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), collectionName, StringComparison.OrdinalIgnoreCase));
        return matchingDirectory ?? tempRoot;
    }

    private IEnumerable<ResolvedCollectionPackInstallTarget> ResolveCollectionPackInstallTargets(string kind, string collectionName)
    {
        foreach (var rule in ResolveCollectionThemeInstallationRules())
        {
            foreach (var installTarget in rule.CollectionInstallTargets
                         .Where(target => target.Enabled && string.Equals(target.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var themeSet in ResolveThemeSets(rule))
                {
                    var path = ExpandCollectionInstallPath(installTarget.Path, themeSet, collectionName, kind);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    yield return new ResolvedCollectionPackInstallTarget(
                        rule.Name,
                        themeSet,
                        path,
                        ResolveTemplate(rule.GameThemePath, DefaultGameThemePathTemplate),
                        ResolveTemplate(rule.CanonicalThemePath, DefaultCanonicalThemePathTemplate));
                }
            }
        }
    }

    private IEnumerable<CollectionPackInstallTarget> ResolveRuntimeGameThemeTargets()
    {
        foreach (var rule in ResolveCollectionThemeInstallationRules())
        {
            foreach (var themeSet in ResolveThemeSets(rule))
            {
                yield return new CollectionPackInstallTarget
                {
                    ThemeSet = themeSet,
                    DeploymentName = rule.Name,
                    GameThemePath = ResolveTemplate(rule.GameThemePath, DefaultGameThemePathTemplate),
                    CanonicalThemePath = ResolveTemplate(rule.CanonicalThemePath, DefaultCanonicalThemePathTemplate)
                };
            }
        }
    }

    private IReadOnlyList<ApiExposeOptions.CollectionPackThemeInstallationOptions> ResolveCollectionThemeInstallationRules()
    {
        var rules = _options.CurrentValue.CollectionPackManager.ThemeInstallations
            .Where(rule => rule.Enabled)
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Name))
            .GroupBy(rule => rule.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        return rules.Count > 0
            ? rules
            : new ApiExposeOptions.CollectionPackManagerOptions().ThemeInstallations;
    }

    private IEnumerable<string> ResolveThemeSets(ApiExposeOptions.CollectionPackThemeInstallationOptions rule)
    {
        var settings = _settingsService.GetScrapingSettings();
        var activeTheme = settings.ThemeSet.Trim();
        var activeMatcher = ResolveTemplate(rule.ActiveThemeMatcher, "*hyperbat*");
        var searchPattern = ResolveTemplate(rule.ThemeDirectorySearchPattern, "*hyperbat*");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(activeTheme) &&
            IsWildcardMatch(activeTheme, activeMatcher) &&
            Directory.Exists(Path.Combine(RetroBatPaths.EmulationStationThemesRoot, activeTheme)) &&
            seen.Add(activeTheme))
        {
            yield return activeTheme;
        }

        if (!Directory.Exists(RetroBatPaths.EmulationStationThemesRoot))
        {
            yield break;
        }

        foreach (var themeDirectory in Directory.EnumerateDirectories(RetroBatPaths.EmulationStationThemesRoot, searchPattern, SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var themeSet = Path.GetFileName(themeDirectory);
            if (!string.IsNullOrWhiteSpace(themeSet) && seen.Add(themeSet))
            {
                yield return themeSet;
            }
        }
    }

    private bool EnsureCollectionSettings(string collectionName)
    {
        return _settingsStore.Update(document =>
        {
            var root = document.Root ?? new XElement("config");
            if (document.Root == null)
            {
                document.Add(root);
            }

            var changed = UpsertStringListSetting(root, "CollectionSystemsCustom", collectionName);
            changed |= UpsertBoolSetting(root, "HideUniqueGroups", value: false);
            return changed;
        });
    }

    private static bool EnsureCollectionConfig(
        string collectionName,
        IReadOnlyList<string> familyNames,
        CollectionGenerationMode generationMode)
    {
        var collectionsRoot = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "collections");
        Directory.CreateDirectory(collectionsRoot);
        var configPath = Path.Combine(collectionsRoot, "custom-" + collectionName + ".cfg");
        var filterPath = Path.Combine(collectionsRoot, collectionName + ".xcc");
        var changed = false;
        if (!generationMode.Static && File.Exists(configPath))
        {
            File.Delete(configPath);
            changed = true;
        }

        if (generationMode.Dynamic)
        {
            var families = familyNames.Count > 0 ? familyNames : [collectionName];
            var document = new XDocument(
                new XDeclaration("1.0", null, null),
                new XElement(
                    "filter",
                    new XAttribute("name", collectionName),
                    families.Select(family => new XElement("family", family))));
            var newXml = $"{document.Declaration}{Environment.NewLine}{document}";
            if (!File.Exists(filterPath) ||
                !string.Equals(File.ReadAllText(filterPath).Trim(), newXml.Trim(), StringComparison.Ordinal))
            {
                File.WriteAllText(filterPath, newXml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                changed = true;
            }

            return changed;
        }

        if (!generationMode.Static)
        {
            return changed;
        }

        var gamePaths = ResolveCollectionGamePaths(collectionName, familyNames);
        var newContent = string.Join(Environment.NewLine, gamePaths) + (gamePaths.Count > 0 ? Environment.NewLine : string.Empty);
        if (!File.Exists(configPath) ||
            !string.Equals(File.ReadAllText(configPath), newContent, StringComparison.Ordinal))
        {
            File.WriteAllText(configPath, newContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            changed = true;
        }

        if (File.Exists(filterPath))
        {
            File.Delete(filterPath);
            changed = true;
        }

        return changed;
    }

    private static bool UpsertStringListSetting(XElement root, string key, string value)
    {
        var existing = root.Elements().FirstOrDefault(element =>
            string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            root.Add(new XText(Environment.NewLine + "  "));
            root.Add(new XElement("string", new XAttribute("name", key), new XAttribute("value", value)));
            return true;
        }

        existing.Name = "string";
        var current = existing.Attribute("value")?.Value ?? string.Empty;
        var values = current
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var matchingExisting = values
            .Where(existingValue => string.Equals(existingValue, value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingExisting.Count == 1 && string.Equals(matchingExisting[0], value, StringComparison.Ordinal))
        {
            return false;
        }

        values.RemoveAll(existingValue => string.Equals(existingValue, value, StringComparison.OrdinalIgnoreCase));
        values.Add(value);
        existing.SetAttributeValue("value", string.Join(",", values));
        return true;
    }

    private static bool UpsertBoolSetting(XElement root, string key, bool value)
    {
        var normalizedValue = value ? "true" : "false";
        var existing = root.Elements().FirstOrDefault(element =>
            string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            root.Add(new XText(Environment.NewLine + "  "));
            root.Add(new XElement("bool", new XAttribute("name", key), new XAttribute("value", normalizedValue)));
            return true;
        }

        var changed = !string.Equals(existing.Name.LocalName, "bool", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.Attribute("value")?.Value, normalizedValue, StringComparison.OrdinalIgnoreCase);
        existing.Name = "bool";
        existing.SetAttributeValue("value", normalizedValue);
        return changed;
    }

    private IReadOnlyList<string> ResolveFamilyNames(string collectionName)
    {
        var comparable = NormalizeComparable(collectionName);
        if (_familyIndex.TryGetValue(comparable, out var indexedFamily) &&
            !string.IsNullOrWhiteSpace(indexedFamily.CanonicalFamily))
        {
            var families = indexedFamily.RelatedFamilies.Count > 0
                ? indexedFamily.RelatedFamilies
                : [indexedFamily.CanonicalFamily];
            return families
                .SelectMany(BuildFamilyFilterValues)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var names = new List<string> { collectionName };
        foreach (var gamelist in Directory.Exists(RetroBatPaths.RomsRoot)
                     ? Directory.EnumerateFiles(RetroBatPaths.RomsRoot, "gamelist.xml", SearchOption.AllDirectories)
                     : Enumerable.Empty<string>())
        {
            try
            {
                using var stream = File.Open(gamelist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var document = XDocument.Load(stream);
                foreach (var family in document.Descendants("family").Select(element => (element.Value ?? string.Empty).Trim()))
                {
                    if (!string.IsNullOrWhiteSpace(family) &&
                        string.Equals(NormalizeComparable(family), comparable, StringComparison.OrdinalIgnoreCase) &&
                        !names.Contains(family, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(family);
                    }
                }
            }
            catch
            {
                // Ignore gamelists currently being written.
            }
        }

        var titleCase = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(collectionName.ToLowerInvariant());
        if (!names.Contains(titleCase, StringComparer.Ordinal))
        {
            names.Add(titleCase);
        }

        return names
            .SelectMany(BuildFamilyFilterValues)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> BuildFamilyFilterValues(string family)
    {
        var trimmed = (family ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        return [trimmed.ToUpperInvariant()];
    }

    private Dictionary<string, CollectionFamilyIndexEntry> BuildAndSaveFamilyIndex()
    {
        var variantsByNormalizedKey = new Dictionary<string, Dictionary<string, CollectionFamilyVariant>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(RetroBatPaths.RomsRoot))
        {
            foreach (var gamelist in Directory.EnumerateFiles(RetroBatPaths.RomsRoot, "gamelist.xml", SearchOption.AllDirectories))
            {
                try
                {
                    using var stream = File.Open(gamelist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var document = XDocument.Load(stream);
                    var system = Path.GetFileName(Path.GetDirectoryName(gamelist) ?? string.Empty);
                    foreach (var family in document.Descendants("game")
                                 .Select(game => (game.Element("family")?.Value ?? string.Empty).Trim())
                                 .Where(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        var normalizedKey = NormalizeComparable(family);
                        if (string.IsNullOrWhiteSpace(normalizedKey))
                        {
                            continue;
                        }

                        if (!variantsByNormalizedKey.TryGetValue(normalizedKey, out var variants))
                        {
                            variants = new Dictionary<string, CollectionFamilyVariant>(StringComparer.Ordinal);
                            variantsByNormalizedKey[normalizedKey] = variants;
                        }

                        if (!variants.TryGetValue(family, out var variant))
                        {
                            variant = new CollectionFamilyVariant { Family = family };
                            variants[family] = variant;
                        }

                        variant.Count++;
                        if (!string.IsNullOrWhiteSpace(system) && !variant.Systems.Contains(system, StringComparer.OrdinalIgnoreCase))
                        {
                            variant.Systems.Add(system);
                        }
                    }
                }
                catch
                {
                    // Ignore gamelists currently being written.
                }
            }
        }

        var entries = variantsByNormalizedKey
            .Select(group =>
            {
                var variants = group.Value.Values
                    .OrderByDescending(variant => variant.Count)
                    .ThenBy(variant => variant.Family, StringComparer.Ordinal)
                    .ToList();
                var canonical = variants.FirstOrDefault();
                return new CollectionFamilyIndexEntry
                {
                    NormalizedKey = group.Key,
                    CanonicalFamily = canonical?.Family ?? group.Key,
                    Count = variants.Sum(variant => variant.Count),
                    Systems = variants
                        .SelectMany(variant => variant.Systems)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(system => system, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    Variants = variants
                        .Select(variant => new CollectionFamilyVariant
                        {
                            Family = variant.Family,
                            Count = variant.Count,
                            Systems = variant.Systems
                                .OrderBy(system => system, StringComparer.OrdinalIgnoreCase)
                                .ToList()
                        })
                        .ToList()
                };
            })
            .OrderBy(entry => entry.NormalizedKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in entries)
        {
            entry.RelatedFamilies = ResolveRelatedFamilies(entry, entries);
        }

        var lines = entries
            .Select(entry => JsonSerializer.Serialize(entry, JsonLineOptions))
            .ToList();
        if (TryWriteFamilyIndex(lines))
        {
            _logger?.LogInformation("Collection family index generated: {Path}, families={Count}", FamilyIndexPath, entries.Count);
        }
        else
        {
            _logger?.LogWarning(
                "Collection family index could not be written because the file is locked: {Path}. Runtime keeps the in-memory index and startup continues.",
                FamilyIndexPath);
        }

        return entries.ToDictionary(entry => entry.NormalizedKey, StringComparer.OrdinalIgnoreCase);
    }

    private bool TryWriteFamilyIndex(IReadOnlyList<string> lines)
    {
        var directory = Path.GetDirectoryName(FamilyIndexPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var tempPath = FamilyIndexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(tempPath, lines, encoding);
                if (File.Exists(FamilyIndexPath))
                {
                    File.Replace(tempPath, FamilyIndexPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, FamilyIndexPath);
                }

                return true;
            }
            catch (IOException ex) when (attempt < 5)
            {
                _logger?.LogDebug(ex, "Collection family index write attempt {Attempt} failed for {Path}.", attempt, FamilyIndexPath);
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException ex) when (attempt < 5)
            {
                _logger?.LogDebug(ex, "Collection family index write attempt {Attempt} failed for {Path}.", attempt, FamilyIndexPath);
                Thread.Sleep(150);
            }
            catch (IOException ex)
            {
                _logger?.LogDebug(ex, "Collection family index write failed for {Path}.", FamilyIndexPath);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger?.LogDebug(ex, "Collection family index write failed for {Path}.", FamilyIndexPath);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best effort cleanup for a startup cache file.
                }
            }
        }

        return false;
    }

    private static List<string> ResolveRelatedFamilies(
        CollectionFamilyIndexEntry baseEntry,
        IReadOnlyList<CollectionFamilyIndexEntry> entries)
    {
        var baseSegments = SplitFamilySegments(baseEntry.CanonicalFamily);
        var baseSegment = baseSegments.Count > 0 ? baseSegments[0] : baseEntry.CanonicalFamily;
        var baseKey = NormalizeComparable(baseSegment);
        if (string.IsNullOrWhiteSpace(baseKey))
        {
            return [baseEntry.CanonicalFamily];
        }

        return entries
            .Where(entry => IsRelatedFamily(entry.CanonicalFamily, baseKey))
            .OrderBy(entry => GetFamilyRelationRank(entry.CanonicalFamily, baseKey))
            .ThenBy(entry => entry.NormalizedKey, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.CanonicalFamily)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsRelatedFamily(string family, string baseKey)
    {
        var segments = SplitFamilySegments(family);
        if (segments.Any(segment => string.Equals(NormalizeComparable(segment), baseKey, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return segments.Count == 1 && IsSingleSegmentLicenseExpansion(NormalizeComparable(segments[0]), baseKey);
    }

    private static int GetFamilyRelationRank(string family, string baseKey)
    {
        var segments = SplitFamilySegments(family);
        if (segments.Count == 1 && string.Equals(NormalizeComparable(segments[0]), baseKey, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (segments.Any(segment => string.Equals(NormalizeComparable(segment), baseKey, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (segments.Count == 1 && IsSingleSegmentLicenseExpansion(NormalizeComparable(segments[0]), baseKey))
        {
            return 2;
        }

        return 9;
    }

    private static bool IsSingleSegmentLicenseExpansion(string segmentKey, string baseKey)
    {
        return segmentKey.StartsWith(baseKey, StringComparison.OrdinalIgnoreCase) ||
            segmentKey.StartsWith("super" + baseKey, StringComparison.OrdinalIgnoreCase) ||
            segmentKey.StartsWith("dr" + baseKey, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitFamilySegments(string family)
    {
        return (family ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static IReadOnlyList<string> ResolveCollectionGamePaths(string collectionName, IReadOnlyList<string> familyNames)
    {
        var comparables = new HashSet<string>(
            (familyNames.Count > 0 ? familyNames : [collectionName])
                .Select(NormalizeComparable)
                .Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (comparables.Count == 0 || !Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return paths.ToList();
        }

        foreach (var gamelist in Directory.EnumerateFiles(RetroBatPaths.RomsRoot, "gamelist.xml", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.Open(gamelist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var document = XDocument.Load(stream);
                var gamelistDirectory = Path.GetDirectoryName(gamelist) ?? RetroBatPaths.RomsRoot;
                foreach (var game in document.Descendants("game"))
                {
                    var family = (game.Element("family")?.Value ?? string.Empty).Trim();
                    var path = (game.Element("path")?.Value ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(path) || !comparables.Contains(NormalizeComparable(family)))
                    {
                        continue;
                    }

                    paths.Add(ToEsGenericPath(Path.GetFullPath(Path.Combine(gamelistDirectory, path))));
                }
            }
            catch
            {
                // Ignore gamelists currently being written.
            }
        }

        return paths.ToList();
    }

    private static IReadOnlyList<CollectionGameCandidate> ResolveCollectionGames(string collectionName, IReadOnlyList<string> familyNames)
    {
        var comparables = new HashSet<string>(
            (familyNames.Count > 0 ? familyNames : [collectionName])
                .Select(NormalizeComparable)
                .Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        var games = new Dictionary<string, CollectionGameCandidate>(StringComparer.OrdinalIgnoreCase);
        if (comparables.Count == 0 || !Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return [];
        }

        foreach (var gamelist in Directory.EnumerateFiles(RetroBatPaths.RomsRoot, "gamelist.xml", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.Open(gamelist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var document = XDocument.Load(stream);
                var systemId = Path.GetFileName(Path.GetDirectoryName(gamelist) ?? string.Empty);
                if (string.IsNullOrWhiteSpace(systemId))
                {
                    continue;
                }

                foreach (var game in document.Descendants("game"))
                {
                    var family = (game.Element("family")?.Value ?? string.Empty).Trim();
                    var path = (game.Element("path")?.Value ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(path) || !comparables.Contains(NormalizeComparable(family)))
                    {
                        continue;
                    }

                    var romStem = ResolveRomStem(path);
                    if (string.IsNullOrWhiteSpace(romStem))
                    {
                        continue;
                    }

                    var key = systemId + "|" + romStem;
                    games.TryAdd(
                        key,
                        new CollectionGameCandidate
                        {
                            SystemId = systemId,
                            RomStem = romStem,
                            GamePath = path,
                            Family = family
                        });
                }
            }
            catch
            {
                // Ignore gamelists currently being written.
            }
        }

        return games.Values
            .OrderBy(game => game.SystemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.RomStem, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CollectionGameCandidate? ResolveCollectionGame(string frontendSystemId, string gamePath)
    {
        if (string.IsNullOrWhiteSpace(frontendSystemId) || string.IsNullOrWhiteSpace(gamePath))
        {
            return null;
        }

        var gamelistPath = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream);
            var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
            var relativePath = ToGameRelativePath(gamePath, systemRoot);
            var normalizedRelative = NormalizePathForCompare(relativePath);
            var normalizedAbsolute = NormalizePathForCompare(gamePath);
            var normalizedFileName = NormalizePathForCompare(Path.GetFileName(gamePath));

            foreach (var game in document.Descendants("game"))
            {
                var path = (game.Element("path")?.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var absoluteFromGamelist = Path.GetFullPath(Path.Combine(systemRoot, path));
                var matches =
                    string.Equals(NormalizePathForCompare(path), normalizedRelative, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizePathForCompare(absoluteFromGamelist), normalizedAbsolute, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizePathForCompare(Path.GetFileName(path)), normalizedFileName, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                {
                    continue;
                }

                var family = (game.Element("family")?.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(family))
                {
                    return null;
                }

                var romStem = ResolveRomStem(path);
                if (string.IsNullOrWhiteSpace(romStem))
                {
                    romStem = ResolveRomStem(gamePath);
                }

                return new CollectionGameCandidate
                {
                    SystemId = frontendSystemId,
                    RomStem = romStem,
                    GamePath = path,
                    Family = family
                };
            }
        }
        catch
        {
            // Ignore gamelist files currently being written.
        }

        return null;
    }

    private static string ResolveRomStem(string gamePath)
    {
        var normalized = (gamePath ?? string.Empty)
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim()
            .TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(stem) ? fileName : stem;
    }

    private static string NormalizeRomName(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static bool IsZipArchive(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            return header[0] == 0x50 &&
                header[1] == 0x4B &&
                (header[2] is 0x03 or 0x05 or 0x07) &&
                (header[3] is 0x04 or 0x06 or 0x08);
        }
        catch
        {
            return false;
        }
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

    private static string EnsureEsRelativePrefix(string value)
    {
        var normalized = (value ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            return normalized;
        }

        return "./" + normalized.TrimStart('/');
    }

    private static string NormalizePathForCompare(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .TrimStart('.', '/')
            .ToLowerInvariant();
    }

    private static string ToEsGenericPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        if (string.Equals(Path.GetExtension(archivePath), ".rar", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await ExtractWithTarAsync(archivePath, destinationDirectory, cancellationToken);
                return;
            }
            catch
            {
                // Some RAR-like files still need 7za; fall through to the generic extractor.
            }
        }

        var sevenZipPath = Path.Combine(RetroBatPaths.RetroBatRoot, "system", "tools", "7za.exe");
        if (File.Exists(sevenZipPath))
        {
            await ExtractWith7ZipAsync(sevenZipPath, archivePath, destinationDirectory, cancellationToken);
            return;
        }

        await ExtractWithTarAsync(archivePath, destinationDirectory, cancellationToken);
    }

    private static async Task ExtractWith7ZipAsync(
        string sevenZipPath,
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-o" + destinationDirectory);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start 7za.exe.");
        var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Collection archive extraction failed for {archivePath}: {await stdErr}");
        }

        await stdOut;
    }

    private static async Task ExtractWithTarAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tar.exe",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-xf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destinationDirectory);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start tar.exe.");
        var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Collection archive extraction failed for {archivePath}: {await stdErr}");
        }

        await stdOut;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool IsSafeSubPath(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate);
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWildcardMatch(string value, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private CollectionPackIndexEntry? FindReusablePack(string packagePath, string sha256)
    {
        return _index.Packs.FirstOrDefault(pack =>
            string.Equals(pack.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pack.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pack.ImporterVersion, ImporterVersion, StringComparison.Ordinal));
    }

    private static bool IsPackCurrent(
        CollectionPackIndexEntry existing,
        CollectionPackage package,
        string collectionName,
        string sha256,
        IReadOnlyList<ResolvedCollectionPackInstallTarget> installTargets,
        IReadOnlyList<string> familyNames,
        CollectionGenerationMode generationMode)
    {
        if (!string.Equals(existing.Kind, package.Kind, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.CollectionName, collectionName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var target in installTargets)
        {
            if (!Directory.Exists(target.Path) ||
                !Directory.EnumerateFileSystemEntries(target.Path, "*", SearchOption.AllDirectories).Any())
            {
                return false;
            }
        }

        var collectionsRoot = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "collections");
        var configPath = Path.Combine(collectionsRoot, "custom-" + collectionName + ".cfg");
        var filterPath = Path.Combine(collectionsRoot, collectionName + ".xcc");
        if (generationMode.Dynamic)
        {
            if (!File.Exists(filterPath) || File.Exists(configPath))
            {
                return false;
            }

            var filterText = File.ReadAllText(filterPath);
            return familyNames.Count == 0 ||
                familyNames.All(family => filterText.Contains($"<family>{family}</family>", StringComparison.Ordinal));
        }

        if (generationMode.Static)
        {
            return File.Exists(configPath) && !File.Exists(filterPath);
        }

        if (File.Exists(configPath) || File.Exists(filterPath))
        {
            return false;
        }

        return true;
    }

    private void UpsertPack(CollectionPackIndexEntry entry)
    {
        _index.Packs.RemoveAll(pack => string.Equals(pack.PackagePath, entry.PackagePath, StringComparison.OrdinalIgnoreCase));
        _index.Packs.Add(entry);
        _index.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void PruneMissingPackages(IEnumerable<string> packagePaths)
    {
        var existingPaths = new HashSet<string>(packagePaths, StringComparer.OrdinalIgnoreCase);
        _index.Packs.RemoveAll(pack => !existingPaths.Contains(pack.PackagePath));
        _index.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static CollectionPackInstallerIndex LoadIndex()
    {
        try
        {
            return File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<CollectionPackInstallerIndex>(File.ReadAllText(IndexPath), JsonOptions) ?? new CollectionPackInstallerIndex()
                : new CollectionPackInstallerIndex();
        }
        catch
        {
            return new CollectionPackInstallerIndex();
        }
    }

    private void SaveIndex()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(_index, JsonOptions));
    }

    private Dictionary<string, string> ReadInstallerSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _settingsStore.ReadAllSettings())
        {
            if (ObservedSettingNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                settings[pair.Key] = pair.Value.Trim();
            }
        }

        return settings;
    }

    private static string ComputeInstallerSettingsSignature(IReadOnlyDictionary<string, string> values)
    {
        var joined = string.Join(
            "\n",
            ObservedSettingNames.Select(key => key + "=" + NormalizeInstallerSettingValue(key, values.GetValueOrDefault(key))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static string NormalizeInstallerSettingValue(string key, string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (!key.StartsWith("global.apiexpose.", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return normalized is "1" or "true" or "yes" or "on" ? "1" : "0";
    }

    private string ResolvePackageRoot()
    {
        var configured = _options.CurrentValue.CollectionPackManager.PackageRootPath;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(RetroBatPaths.PluginRoot, "package-installer", "collections")
            : ResolvePath(configured);
    }

    private bool IsConfiguredCollectionKind(string kind)
    {
        return ResolveCollectionThemeInstallationRules()
            .SelectMany(rule => rule.CollectionInstallTargets)
            .Any(target => target.Enabled && string.Equals(target.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveGameThemeDestinationRoot(CollectionPackInstallTarget target, CollectionGameCandidate game)
    {
        return ExpandCollectionPath(
            ResolveTemplate(target.GameThemePath, DefaultGameThemePathTemplate),
            target.ThemeSet,
            collectionName: string.Empty,
            kind: string.Empty,
            frontendSystem: game.SystemId,
            rom: game.RomStem);
    }

    private static string ResolveCanonicalThemeDestinationRoot(CollectionPackInstallTarget target, string systemId, string relatedSlug)
    {
        return ExpandCollectionPath(
            ResolveTemplate(target.CanonicalThemePath, DefaultCanonicalThemePathTemplate),
            target.ThemeSet,
            collectionName: string.Empty,
            kind: string.Empty,
            frontendSystem: systemId,
            rom: relatedSlug);
    }

    private static string ExpandCollectionInstallPath(string template, string themeSet, string collectionName, string kind)
    {
        return ExpandCollectionPath(
            template,
            themeSet,
            collectionName,
            kind,
            frontendSystem: string.Empty,
            rom: string.Empty);
    }

    private static string ExpandCollectionPath(
        string template,
        string themeSet,
        string collectionName,
        string kind,
        string frontendSystem,
        string rom)
    {
        var expanded = ResolveTemplate(template, string.Empty)
            .Replace("{PluginRoot}", RetroBatPaths.PluginRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{RetroBatRoot}", RetroBatPaths.RetroBatRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{EmulationStationThemesRoot}", RetroBatPaths.EmulationStationThemesRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{EmulationStationConfigRoot}", RetroBatPaths.EmulationStationConfigRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{themeSet}", themeSet, StringComparison.OrdinalIgnoreCase)
            .Replace("{collection}", collectionName, StringComparison.OrdinalIgnoreCase)
            .Replace("{kind}", kind, StringComparison.OrdinalIgnoreCase)
            .Replace("{frontendSystem}", frontendSystem, StringComparison.OrdinalIgnoreCase)
            .Replace("{system}", frontendSystem, StringComparison.OrdinalIgnoreCase)
            .Replace("{romStem}", rom, StringComparison.OrdinalIgnoreCase)
            .Replace("{rom}", rom, StringComparison.OrdinalIgnoreCase)
            .Replace("{canonicalRom}", rom, StringComparison.OrdinalIgnoreCase);

        return ResolvePath(expanded);
    }

    private static string ResolvePath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, configuredPath));
    }

    private static string ResolveTemplate(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private async Task NotifyAsync(string message, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("{Message}", message);
        await _notifications.NotifyAsync(message, cancellationToken);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeCollectionName(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeComparable(string value)
    {
        return new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static readonly string[] InstallerSettingNames =
    [
        "global.apiexpose.collections_pack_manager.enabled",
        "global.apiexpose.collections_pack_manager.pack_installer.enabled",
        "global.apiexpose.collections_pack_manager.dynamic_collections.enabled",
        "global.apiexpose.collections_pack_manager.static_collections.enabled",
        "global.apiexpose.collections_pack_manager.apply_collection_theme_to_games.enabled"
    ];

    private static readonly string[] ObservedSettingNames =
    [
        .. InstallerSettingNames,
        "CollectionSystemsCustom",
        "HideUniqueGroups",
        "UseCustomCollectionsSystemEx"
    ];

    private const string DefaultGameThemePathTemplate = "{EmulationStationThemesRoot}/{themeSet}/_gametheme/{frontendSystem}/hyperbat/{rom}";
    private const string DefaultCanonicalThemePathTemplate = "{EmulationStationThemesRoot}/{themeSet}/_gametheme/_canonical/hyperbat/{frontendSystem}/{rom}";
    private static string TempRoot => Path.Combine(RetroBatPaths.PluginRoot, "temp", "package-installer", "collections");
    private static string IndexPath => Path.Combine(RetroBatPaths.PluginRoot, "logs", "package-installer", "collections-index.json");
    private static string FamilyIndexPath => Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "gamelist_family.jsonl");
}

public sealed class CollectionFamilyIndexEntry
{
    public string NormalizedKey { get; set; } = string.Empty;
    public string CanonicalFamily { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<string> Systems { get; set; } = new();
    public List<string> RelatedFamilies { get; set; } = new();
    public List<CollectionFamilyVariant> Variants { get; set; } = new();
}

public sealed class CollectionFamilyVariant
{
    public string Family { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<string> Systems { get; set; } = new();
}

public sealed class CollectionPackInstallerIndex
{
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<CollectionPackIndexEntry> Packs { get; set; } = new();
}

public sealed class CollectionPackIndexEntry
{
    public string PackagePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string CollectionName { get; set; } = string.Empty;
    public string ImporterVersion { get; set; } = string.Empty;
    public DateTime InstalledAtUtc { get; set; }
    public List<CollectionPackInstallTarget> Targets { get; set; } = new();
}

public sealed class CollectionPackInstallTarget
{
    public string DeploymentName { get; set; } = string.Empty;
    public string ThemeSet { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string GameThemePath { get; set; } = string.Empty;
    public string CanonicalThemePath { get; set; } = string.Empty;
}

internal sealed record CollectionPackage(string Path, string Kind);

internal sealed record CollectionGenerationMode(bool Dynamic, bool Static);

internal sealed record ResolvedCollectionPackInstallTarget(
    string DeploymentName,
    string ThemeSet,
    string Path,
    string GameThemePath,
    string CanonicalThemePath)
{
    public CollectionPackInstallTarget ToIndexTarget()
    {
        return new CollectionPackInstallTarget
        {
            DeploymentName = DeploymentName,
            ThemeSet = ThemeSet,
            Path = Path,
            GameThemePath = GameThemePath,
            CanonicalThemePath = CanonicalThemePath
        };
    }
}

internal sealed record CollectionPackInstallResult(bool Changed)
{
    public static CollectionPackInstallResult NoChange { get; } = new(false);
}

internal sealed record CollectionThemeApplyResult(bool Handled, bool Changed)
{
    public static CollectionThemeApplyResult NotHandled { get; } = new(false, false);
    public static CollectionThemeApplyResult HandledNoChange { get; } = new(true, false);
}

internal sealed class CollectionGameCandidate
{
    public string SystemId { get; set; } = string.Empty;
    public string RomStem { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
}

internal sealed class CollectionGameThemeMarker
{
    public string GeneratorVersion { get; set; } = string.Empty;
    public string CollectionName { get; set; } = string.Empty;
    public string ThemeSet { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string RomStem { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string SourceThemePath { get; set; } = string.Empty;
    public string SourceThemeSha256 { get; set; } = string.Empty;
    public string GeneratedThemeSha256 { get; set; } = string.Empty;
    public List<string> Families { get; set; } = new();
    public DateTime GeneratedAtUtc { get; set; }
}

internal sealed class CanonicalGameThemeSourceMarker
{
    public string GeneratorVersion { get; set; } = string.Empty;
    public string SourceArchivePath { get; set; } = string.Empty;
    public string SourceArchiveSha256 { get; set; } = string.Empty;
    public DateTime ExtractedAtUtc { get; set; }
}

internal sealed class CanonicalGameThemeLinkMarker
{
    public string GeneratorVersion { get; set; } = string.Empty;
    public string ThemeSet { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string RomStem { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string RelatedSlug { get; set; } = string.Empty;
    public string CanonicalThemePath { get; set; } = string.Empty;
    public string SourceArchiveSha256 { get; set; } = string.Empty;
    public string GeneratedThemeSha256 { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
}
