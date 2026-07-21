using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;
using Microsoft.Extensions.Options;
using RetroBat.Api.Controllers;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Providers.EmulationStation;

namespace RetroBat.Api.Media;

public sealed class RomPackInstallerService : IHostedService, IDisposable
{
    private const string ImporterVersion = "20260522-multisystem-arcade-samples-v10";
    private const int StartupScanStateVersion = 1;
    private const long MaxZipLocalHeaderScanBytes = 1024L * 1024L * 1024L;
    private const string OnTheFlyPlaceholderMarker = "APIEXPOSE_ON_THE_FLY_ROM_PLACEHOLDER_v1";
    private const string OnTheFlyExtractionProgressTaskId = "rom-pack-on-the-fly-extraction";
    private const int OnTheFlyPlaceholderWriteRetryCount = 8;
    private static readonly TimeSpan OnTheFlyPlaceholderWriteRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly string[] LocalMediaManagedVisibleSlots = ["image", "marquee", "thumbnail", "fanart"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LogJsonOptions = new() { WriteIndented = false };
    private static readonly HashSet<string> PackExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };
    private static readonly HashSet<string> RomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".iso", ".cue", ".chd", ".m3u", ".nes", ".sfc", ".smc", ".bin", ".md", ".gen",
        ".cpr", ".dsk",
        ".32x", ".gba", ".gbc", ".gb", ".n64", ".z64", ".v64", ".nds", ".cdi", ".gdi", ".pbp"
    };
    private static readonly IReadOnlyDictionary<string, string> PackSystemFolderAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["toaplan_cave_stg"] = "cave",
        ["toaplan-cave-stg"] = "cave",
        ["mame-current"] = "mame",
        ["mame_standalone"] = "mame",
        ["mame-standalone"] = "mame"
    };
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly IOptionsMonitor<EmulationStationWatcherOptions> _emulationStationWatcherOptions;
    private readonly IEventBus _eventBus;
    private readonly IEmulationStationNotificationService _notifications;
    private readonly IStartupOverlayService _startupOverlay;
    private readonly ITaskProgressService _taskProgress;
    private readonly MediaRuntimeState _runtimeState;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly LocalGamelistUpdateService _localGamelistUpdateService;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly IEsSettingsChangeBus _settingsChangeBus;
    private readonly ILogger<RomPackInstallerService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDisposable? _subscription;
    private IDisposable? _settingsSubscription;
    private readonly object _gameSelectedDebounceLock = new();
    private CancellationTokenSource? _gameSelectedDebounceCts;
    private string _lastInstallerSettingsSignature = string.Empty;
    private Dictionary<string, string> _lastInstallerSettings = new(StringComparer.OrdinalIgnoreCase);
    private RomPackInstallerIndex _index = new();

    private enum OnTheFlyRomExtractionTrigger
    {
        GameSelected,
        GameStart
    }

    public RomPackInstallerService(
        ApiExposeRuntimeOptionsService runtimeOptions,
        IOptionsMonitor<EmulationStationWatcherOptions> emulationStationWatcherOptions,
        IEventBus eventBus,
        IEmulationStationNotificationService notifications,
        IStartupOverlayService startupOverlay,
        ITaskProgressService taskProgress,
        MediaRuntimeState runtimeState,
        GameNameNormalizer gameNameNormalizer,
        LocalGamelistUpdateService localGamelistUpdateService,
        GamelistUpdateService gamelistUpdateService,
        IEsSettingsStore settingsStore,
        IEsSettingsChangeBus settingsChangeBus,
        ILogger<RomPackInstallerService>? logger = null)
    {
        _runtimeOptions = runtimeOptions;
        _emulationStationWatcherOptions = emulationStationWatcherOptions;
        _eventBus = eventBus;
        _notifications = notifications;
        _startupOverlay = startupOverlay;
        _taskProgress = taskProgress;
        _runtimeState = runtimeState;
        _gameNameNormalizer = gameNameNormalizer;
        _localGamelistUpdateService = localGamelistUpdateService;
        _gamelistUpdateService = gamelistUpdateService;
        _settingsStore = settingsStore;
        _settingsChangeBus = settingsChangeBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _index = LoadIndex();
        _subscription = _eventBus.Subscribe<EventEnvelope>(OnEvent);
        _lastInstallerSettings = ReadInstallerSettings() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _lastInstallerSettingsSignature = ComputeInstallerSettingsSignature(_lastInstallerSettings);
        _settingsSubscription = _settingsChangeBus.Subscribe((_, token) => HandleInstallerSettingsChangedAsync(token));
        return InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _settingsSubscription?.Dispose();
        _gameSelectedDebounceCts?.Cancel();
        _gameSelectedDebounceCts?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _settingsSubscription?.Dispose();
        _gameSelectedDebounceCts?.Dispose();
        _gate.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await AppendInstallerLogAsync("startup-scan-start", new { }, cancellationToken);
            await RunConfiguredImportAsync(trigger: "startup", cancellationToken);
            await AppendInstallerLogAsync("startup-scan-complete", new { }, cancellationToken);
            await StartupGamelistPreparationLog.AppendAsync(
                "rom-pack-installer",
                "completed",
                new { elapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "ROM Pack Installer startup scan failed.");
            await AppendInstallerLogAsync(
                "startup-scan-failed",
                new { exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
            await StartupGamelistPreparationLog.AppendAsync(
                "rom-pack-installer",
                "failed",
                new { exceptionType = ex.GetType().FullName, ex.Message, elapsedMs = stopwatch.ElapsedMilliseconds },
                CancellationToken.None);
        }
    }

    public async Task<RomPackInstallerIndex> RescanAsync(CancellationToken cancellationToken)
    {
        await RunConfiguredImportAsync(trigger: "api-rescan", cancellationToken, announceOnTheFly: false);
        return _index;
    }

    private async Task RunConfiguredImportAsync(
        string trigger,
        CancellationToken cancellationToken,
        bool announceOnTheFly = true)
    {
        var onTheFly = _runtimeOptions.IsOnTheFlyRomInstallerEnabled();
        if (!_runtimeOptions.IsRomPackInstallerEnabled() && !onTheFly)
        {
            ReportStartupProgress(1, 1, "inactif");
            _logger?.LogInformation("ROM Pack Installer disabled; package scan skipped. Trigger={Trigger}", trigger);
            await AppendInstallerLogAsync("scan-skipped-disabled", new { trigger, onTheFly }, cancellationToken);
            if (string.Equals(trigger, "startup", StringComparison.OrdinalIgnoreCase))
            {
                await StartupGamelistPreparationLog.AppendAsync(
                    "rom-pack-installer",
                    "skipped",
                    new { reason = "disabled", onTheFly },
                    cancellationToken);
            }
            return;
        }

        var packageSnapshot = BuildPackageDirectorySnapshot(PackageRoot);
        var settingsSignature = BuildEffectiveStartupSettingsSignature(onTheFly);
        var cacheSkipReason = string.Empty;
        var migratedLegacySignature = false;
        var canSkipStartupScan = string.Equals(trigger, "startup", StringComparison.OrdinalIgnoreCase) &&
            TrySkipStartupScanFromCache(packageSnapshot, settingsSignature, onTheFly, out cacheSkipReason);
        if (!canSkipStartupScan &&
            string.Equals(trigger, "startup", StringComparison.OrdinalIgnoreCase) &&
            onTheFly &&
            IsSettingEnabled(_lastInstallerSettings, "ParseGamelistOnly"))
        {
            var legacySettingsSignature = BuildLegacyStartupSettingsSignature(onTheFly);
            canSkipStartupScan = TrySkipStartupScanFromCache(
                packageSnapshot,
                legacySettingsSignature,
                onTheFly,
                out cacheSkipReason);
            migratedLegacySignature = canSkipStartupScan;
        }

        if (canSkipStartupScan)
        {
            if (migratedLegacySignature)
            {
                SaveStartupScanState(packageSnapshot, settingsSignature, onTheFly);
            }

            ReportStartupProgress(1, 1, "cache-hit");
            await AppendInstallerLogAsync(
                "startup-scan-cache-hit",
                new
                {
                    packageSnapshot.PackageCount,
                    packageSnapshot.Fingerprint,
                    settingsSignature,
                    onTheFly,
                    reason = cacheSkipReason
                },
                cancellationToken);
            await StartupGamelistPreparationLog.AppendAsync(
                "rom-pack-installer",
                "cache-hit",
                new
                {
                    packageSnapshot.PackageCount,
                    packageSnapshot.Fingerprint,
                    onTheFly,
                    reason = cacheSkipReason
                },
                cancellationToken);
            return;
        }

        if (onTheFly)
        {
            if (announceOnTheFly)
            {
                ReportStartupProgress(0, 1, "on-the-fly actif: indexation sans extraction");
                LogInstallerProgress("On-the-fly ROM Installer actif - indexation des packs sans installation des ROMs");
            }

            await EnsureParseGamelistOnlyAsync(cancellationToken);
            _lastInstallerSettings = ReadInstallerSettings() ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settingsSignature = BuildEffectiveStartupSettingsSignature(onTheFly);
        }

        await AppendInstallerLogAsync("scan-enabled", new { trigger, onTheFly }, cancellationToken);
        var changed = await ScanAndInstallAsync(onTheFly, cancellationToken);
        if (string.Equals(trigger, "startup", StringComparison.OrdinalIgnoreCase))
        {
            SaveStartupScanState(packageSnapshot, settingsSignature, onTheFly);
            await StartupGamelistPreparationLog.AppendAsync(
                "rom-pack-installer",
                "scan-complete",
                new
                {
                    packageSnapshot.PackageCount,
                    packageSnapshot.Fingerprint,
                    onTheFly,
                    changed
                },
                cancellationToken);
        }
    }

    private async Task HandleInstallerSettingsChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
            await _settingsStore.WaitForStableFileAsync(cancellationToken);
            var currentSettings = ReadInstallerSettings();
            if (currentSettings == null)
            {
                _logger?.LogDebug("ROM Pack Installer settings change ignored: es_settings.cfg is temporarily unreadable.");
                return;
            }

            var signature = ComputeInstallerSettingsSignature(currentSettings);
            if (string.Equals(signature, _lastInstallerSettingsSignature, StringComparison.Ordinal))
            {
                return;
            }

            var announceOnTheFly = !IsOnTheFlySettingEnabled(_lastInstallerSettings) &&
                IsOnTheFlySettingEnabled(currentSettings);
            var shouldScanPackages = ShouldScanPackagesAfterInstallerSettingsChange(_lastInstallerSettings, currentSettings);
            _lastInstallerSettings = currentSettings;
            _lastInstallerSettingsSignature = signature;
            if (!shouldScanPackages)
            {
                _logger?.LogInformation("ROM Pack Installer settings changed without package scan requirement.");
                return;
            }

            await RunConfiguredImportAsync(trigger: "settings-change", cancellationToken, announceOnTheFly);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer settings write.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ROM Pack Installer settings change handling failed.");
        }
    }

    private void OnEvent(EventEnvelope envelope)
    {
        if (string.Equals(envelope.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
        {
            if (!_runtimeOptions.ShouldResetOnTheFlyRomAfterGameEnd())
            {
                return;
            }

            var finishedGame = ExtractGameReference(envelope.Payload);
            if (finishedGame == null ||
                string.IsNullOrWhiteSpace(finishedGame.SystemId) ||
                string.IsNullOrWhiteSpace(finishedGame.GamePath))
            {
                return;
            }

            _ = Task.Run(() => ResetOnTheFlyRomAfterGameEndAsync(finishedGame, CancellationToken.None));
            return;
        }

        if (!string.Equals(envelope.Type, "ui.game.selected", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_runtimeOptions.ShouldExtractOnTheFlyRomOnGameSelected())
        {
            return;
        }

        var selected = ExtractGameReference(envelope.Payload);
        if (selected == null || string.IsNullOrWhiteSpace(selected.SystemId) || string.IsNullOrWhiteSpace(selected.GamePath))
        {
            return;
        }

        CancellationTokenSource? previousDebounceCts;
        CancellationTokenSource debounceCts;
        lock (_gameSelectedDebounceLock)
        {
            previousDebounceCts = _gameSelectedDebounceCts;
            _gameSelectedDebounceCts = new CancellationTokenSource();
            debounceCts = _gameSelectedDebounceCts;
        }

        previousDebounceCts?.Cancel();
        var debounceToken = debounceCts.Token;
        _ = Task.Run(() => DebouncedInstallSelectedMissingRomAsync(selected, debounceCts, debounceToken), debounceToken);
    }

    private async Task DebouncedInstallSelectedMissingRomAsync(
        GameReference selected,
        CancellationTokenSource debounceCts,
        CancellationToken cancellationToken)
    {
        try
        {
            var debounceMs = Math.Clamp(
                _emulationStationWatcherOptions.CurrentValue.GameSelectedLocalProjectionDebounceMs,
                0,
                10000);
            if (debounceMs > 0)
            {
                await Task.Delay(debounceMs, cancellationToken);
            }

            if (!IsCurrentlySelectedGame(selected))
            {
                _logger?.LogDebug(
                    "On-the-fly ROM extraction skipped after debounce: selection changed for system={SystemId}, path={GamePath}",
                    selected.SystemId,
                    selected.GamePath);
                return;
            }

            await TryInstallMissingRomAsync(selected, OnTheFlyRomExtractionTrigger.GameSelected, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug(
                "On-the-fly ROM extraction debounced by newer game-selected: system={SystemId}, path={GamePath}",
                selected.SystemId,
                selected.GamePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "On-the-fly ROM extraction failed after debounce: system={SystemId}, path={GamePath}",
                selected.SystemId,
                selected.GamePath);
        }
        finally
        {
            lock (_gameSelectedDebounceLock)
            {
                if (ReferenceEquals(_gameSelectedDebounceCts, debounceCts))
                {
                    _gameSelectedDebounceCts = null;
                }
            }

            debounceCts.Dispose();
        }
    }

    private async Task ResetOnTheFlyRomAfterGameEndAsync(GameReference finishedGame, CancellationToken cancellationToken)
    {
        try
        {
            var delayMs = _runtimeOptions.GetOnTheFlyRomResetAfterGameEndDelayMs();
            if (delayMs > 0)
            {
                await AppendInstallerLogAsync(
                    "on-the-fly-reset-delayed",
                    new
                    {
                        finishedGame.SystemId,
                        finishedGame.GameName,
                        finishedGame.GamePath,
                        delayMs
                    },
                    cancellationToken);
                await Task.Delay(delayMs, cancellationToken);
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                _index = LoadIndex();
                var romEntry = FindIndexedRom(finishedGame.SystemId, finishedGame.GamePath, finishedGame.GameName);
                if (romEntry == null)
                {
                    return;
                }

                var targetPath = Path.Combine(
                    RetroBatPaths.RomsRoot,
                    romEntry.SystemId,
                    romEntry.DestinationFileName.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(targetPath) || IsOnTheFlyPlaceholder(targetPath))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Path.Combine(RetroBatPaths.RomsRoot, romEntry.SystemId));
                await WriteOnTheFlyPlaceholderWithRetryAsync(targetPath, romEntry, cancellationToken);
                _logger?.LogInformation(
                    "On-the-fly ROM reset to placeholder after game-end: system={SystemId}, game={GameName}, target={TargetPath}",
                    romEntry.SystemId,
                    romEntry.GameName,
                    targetPath);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                ex,
                "On-the-fly ROM reset after game-end failed: system={SystemId}, path={GamePath}",
                finishedGame.SystemId,
                finishedGame.GamePath);
        }
    }

    private async Task<bool> ScanAndInstallAsync(bool onTheFly, CancellationToken cancellationToken)
    {
        var packageRoot = PackageRoot;
        if (!Directory.Exists(packageRoot))
        {
            Directory.CreateDirectory(packageRoot);
            ReportStartupProgress(1, 1, "aucun pack");
            return false;
        }

        var packages = Directory.EnumerateFiles(packageRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => PackExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (packages.Count == 0)
        {
            ReportStartupProgress(1, 1, "aucun pack");
            await AppendInstallerLogAsync("scan-no-packages", new { packageRoot }, cancellationToken);
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AppendInstallerLogAsync(
                "scan-packages-found",
                new { packageRoot, count = packages.Count, packages = packages.Select(Path.GetFileName).ToArray() },
                cancellationToken);
            ReportStartupProgress(0, packages.Count, "scan packages");
            var processed = 0;
            var anyChanged = false;
            var indexChanged = false;
            foreach (var packagePath in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                var fileName = Path.GetFileName(packagePath);
                var fileInfo = new FileInfo(packagePath);
                ReportStartupProgress(processed - 1, packages.Count, $"controle {fileName}");
                await AppendInstallerLogAsync(
                    "package-check",
                    new { packagePath, fileInfo.Length, fileInfo.LastWriteTimeUtc, onTheFly },
                    cancellationToken);

                var existing = FindReusableInstalledPack(packagePath, fileInfo, onTheFly);
                if (existing == null)
                {
                    await AuditIndexedPackReuseInvalidatedAsync(packagePath, fileInfo, onTheFly, "file-info-match", cancellationToken);
                }
                var hash = existing?.Sha256 ?? string.Empty;
                if (existing == null)
                {
                    if (TryGetKnownPackageHash(packagePath, fileInfo, out hash))
                    {
                        ReportStartupProgress(processed - 1, packages.Count, $"hash cache {fileName}");
                        await AppendInstallerLogAsync("package-hash-reused", new { packagePath, hash }, cancellationToken);
                    }
                    else
                    {
                        ReportStartupProgress(processed - 1, packages.Count, $"hash {fileName}");
                        hash = ComputeSha256(packagePath);
                        SaveKnownPackageHash(packagePath, fileInfo, hash);
                    }

                    existing = FindReusableInstalledPack(packagePath, hash, onTheFly);
                    if (existing != null)
                    {
                        existing.Size = fileInfo.Length;
                        existing.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                        indexChanged = true;
                    }
                    else
                    {
                        await AuditIndexedPackReuseInvalidatedAsync(packagePath, fileInfo, onTheFly, "hash-match", cancellationToken, hash);
                    }
                }

                var forceArchiveReindex = RequiresRarListingRepair(packagePath, hash);
                if (existing == null &&
                    !forceArchiveReindex &&
                    TryRebuildMaterializedPackIndex(packagePath, fileInfo, hash, onTheFly, out var rebuilt))
                {
                    UpsertPack(rebuilt);
                    SaveIndex();
                    existing = rebuilt;
                    await AppendInstallerLogAsync(
                        "package-index-rebuilt",
                        new
                        {
                            packagePath,
                            rebuilt.SystemId,
                            roms = rebuilt.Roms.Count,
                            onTheFly,
                            reason = "materialized-runtime"
                        },
                        cancellationToken);
                }

                if (existing != null)
                {
                    if (RefreshReusablePackMetadata(existing, fileInfo))
                    {
                        indexChanged = true;
                    }

                    await AppendInstallerLogAsync(
                        "package-reused",
                        new
                        {
                            packagePath,
                            existing.SystemId,
                            roms = existing.Roms.Count,
                            onTheFly,
                            importerVersion = existing.ImporterVersion
                        },
                        cancellationToken);
                    if (onTheFly)
                    {
                        var runtimeState = ResolveIndexedPackRuntimeState(existing, onTheFly);
                        if (!runtimeState.Reusable)
                        {
                            await AppendInstallerLogAsync(
                                "package-reconciliation-needed",
                                new
                                {
                                    packagePath,
                                    existing.SystemId,
                                    reason = runtimeState.Reason,
                                    runtimeState.GamelistPath,
                                    runtimeState.MediaRoot
                                },
                                cancellationToken);
                            ReportStartupProgress(processed - 1, packages.Count, $"reparation {fileName}");
                            if (RequiresFullPackageReinstall(runtimeState))
                            {
                                await AppendInstallerLogAsync(
                                    "package-reinstall-start",
                                    new
                                    {
                                        packagePath,
                                        hash,
                                        existing.SystemId,
                                        reason = runtimeState.Reason,
                                        onTheFly
                                    },
                                    cancellationToken);
                                ReportStartupProgress(processed - 1, packages.Count, $"reinstallation {fileName}");
                                var reinstalled = await InstallPackageAsync(packagePath, hash, onTheFly, cancellationToken);
                                UpsertPack(reinstalled);
                                SaveIndex();
                                indexChanged = true;
                                anyChanged = true;
                                await AppendInstallerLogAsync(
                                    "package-reinstall-complete",
                                    new
                                    {
                                        packagePath,
                                        reinstalled.SystemId,
                                        roms = reinstalled.Roms.Count,
                                        onTheFly,
                                        reason = runtimeState.Reason
                                    },
                                    cancellationToken);
                            }
                            else
                            {
                                var reconciled = await ReconcileIndexedPackGamelistAsync(existing, cancellationToken);
                                anyChanged |= reconciled;
                            }
                        }
                    }

                    ReportStartupProgress(processed, packages.Count, $"deja indexe: {fileName}");
                    continue;
                }

                ReportStartupProgress(processed - 1, packages.Count, $"installation {fileName}");
                LogInstallerProgress($"Installation du pack {fileName}");
                await AppendInstallerLogAsync("package-install-start", new { packagePath, hash, onTheFly }, cancellationToken);
                RomPackIndexEntry result;
                try
                {
                    result = await InstallPackageAsync(packagePath, hash, onTheFly, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await AppendInstallerLogAsync(
                        "package-install-failed",
                        new { packagePath, exceptionType = ex.GetType().FullName, ex.Message },
                        CancellationToken.None);
                    throw;
                }

                UpsertPack(result);
                SaveIndex();
                await AppendInstallerLogAsync(
                    "package-install-complete",
                    new { packagePath, result.SystemId, roms = result.Roms.Count, result.OnTheFlyMode, result.UnzipRoms },
                    cancellationToken);
                anyChanged = true;
                ReportStartupProgress(processed, packages.Count, $"{result.SystemId}: {result.Roms.Count} jeux");
                LogInstallerProgress($"Pack indexe {fileName} ({result.SystemId}, {result.Roms.Count} jeux)");
            }

            if (indexChanged)
            {
                SaveIndex();
            }

            if (anyChanged)
            {
                _runtimeState.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
            }

            return anyChanged;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RomPackIndexEntry> InstallPackageAsync(
        string packagePath,
        string hash,
        bool onTheFly,
        CancellationToken cancellationToken)
    {
        if (onTheFly)
        {
            return await InstallPackageOnTheFlyAsync(packagePath, hash, cancellationToken);
        }

        var tempRoot = Path.Combine(TempRoot, Path.GetFileNameWithoutExtension(packagePath) + "-" + hash[..12]);
        if (Directory.Exists(tempRoot))
        {
            TryDeleteDirectory(tempRoot);
        }

        Directory.CreateDirectory(tempRoot);
        // A pack import may temporarily materialize ROM entries to qualify missing md5/crc data;
        // the staging directory must never survive the import process.
        try
        {
        await ExtractArchiveAsync(packagePath, tempRoot, cancellationToken);

        var contentRoot = ResolveContentRoot(tempRoot, packagePath);
        var systemId = ResolveSystemId(contentRoot, packagePath);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            throw new InvalidOperationException($"Unable to resolve target system for pack: {packagePath}");
        }

        await AppendInstallerLogAsync(
            "package-content-root-resolved",
            new
            {
                packagePath,
                systemId,
                contentRoot = Path.GetRelativePath(tempRoot, contentRoot).Replace('\\', '/')
            },
            cancellationToken);

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        Directory.CreateDirectory(systemRoot);
        var gamelistPath = Directory.EnumerateFiles(contentRoot, "gamelist.xml", SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
        var document = !string.IsNullOrWhiteSpace(gamelistPath) && File.Exists(gamelistPath)
            ? XDocument.Load(gamelistPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("gameList"));
        var root = document.Root ?? new XElement("gameList");
        if (document.Root == null)
        {
            document.Add(root);
        }

        if (!root.Elements("game").Any())
        {
            foreach (var romPath in EnumeratePackageRomCandidates(contentRoot))
            {
                var relativePath = "./" + Path.GetRelativePath(contentRoot, romPath).Replace('\\', '/');
                root.Add(new XText(Environment.NewLine + "  "));
                root.Add(new XElement(
                    "game",
                    new XElement("path", relativePath),
                    new XElement("name", _gameNameNormalizer.NormalizeDisplayName(null, romPath)),
                    new XElement("md5", ComputeRomMd5(romPath))));
            }
            root.Add(new XText(Environment.NewLine));
        }

        var packEntry = new RomPackIndexEntry
        {
            PackagePath = packagePath,
            Sha256 = hash,
            Size = new FileInfo(packagePath).Length,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(packagePath),
            InstalledAtUtc = DateTime.UtcNow,
            Status = "installed",
            ImporterVersion = ImporterVersion,
            SystemId = systemId,
            OnTheFlyMode = onTheFly,
            UnzipRoms = _runtimeOptions.ShouldUnzipRomPackInstallerRoms()
        };

        var changedGamelist = false;
        foreach (var game in root.Elements("game").ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawPath = game.Element("path")?.Value?.Trim();
            var sourceName = game.Element("name")?.Value?.Trim() ?? Path.GetFileNameWithoutExtension(rawPath);
            var name = _gameNameNormalizer.NormalizeDisplayName(sourceName, rawPath);
            var sourceRom = ResolvePackageRelativeFile(contentRoot, rawPath);
            if (string.IsNullOrWhiteSpace(sourceRom) || !File.Exists(sourceRom))
            {
                continue;
            }

            var destinationRomName = ResolveInstalledRomFileName(sourceRom, tempRoot, onTheFly);
            var destinationRomPath = Path.Combine(systemRoot, destinationRomName);
            if (!onTheFly)
            {
                Directory.CreateDirectory(systemRoot);
                InstallRomFile(sourceRom, destinationRomPath, tempRoot, cancellationToken);
            }

            var relativeGamePath = "./" + destinationRomName.Replace('\\', '/');
            game.SetElementValue("path", relativeGamePath);
            game.SetElementValue("name", name);
            var slug = _gameNameNormalizer.NormalizeGameSlug(name, relativeGamePath);
            packEntry.MediaFilesImportedCount += ImportPackMedia(contentRoot, game, systemId, slug, sourceRom);
            ClearLocalMediaManagedVisibleSlots(game);
            changedGamelist = true;

            var romEntry = new RomPackRomEntry
            {
                SystemId = systemId,
                GameName = name,
                GamePath = relativeGamePath,
                DestinationFileName = destinationRomName,
                PackRelativePath = Path.GetRelativePath(contentRoot, sourceRom).Replace('\\', '/'),
                ArchiveEntryPath = Path.GetRelativePath(tempRoot, sourceRom).Replace('\\', '/'),
                Md5 = NormalizeHex(game.Element("md5")?.Value),
                Crc32 = NormalizeHex(game.Element("crc32")?.Value),
                Size = new FileInfo(sourceRom).Length
            };
            packEntry.Roms.Add(romEntry);
            if (onTheFly)
            {
                EnsureOnTheFlyPlaceholder(systemRoot, romEntry);
            }
        }

        if (root.Elements("game").Any())
        {
            MergeGamelist(systemRoot, root, changedGamelist, cancellationToken);
            if (changedGamelist)
            {
                await _localGamelistUpdateService.UpdateAsync(
                    new LocalGamelistUpdateRequest { Scope = "system", SystemId = systemId, SuppressTaskProgress = true },
                    cancellationToken);
            }
        }

        return packEntry;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<RomPackIndexEntry> InstallPackageOnTheFlyAsync(
        string packagePath,
        string hash,
        CancellationToken cancellationToken)
    {
        var sevenZipPath = GetSevenZipPath();
        if (!File.Exists(sevenZipPath))
        {
            throw new InvalidOperationException($"7za.exe introuvable: {sevenZipPath}");
        }

        var entries = await ListArchiveEntriesAsync(sevenZipPath, packagePath, cancellationToken, romsOnly: false);
        var systemContents = ResolveArchiveSystemContents(entries, packagePath);
        if (systemContents.Count == 0)
        {
            var contentPrefix = ResolveArchiveContentPrefix(entries, packagePath);
            var systemId = ResolveSystemIdFromArchiveContent(contentPrefix, packagePath);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                throw new InvalidOperationException($"Unable to resolve target system for pack: {packagePath}");
            }

            systemContents = [new ArchiveSystemContent(contentPrefix, systemId)];
        }
        RemoveIndexedOnTheFlyPackageArtifacts(packagePath);

        await AppendInstallerLogAsync(
            "package-on-the-fly-index-listing",
            new
            {
                packagePath,
                systemId = systemContents.Count == 1 ? systemContents[0].SystemId : "multi",
                contentRoots = systemContents.Select(content => new { content.SystemId, content.ContentPrefix }).ToArray(),
                entries = entries.Count
            },
            cancellationToken);

        var packEntry = new RomPackIndexEntry
        {
            PackagePath = packagePath,
            Sha256 = hash,
            Size = new FileInfo(packagePath).Length,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(packagePath),
            InstalledAtUtc = DateTime.UtcNow,
            Status = "installed",
            ImporterVersion = ImporterVersion,
            SystemId = systemContents.Count == 1 ? systemContents[0].SystemId : "multi",
            OnTheFlyMode = true,
            UnzipRoms = _runtimeOptions.ShouldUnzipRomPackInstallerRoms()
        };

        var samplesImported = await InstallArchiveSamplesAsync(
            packagePath,
            entries,
            systemContents.Select(content => content.SystemId).ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (samplesImported > 0)
        {
            await AppendInstallerLogAsync(
                "package-archive-samples-installed",
                new { packagePath, samplesImported },
                cancellationToken);
        }

        var mediaTempRoot = Path.Combine(TempRoot, "media-" + Guid.NewGuid().ToString("N"));
        try
        {
            var totalGames = Math.Max(
                1,
                systemContents.Sum(content => entries
                    .Count(entry =>
                        ArchiveEntryIsUnderContentPrefix(entry.Path, content.ContentPrefix) &&
                        IsArchiveRomCandidate(StripArchiveContentPrefix(entry.Path, content.ContentPrefix)))));
            var processedGames = 0;
            foreach (var content in systemContents)
            {
                var systemId = content.SystemId;
                var contentPrefix = content.ContentPrefix;
                var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
                Directory.CreateDirectory(systemRoot);
                var root = await LoadPackageGamelistRootFromArchiveAsync(
                    packagePath,
                    entries,
                    contentPrefix,
                    cancellationToken);
                if (!root.Elements("game").Any())
                {
                    BuildMinimalOnTheFlyGamelist(root, entries, contentPrefix);
                }

                var changedGamelist = false;
                var games = root.Elements("game").ToList();
                foreach (var game in games)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rawPath = game.Element("path")?.Value?.Trim();
                    var sourceName = game.Element("name")?.Value?.Trim() ?? Path.GetFileNameWithoutExtension(rawPath);
                    var sourceEntry = ResolveArchiveRelativeEntry(entries, contentPrefix, rawPath);
                    if (sourceEntry == null)
                    {
                        continue;
                    }

                    var packRelativePath = StripArchiveContentPrefix(sourceEntry.Path, contentPrefix);
                    var destinationRomName = ResolveInstalledRomFileNameFromArchiveEntry(packRelativePath);
                    if (string.IsNullOrWhiteSpace(destinationRomName))
                    {
                        continue;
                    }

                    var relativeGamePath = "./" + destinationRomName.Replace('\\', '/');
                    var name = _gameNameNormalizer.NormalizeDisplayName(sourceName, relativeGamePath);
                    ReportStartupProgress(processedGames, totalGames, $"{systemId}: index {name}");
                    game.SetElementValue("path", relativeGamePath);
                    game.SetElementValue("name", name);
                    await EnsureArchiveRomIdentifiersAsync(
                        packagePath,
                        sourceEntry,
                        mediaTempRoot,
                        game,
                        cancellationToken);
                    var slug = _gameNameNormalizer.NormalizeGameSlug(name, relativeGamePath);
                    packEntry.MediaFilesImportedCount += await ImportPackMediaFromArchiveAsync(
                        packagePath,
                        entries,
                        contentPrefix,
                        mediaTempRoot,
                        game,
                        systemId,
                        slug,
                        packRelativePath,
                        cancellationToken);
                    ClearLocalMediaManagedVisibleSlots(game);
                    changedGamelist = true;

                    var romEntry = new RomPackRomEntry
                    {
                        SystemId = systemId,
                        GameName = name,
                        GamePath = relativeGamePath,
                        DestinationFileName = destinationRomName,
                        PackRelativePath = packRelativePath,
                        ArchiveEntryPath = sourceEntry.Path,
                        Size = sourceEntry.Size,
                        Md5 = NormalizeHex(game.Element("md5")?.Value),
                        Crc32 = NormalizeHex(game.Element("crc32")?.Value)
                    };
                    packEntry.Roms.Add(romEntry);
                    EnsureOnTheFlyPlaceholder(systemRoot, romEntry);
                    processedGames++;
                    ReportStartupProgress(processedGames, totalGames, $"{systemId}: {processedGames}/{totalGames}");
                }

                if (root.Elements("game").Any())
                {
                    MergeGamelist(systemRoot, root, changedGamelist, cancellationToken);
                    if (changedGamelist)
                    {
                        await _localGamelistUpdateService.UpdateAsync(
                            new LocalGamelistUpdateRequest { Scope = "system", SystemId = systemId, SuppressTaskProgress = true },
                            cancellationToken);
                    }
                }
            }
        }
        finally
        {
            TryDeleteDirectory(mediaTempRoot);
        }

        return packEntry;
    }

    private async Task<bool> ReconcileIndexedPackGamelistAsync(
        RomPackIndexEntry pack,
        CancellationToken cancellationToken)
    {
        if (IsMultiSystemPack(pack))
        {
            var changed = false;
            foreach (var group in pack.Roms.GroupBy(rom => rom.SystemId, StringComparer.OrdinalIgnoreCase))
            {
                var systemPack = new RomPackIndexEntry
                {
                    PackagePath = pack.PackagePath,
                    Sha256 = pack.Sha256,
                    Size = pack.Size,
                    LastWriteTimeUtc = pack.LastWriteTimeUtc,
                    InstalledAtUtc = pack.InstalledAtUtc,
                    Status = pack.Status,
                    ImporterVersion = pack.ImporterVersion,
                    SystemId = group.Key,
                    OnTheFlyMode = pack.OnTheFlyMode,
                    UnzipRoms = pack.UnzipRoms,
                    MediaFilesImportedCount = pack.MediaFilesImportedCount,
                    Roms = group.ToList()
                };
                changed |= await ReconcileIndexedPackGamelistAsync(systemPack, cancellationToken);
            }

            return changed;
        }

        if (pack.Roms.Count == 0 || string.IsNullOrWhiteSpace(pack.SystemId))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, pack.SystemId);
        Directory.CreateDirectory(systemRoot);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        var document = LoadOrCreateGamelistDocument(gamelistPath);
        var root = document.Root ?? new XElement("gameList");
        if (document.Root == null)
        {
            document.Add(root);
        }

        var existingPaths = root.Elements("game")
            .Select(game => NormalizeGamelistPath(game.Element("path")?.Value))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousNodes = LoadPreviousGamelistNodes(systemRoot);
        var restored = 0;
        var placeholdersCreated = 0;

        foreach (var rom in pack.Roms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeGamelistPath(rom.GamePath);
            if (string.IsNullOrWhiteSpace(relativePath) || existingPaths.Contains(relativePath))
            {
                continue;
            }

            var node = previousNodes.TryGetValue(relativePath, out var previousNode)
                ? new XElement(previousNode)
                : BuildMinimalIndexedRomNode(rom, relativePath);
            node.SetElementValue("path", relativePath);
            if (string.IsNullOrWhiteSpace(node.Element("name")?.Value))
            {
                node.SetElementValue("name", ResolveIndexedRomDisplayName(rom));
            }
            if (string.IsNullOrWhiteSpace(NormalizeHex(node.Element("md5")?.Value)) &&
                !string.IsNullOrWhiteSpace(rom.Md5))
            {
                node.SetElementValue("md5", NormalizeHex(rom.Md5));
            }
            if (string.IsNullOrWhiteSpace(NormalizeHex(node.Element("crc32")?.Value)) &&
                !string.IsNullOrWhiteSpace(rom.Crc32))
            {
                node.SetElementValue("crc32", NormalizeHex(rom.Crc32));
            }

            root.Add(new XText(Environment.NewLine + "  "));
            root.Add(node);
            existingPaths.Add(relativePath);
            restored++;
        }

        foreach (var rom in pack.Roms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnsureOnTheFlyPlaceholder(systemRoot, rom))
            {
                placeholdersCreated++;
            }
        }

        var visibleSlotsNormalized = await EnsureIndexedVisibleSlotsAsync(
            pack,
            root,
            gamelistPath,
            cancellationToken);

        if (restored == 0 && placeholdersCreated == 0 && !visibleSlotsNormalized)
        {
            return false;
        }

        if (restored > 0)
        {
            root.Add(new XText(Environment.NewLine));
            _gamelistUpdateService.SaveExternalGamelistDocument(
                document,
                gamelistPath,
                "rom-pack-reconcile-restored-games",
                cancellationToken);
            await _localGamelistUpdateService.UpdateAsync(
                new LocalGamelistUpdateRequest { Scope = "system", SystemId = pack.SystemId, SuppressTaskProgress = true },
                cancellationToken);
            ReportStartupProgress(1, 1, $"gamelist restauree {pack.SystemId}: {restored} jeux");
            LogInstallerProgress($"Gamelist pack restauree : {pack.SystemId} ({restored} jeux)");
        }

        _logger?.LogInformation(
            "ROM pack gamelist reconciled: system={SystemId}, package={PackagePath}, restored={Restored}, placeholders={Placeholders}, indexed={Indexed}.",
            pack.SystemId,
            pack.PackagePath,
            restored,
            placeholdersCreated,
            pack.Roms.Count);
        return true;
    }

    private void RemoveIndexedOnTheFlyPackageArtifacts(string packagePath)
    {
        var existingPacks = _index.Packs
            .Where(pack =>
                pack.OnTheFlyMode &&
                string.Equals(pack.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (existingPacks.Count == 0)
        {
            return;
        }

        foreach (var group in existingPacks
            .SelectMany(pack => pack.Roms)
            .Where(rom => !string.IsNullOrWhiteSpace(rom.SystemId))
            .GroupBy(rom => rom.SystemId, StringComparer.OrdinalIgnoreCase))
        {
            var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, group.Key);
            if (!Directory.Exists(systemRoot))
            {
                continue;
            }

            var roms = group.ToList();
            foreach (var rom in roms)
            {
                var targetPath = Path.Combine(systemRoot, rom.DestinationFileName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(targetPath) && IsOnTheFlyPlaceholder(targetPath))
                {
                    try
                    {
                        File.Delete(targetPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger?.LogDebug(ex, "Unable to remove stale on-the-fly placeholder {Path}", targetPath);
                    }
                }
            }

            RemoveIndexedGamesFromGamelist(systemRoot, roms);
        }
    }

    private void RemoveIndexedGamesFromGamelist(string systemRoot, IReadOnlyCollection<RomPackRomEntry> roms)
    {
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(gamelistPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return;
        }

        var paths = roms
            .SelectMany(rom => new[]
            {
                NormalizeGamelistPath(rom.GamePath),
                NormalizeGamelistPath("./" + rom.DestinationFileName.Replace('\\', '/'))
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = false;
        foreach (var game in (document.Root?.Elements("game") ?? Enumerable.Empty<XElement>()).ToList())
        {
            var path = NormalizeGamelistPath(game.Element("path")?.Value);
            if (paths.Contains(path))
            {
                game.Remove();
                removed = true;
            }
        }

        if (!removed)
        {
            return;
        }

        _gamelistUpdateService.SaveExternalGamelistDocument(
            document,
            gamelistPath,
            "rom-pack-remove-indexed-games",
            CancellationToken.None,
            allowMediaTagDrop: true);
    }

    private async Task<bool> EnsureIndexedVisibleSlotsAsync(
        RomPackIndexEntry pack,
        XElement root,
        string gamelistPath,
        CancellationToken cancellationToken)
    {
        var indexedPaths = pack.Roms
            .Select(rom => NormalizeGamelistPath(rom.GamePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (indexedPaths.Count == 0)
        {
            return false;
        }

        var needsVisibleSlotNormalization = root.Elements("game")
            .Where(game => indexedPaths.Contains(NormalizeGamelistPath(game.Element("path")?.Value)))
            .Any(game => LocalMediaManagedVisibleSlots.Any(slot => game.Element(slot) == null));
        if (!needsVisibleSlotNormalization)
        {
            return false;
        }

        var updateResult = await _localGamelistUpdateService.UpdateAsync(
            new LocalGamelistUpdateRequest { Scope = "system", SystemId = pack.SystemId, SuppressTaskProgress = true },
            cancellationToken);
        if (updateResult.SystemsUpdated > 0)
        {
            return true;
        }

        var changed = false;
        foreach (var game in root.Elements("game"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indexedPaths.Contains(NormalizeGamelistPath(game.Element("path")?.Value)))
            {
                continue;
            }

            foreach (var slot in LocalMediaManagedVisibleSlots)
            {
                if (game.Element(slot) == null)
                {
                    game.Add(new XElement(slot, string.Empty));
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        _gamelistUpdateService.SaveExternalGamelistDocument(
            root.Document ?? new XDocument(root),
            gamelistPath,
            "rom-pack-visible-slots-normalized",
            cancellationToken);
        await AppendInstallerLogAsync(
            "package-visible-slots-normalized",
            new { pack.SystemId, pack.PackagePath, slots = LocalMediaManagedVisibleSlots },
            cancellationToken);
        return true;
    }

    public async Task<OnTheFlyRomInstallResult> EnsureLaunchRomAsync(
        string rawGameStartArguments,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeOptions.IsOnTheFlyRomInstallerEnabled())
        {
            return OnTheFlyRomInstallResult.Skipped("disabled");
        }

        if (!_runtimeOptions.ShouldExtractOnTheFlyRomOnGameStart())
        {
            return OnTheFlyRomInstallResult.Skipped("trigger-not-game-start");
        }

        var args = ParseEventArguments(rawGameStartArguments ?? string.Empty);
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            return OnTheFlyRomInstallResult.Skipped("missing-game-start-path");
        }

        var firstArg = args[0].Trim();
        var hasSystemPrefix = args.Count >= 2 && IsLikelySystemId(firstArg) && IsLikelyGamePath(args[1]);
        var systemId = hasSystemPrefix
            ? firstArg
            : string.Empty;
        var gamePath = hasSystemPrefix
            ? args[1].Trim()
            : firstArg;
        var gameNameIndex = hasSystemPrefix ? 2 : 1;
        var gameName = args.Count > gameNameIndex && !string.IsNullOrWhiteSpace(args[gameNameIndex])
            ? args[gameNameIndex].Trim()
            : Path.GetFileNameWithoutExtension(gamePath);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            systemId = InferSystemIdFromGamePath(gamePath);
        }
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return OnTheFlyRomInstallResult.Skipped("system-not-inferred");
        }

        return await TryInstallMissingRomAsync(
            new GameReference
            {
                SystemId = systemId,
                GamePath = gamePath,
                GameName = gameName
            },
            OnTheFlyRomExtractionTrigger.GameStart,
            cancellationToken);
    }

    private async Task<OnTheFlyRomInstallResult> TryInstallMissingRomAsync(
        GameReference selected,
        OnTheFlyRomExtractionTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (!_runtimeOptions.IsOnTheFlyRomInstallerEnabled())
        {
            return OnTheFlyRomInstallResult.Skipped("disabled");
        }

        if (string.IsNullOrWhiteSpace(selected.SystemId) || string.IsNullOrWhiteSpace(selected.GamePath))
        {
            return OnTheFlyRomInstallResult.Skipped("missing-selection");
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, selected.SystemId);
        var targetPath = ResolveGamePath(systemRoot, selected.GamePath);
        if ((File.Exists(targetPath) && !IsOnTheFlyPlaceholder(targetPath)) || Directory.Exists(targetPath))
        {
            return OnTheFlyRomInstallResult.Skipped("already-installed", targetPath);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _index = LoadIndex();
            var romEntry = FindIndexedRom(selected.SystemId, selected.GamePath, selected.GameName);
            if (romEntry == null)
            {
                _logger?.LogInformation(
                    "On-the-fly ROM install skipped: no indexed pack entry for system={SystemId}, path={GamePath}",
                    selected.SystemId,
                    selected.GamePath);
                return OnTheFlyRomInstallResult.Skipped("not-indexed", targetPath);
            }

            var pack = _index.Packs.FirstOrDefault(candidate =>
                candidate.Roms.Any(rom => ReferenceEquals(rom, romEntry)) ||
                candidate.Roms.Any(rom => SameRomEntry(rom, romEntry)));
            if (pack == null || !File.Exists(pack.PackagePath))
            {
                return OnTheFlyRomInstallResult.Skipped("pack-missing", targetPath);
            }

            if (trigger == OnTheFlyRomExtractionTrigger.GameSelected && !IsCurrentlySelectedGame(selected))
            {
                _logger?.LogDebug(
                    "On-the-fly ROM extraction skipped before messagebox: target is no longer selected for system={SystemId}, path={GamePath}",
                    selected.SystemId,
                    selected.GamePath);
                return OnTheFlyRomInstallResult.Skipped("selection-changed-before-messagebox", targetPath);
            }

            var isGameStart = trigger == OnTheFlyRomExtractionTrigger.GameStart;
            if (isGameStart)
            {
                _taskProgress.Report(
                    OnTheFlyExtractionProgressTaskId,
                    "Extraction de l'archive",
                    0,
                    100,
                    $"{romEntry.GameName} - patientez, le jeu va demarrer automatiquement");
            }
            else
            {
                await MessageBoxAsync(
                    $"Extraction de l'archive : {romEntry.GameName}.{Environment.NewLine}Ne lancez pas encore le jeu!",
                    cancellationToken);
            }

            if (trigger == OnTheFlyRomExtractionTrigger.GameSelected && !IsCurrentlySelectedGame(selected))
            {
                _logger?.LogDebug(
                    "On-the-fly ROM extraction skipped after messagebox: target is no longer selected for system={SystemId}, path={GamePath}",
                    selected.SystemId,
                    selected.GamePath);
                return OnTheFlyRomInstallResult.Skipped("selection-changed-after-messagebox", targetPath);
            }

            var archiveEntry = await ResolveRomArchiveEntryAsync(pack, romEntry, cancellationToken);
            if (archiveEntry == null)
            {
                _logger?.LogWarning(
                    "On-the-fly ROM extraction skipped: archive entry not found for system={SystemId}, game={GameName}, packRelativePath={PackRelativePath}, package={PackagePath}",
                    romEntry.SystemId,
                    romEntry.GameName,
                    romEntry.PackRelativePath,
                    pack.PackagePath);
                return OnTheFlyRomInstallResult.Skipped("archive-entry-missing", targetPath);
            }

            if (!string.Equals(romEntry.ArchiveEntryPath, archiveEntry.Path, StringComparison.Ordinal) ||
                romEntry.Size != archiveEntry.Size)
            {
                romEntry.ArchiveEntryPath = archiveEntry.Path;
                romEntry.Size = archiveEntry.Size;
                SaveIndex();
            }

            var extractionSize = archiveEntry.Size > 0 ? archiveEntry.Size : pack.Size;
            var timeout = EstimateExtractionTimeout(extractionSize);
            var tempRoot = Path.Combine(TempRoot, "on-the-fly-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                var sourcePath = await ExtractArchiveEntryWithProgressAsync(
                    pack.PackagePath,
                    archiveEntry,
                    romEntry,
                    tempRoot,
                    romEntry.GameName,
                    extractionSize,
                    timeout,
                    timeoutCts.Token);
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    return OnTheFlyRomInstallResult.Skipped("source-rom-missing", targetPath);
                }

                Directory.CreateDirectory(systemRoot);
                _taskProgress.Report(OnTheFlyExtractionProgressTaskId, "Extraction de l'archive", 92, 100, "installation du jeu");
                await InstallRomFileWithProgressAsync(
                    sourcePath,
                    Path.Combine(systemRoot, romEntry.DestinationFileName),
                    tempRoot,
                    romEntry.GameName,
                    timeout,
                    timeoutCts.Token);
                _taskProgress.Report(OnTheFlyExtractionProgressTaskId, "Extraction de l'archive", 100, 100, "jeu pret");
                await NotifyAsync(
                    isGameStart
                        ? $"Jeu installe : {romEntry.GameName}, lancement en cours."
                        : $"Jeu installe : {romEntry.GameName}, maintenant vous pouvez le lancer.",
                    cancellationToken);
                _logger?.LogInformation(
                    "On-the-fly ROM installed: system={SystemId}, game={GameName}, target={TargetPath}",
                    romEntry.SystemId,
                    romEntry.GameName,
                    targetPath);
                return OnTheFlyRomInstallResult.Success(targetPath, pack.PackagePath, romEntry.GameName);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await NotifyAsync($"Extraction interrompue : {romEntry.GameName}. Archive trop longue a extraire.", CancellationToken.None);
                return OnTheFlyRomInstallResult.TimedOut(targetPath, pack.PackagePath, romEntry.GameName, (int)timeout.TotalSeconds);
            }
            finally
            {
                _taskProgress.Complete(OnTheFlyExtractionProgressTaskId);
                TryDeleteDirectory(tempRoot);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MergeGamelist(string systemRoot, XElement packageRoot, bool changedGamelist, CancellationToken cancellationToken)
    {
        if (!changedGamelist)
        {
            return;
        }

        var targetPath = Path.Combine(systemRoot, "gamelist.xml");
        var targetDocument = File.Exists(targetPath)
            ? XDocument.Load(targetPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("gameList"));
        var targetRoot = targetDocument.Root ?? new XElement("gameList");
        if (targetDocument.Root == null)
        {
            targetDocument.Add(targetRoot);
        }

        foreach (var sourceGame in packageRoot.Elements("game"))
        {
            var sourcePath = sourceGame.Element("path")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            var existing = targetRoot.Elements("game").FirstOrDefault(game =>
                string.Equals(game.Element("path")?.Value?.Trim(), sourcePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.ReplaceWith(new XElement(sourceGame));
            }
            else
            {
                targetRoot.Add(new XText(Environment.NewLine + "  "));
                targetRoot.Add(new XElement(sourceGame));
            }
        }

        targetRoot.Add(new XText(Environment.NewLine));
        _gamelistUpdateService.SaveExternalGamelistDocument(
            targetDocument,
            targetPath,
            "rom-pack-merge",
            cancellationToken,
            allowMediaTagDrop: true);
    }

    private static IEnumerable<(string RawPath, string Kind, string Key)> EnumeratePackGamelistMediaElements(XElement game)
    {
        foreach (var element in game.Elements())
        {
            var rawPath = element.Value?.Trim();
            var kind = ResolvePackGamelistMediaKind(element.Name.LocalName, rawPath);
            if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(kind))
            {
                continue;
            }

            yield return (rawPath, kind, NormalizeArchivePath(rawPath) + "\0" + kind);
        }
    }

    private static string ResolvePackGamelistMediaKind(string tagName, string? rawPath)
    {
        var fromPath = ResolvePackGamelistMediaKindFromPath(rawPath);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        return (tagName ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "image" or "screenshot" => MediaKinds.Thumbnail,
            "thumbnail" or "thumb" => MediaKinds.Box3d,
            "marquee" or "wheel" => MediaKinds.Wheel,
            "wheelcarbon" or "wheel-carbon" => MediaKinds.WheelCarbon,
            "wheelsteel" or "wheel-steel" => MediaKinds.WheelSteel,
            "fanart" => MediaKinds.Fanart,
            "boxart" or "box2d" or "box-2d" => MediaKinds.BoxFront,
            "boxback" or "box-back" => MediaKinds.BoxBack,
            "box3d" or "box-3d" => MediaKinds.Box3d,
            "cartridge" or "cart" => MediaKinds.Cartridge,
            "label" => MediaKinds.Label,
            "titleshot" or "sstitle" => MediaKinds.Image,
            "extra1" or "flyer" => MediaKinds.Flyer,
            "figurine" => MediaKinds.Figurine,
            "mix" or "mixrbv2" => MediaKinds.MixRbv2,
            "mixrbv1" => MediaKinds.MixRbv1,
            "bezel" => MediaKinds.Bezel,
            "map" => MediaKinds.Map,
            "manual" => MediaKinds.Manual,
            "magazine" => MediaKinds.Magazine,
            "video" => MediaKinds.Video,
            "videonormalized" or "video-normalized" => MediaKinds.VideoNormalized,
            "screenmarquee" or "screen-marquee" => MediaKinds.ScreenMarquee,
            "screenmarqueesmall" or "screen-marquee-small" => MediaKinds.ScreenMarqueeSmall,
            "steamgrid" => MediaKinds.SteamGrid,
            _ => string.Empty
        };
    }

    private static string ResolvePackGamelistMediaKindFromPath(string? rawPath)
    {
        var normalizedPath = NormalizeArchivePath(rawPath ?? string.Empty);
        var fileName = Path.GetFileNameWithoutExtension(GetArchiveFileName(normalizedPath));
        var normalizedFileName = NormalizePackMediaPathSegment(fileName);
        var fromFileName = ResolvePackGamelistMediaKindFromFileName(normalizedFileName);
        if (!string.IsNullOrWhiteSpace(fromFileName))
        {
            return fromFileName;
        }

        var segments = normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePackMediaPathSegment)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        if (HasAnySegment(segments, "videos", "video")) return MediaKinds.Video;
        if (HasAnySegment(segments, "manuals", "manual")) return MediaKinds.Manual;
        if (HasAnySegment(segments, "magazines", "magazine")) return MediaKinds.Magazine;
        if (HasAnySegment(segments, "maps", "map")) return MediaKinds.Map;
        if (HasAnySegment(segments, "bezels", "bezel")) return MediaKinds.Bezel;
        if (HasAnySegment(segments, "boxback", "boxbacks", "box-back")) return MediaKinds.BoxBack;
        if (HasAnySegment(segments, "cartridges", "cartridge", "carts", "cart", "support2d", "support-2d")) return MediaKinds.Cartridge;
        if (HasAnySegment(segments, "labels", "label", "supporttexture", "support-texture")) return MediaKinds.Label;
        if (HasAnySegment(segments, "fanarts", "fanart")) return MediaKinds.Fanart;
        if (HasAnySegment(segments, "titles", "title", "titleshots", "titleshot", "sstitle")) return MediaKinds.Image;
        if (HasAnySegment(segments, "box2d", "box-2d", "boxfront", "box-front", "covers", "cover")) return MediaKinds.BoxFront;
        if (HasAnySegment(segments, "box3d", "box-3d", "thumbnails", "thumbnail", "thumbs", "thumb")) return MediaKinds.Box3d;
        if (HasAnySegment(segments, "marquee", "marquees", "wheels", "wheel")) return MediaKinds.Wheel;
        if (HasAnySegment(segments, "screenshots", "screenshot", "images", "image")) return MediaKinds.Thumbnail;
        if (HasAnySegment(segments, "flyers", "flyer")) return MediaKinds.Flyer;
        if (HasAnySegment(segments, "figurines", "figurine")) return MediaKinds.Figurine;
        if (HasAnySegment(segments, "mix", "mixes", "mixrbv2")) return MediaKinds.MixRbv2;
        if (HasAnySegment(segments, "mixrbv1")) return MediaKinds.MixRbv1;
        if (HasAnySegment(segments, "steamgrid", "steamgrids")) return MediaKinds.SteamGrid;

        return string.Empty;
    }

    private static string ResolvePackGamelistMediaKindFromFileName(string normalizedFileName)
    {
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            return string.Empty;
        }

        if (FileNameHasAnyMediaSuffix(normalizedFileName, "video")) return MediaKinds.Video;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "manual")) return MediaKinds.Manual;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "magazine")) return MediaKinds.Magazine;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "map")) return MediaKinds.Map;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "bezel")) return MediaKinds.Bezel;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "boxback", "box-back")) return MediaKinds.BoxBack;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "cartridge", "cart", "support2d", "support-2d")) return MediaKinds.Cartridge;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "label", "supporttexture", "support-texture")) return MediaKinds.Label;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "fanart")) return MediaKinds.Fanart;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "titleshot", "sstitle")) return MediaKinds.Image;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "box2d", "box-2d", "boxfront", "box-front", "box")) return MediaKinds.BoxFront;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "box3d", "box-3d", "thumbnail", "thumb")) return MediaKinds.Box3d;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "wheelcarbon", "wheel-carbon")) return MediaKinds.WheelCarbon;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "wheelsteel", "wheel-steel")) return MediaKinds.WheelSteel;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "wheel", "marquee", "logo")) return MediaKinds.Wheel;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "screenshot", "image")) return MediaKinds.Thumbnail;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "flyer")) return MediaKinds.Flyer;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "figurine")) return MediaKinds.Figurine;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "mixrbv1")) return MediaKinds.MixRbv1;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "mix", "mixrbv2")) return MediaKinds.MixRbv2;
        if (FileNameHasAnyMediaSuffix(normalizedFileName, "steamgrid")) return MediaKinds.SteamGrid;

        return string.Empty;
    }

    private static bool FileNameHasAnyMediaSuffix(string normalizedFileName, params string[] suffixes)
    {
        return suffixes.Any(suffix =>
            string.Equals(normalizedFileName, suffix, StringComparison.OrdinalIgnoreCase) ||
            normalizedFileName.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase) ||
            normalizedFileName.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAnySegment(IReadOnlySet<string> segments, params string[] candidates)
    {
        return candidates.Any(segments.Contains);
    }

    private static string NormalizePackMediaPathSegment(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
    }

    private int ImportPackMedia(string contentRoot, XElement game, string systemId, string gameSlug, string sourceRom)
    {
        var imported = 0;
        // Current pack convention:
        // -image is a screenshot, -marquee is a simple logo, and -thumb is box3d.
        // Store these assets canonically so the local media manager can reallocate
        // visible ES slots from appsettings without keeping hybrid roms/images paths.
        var gamelistMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var media in EnumeratePackGamelistMediaElements(game))
        {
            if (!gamelistMedia.Add(media.Key))
            {
                continue;
            }

            if (string.Equals(media.Kind, MediaKinds.Box3d, StringComparison.OrdinalIgnoreCase))
            {
                RemoveLegacyMisclassifiedPackThumb(contentRoot, media.RawPath, systemId, gameSlug);
            }

            imported += CopyMediaIfPresent(contentRoot, media.RawPath, systemId, gameSlug, media.Kind) ? 1 : 0;
        }

        var stem = Path.GetFileNameWithoutExtension(sourceRom);
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-image"), systemId, gameSlug, MediaKinds.Thumbnail) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-screenshot"), systemId, gameSlug, MediaKinds.Thumbnail) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-titleshot"), systemId, gameSlug, MediaKinds.Image) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-sstitle"), systemId, gameSlug, MediaKinds.Image) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-logo"), systemId, gameSlug, MediaKinds.Logo) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-wheel"), systemId, gameSlug, MediaKinds.Wheel) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-marquee"), systemId, gameSlug, MediaKinds.Logo) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-wheelcarbon"), systemId, gameSlug, MediaKinds.WheelCarbon) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-wheel-carbon"), systemId, gameSlug, MediaKinds.WheelCarbon) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-wheelsteel"), systemId, gameSlug, MediaKinds.WheelSteel) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-wheel-steel"), systemId, gameSlug, MediaKinds.WheelSteel) ? 1 : 0;
        var siblingThumb = FindSiblingMedia(contentRoot, stem, "-thumb");
        RemoveLegacyMisclassifiedPackThumb(contentRoot, siblingThumb, systemId, gameSlug);
        imported += CopyMediaIfPresent(contentRoot, siblingThumb, systemId, gameSlug, MediaKinds.Box3d) ? 1 : 0;
        var siblingThumbnail = FindSiblingMedia(contentRoot, stem, "-thumbnail");
        RemoveLegacyMisclassifiedPackThumb(contentRoot, siblingThumbnail, systemId, gameSlug);
        imported += CopyMediaIfPresent(contentRoot, siblingThumbnail, systemId, gameSlug, MediaKinds.Box3d) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-box"), systemId, gameSlug, MediaKinds.BoxFront) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-box2d"), systemId, gameSlug, MediaKinds.BoxFront) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-box-2d"), systemId, gameSlug, MediaKinds.BoxFront) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-box3d"), systemId, gameSlug, MediaKinds.Box3d) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-box-3d"), systemId, gameSlug, MediaKinds.Box3d) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-fanart"), systemId, gameSlug, MediaKinds.Fanart) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-flyer"), systemId, gameSlug, MediaKinds.Flyer) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-video"), systemId, gameSlug, MediaKinds.Video) ? 1 : 0;
        imported += CopyMediaIfPresent(contentRoot, FindSiblingMedia(contentRoot, stem, "-manual"), systemId, gameSlug, MediaKinds.Manual) ? 1 : 0;
        return imported;
    }

    private static void RemoveLegacyMisclassifiedPackThumb(string contentRoot, string? rawPath, string systemId, string gameSlug)
    {
        var sourcePath = ResolvePackageRelativeFile(contentRoot, rawPath);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return;
        }

        var legacyBox2dPath = GetCanonicalImportPath(systemId, gameSlug, MediaKinds.BoxFront, sourcePath);
        if (!File.Exists(legacyBox2dPath) || !HaveSameHash(sourcePath, legacyBox2dPath))
        {
            return;
        }

        File.Delete(legacyBox2dPath);
    }

    private static bool HaveSameHash(string leftPath, string rightPath)
    {
        try
        {
            return string.Equals(ComputeSha256(leftPath), ComputeSha256(rightPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ClearLocalMediaManagedVisibleSlots(XElement game)
    {
        foreach (var elementName in LocalMediaManagedVisibleSlots)
        {
            game.Element(elementName)?.Remove();
        }
    }

    private bool CopyMediaIfPresent(string contentRoot, string? rawPath, string systemId, string gameSlug, string kind)
    {
        var sourcePath = ResolvePackageRelativeFile(contentRoot, rawPath);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return false;
        }

        var destinationPath = GetCanonicalImportPath(systemId, gameSlug, kind, sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return true;
    }

    private static string GetCanonicalImportPath(string systemId, string gameSlug, string kind, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = string.Equals(kind, MediaKinds.Video, StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".png";
        }

        var relative = kind switch
        {
            MediaKinds.Image => Path.Combine(systemId, "games", gameSlug, "artwork", "screentitle" + extension),
            MediaKinds.Thumbnail => Path.Combine(systemId, "games", gameSlug, "artwork", "screenshot" + extension),
            MediaKinds.Logo => Path.Combine(systemId, "games", gameSlug, "ui", "wheels", "wheel" + extension),
            MediaKinds.Wheel => Path.Combine(systemId, "games", gameSlug, "ui", "wheels", "wheel" + extension),
            MediaKinds.WheelCarbon => Path.Combine(systemId, "games", gameSlug, "ui", "wheels", "wheel-carbon" + extension),
            MediaKinds.WheelSteel => Path.Combine(systemId, "games", gameSlug, "ui", "wheels", "wheel-steel" + extension),
            MediaKinds.Marquee => Path.Combine(systemId, "games", gameSlug, "artwork", "marquee", "marquee" + extension),
            MediaKinds.ScreenMarquee => Path.Combine(systemId, "games", gameSlug, "artwork", "marquee", "screenmarquee" + extension),
            MediaKinds.ScreenMarqueeSmall => Path.Combine(systemId, "games", gameSlug, "artwork", "marquee", "screenmarquee-small" + extension),
            MediaKinds.SteamGrid => Path.Combine(systemId, "games", gameSlug, "ui", "steamgrid" + extension),
            MediaKinds.MixRbv1 => Path.Combine(systemId, "games", gameSlug, "artwork", "mix", "mixrbv1" + extension),
            MediaKinds.MixRbv2 => Path.Combine(systemId, "games", gameSlug, "artwork", "mix", "mixrbv2" + extension),
            MediaKinds.BoxFront => Path.Combine(systemId, "games", gameSlug, "artwork", "box", "front" + extension),
            MediaKinds.BoxSide => Path.Combine(systemId, "games", gameSlug, "artwork", "box", "side" + extension),
            MediaKinds.BoxTexture => Path.Combine(systemId, "games", gameSlug, "artwork", "box", "texture" + extension),
            MediaKinds.Box3d => Path.Combine(systemId, "games", gameSlug, "artwork", "box", "3d" + extension),
            MediaKinds.Cartridge => Path.Combine(systemId, "games", gameSlug, "artwork", "cartridge" + extension),
            MediaKinds.Label => Path.Combine(systemId, "games", gameSlug, "artwork", "label" + extension),
            MediaKinds.Fanart => Path.Combine(systemId, "games", gameSlug, "artwork", "fanart" + extension),
            MediaKinds.Flyer => Path.Combine(systemId, "games", gameSlug, "artwork", "flyer" + extension),
            MediaKinds.Figurine => Path.Combine(systemId, "games", gameSlug, "artwork", "figurine" + extension),
            MediaKinds.Bezel => Path.Combine(systemId, "games", gameSlug, "artwork", "bezels", "bezel" + extension),
            MediaKinds.BoxBack => Path.Combine(systemId, "games", gameSlug, "artwork", "box", "back" + extension),
            MediaKinds.Map => Path.Combine(systemId, "games", gameSlug, "documents", "maps", "map" + extension),
            MediaKinds.Manual => Path.Combine(systemId, "games", gameSlug, "documents", "manual" + extension),
            MediaKinds.Magazine => Path.Combine(systemId, "games", gameSlug, "documents", "magazine" + extension),
            MediaKinds.Video => Path.Combine(systemId, "games", gameSlug, "video" + extension),
            MediaKinds.VideoNormalized => Path.Combine(systemId, "games", gameSlug, "video-normalized" + extension),
            MediaKinds.ThemeHb => Path.Combine(systemId, "games", gameSlug, "themes", "themehb" + extension),
            _ => Path.Combine(systemId, "games", gameSlug, kind + extension)
        };
        return Path.Combine(RetroBatPaths.MediaSystemsRoot, relative);
    }

    private Task EnsureParseGamelistOnlyAsync(CancellationToken cancellationToken)
    {
        var changed = _settingsStore.Update(document =>
        {
            var root = document.Root ?? new XElement("config");
            if (document.Root == null)
            {
                document.Add(root);
            }

            var existing = root.Elements().FirstOrDefault(element =>
                string.Equals(element.Attribute("name")?.Value, "ParseGamelistOnly", StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.Equals(existing.Name.LocalName, "bool", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Attribute("value")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                existing.Name = "bool";
                existing.SetAttributeValue("value", "true");
                return true;
            }

            root.Add(new XText(Environment.NewLine + "  "));
            root.Add(new XElement("bool", new XAttribute("name", "ParseGamelistOnly"), new XAttribute("value", "true")));
            return true;
        }, cancellationToken);

        if (changed)
        {
            ReportStartupProgress(0, 1, "ParseGamelistOnly active");
            LogInstallerProgress("ParseGamelistOnly active pour On-the-fly ROM Installer");
        }

        return Task.CompletedTask;
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
                // 7za remains a fallback for rare RAR-like files handled as ZIP/SFX.
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

    private async Task ExtractArchiveWithProgressAsync(
        string archivePath,
        string destinationDirectory,
        string gameName,
        long archiveSize,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var title = "Extraction de l'archive";
        var detail = $"{gameName} - {FormatBytes(archiveSize)}";
        _taskProgress.Report(OnTheFlyExtractionProgressTaskId, title, 0, 100, detail);

        var stopwatch = Stopwatch.StartNew();
        var extractionTask = ExtractArchiveAsync(archivePath, destinationDirectory, cancellationToken);
        while (!extractionTask.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var progress = EstimateTimedProgress(stopwatch.Elapsed, timeout, 5, 88);
            _taskProgress.Report(OnTheFlyExtractionProgressTaskId, title, progress, 100, detail);
        }

        await extractionTask;
        _taskProgress.Report(OnTheFlyExtractionProgressTaskId, title, 90, 100, "archive extraite");
    }

    private async Task<string> ExtractArchiveEntryWithProgressAsync(
        string archivePath,
        ArchiveEntryInfo archiveEntry,
        RomPackRomEntry romEntry,
        string destinationDirectory,
        string gameName,
        long entrySize,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var title = "Extraction de l'archive";
        var detail = $"{gameName} - {FormatBytes(entrySize)}";
        _taskProgress.Report(OnTheFlyExtractionProgressTaskId, title, 0, 100, detail);

        var stopwatch = Stopwatch.StartNew();
        var extractionTask = ExtractArchiveEntryAsync(
            archivePath,
            destinationDirectory,
            archiveEntry,
            cancellationToken);
        while (!extractionTask.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var progress = EstimateTimedProgress(stopwatch.Elapsed, timeout, 5, 88);
            _taskProgress.Report(OnTheFlyExtractionProgressTaskId, title, progress, 100, detail);
        }

        await extractionTask;
        _taskProgress.Report(OnTheFlyExtractionProgressTaskId, title, 90, 100, "jeu extrait");
        return ResolveExtractedArchiveEntryPath(destinationDirectory, archiveEntry.Path, romEntry.PackRelativePath);
    }

    private static async Task ExtractArchiveEntryAsync(
        string archivePath,
        string destinationDirectory,
        ArchiveEntryInfo archiveEntry,
        CancellationToken cancellationToken)
    {
        if (await TryExtractZipLocalEntryAsync(archivePath, destinationDirectory, archiveEntry, cancellationToken))
        {
            return;
        }

        await ExtractArchiveEntryAsync(archivePath, destinationDirectory, archiveEntry.Path, cancellationToken);
    }

    private static async Task ExtractArchiveEntryAsync(
        string archivePath,
        string destinationDirectory,
        string entryPath,
        CancellationToken cancellationToken)
    {
        if (await TryExtractZipLocalEntryAsync(archivePath, destinationDirectory, entryPath, cancellationToken))
        {
            return;
        }

        try
        {
            await ExtractWithTarAsync(archivePath, destinationDirectory, cancellationToken, entryPath);
            return;
        }
        catch
        {
            // 7za remains the final extractor for formats tar/libarchive cannot open.
        }

        var sevenZipPath = GetSevenZipPath();
        if (!File.Exists(sevenZipPath))
        {
            throw new InvalidOperationException($"7za.exe introuvable: {sevenZipPath}");
        }

        await ExtractWith7ZipAsync(sevenZipPath, archivePath, destinationDirectory, cancellationToken, entryPath);
    }

    private async Task InstallRomFileWithProgressAsync(
        string sourceRom,
        string destinationRomPath,
        string tempRoot,
        string gameName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationRomPath)!);
        if (!_runtimeOptions.ShouldUnzipRomPackInstallerRoms() ||
            !PackExtensions.Contains(Path.GetExtension(sourceRom)))
        {
            await CopyFileAsync(sourceRom, destinationRomPath, cancellationToken);
            return;
        }

        var innerRoot = Path.Combine(tempRoot, "__rom_unpack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(innerRoot);
        try
        {
            _taskProgress.Report(OnTheFlyExtractionProgressTaskId, "Extraction de l'archive", 94, 100, "decompression de la rom");
            await ExtractArchiveAsync(sourceRom, innerRoot, cancellationToken);
            var innerRom = EnumeratePackageRomCandidates(innerRoot).FirstOrDefault()
                ?? Directory.EnumerateFiles(innerRoot, "*.*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(innerRom) || !File.Exists(innerRom))
            {
                await CopyFileAsync(sourceRom, destinationRomPath, cancellationToken);
                return;
            }

            _taskProgress.Report(
                OnTheFlyExtractionProgressTaskId,
                "Extraction de l'archive",
                EstimateTimedProgress(timeout, timeout, 96, 98),
                100,
                gameName);
            await CopyFileAsync(innerRom, destinationRomPath, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(innerRoot);
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static TimeSpan EstimateExtractionTimeout(long archiveSize)
    {
        var megabytes = Math.Max(1d, archiveSize / 1024d / 1024d);
        var seconds = 45 + (int)Math.Ceiling(megabytes / 6d);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 90, 900));
    }

    private static int EstimateTimedProgress(TimeSpan elapsed, TimeSpan timeout, int minimum, int maximum)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return minimum;
        }

        var ratio = Math.Clamp(elapsed.TotalSeconds / timeout.TotalSeconds, 0d, 1d);
        return Math.Clamp(minimum + (int)Math.Round((maximum - minimum) * ratio), minimum, maximum);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " o";
        }

        var value = bytes / 1024d;
        var units = new[] { "Ko", "Mo", "Go", "To" };
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return value.ToString(value >= 10 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unitIndex];
    }

    private static async Task ExtractWith7ZipAsync(
        string sevenZipPath,
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken,
        string? entryPath = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-o" + destinationDirectory);
        if (!string.IsNullOrWhiteSpace(entryPath))
        {
            startInfo.ArgumentList.Add(entryPath);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start 7za.exe.");
        var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Archive extraction failed for {archivePath}: {await stdErr}");
        }

        await stdOut;
    }

    private async Task<ArchiveEntryInfo?> ResolveRomArchiveEntryAsync(
        RomPackIndexEntry pack,
        RomPackRomEntry romEntry,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(romEntry.ArchiveEntryPath) && romEntry.Size > 0)
        {
            return new ArchiveEntryInfo(romEntry.ArchiveEntryPath, romEntry.Size);
        }

        var sevenZipPath = GetSevenZipPath();
        if (!File.Exists(sevenZipPath))
        {
            return !string.IsNullOrWhiteSpace(romEntry.ArchiveEntryPath)
                ? new ArchiveEntryInfo(romEntry.ArchiveEntryPath, romEntry.Size)
                : null;
        }

        var entries = await ListArchiveEntriesAsync(sevenZipPath, pack.PackagePath, cancellationToken);
        var match = FindMatchingArchiveEntry(entries, romEntry);
        if (match != null)
        {
            return match;
        }

        return !string.IsNullOrWhiteSpace(romEntry.ArchiveEntryPath)
            ? new ArchiveEntryInfo(romEntry.ArchiveEntryPath, romEntry.Size)
            : null;
    }

    private static ArchiveEntryInfo? FindMatchingArchiveEntry(
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        RomPackRomEntry romEntry)
    {
        var candidates = new[]
            {
                romEntry.ArchiveEntryPath,
                romEntry.PackRelativePath
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeArchivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var romFileName = GetArchiveFileName(romEntry.PackRelativePath);

        foreach (var candidate in candidates)
        {
            var exact = entries.FirstOrDefault(entry =>
                string.Equals(NormalizeArchivePath(entry.Path), candidate, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }
        }

        foreach (var candidate in candidates)
        {
            var suffix = "/" + candidate;
            var suffixMatch = entries
                .Where(entry => NormalizeArchivePath(entry.Path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Path.Length)
                .FirstOrDefault();
            if (suffixMatch != null)
            {
                return suffixMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(romFileName))
        {
            return entries
                .Where(entry => string.Equals(GetArchiveFileName(entry.Path), romFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Path.Length)
                .FirstOrDefault();
        }

        return null;
    }

    private static async Task<IReadOnlyCollection<ArchiveEntryInfo>> ListArchiveEntriesAsync(
        string sevenZipPath,
        string archivePath,
        CancellationToken cancellationToken,
        bool romsOnly = true)
    {
        var candidates = new List<IReadOnlyCollection<ArchiveEntryInfo>>();
        if (File.Exists(sevenZipPath))
        {
            try
            {
                candidates.Add(await ListArchiveEntriesWith7ZipAsync(sevenZipPath, archivePath, cancellationToken, romsOnly));
            }
            catch
            {
                // Other archive readers below may still handle malformed or misnamed packs.
            }
        }

        try
        {
            candidates.Add(await ListArchiveEntriesWithTarAsync(archivePath, cancellationToken, romsOnly));
        }
        catch
        {
            // Windows tar/libarchive is a useful second opinion, especially for RAR/SFX packs.
        }

        if (ShouldTryZipLocalHeaderScan(candidates))
        {
            var zipLocalEntries = await ListArchiveEntriesFromZipLocalHeadersAsync(archivePath, cancellationToken, romsOnly);
            if (zipLocalEntries.Count > 0)
            {
                candidates.Add(zipLocalEntries);
            }
        }

        var best = candidates
            .OrderByDescending(entries => entries.Count)
            .FirstOrDefault();
        if (best is { Count: > 0 })
        {
            return best;
        }

        throw new InvalidOperationException($"Archive listing failed for {archivePath}: no archive reader returned entries.");
    }

    private static async Task<IReadOnlyCollection<ArchiveEntryInfo>> ListArchiveEntriesWith7ZipAsync(
        string sevenZipPath,
        string archivePath,
        CancellationToken cancellationToken,
        bool romsOnly)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("l");
        startInfo.ArgumentList.Add("-slt");
        startInfo.ArgumentList.Add(archivePath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start 7za.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Archive listing failed for {archivePath}: {await errorTask}");
        }

        return Parse7ZipListing(output, archivePath, romsOnly);
    }

    private static bool ShouldTryZipLocalHeaderScan(IReadOnlyCollection<IReadOnlyCollection<ArchiveEntryInfo>> candidates)
    {
        return candidates.Count == 0 || candidates.Max(entries => entries.Count) <= 1;
    }

    private static async Task<IReadOnlyCollection<ArchiveEntryInfo>> ListArchiveEntriesFromZipLocalHeadersAsync(
        string archivePath,
        CancellationToken cancellationToken,
        bool romsOnly)
    {
        var info = new FileInfo(archivePath);
        if (!info.Exists || info.Length > MaxZipLocalHeaderScanBytes)
        {
            return Array.Empty<ArchiveEntryInfo>();
        }

        var data = await File.ReadAllBytesAsync(archivePath, cancellationToken);
        var entries = new Dictionary<string, ArchiveEntryInfo>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset <= data.Length - 30; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsZipLocalHeader(data, offset))
            {
                continue;
            }

            var entry = TryReadZipLocalHeaderEntry(data, offset, romsOnly);
            if (entry == null)
            {
                continue;
            }

            entries[NormalizeArchivePath(entry.Path)] = entry;
            if (entry.DataOffset != null && entry.CompressedSize > 0)
            {
                var nextOffset = entry.DataOffset.Value + entry.CompressedSize.Value;
                if (nextOffset > offset && nextOffset < data.Length)
                {
                    offset = (int)Math.Min(nextOffset - 1, int.MaxValue - 1);
                }
            }
        }

        return entries.Values
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ArchiveEntryInfo? TryReadZipLocalHeaderEntry(byte[] data, int offset, bool romsOnly)
    {
        if (offset + 30 > data.Length)
        {
            return null;
        }

        var flags = BitConverter.ToUInt16(data, offset + 6);
        var method = BitConverter.ToUInt16(data, offset + 8);
        var compressedSize = BitConverter.ToUInt32(data, offset + 18);
        var uncompressedSize = BitConverter.ToUInt32(data, offset + 22);
        var fileNameLength = BitConverter.ToUInt16(data, offset + 26);
        var extraLength = BitConverter.ToUInt16(data, offset + 28);
        if (fileNameLength == 0 || fileNameLength > 4096)
        {
            return null;
        }

        var nameOffset = offset + 30;
        var dataOffset = nameOffset + fileNameLength + extraLength;
        if (nameOffset + fileNameLength > data.Length || dataOffset > data.Length)
        {
            return null;
        }

        var rawPath = Encoding.UTF8.GetString(data, nameOffset, fileNameLength);
        if (rawPath.IndexOf('\0') >= 0)
        {
            return null;
        }

        var path = NormalizeArchivePath(rawPath);
        if (string.IsNullOrWhiteSpace(path) ||
            path.EndsWith("/", StringComparison.Ordinal) ||
            (romsOnly && !IsArchiveRomCandidate(path)))
        {
            return null;
        }

        var usesDataDescriptor = (flags & 0x08) != 0;
        var knownCompressedSize = usesDataDescriptor ? 0 : compressedSize;
        if (knownCompressedSize > 0 && dataOffset + knownCompressedSize > data.Length)
        {
            return null;
        }

        return new ArchiveEntryInfo(
            path,
            uncompressedSize,
            offset,
            knownCompressedSize,
            method,
            dataOffset);
    }

    private static bool IsZipLocalHeader(byte[] data, int offset)
    {
        return data[offset] == 0x50 &&
            data[offset + 1] == 0x4b &&
            data[offset + 2] == 0x03 &&
            data[offset + 3] == 0x04;
    }

    private static async Task<bool> TryExtractZipLocalEntryAsync(
        string archivePath,
        string destinationDirectory,
        string entryPath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(archivePath);
        if (!info.Exists || info.Length > MaxZipLocalHeaderScanBytes)
        {
            return false;
        }

        var entries = await ListArchiveEntriesFromZipLocalHeadersAsync(archivePath, cancellationToken, romsOnly: false);
        var entry = entries.FirstOrDefault(candidate =>
            string.Equals(NormalizeArchivePath(candidate.Path), NormalizeArchivePath(entryPath), StringComparison.OrdinalIgnoreCase));
        return await TryExtractZipLocalEntryAsync(archivePath, destinationDirectory, entry, cancellationToken);
    }

    private static async Task<bool> TryExtractZipLocalEntryAsync(
        string archivePath,
        string destinationDirectory,
        ArchiveEntryInfo? entry,
        CancellationToken cancellationToken)
    {
        if (entry?.DataOffset == null ||
            entry.CompressedSize == null ||
            entry.CompressionMethod == null ||
            entry.CompressedSize <= 0)
        {
            return false;
        }

        await using var archive = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            useAsync: true);
        archive.Seek(entry.DataOffset.Value, SeekOrigin.Begin);
        await using var limited = new LimitedReadStream(archive, entry.CompressedSize.Value);

        var targetPath = Path.Combine(
            destinationDirectory,
            NormalizeArchivePath(entry.Path).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        switch (entry.CompressionMethod.Value)
        {
            case 0:
                await limited.CopyToAsync(output, cancellationToken);
                return true;
            case 8:
                await using (var deflate = new DeflateStream(limited, CompressionMode.Decompress))
                {
                    await deflate.CopyToAsync(output, cancellationToken);
                }

                return true;
            default:
                return false;
        }
    }

    private static async Task<IReadOnlyCollection<ArchiveEntryInfo>> ListArchiveEntriesWithTarAsync(
        string archivePath,
        CancellationToken cancellationToken,
        bool romsOnly)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tar.exe",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-tf");
        startInfo.ArgumentList.Add(archivePath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start tar.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Archive listing failed for {archivePath}: {await errorTask}");
        }

        return ParseTarListing(output, romsOnly);
    }

    private static IReadOnlyCollection<ArchiveEntryInfo> ParseTarListing(string output, bool romsOnly)
    {
        return output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeArchivePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !path.EndsWith("/", StringComparison.Ordinal))
            .Where(path => !romsOnly || IsArchiveRomCandidate(path))
            .Select(path => new ArchiveEntryInfo(path, 0))
            .ToList();
    }

    private static IReadOnlyCollection<ArchiveEntryInfo> Parse7ZipListing(string output, string archivePath, bool romsOnly)
    {
        var entries = new List<ArchiveEntryInfo>();
        string? currentPath = null;
        long currentSize = 0;
        var currentIsFolder = false;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(currentPath) ||
                currentIsFolder ||
                string.Equals(currentPath, archivePath, StringComparison.OrdinalIgnoreCase) ||
                (romsOnly && !IsArchiveRomCandidate(currentPath)))
            {
                currentPath = null;
                currentSize = 0;
                currentIsFolder = false;
                return;
            }

            entries.Add(new ArchiveEntryInfo(NormalizeArchivePath(currentPath), currentSize));
            currentPath = null;
            currentSize = 0;
            currentIsFolder = false;
        }

        foreach (var rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (rawLine.StartsWith("Path = ", StringComparison.Ordinal))
            {
                Flush();
                currentPath = rawLine["Path = ".Length..].Trim();
                continue;
            }

            if (rawLine.StartsWith("Size = ", StringComparison.Ordinal) &&
                long.TryParse(rawLine["Size = ".Length..].Trim(), out var size))
            {
                currentSize = size;
                continue;
            }

            if (rawLine.StartsWith("Folder = ", StringComparison.Ordinal))
            {
                currentIsFolder = string.Equals(rawLine["Folder = ".Length..].Trim(), "+", StringComparison.Ordinal);
            }
        }

        Flush();
        return entries;
    }

    private static string ResolveExtractedArchiveEntryPath(
        string destinationDirectory,
        string archiveEntryPath,
        string packRelativePath)
    {
        var exactPath = Path.Combine(destinationDirectory, archiveEntryPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(exactPath))
        {
            return exactPath;
        }

        var normalizedPackRelativePath = NormalizeArchivePath(packRelativePath);
        var suffix = Path.DirectorySeparatorChar + normalizedPackRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var suffixMatch = Directory.EnumerateFiles(destinationDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(suffixMatch))
        {
            return suffixMatch;
        }

        var fileName = GetArchiveFileName(packRelativePath);
        return Directory.EnumerateFiles(destinationDirectory, fileName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveArchiveContentPrefix(IReadOnlyCollection<ArchiveEntryInfo> entries, string packagePath)
    {
        var systemIds = Directory.Exists(RetroBatPaths.RomsRoot)
            ? Directory.EnumerateDirectories(RetroBatPaths.RomsRoot)
                .Select(Path.GetFileName)
                .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = entries
            .SelectMany(entry => EnumerateArchiveDirectoryPrefixes(entry.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(prefix => prefix.Count(ch => ch == '/'))
            .ThenBy(prefix => prefix.Length)
            .ToList();

        var retroBatRomsCandidate = candidates.FirstOrDefault(prefix =>
        {
            var segments = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 &&
                string.Equals(segments[^2], "roms", StringComparison.OrdinalIgnoreCase) &&
                systemIds.Contains(segments[^1]) &&
                ArchivePrefixHasGamelistOrRoms(entries, prefix);
        });
        if (!string.IsNullOrWhiteSpace(retroBatRomsCandidate))
        {
            return retroBatRomsCandidate;
        }

        var systemCandidate = candidates.FirstOrDefault(prefix =>
        {
            var name = GetArchiveFileName(prefix);
            return systemIds.Contains(name) && ArchivePrefixHasGamelistOrRoms(entries, prefix);
        });
        if (!string.IsNullOrWhiteSpace(systemCandidate))
        {
            return systemCandidate;
        }

        var archiveName = Path.GetFileNameWithoutExtension(packagePath);
        var namedCandidate = candidates.FirstOrDefault(prefix =>
            string.Equals(GetArchiveFileName(prefix), archiveName, StringComparison.OrdinalIgnoreCase));
        return namedCandidate ?? string.Empty;
    }

    private static List<ArchiveSystemContent> ResolveArchiveSystemContents(
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        string packagePath)
    {
        var systemIds = Directory.Exists(RetroBatPaths.RomsRoot)
            ? Directory.EnumerateDirectories(RetroBatPaths.RomsRoot)
                .Select(Path.GetFileName)
                .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contents = entries
            .SelectMany(entry => EnumerateArchiveDirectoryPrefixes(entry.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(prefix => new
            {
                Prefix = prefix,
                SystemId = ResolvePackSystemFolderToSystemId(GetArchiveFileName(prefix), systemIds)
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.SystemId))
            .Where(candidate => ArchivePrefixHasGamelistOrRoms(entries, candidate.Prefix))
            .GroupBy(candidate => candidate.Prefix, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ArchiveSystemContent(group.Key, group.First().SystemId))
            .OrderBy(content => content.ContentPrefix, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (contents.Count > 0)
        {
            return RemoveNestedArchiveSystemContents(contents);
        }

        var packageSystemId = ResolveSystemIdFromPackageName(packagePath);
        return string.IsNullOrWhiteSpace(packageSystemId)
            ? []
            : [new ArchiveSystemContent(string.Empty, packageSystemId)];
    }

    private static List<ArchiveSystemContent> RemoveNestedArchiveSystemContents(List<ArchiveSystemContent> contents)
    {
        return contents
            .Where(content => !contents.Any(other =>
                !ReferenceEquals(content, other) &&
                string.Equals(content.SystemId, other.SystemId, StringComparison.OrdinalIgnoreCase) &&
                ArchiveEntryIsUnderContentPrefix(other.ContentPrefix, content.ContentPrefix) &&
                !string.Equals(content.ContentPrefix, other.ContentPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string ResolvePackSystemFolderToSystemId(string folderName, IReadOnlySet<string> knownSystemIds)
    {
        var normalized = NormalizePackSystemFolderName(folderName);
        if (PackSystemFolderAliases.TryGetValue(normalized, out var alias))
        {
            return alias;
        }

        return knownSystemIds.Contains(normalized) ? normalized : string.Empty;
    }

    private static string NormalizePackSystemFolderName(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant().Replace('\\', '/').Replace(' ', '_');
    }

    private static IEnumerable<string> EnumerateArchiveDirectoryPrefixes(string entryPath)
    {
        var normalized = NormalizeArchivePath(entryPath);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length; i++)
        {
            yield return string.Join('/', segments.Take(i));
        }
    }

    private static bool ArchivePrefixHasGamelistOrRoms(IReadOnlyCollection<ArchiveEntryInfo> entries, string prefix)
    {
        var normalizedPrefix = NormalizeArchivePath(prefix);
        return entries.Any(entry =>
        {
            if (!ArchiveEntryIsUnderContentPrefix(entry.Path, normalizedPrefix))
            {
                return false;
            }

            var relative = StripArchiveContentPrefix(entry.Path, normalizedPrefix);
            return string.Equals(relative, "gamelist.xml", StringComparison.OrdinalIgnoreCase) ||
                IsArchiveRomCandidate(relative);
        });
    }

    private static string ResolveSystemIdFromArchiveContent(string contentPrefix, string packagePath)
    {
        var contentName = GetArchiveFileName(contentPrefix);
        if (!string.IsNullOrWhiteSpace(contentName) &&
            Directory.Exists(Path.Combine(RetroBatPaths.RomsRoot, contentName)))
        {
            return contentName;
        }

        var packageSystemId = ResolveSystemIdFromPackageName(packagePath);
        if (!string.IsNullOrWhiteSpace(packageSystemId))
        {
            return packageSystemId;
        }

        return contentName ?? string.Empty;
    }

    private async Task<XElement> LoadPackageGamelistRootFromArchiveAsync(
        string packagePath,
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        string contentPrefix,
        CancellationToken cancellationToken)
    {
        var gamelistEntry = entries
            .Where(entry => string.Equals(StripArchiveContentPrefix(entry.Path, contentPrefix), "gamelist.xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Path.Length)
            .FirstOrDefault();
        if (gamelistEntry == null)
        {
            return new XElement("gameList");
        }

        var tempRoot = Path.Combine(TempRoot, "gamelist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await ExtractArchiveEntryAsync(packagePath, tempRoot, gamelistEntry.Path, cancellationToken);
            var extractedPath = ResolveExtractedArchiveEntryPath(tempRoot, gamelistEntry.Path, "gamelist.xml");
            if (string.IsNullOrWhiteSpace(extractedPath) || !File.Exists(extractedPath))
            {
                return new XElement("gameList");
            }

            var document = XDocument.Load(extractedPath, LoadOptions.PreserveWhitespace);
            return document.Root != null ? new XElement(document.Root) : new XElement("gameList");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private void BuildMinimalOnTheFlyGamelist(
        XElement root,
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        string contentPrefix)
    {
        foreach (var entry in entries
            .Where(entry => ArchiveEntryIsUnderContentPrefix(entry.Path, contentPrefix))
            .Where(entry => IsArchiveRomCandidate(StripArchiveContentPrefix(entry.Path, contentPrefix)))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = "./" + StripArchiveContentPrefix(entry.Path, contentPrefix);
            root.Add(new XText(Environment.NewLine + "  "));
            root.Add(new XElement(
                "game",
                new XElement("path", relativePath),
                new XElement("name", _gameNameNormalizer.NormalizeDisplayName(null, relativePath))));
        }

        if (root.Elements("game").Any())
        {
            root.Add(new XText(Environment.NewLine));
        }
    }

    private static ArchiveEntryInfo? ResolveArchiveRelativeEntry(
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        string contentPrefix,
        string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var normalized = NormalizeArchivePath(rawPath);
        var direct = CombineArchivePath(contentPrefix, normalized);
        var exact = entries.FirstOrDefault(entry =>
            string.Equals(NormalizeArchivePath(entry.Path), direct, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var fileName = GetArchiveFileName(normalized);
        return string.IsNullOrWhiteSpace(fileName)
            ? null
            : entries.FirstOrDefault(entry =>
                string.Equals(GetArchiveFileName(entry.Path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int> ImportPackMediaFromArchiveAsync(
        string packagePath,
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        string contentPrefix,
        string tempRoot,
        XElement game,
        string systemId,
        string gameSlug,
        string packRelativeRomPath,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var gamelistMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var media in EnumeratePackGamelistMediaElements(game))
        {
            if (!gamelistMedia.Add(media.Key))
            {
                continue;
            }

            imported += await CopyArchiveMediaIfPresent(
                packagePath,
                entries,
                contentPrefix,
                tempRoot,
                media.RawPath,
                systemId,
                gameSlug,
                media.Kind,
                cancellationToken) ? 1 : 0;
        }

        var stem = Path.GetFileNameWithoutExtension(GetArchiveFileName(packRelativeRomPath));
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-image"), tempRoot, systemId, gameSlug, MediaKinds.Thumbnail, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-screenshot"), tempRoot, systemId, gameSlug, MediaKinds.Thumbnail, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-titleshot"), tempRoot, systemId, gameSlug, MediaKinds.Image, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-sstitle"), tempRoot, systemId, gameSlug, MediaKinds.Image, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-logo"), tempRoot, systemId, gameSlug, MediaKinds.Logo, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-wheel"), tempRoot, systemId, gameSlug, MediaKinds.Wheel, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-marquee"), tempRoot, systemId, gameSlug, MediaKinds.Logo, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-wheelcarbon"), tempRoot, systemId, gameSlug, MediaKinds.WheelCarbon, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-wheel-carbon"), tempRoot, systemId, gameSlug, MediaKinds.WheelCarbon, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-wheelsteel"), tempRoot, systemId, gameSlug, MediaKinds.WheelSteel, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-wheel-steel"), tempRoot, systemId, gameSlug, MediaKinds.WheelSteel, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-thumb"), tempRoot, systemId, gameSlug, MediaKinds.Box3d, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-thumbnail"), tempRoot, systemId, gameSlug, MediaKinds.Box3d, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-box"), tempRoot, systemId, gameSlug, MediaKinds.BoxFront, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-box2d"), tempRoot, systemId, gameSlug, MediaKinds.BoxFront, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-box-2d"), tempRoot, systemId, gameSlug, MediaKinds.BoxFront, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-box3d"), tempRoot, systemId, gameSlug, MediaKinds.Box3d, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-box-3d"), tempRoot, systemId, gameSlug, MediaKinds.Box3d, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-fanart"), tempRoot, systemId, gameSlug, MediaKinds.Fanart, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-flyer"), tempRoot, systemId, gameSlug, MediaKinds.Flyer, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-video"), tempRoot, systemId, gameSlug, MediaKinds.Video, cancellationToken) ? 1 : 0;
        imported += await CopyArchiveMediaIfPresent(packagePath, FindSiblingArchiveMedia(entries, contentPrefix, stem, "-manual"), tempRoot, systemId, gameSlug, MediaKinds.Manual, cancellationToken) ? 1 : 0;
        return imported;
    }

    private async Task<int> InstallArchiveSamplesAsync(
        string packagePath,
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        IReadOnlySet<string> packageSystemIds,
        CancellationToken cancellationToken)
    {
        var sampleEntries = entries
            .Where(entry => IsArchiveSampleEntry(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sampleEntries.Count == 0)
        {
            return 0;
        }

        var destinationRoots = ResolveSampleDestinationRoots(packageSystemIds).ToArray();
        if (destinationRoots.Length == 0)
        {
            return 0;
        }

        var tempRoot = Path.Combine(TempRoot, "samples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var copied = 0;
        try
        {
            foreach (var entry in sampleEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExtractArchiveEntryAsync(packagePath, tempRoot, entry, cancellationToken);
                var extractedPath = ResolveExtractedArchiveEntryPath(tempRoot, entry.Path, entry.Path);
                if (string.IsNullOrWhiteSpace(extractedPath) || !File.Exists(extractedPath))
                {
                    continue;
                }

                var fileName = GetArchiveFileName(entry.Path);
                foreach (var destinationRoot in destinationRoots)
                {
                    Directory.CreateDirectory(destinationRoot);
                    File.Copy(extractedPath, Path.Combine(destinationRoot, fileName), overwrite: true);
                    copied++;
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }

        return copied;
    }

    private static IEnumerable<string> ResolveSampleDestinationRoots(IReadOnlySet<string> packageSystemIds)
    {
        if (packageSystemIds.Contains("fbneo"))
        {
            yield return Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "fbneo", "samples");
        }

        if (packageSystemIds.Contains("mame") || packageSystemIds.Contains("arcade"))
        {
            yield return Path.Combine(RetroBatPaths.RetroBatRoot, "bios", "mame", "samples");
        }
    }

    private static bool IsArchiveSampleEntry(string path)
    {
        var segments = NormalizeArchivePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
            segments.Any(segment => string.Equals(segment, "samples", StringComparison.OrdinalIgnoreCase)) &&
            !path.EndsWith("/", StringComparison.Ordinal);
    }

    private async Task<bool> CopyArchiveMediaIfPresent(
        string packagePath,
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        string contentPrefix,
        string tempRoot,
        string? rawPath,
        string systemId,
        string gameSlug,
        string kind,
        CancellationToken cancellationToken)
    {
        var entry = ResolveArchiveRelativeEntry(entries, contentPrefix, rawPath);
        return await CopyArchiveMediaIfPresent(packagePath, entry, tempRoot, systemId, gameSlug, kind, cancellationToken);
    }

    private async Task<bool> CopyArchiveMediaIfPresent(
        string packagePath,
        ArchiveEntryInfo? entry,
        string tempRoot,
        string systemId,
        string gameSlug,
        string kind,
        CancellationToken cancellationToken)
    {
        if (entry == null || IsArchiveRomCandidate(entry.Path))
        {
            return false;
        }

        Directory.CreateDirectory(tempRoot);
        var extractionRoot = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);
        try
        {
            await ExtractArchiveEntryAsync(packagePath, extractionRoot, entry.Path, cancellationToken);
            var sourcePath = ResolveExtractedArchiveEntryPath(extractionRoot, entry.Path, entry.Path);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return false;
            }

            var destinationPath = GetCanonicalImportPath(systemId, gameSlug, kind, sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return true;
        }
        finally
        {
            TryDeleteDirectory(extractionRoot);
        }
    }

    private async Task EnsureArchiveRomIdentifiersAsync(
        string packagePath,
        ArchiveEntryInfo sourceEntry,
        string tempRoot,
        XElement game,
        CancellationToken cancellationToken)
    {
        var hasMd5 = !string.IsNullOrWhiteSpace(NormalizeHex(game.Element("md5")?.Value));
        var hasCrc32 = !string.IsNullOrWhiteSpace(NormalizeHex(game.Element("crc32")?.Value));
        if (hasMd5 || hasCrc32)
        {
            return;
        }

        Directory.CreateDirectory(tempRoot);
        var extractionRoot = Path.Combine(tempRoot, "rom-ident-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);
        try
        {
            ReportStartupProgress(0, 1, $"identifiants {GetArchiveFileName(sourceEntry.Path)}");
            await ExtractArchiveEntryAsync(packagePath, extractionRoot, sourceEntry, cancellationToken);
            var sourcePath = ResolveExtractedArchiveEntryPath(extractionRoot, sourceEntry.Path, sourceEntry.Path);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            game.SetElementValue("md5", ComputeRomMd5(sourcePath));
            game.SetElementValue("crc32", ComputeCrc32(sourcePath));
        }
        finally
        {
            TryDeleteDirectory(extractionRoot);
        }
    }

    private static ArchiveEntryInfo? FindSiblingArchiveMedia(
        IReadOnlyCollection<ArchiveEntryInfo> entries,
        string contentPrefix,
        string romStem,
        string suffix)
    {
        if (string.IsNullOrWhiteSpace(romStem))
        {
            return null;
        }

        return entries
            .Where(entry =>
            {
                var relative = StripArchiveContentPrefix(entry.Path, contentPrefix);
                if (IsArchiveRomCandidate(relative))
                {
                    return false;
                }

                return string.Equals(
                    Path.GetFileNameWithoutExtension(GetArchiveFileName(relative)),
                    romStem + suffix,
                    StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private string ResolveInstalledRomFileNameFromArchiveEntry(string packRelativePath)
    {
        return GetArchiveFileName(packRelativePath);
    }

    private static string StripArchiveContentPrefix(string entryPath, string contentPrefix)
    {
        var normalizedEntry = NormalizeArchivePath(entryPath);
        var normalizedPrefix = NormalizeArchivePath(contentPrefix);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
        {
            return normalizedEntry;
        }

        if (string.Equals(normalizedEntry, normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = normalizedPrefix.TrimEnd('/') + "/";
        return normalizedEntry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedEntry[prefix.Length..]
            : normalizedEntry;
    }

    private static bool ArchiveEntryIsUnderContentPrefix(string entryPath, string contentPrefix)
    {
        var normalizedEntry = NormalizeArchivePath(entryPath);
        var normalizedPrefix = NormalizeArchivePath(contentPrefix);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
        {
            return true;
        }

        return string.Equals(normalizedEntry, normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
            normalizedEntry.StartsWith(normalizedPrefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineArchivePath(string prefix, string relativePath)
    {
        var normalizedPrefix = NormalizeArchivePath(prefix);
        var normalizedRelative = NormalizeArchivePath(relativePath);
        return string.IsNullOrWhiteSpace(normalizedPrefix)
            ? normalizedRelative
            : normalizedPrefix.TrimEnd('/') + "/" + normalizedRelative;
    }

    private static bool IsArchiveRomCandidate(string path)
    {
        var normalized = NormalizeArchivePath(path);
        return RomExtensions.Contains(GetArchiveExtension(normalized)) && !IsProbablyMediaOrMetadataArchivePath(normalized);
    }

    private static bool IsProbablyMediaOrMetadataArchivePath(string path)
    {
        var normalized = "/" + NormalizeArchivePath(path);
        return normalized.Contains("/images/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/videos/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/media/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/manuals/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/samples/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/downloaded_images/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSevenZipPath()
    {
        return Path.Combine(RetroBatPaths.RetroBatRoot, "system", "tools", "7za.exe");
    }

    private static string NormalizeArchivePath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('.', '/');
    }

    private static string GetArchiveFileName(string path)
    {
        return Path.GetFileName(NormalizeArchivePath(path).Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetArchiveExtension(string path)
    {
        return Path.GetExtension(GetArchiveFileName(path));
    }

    private static async Task ExtractWithTarAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken,
        string? entryPath = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tar.exe",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-xf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destinationDirectory);
        if (!string.IsNullOrWhiteSpace(entryPath))
        {
            startInfo.ArgumentList.Add(entryPath);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start tar.exe.");
        var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Archive extraction failed for {archivePath}: {await stdErr}");
        }

        await stdOut;
    }

    private static IEnumerable<string> EnumeratePackageRomCandidates(string contentRoot)
    {
        return Directory.EnumerateFiles(contentRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => RomExtensions.Contains(Path.GetExtension(path)) && !IsProbablyMediaOrMetadataPath(contentRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsProbablyMediaOrMetadataPath(string contentRoot, string path)
    {
        var relative = Path.GetRelativePath(contentRoot, path).Replace('\\', '/');
        return relative.Contains("/images/", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("/videos/", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("/media/", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("/manuals/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindSiblingMedia(string contentRoot, string romStem, string suffix)
    {
        if (string.IsNullOrWhiteSpace(romStem))
        {
            return null;
        }

        return Directory.EnumerateFiles(contentRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                return stem.Equals(romStem + suffix, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string ResolveContentRoot(string tempRoot, string packagePath)
    {
        var candidates = new[] { tempRoot }
            .Concat(Directory.EnumerateDirectories(tempRoot, "*", SearchOption.AllDirectories))
            .OrderBy(path => Path.GetRelativePath(tempRoot, path).Count(ch => ch == Path.DirectorySeparatorChar))
            .ThenBy(path => path.Length)
            .ToList();

        var retroBatRomsCandidate = ResolveRetroBatArchiveSystemContentRoot(candidates);
        if (!string.IsNullOrWhiteSpace(retroBatRomsCandidate))
        {
            return retroBatRomsCandidate;
        }

        var namedCandidate = candidates.FirstOrDefault(path =>
            Directory.Exists(Path.Combine(RetroBatPaths.RomsRoot, Path.GetFileName(path))) &&
            (File.Exists(Path.Combine(path, "gamelist.xml")) || HasRomCandidate(path)));
        if (!string.IsNullOrWhiteSpace(namedCandidate))
        {
            return namedCandidate;
        }

        var archiveName = Path.GetFileNameWithoutExtension(packagePath);
        var byArchiveName = candidates.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), archiveName, StringComparison.OrdinalIgnoreCase));
        return byArchiveName ?? candidates.First();
    }

    private static string ResolveRetroBatArchiveSystemContentRoot(IReadOnlyList<string> candidates)
    {
        var systemIds = Directory.Exists(RetroBatPaths.RomsRoot)
            ? Directory.EnumerateDirectories(RetroBatPaths.RomsRoot)
                .Select(Path.GetFileName)
                .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (systemIds.Count == 0)
        {
            return string.Empty;
        }

        return candidates
            .Where(path =>
            {
                var directory = new DirectoryInfo(path);
                return systemIds.Contains(directory.Name) &&
                    string.Equals(directory.Parent?.Name, "roms", StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(Path.Combine(path, "gamelist.xml")) || HasRomCandidate(path));
            })
            .OrderByDescending(path => File.Exists(Path.Combine(path, "gamelist.xml")))
            .ThenBy(path => path.Length)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveSystemId(string contentRoot, string packagePath)
    {
        var contentName = Path.GetFileName(contentRoot);
        if (!string.IsNullOrWhiteSpace(contentName) &&
            Directory.Exists(Path.Combine(RetroBatPaths.RomsRoot, contentName)))
        {
            return contentName;
        }

        var packageSystemId = ResolveSystemIdFromPackageName(packagePath);
        if (!string.IsNullOrWhiteSpace(packageSystemId))
        {
            return packageSystemId;
        }

        return contentName ?? string.Empty;
    }

    private static string ResolveSystemIdFromPackageName(string packagePath)
    {
        var archiveName = Path.GetFileNameWithoutExtension(packagePath);
        var systemIds = Directory.Exists(RetroBatPaths.RomsRoot)
            ? Directory.EnumerateDirectories(RetroBatPaths.RomsRoot)
                .Select(Path.GetFileName)
                .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                .Cast<string>()
                .OrderByDescending(systemId => systemId.Length)
                .ThenBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        var aliasedSystemId = ResolveAliasedPackageSystemId(archiveName, systemIds);
        if (!string.IsNullOrWhiteSpace(aliasedSystemId))
        {
            return aliasedSystemId;
        }

        var exactSystemId = systemIds.FirstOrDefault(systemId =>
            string.Equals(archiveName, systemId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exactSystemId))
        {
            return exactSystemId;
        }

        return systemIds.FirstOrDefault(systemId => ContainsBoundedToken(archiveName, systemId)) ?? string.Empty;
    }

    private static string ResolveAliasedPackageSystemId(string archiveName, IReadOnlyCollection<string> systemIds)
    {
        var normalized = NormalizePackageSystemToken(archiveName);
        var alias = normalized.Contains("megadrive32x", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("mega32x", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("sega32x", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, "32x", StringComparison.OrdinalIgnoreCase)
            ? "sega32x"
            : string.Empty;

        return string.IsNullOrWhiteSpace(alias) || !systemIds.Contains(alias, StringComparer.OrdinalIgnoreCase)
            ? string.Empty
            : alias;
    }

    private static string NormalizePackageSystemToken(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static bool ContainsBoundedToken(string value, string token)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var afterIndex = index + token.Length;
            var afterOk = afterIndex >= value.Length || !char.IsLetterOrDigit(value[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index = value.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool HasRomCandidate(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Any(path => RomExtensions.Contains(Path.GetExtension(path)));
    }

    private static string? ResolvePackageRelativeFile(string contentRoot, string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var normalized = rawPath.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directPath = Path.GetFullPath(Path.Combine(contentRoot, normalized));
        var fullRoot = Path.GetFullPath(contentRoot);
        if (directPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(directPath))
        {
            return directPath;
        }

        var fileName = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(fileName)
            ? null
            : Directory.EnumerateFiles(contentRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string ResolveGamePath(string systemRoot, string rawPath)
    {
        if (Path.IsPathRooted(rawPath))
        {
            return rawPath;
        }

        var normalized = rawPath.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(systemRoot, normalized));
    }

    private static string InferSystemIdFromGamePath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        var fullGamePath = Path.GetFullPath(gamePath);
        var romsRoot = Path.GetFullPath(RetroBatPaths.RomsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullGamePath.StartsWith(romsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var relative = Path.GetRelativePath(romsRoot, fullGamePath);
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : string.Empty;
    }

    private static XDocument LoadOrCreateGamelistDocument(string gamelistPath)
    {
        if (!File.Exists(gamelistPath))
        {
            return new XDocument(new XElement("gameList"));
        }

        try
        {
            using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return new XDocument(new XElement("gameList"));
        }
    }

    private static Dictionary<string, XElement> LoadPreviousGamelistNodes(string systemRoot)
    {
        var nodes = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var previousPath = Path.Combine(systemRoot, "gamelist.xml.old");
        if (!File.Exists(previousPath))
        {
            return nodes;
        }

        try
        {
            var document = XDocument.Load(previousPath, LoadOptions.PreserveWhitespace);
            foreach (var game in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
            {
                var path = NormalizeGamelistPath(game.Element("path")?.Value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    nodes[path] = new XElement(game);
                }
            }
        }
        catch
        {
            nodes.Clear();
        }

        return nodes;
    }

    private static XElement BuildMinimalIndexedRomNode(RomPackRomEntry rom, string relativePath)
    {
        var node = new XElement(
            "game",
            new XElement("path", relativePath),
            new XElement("name", ResolveIndexedRomDisplayName(rom)));
        if (!string.IsNullOrWhiteSpace(rom.Md5))
        {
            node.Add(new XElement("md5", NormalizeHex(rom.Md5)));
        }

        if (!string.IsNullOrWhiteSpace(rom.Crc32))
        {
            node.Add(new XElement("crc32", NormalizeHex(rom.Crc32)));
        }

        return node;
    }

    private static string ResolveIndexedRomDisplayName(RomPackRomEntry rom)
    {
        if (!string.IsNullOrWhiteSpace(rom.GameName))
        {
            return GameNameNormalizer.NormalizeDisplayNameValue(rom.GameName, rom.GamePath);
        }

        var fileName = !string.IsNullOrWhiteSpace(rom.DestinationFileName)
            ? rom.DestinationFileName
            : rom.GamePath;
        return GameNameNormalizer.NormalizeDisplayNameValue(null, fileName);
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

    private static bool EnsureOnTheFlyPlaceholder(string systemRoot, RomPackRomEntry rom)
    {
        if (string.IsNullOrWhiteSpace(rom.DestinationFileName))
        {
            return false;
        }

        var destinationPath = Path.Combine(
            systemRoot,
            rom.DestinationFileName.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? systemRoot);
        WriteOnTheFlyPlaceholder(destinationPath, rom);
        return true;
    }

    private static void WriteOnTheFlyPlaceholder(string destinationPath, RomPackRomEntry rom)
    {
        var payload = string.Join(
            Environment.NewLine,
            OnTheFlyPlaceholderMarker,
            "This lightweight file keeps EmulationStation from pruning an on-the-fly ROM entry.",
            "The real ROM is extracted from the indexed pack when the configured trigger runs.",
            "system=" + rom.SystemId,
            "game=" + rom.GameName,
            "packPath=" + rom.PackRelativePath,
            string.Empty);
        WriteTextAtomically(destinationPath, payload);
    }

    private static async Task WriteOnTheFlyPlaceholderWithRetryAsync(
        string destinationPath,
        RomPackRomEntry rom,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                WriteOnTheFlyPlaceholder(destinationPath, rom);
                return;
            }
            catch (Exception ex) when (
                attempt < OnTheFlyPlaceholderWriteRetryCount &&
                ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(OnTheFlyPlaceholderWriteRetryDelay, cancellationToken);
            }
        }
    }

    private static void WriteTextAtomically(string destinationPath, string payload)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, payload, Encoding.UTF8);
            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsOnTheFlyPlaceholder(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 8192)
            {
                return false;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[info.Length];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return text.Contains(OnTheFlyPlaceholderMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    private RomPackRomEntry? FindIndexedRom(string systemId, string gamePath, string gameName)
    {
        var fileName = Path.GetFileName(gamePath);
        return _index.Packs
            .Where(pack => string.Equals(pack.Status, "installed", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pack => pack.Roms)
            .FirstOrDefault(rom =>
                string.Equals(rom.SystemId, systemId, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(Path.GetFileName(rom.GamePath), fileName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rom.GamePath, gamePath, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(gameName) && string.Equals(rom.GameName, gameName, StringComparison.OrdinalIgnoreCase))));
    }

    private RomPackIndexEntry? FindReusableInstalledPack(string packagePath, FileInfo fileInfo, bool onTheFly)
    {
        return _index.Packs.FirstOrDefault(pack =>
            IsReusableInstalledPack(pack, packagePath, onTheFly) &&
            pack.Size == fileInfo.Length &&
            pack.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc);
    }

    private RomPackIndexEntry? FindReusableInstalledPack(string packagePath, string hash, bool onTheFly)
    {
        return _index.Packs.FirstOrDefault(pack =>
            IsReusableInstalledPack(pack, packagePath, onTheFly) &&
            string.Equals(pack.Sha256, hash, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsReusableInstalledPack(RomPackIndexEntry pack, string packagePath, bool onTheFly)
    {
        var expectedSystemId = ResolveSystemIdFromPackageName(packagePath);
        if (!IsMultiSystemPack(pack) &&
            !string.IsNullOrWhiteSpace(expectedSystemId) &&
            !string.Equals(pack.SystemId, expectedSystemId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(pack.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pack.Status, "installed", StringComparison.OrdinalIgnoreCase) &&
            pack.OnTheFlyMode == onTheFly &&
            pack.UnzipRoms == _runtimeOptions.ShouldUnzipRomPackInstallerRoms() &&
            pack.Roms.Count > 0 &&
            !string.IsNullOrWhiteSpace(pack.Sha256) &&
            !RequiresRarListingRepair(pack, packagePath);
    }

    private static bool IsMultiSystemPack(RomPackIndexEntry pack)
    {
        return string.Equals(pack.SystemId, "multi", StringComparison.OrdinalIgnoreCase) ||
            pack.Roms
                .Select(rom => rom.SystemId)
                .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();
    }

    private static bool RequiresRarListingRepair(RomPackIndexEntry pack, string packagePath)
    {
        return string.Equals(Path.GetExtension(packagePath), ".rar", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(pack.ImporterVersion, ImporterVersion, StringComparison.OrdinalIgnoreCase) &&
            pack.Roms.Count <= 1;
    }

    private bool RequiresRarListingRepair(string packagePath, string hash)
    {
        return _index.Packs.Any(pack =>
            string.Equals(pack.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(hash) || string.Equals(pack.Sha256, hash, StringComparison.OrdinalIgnoreCase)) &&
            RequiresRarListingRepair(pack, packagePath));
    }

    private static bool RefreshReusablePackMetadata(RomPackIndexEntry pack, FileInfo fileInfo)
    {
        var changed = false;
        if (pack.Size != fileInfo.Length)
        {
            pack.Size = fileInfo.Length;
            changed = true;
        }

        if (pack.LastWriteTimeUtc != fileInfo.LastWriteTimeUtc)
        {
            pack.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            changed = true;
        }

        if (!string.Equals(pack.ImporterVersion, ImporterVersion, StringComparison.OrdinalIgnoreCase))
        {
            pack.ImporterVersion = ImporterVersion;
            changed = true;
        }

        return changed;
    }

    private async Task AuditIndexedPackReuseInvalidatedAsync(
        string packagePath,
        FileInfo fileInfo,
        bool onTheFly,
        string phase,
        CancellationToken cancellationToken,
        string hash = "")
    {
        var candidates = _index.Packs
            .Where(pack => string.Equals(pack.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase))
            .Where(pack => string.IsNullOrWhiteSpace(hash) || string.Equals(pack.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var pack in candidates)
        {
            var state = ResolveIndexedPackRuntimeState(pack, onTheFly);
            if (state.Reusable)
            {
                continue;
            }

            await AppendInstallerLogAsync(
                "package-reuse-invalidated",
                new
                {
                    packagePath,
                    phase,
                    pack.SystemId,
                    pack.Roms.Count,
                    onTheFly,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc,
                    reason = state.Reason,
                    state.GamelistPath,
                    state.MediaRoot
                },
                cancellationToken);
        }
    }

    private static bool IsIndexedPackRuntimeMaterialized(RomPackIndexEntry pack, bool onTheFly)
    {
        return ResolveIndexedPackRuntimeState(pack, onTheFly).Reusable;
    }

    private static IndexedPackRuntimeState ResolveIndexedPackRuntimeState(RomPackIndexEntry pack, bool onTheFly)
    {
        if (IsMultiSystemPack(pack))
        {
            foreach (var group in pack.Roms.GroupBy(rom => rom.SystemId, StringComparer.OrdinalIgnoreCase))
            {
                var roms = group.ToList();
                if (string.IsNullOrWhiteSpace(group.Key))
                {
                    return new IndexedPackRuntimeState(false, "missing-system-id", string.Empty, string.Empty);
                }

                var groupSystemRoot = Path.Combine(RetroBatPaths.RomsRoot, group.Key);
                var groupGamelistPath = Path.Combine(groupSystemRoot, "gamelist.xml");
                if (!File.Exists(groupGamelistPath) || !GamelistContainsIndexedGames(groupGamelistPath, roms))
                {
                    return new IndexedPackRuntimeState(false, "gamelist-missing-or-incomplete", groupGamelistPath, string.Empty);
                }

                if (!roms.All(rom => IndexedRomTargetExists(groupSystemRoot, rom)))
                {
                    return new IndexedPackRuntimeState(
                        false,
                        onTheFly ? "on-the-fly-placeholder-missing" : "rom-target-missing",
                        groupGamelistPath,
                        string.Empty);
                }
            }

            if (pack.MediaFilesImportedCount > 0)
            {
                var hasAnyMedia = pack.Roms
                    .Select(rom => rom.SystemId)
                    .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Any(systemId =>
                    {
                        var mediaRoot = Path.Combine(RetroBatPaths.MediaSystemsRoot, systemId);
                        return Directory.Exists(mediaRoot) &&
                            Directory.EnumerateFiles(mediaRoot, "*", SearchOption.AllDirectories).Any();
                    });
                if (!hasAnyMedia)
                {
                    return new IndexedPackRuntimeState(false, "canonical-media-missing", string.Empty, string.Empty);
                }
            }

            return new IndexedPackRuntimeState(true, string.Empty, string.Empty, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(pack.SystemId))
        {
            return new IndexedPackRuntimeState(false, "missing-system-id", string.Empty, string.Empty);
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, pack.SystemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        var mediaRoot = Path.Combine(RetroBatPaths.MediaSystemsRoot, pack.SystemId);
        if (pack.MediaFilesImportedCount > 0 &&
            (!Directory.Exists(mediaRoot) || !Directory.EnumerateFiles(mediaRoot, "*", SearchOption.AllDirectories).Any()))
        {
            return new IndexedPackRuntimeState(false, "canonical-media-missing", gamelistPath, mediaRoot);
        }

        if (!File.Exists(gamelistPath) || !GamelistContainsIndexedGames(gamelistPath, pack.Roms))
        {
            return new IndexedPackRuntimeState(false, "gamelist-missing-or-incomplete", gamelistPath, string.Empty);
        }

        if (!pack.Roms.All(rom => IndexedRomTargetExists(systemRoot, rom)))
        {
            return new IndexedPackRuntimeState(
                false,
                onTheFly ? "on-the-fly-placeholder-missing" : "rom-target-missing",
                gamelistPath,
                string.Empty);
        }

        return new IndexedPackRuntimeState(true, string.Empty, gamelistPath, mediaRoot);
    }

    private static bool RequiresFullPackageReinstall(IndexedPackRuntimeState runtimeState)
    {
        return string.Equals(runtimeState.Reason, "canonical-media-missing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndexedRomTargetExists(string systemRoot, RomPackRomEntry rom)
    {
        if (string.IsNullOrWhiteSpace(rom.DestinationFileName))
        {
            return false;
        }

        var targetPath = Path.Combine(systemRoot, rom.DestinationFileName.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(targetPath) || Directory.Exists(targetPath);
    }

    private bool TryRebuildMaterializedPackIndex(
        string packagePath,
        FileInfo fileInfo,
        string hash,
        bool onTheFly,
        out RomPackIndexEntry rebuilt)
    {
        rebuilt = new RomPackIndexEntry();
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        var systemId = ResolveSystemIdFromPackageName(packagePath);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return false;
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, systemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return false;
        }

        XDocument document;
        try
        {
            using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }

        var roms = new List<RomPackRomEntry>();
        foreach (var game in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
        {
            var rawPath = game.Element("path")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var destinationFileName = ResolveDestinationFileNameFromGamelistPath(rawPath);
            if (string.IsNullOrWhiteSpace(destinationFileName))
            {
                continue;
            }

            var targetPath = Path.Combine(systemRoot, destinationFileName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                continue;
            }

            var name = game.Element("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = _gameNameNormalizer.NormalizeDisplayName(null, destinationFileName);
            }

            roms.Add(new RomPackRomEntry
            {
                SystemId = systemId,
                GameName = name,
                GamePath = "./" + destinationFileName.Replace('\\', '/'),
                DestinationFileName = destinationFileName.Replace('\\', '/'),
                PackRelativePath = destinationFileName.Replace('\\', '/'),
                ArchiveEntryPath = $"{systemId}/{destinationFileName.Replace('\\', '/')}",
                Md5 = NormalizeHex(game.Element("md5")?.Value),
                Crc32 = NormalizeHex(game.Element("crc32")?.Value),
                Size = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0
            });
        }

        if (roms.Count == 0)
        {
            return false;
        }

        if (onTheFly && !roms.All(rom => IndexedRomTargetExists(systemRoot, rom)))
        {
            return false;
        }

        rebuilt = new RomPackIndexEntry
        {
            PackagePath = packagePath,
            Sha256 = hash,
            Size = fileInfo.Length,
            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
            InstalledAtUtc = DateTime.UtcNow,
            Status = "installed",
            ImporterVersion = ImporterVersion,
            SystemId = systemId,
            OnTheFlyMode = onTheFly,
            UnzipRoms = _runtimeOptions.ShouldUnzipRomPackInstallerRoms(),
            MediaFilesImportedCount = 0,
            Roms = roms
        };
        return true;
    }

    private static string ResolveDestinationFileNameFromGamelistPath(string rawPath)
    {
        var normalized = NormalizeGamelistPath(rawPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.TrimStart('.', '/');
    }

    private static bool GamelistContainsIndexedGames(string gamelistPath, IReadOnlyCollection<RomPackRomEntry> roms)
    {
        if (roms.Count == 0)
        {
            return false;
        }

        try
        {
            using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var existingPaths = document.Root?
                .Elements("game")
                .Select(game => NormalizeGamelistPath(game.Element("path")?.Value))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return roms
                .Select(rom => NormalizeGamelistPath(rom.GamePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .All(existingPaths.Contains);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    private void ReportStartupProgress(int current, int total, string detail)
    {
        _startupOverlay.UpdateStartupProgress(
            "startup_rom_pack_installer",
            Math.Max(0, current),
            Math.Max(1, total),
            detail);
    }

    private void LogInstallerProgress(string message)
    {
        _logger?.LogInformation("{Message}", message);
    }

    private static bool IsCurrentlySelectedGame(GameReference selected)
    {
        if (!TryReadCurrentSelectedGameFromEventsIni(out var currentSystemId, out var currentGamePath))
        {
            return false;
        }

        var sameSystem = string.Equals(currentSystemId, selected.SystemId, StringComparison.OrdinalIgnoreCase);
        if (!sameSystem)
        {
            return false;
        }

        return string.Equals(
            NormalizeGamePathForSelection(currentGamePath),
            NormalizeGamePathForSelection(selected.GamePath),
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

            var lines = EventsIniFile.ReadAllLines(RetroBatPaths.EventsIniPath);
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

    private static bool IsLikelySystemId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 32 &&
            value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '+');
    }

    private static bool IsLikelyGamePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Replace('\\', '/');
        return normalized.Contains('/') || normalized.StartsWith(".", StringComparison.Ordinal) || RomExtensions.Contains(Path.GetExtension(value));
    }

    private static string NormalizeGamePathForSelection(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .TrimStart('.', '/');
    }

    private static GameReference? ExtractGameReference(object? payload)
    {
        var context = payload?.GetType().GetProperty("Context")?.GetValue(payload) as GameState;
        if (context?.Selected != null)
        {
            return context.Selected;
        }

        var rawArgs = payload?.GetType().GetProperty("RawArgs")?.GetValue(payload);
        if (rawArgs is IEnumerable<string> args)
        {
            var values = args.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (values.Length >= 2)
            {
                return new GameReference
                {
                    SystemId = values[0],
                    GamePath = values[1],
                    GameName = values.Length >= 3 ? values[2] : Path.GetFileNameWithoutExtension(values[1])
                };
            }
        }

        return null;
    }

    private static bool SameRomEntry(RomPackRomEntry left, RomPackRomEntry right)
    {
        return string.Equals(left.SystemId, right.SystemId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.GamePath, right.GamePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.PackRelativePath, right.PackRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveInstalledRomFileName(string sourceRom, string tempRoot, bool onTheFly)
    {
        if (!_runtimeOptions.ShouldUnzipRomPackInstallerRoms() ||
            !PackExtensions.Contains(Path.GetExtension(sourceRom)))
        {
            return Path.GetFileName(sourceRom);
        }

        var innerRoot = Path.Combine(tempRoot, "__rom_unpack_probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(innerRoot);
        try
        {
            ExtractArchiveAsync(sourceRom, innerRoot, CancellationToken.None).GetAwaiter().GetResult();
            var innerRom = EnumeratePackageRomCandidates(innerRoot).FirstOrDefault()
                ?? Directory.EnumerateFiles(innerRoot, "*.*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            return string.IsNullOrWhiteSpace(innerRom)
                ? Path.GetFileName(sourceRom)
                : Path.GetFileName(innerRom);
        }
        finally
        {
            TryDeleteDirectory(innerRoot);
        }
    }

    private void InstallRomFile(string sourceRom, string destinationRomPath, string tempRoot, CancellationToken cancellationToken)
    {
        if (!_runtimeOptions.ShouldUnzipRomPackInstallerRoms() ||
            !PackExtensions.Contains(Path.GetExtension(sourceRom)))
        {
            File.Copy(sourceRom, destinationRomPath, overwrite: true);
            return;
        }

        var innerRoot = Path.Combine(tempRoot, "__rom_unpack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(innerRoot);
        try
        {
            ExtractArchiveAsync(sourceRom, innerRoot, cancellationToken).GetAwaiter().GetResult();
            var innerRom = EnumeratePackageRomCandidates(innerRoot).FirstOrDefault()
                ?? Directory.EnumerateFiles(innerRoot, "*.*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(innerRom) || !File.Exists(innerRom))
            {
                File.Copy(sourceRom, destinationRomPath, overwrite: true);
                return;
            }

            File.Copy(innerRom, destinationRomPath, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(innerRoot);
        }
    }

    private void UpsertPack(RomPackIndexEntry entry)
    {
        _index.Packs.RemoveAll(pack =>
            string.Equals(pack.PackagePath, entry.PackagePath, StringComparison.OrdinalIgnoreCase));
        _index.Packs.Add(entry);
        _index.UpdatedAtUtc = DateTime.UtcNow;
    }

    private RomPackInstallerIndex LoadIndex()
    {
        try
        {
            return File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<RomPackInstallerIndex>(File.ReadAllText(IndexPath), JsonOptions) ?? new RomPackInstallerIndex()
                : new RomPackInstallerIndex();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Unable to load ROM pack installer index.");
            return new RomPackInstallerIndex();
        }
    }

    private bool TrySkipStartupScanFromCache(
        PackageDirectorySnapshot packageSnapshot,
        string settingsSignature,
        bool onTheFly,
        out string reason)
    {
        reason = string.Empty;
        var state = LoadStartupScanState();
        if (state.StateVersion != StartupScanStateVersion)
        {
            reason = "state-version";
            return false;
        }

        if (!string.Equals(state.ImporterVersion, ImporterVersion, StringComparison.Ordinal))
        {
            reason = "importer-version";
            return false;
        }

        if (state.OnTheFly != onTheFly)
        {
            reason = "on-the-fly-mode";
            return false;
        }

        if (!string.Equals(state.SettingsSignature, settingsSignature, StringComparison.Ordinal))
        {
            reason = "settings-signature";
            return false;
        }

        if (state.PackageCount != packageSnapshot.PackageCount ||
            !string.Equals(state.PackageFingerprint, packageSnapshot.Fingerprint, StringComparison.Ordinal))
        {
            reason = "package-fingerprint";
            return false;
        }

        if (!IsIndexStampUnchanged(state))
        {
            reason = "index-stamp";
            return false;
        }

        if (!AllIndexedPacksReusableForSnapshot(packageSnapshot, onTheFly, out reason))
        {
            return false;
        }

        reason = "clean";
        return true;
    }

    private bool AllIndexedPacksReusableForSnapshot(PackageDirectorySnapshot packageSnapshot, bool onTheFly, out string reason)
    {
        reason = string.Empty;
        foreach (var package in packageSnapshot.Packages)
        {
            var pack = _index.Packs.FirstOrDefault(candidate =>
                IsReusableInstalledPack(candidate, package.Path, onTheFly) &&
                candidate.Size == package.Size &&
                candidate.LastWriteTimeUtc == package.LastWriteTimeUtc);
            if (pack == null)
            {
                reason = "package-index-miss";
                return false;
            }

            var runtimeState = ResolveIndexedPackRuntimeState(pack, onTheFly);
            if (!runtimeState.Reusable)
            {
                reason = "runtime-" + runtimeState.Reason;
                return false;
            }
        }

        reason = "clean";
        return true;
    }

    private static bool IsIndexStampUnchanged(RomPackInstallerStartupScanState state)
    {
        if (!File.Exists(IndexPath))
        {
            return state.IndexByteLength == 0 && state.IndexWriteTicksUtc == 0;
        }

        var info = new FileInfo(IndexPath);
        return state.IndexByteLength == info.Length &&
            state.IndexWriteTicksUtc == info.LastWriteTimeUtc.Ticks;
    }

    private static RomPackInstallerStartupScanState LoadStartupScanState()
    {
        try
        {
            return File.Exists(RetroBatPaths.RomPackInstallerStartupStatePath)
                ? JsonSerializer.Deserialize<RomPackInstallerStartupScanState>(
                    File.ReadAllText(RetroBatPaths.RomPackInstallerStartupStatePath),
                    JsonOptions) ?? new RomPackInstallerStartupScanState()
                : new RomPackInstallerStartupScanState();
        }
        catch
        {
            return new RomPackInstallerStartupScanState();
        }
    }

    private static void SaveStartupScanState(PackageDirectorySnapshot packageSnapshot, string settingsSignature, bool onTheFly)
    {
        try
        {
            var state = new RomPackInstallerStartupScanState
            {
                StateVersion = StartupScanStateVersion,
                ImporterVersion = ImporterVersion,
                OnTheFly = onTheFly,
                SettingsSignature = settingsSignature,
                PackageFingerprint = packageSnapshot.Fingerprint,
                PackageCount = packageSnapshot.PackageCount,
                IndexWriteTicksUtc = File.Exists(IndexPath) ? File.GetLastWriteTimeUtc(IndexPath).Ticks : 0,
                IndexByteLength = File.Exists(IndexPath) ? new FileInfo(IndexPath).Length : 0,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            Directory.CreateDirectory(Path.GetDirectoryName(RetroBatPaths.RomPackInstallerStartupStatePath)!);
            File.WriteAllText(
                RetroBatPaths.RomPackInstallerStartupStatePath,
                JsonSerializer.Serialize(state, JsonOptions),
                Encoding.UTF8);
        }
        catch
        {
            // Startup scan cache must never block pack installation.
        }
    }

    private string BuildEffectiveStartupSettingsSignature(bool onTheFly)
    {
        var builder = new StringBuilder();
        builder.Append("installer=").Append(_runtimeOptions.IsRomPackInstallerEnabled()).AppendLine();
        builder.Append("onTheFly=").Append(onTheFly).AppendLine();
        builder.Append("unzip=").Append(_runtimeOptions.ShouldUnzipRomPackInstallerRoms()).AppendLine();
        builder.Append("esSettings=").Append(ComputeInstallerSettingsSignature(_lastInstallerSettings)).AppendLine();
        builder.Append("importer=").Append(ImporterVersion).AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private string BuildLegacyStartupSettingsSignature(bool onTheFly)
    {
        var legacySettings = _lastInstallerSettings
            .Where(pair => !string.Equals(pair.Key, "ParseGamelistOnly", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        builder.Append("installer=").Append(_runtimeOptions.IsRomPackInstallerEnabled()).AppendLine();
        builder.Append("onTheFly=").Append(onTheFly).AppendLine();
        builder.Append("unzip=").Append(_runtimeOptions.ShouldUnzipRomPackInstallerRoms()).AppendLine();
        builder.Append("esSettings=").Append(ComputeInstallerSettingsSignature(legacySettings, includeParseGamelistOnly: false)).AppendLine();
        builder.Append("importer=").Append(ImporterVersion).AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static PackageDirectorySnapshot BuildPackageDirectorySnapshot(string packageRoot)
    {
        if (!Directory.Exists(packageRoot))
        {
            return new PackageDirectorySnapshot(packageRoot, false, string.Empty, 0, Array.Empty<PackageFileSnapshot>());
        }

        var packages = Directory.EnumerateFiles(packageRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => PackExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new PackageFileSnapshot(path, info.Length, info.LastWriteTimeUtc);
            })
            .ToArray();

        var builder = new StringBuilder();
        foreach (var package in packages)
        {
            builder.Append(Path.GetFileName(package.Path))
                .Append('|')
                .Append(package.Size)
                .Append('|')
                .Append(package.LastWriteTimeUtc.Ticks)
                .AppendLine();
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        return new PackageDirectorySnapshot(packageRoot, true, fingerprint, packages.Length, packages);
    }

    private static string ComputeInstallerSettingsSignature(
        IReadOnlyDictionary<string, string> values,
        bool includeParseGamelistOnly = true)
    {
        var settingNames = includeParseGamelistOnly
            ? InstallerSettingNames
            : InstallerSettingNames.Where(name =>
                !string.Equals(name, "ParseGamelistOnly", StringComparison.OrdinalIgnoreCase));
        var joined = string.Join(
            "\n",
            settingNames.Select(key => key + "=" + NormalizeInstallerSettingValue(values.GetValueOrDefault(key))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private Dictionary<string, string>? ReadInstallerSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _settingsStore.ReadAllSettings())
        {
            if (IsInstallerSetting(pair.Key))
            {
                settings[pair.Key] = pair.Value.Trim();
            }
        }

        return settings;
    }

    private static readonly string[] InstallerSettingNames =
    [
        "global.apiexpose.romset.pack_installer.enabled",
        "global.apiexpose.romset.pack_installer.unzip_roms",
        "global.apiexpose.romset.pack_installer.on_the_fly.enabled",
        "global.apiexpose.romset.pack_installer.on_the_fly.trigger",
        "ParseGamelistOnly"
    ];

    private static bool IsInstallerSetting(string name)
    {
        return InstallerSettingNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSettingEnabled(IReadOnlyDictionary<string, string> values, string key)
    {
        return string.Equals(NormalizeInstallerSettingValue(values.GetValueOrDefault(key)), "1", StringComparison.Ordinal);
    }

    private static bool ShouldScanPackagesAfterInstallerSettingsChange(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current)
    {
        var previousPackInstaller = IsSettingEnabled(previous, "global.apiexpose.romset.pack_installer.enabled");
        var currentPackInstaller = IsSettingEnabled(current, "global.apiexpose.romset.pack_installer.enabled");
        var previousOnTheFly = IsOnTheFlySettingEnabled(previous);
        var currentOnTheFly = IsOnTheFlySettingEnabled(current);
        var previousUnzipRoms = IsSettingEnabled(previous, "global.apiexpose.romset.pack_installer.unzip_roms");
        var currentUnzipRoms = IsSettingEnabled(current, "global.apiexpose.romset.pack_installer.unzip_roms");

        if (!previousPackInstaller && currentPackInstaller)
        {
            return true;
        }

        if (!previousOnTheFly && currentOnTheFly)
        {
            return true;
        }

        if ((currentPackInstaller || currentOnTheFly) && previousUnzipRoms != currentUnzipRoms)
        {
            return true;
        }

        return false;
    }

    private static string NormalizeInstallerSettingValue(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "yes" or "on" ? "1" : "0";
    }

    private static bool IsOnTheFlySettingEnabled(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("global.apiexpose.romset.pack_installer.on_the_fly.trigger", out var trigger) &&
            !string.IsNullOrWhiteSpace(trigger))
        {
            return !string.Equals(NormalizeOnTheFlyTrigger(trigger), "never", StringComparison.OrdinalIgnoreCase);
        }

        return IsSettingEnabled(values, "global.apiexpose.romset.pack_installer.on_the_fly.enabled");
    }

    private static string NormalizeOnTheFlyTrigger(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "never" or "disabled" or "off" or "none" or "0" => "never",
            "game_selected" or "selected" => "game_selected",
            _ => "game_start"
        };
    }

    private void SaveIndex()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(_index, JsonOptions));
    }

    private static bool TryGetKnownPackageHash(string packagePath, FileInfo fileInfo, out string hash)
    {
        hash = string.Empty;
        var cache = LoadPackageHashCache();
        var entry = cache.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase) &&
            candidate.Size == fileInfo.Length &&
            candidate.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc &&
            !string.IsNullOrWhiteSpace(candidate.Sha256));
        if (entry != null)
        {
            hash = entry.Sha256;
            return true;
        }

        if (!TryRecoverPackageHashFromInstallerLog(packagePath, fileInfo, out hash))
        {
            return false;
        }

        SaveKnownPackageHash(packagePath, fileInfo, hash);
        return true;
    }

    private static PackageHashCache LoadPackageHashCache()
    {
        try
        {
            return File.Exists(PackageHashCachePath)
                ? JsonSerializer.Deserialize<PackageHashCache>(File.ReadAllText(PackageHashCachePath), JsonOptions) ?? new PackageHashCache()
                : new PackageHashCache();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PackageHashCache();
        }
    }

    private static void SaveKnownPackageHash(string packagePath, FileInfo fileInfo, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        var cache = LoadPackageHashCache();
        cache.Entries.RemoveAll(entry => string.Equals(entry.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase));
        cache.Entries.Add(new PackageHashCacheEntry
        {
            PackagePath = packagePath,
            Size = fileInfo.Length,
            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
            Sha256 = hash,
            UpdatedAtUtc = DateTime.UtcNow
        });
        cache.UpdatedAtUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(PackageHashCachePath)!);
        File.WriteAllText(PackageHashCachePath, JsonSerializer.Serialize(cache, JsonOptions));
    }

    private static bool TryRecoverPackageHashFromInstallerLog(string packagePath, FileInfo fileInfo, out string hash)
    {
        hash = string.Empty;
        var logPath = Path.Combine(RetroBatPaths.PluginRoot, ".log", "package-installer", "rom-pack-installer.jsonl");
        if (!File.Exists(logPath))
        {
            return false;
        }

        var currentFingerprintSeen = false;
        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("status", out var statusProperty) ||
                    !root.TryGetProperty("details", out var details))
                {
                    continue;
                }

                var status = statusProperty.GetString() ?? string.Empty;
                if (string.Equals(status, "package-check", StringComparison.OrdinalIgnoreCase) &&
                    TryReadJsonString(details, "packagePath", out var checkedPath) &&
                    string.Equals(checkedPath, packagePath, StringComparison.OrdinalIgnoreCase) &&
                    TryReadJsonInt64(details, "Length", out var length) &&
                    length == fileInfo.Length &&
                    TryReadJsonDateTime(details, "LastWriteTimeUtc", out var lastWriteTimeUtc) &&
                    lastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
                {
                    currentFingerprintSeen = true;
                    continue;
                }

                if (currentFingerprintSeen &&
                    string.Equals(status, "package-install-start", StringComparison.OrdinalIgnoreCase) &&
                    TryReadJsonString(details, "packagePath", out var installPath) &&
                    string.Equals(installPath, packagePath, StringComparison.OrdinalIgnoreCase) &&
                    TryReadJsonString(details, "hash", out var recoveredHash) &&
                    !string.IsNullOrWhiteSpace(recoveredHash))
                {
                    hash = recoveredHash;
                }
            }
            catch (JsonException)
            {
                // Ignore partial diagnostic lines.
            }
        }

        return !string.IsNullOrWhiteSpace(hash);
    }

    private static bool TryReadJsonString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadJsonInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out value);
    }

    private static bool TryReadJsonDateTime(JsonElement element, string propertyName, out DateTime value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDateTime(out value);
    }

    private static async Task AppendInstallerLogAsync(
        string status,
        object details,
        CancellationToken cancellationToken)
    {
        try
        {
            var logPath = Path.Combine(RetroBatPaths.PluginRoot, ".log", "package-installer", "rom-pack-installer.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var payload = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                status,
                details
            }, LogJsonOptions);
            await File.AppendAllTextAsync(logPath, payload + Environment.NewLine, Encoding.UTF8, cancellationToken);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Diagnostics must never block pack preparation.
        }
    }

    private async Task NotifyAsync(string message, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("{Message}", message);
        await _notifications.NotifyAsync(message, cancellationToken);
    }

    private async Task MessageBoxAsync(string message, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("{Message}", message);
        await _notifications.MessageBoxAsync(message, cancellationToken);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeRomMd5(string path)
    {
        try
        {
            // Le md5 doit etre celui du CONTENU de la rom, pas de l'archive :
            // c'est lui qui matche les referentiels (No-Intro, RetroAchievements,
            // base consolidee resources/gamelist). Un .zip mono-rom est donc
            // hashe sur son entree decompressee ; multi-entrees ou non-zip :
            // le fichier tel quel.
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(path);
                    var entries = archive.Entries.Where(entry => entry.Length > 0).ToList();
                    if (entries.Count == 1)
                    {
                        using var entryStream = entries[0].Open();
                        return Convert.ToHexString(MD5.HashData(entryStream)).ToLowerInvariant();
                    }
                }
                catch (InvalidDataException)
                {
                    // Pas une vraie archive zip : hash du fichier brut.
                }
            }

            using var stream = File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ComputeCrc32(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var crc = 0xFFFFFFFFu;
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    crc = (crc >> 8) ^ Crc32Table[(crc ^ buffer[i]) & 0xFF];
                }
            }

            crc ^= 0xFFFFFFFFu;
            return crc.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    private static string NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var c in value)
        {
            if (Uri.IsHexDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
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

    private static string PackageRoot => Path.Combine(RetroBatPaths.PluginRoot, "package-installer");
    private static string TempRoot => Path.Combine(RetroBatPaths.PluginRoot, ".temp", "package-installer");
    private static string IndexPath => RetroBatPaths.RomPackInstallerIndexPath;
    private static string PackageHashCachePath => RetroBatPaths.RomPackInstallerHashCachePath;
}

public sealed class RomPackInstallerIndex
{
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<RomPackIndexEntry> Packs { get; set; } = new();
}

public sealed class RomPackInstallerStartupScanState
{
    public int StateVersion { get; set; }
    public string ImporterVersion { get; set; } = string.Empty;
    public bool OnTheFly { get; set; }
    public string SettingsSignature { get; set; } = string.Empty;
    public string PackageFingerprint { get; set; } = string.Empty;
    public int PackageCount { get; set; }
    public long IndexWriteTicksUtc { get; set; }
    public long IndexByteLength { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed record PackageDirectorySnapshot(
    string Root,
    bool Exists,
    string Fingerprint,
    int PackageCount,
    IReadOnlyList<PackageFileSnapshot> Packages);

internal sealed record PackageFileSnapshot(string Path, long Size, DateTime LastWriteTimeUtc);

public sealed class RomPackIndexEntry
{
    public string PackagePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTime InstalledAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ImporterVersion { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public bool OnTheFlyMode { get; set; }
    public bool UnzipRoms { get; set; }
    public int MediaFilesImportedCount { get; set; }
    public List<RomPackRomEntry> Roms { get; set; } = new();
}

public sealed class RomPackRomEntry
{
    public string SystemId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string DestinationFileName { get; set; } = string.Empty;
    public string PackRelativePath { get; set; } = string.Empty;
    public string ArchiveEntryPath { get; set; } = string.Empty;
    public string Md5 { get; set; } = string.Empty;
    public string Crc32 { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class PackageHashCache
{
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<PackageHashCacheEntry> Entries { get; set; } = new();
}

public sealed class PackageHashCacheEntry
{
    public string PackagePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record ArchiveEntryInfo(
    string Path,
    long Size,
    long? LocalHeaderOffset = null,
    long? CompressedSize = null,
    int? CompressionMethod = null,
    long? DataOffset = null);

internal sealed record ArchiveSystemContent(string ContentPrefix, string SystemId);

internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public LimitedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _remaining = Math.Max(0, length);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _remaining;
    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
        _remaining -= read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}

public sealed class OnTheFlyRomInstallResult
{
    public string Status { get; init; } = "skipped";
    public string Reason { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string PackagePath { get; init; } = string.Empty;
    public string GameName { get; init; } = string.Empty;
    public bool Installed { get; init; }
    public bool AlreadyAvailable { get; init; }
    public int TimeoutSeconds { get; init; }

    public static OnTheFlyRomInstallResult Skipped(string reason, string targetPath = "")
    {
        return new OnTheFlyRomInstallResult
        {
            Status = "skipped",
            Reason = reason,
            TargetPath = targetPath,
            AlreadyAvailable = string.Equals(reason, "already-installed", StringComparison.OrdinalIgnoreCase)
        };
    }

    public static OnTheFlyRomInstallResult Success(string targetPath, string packagePath, string gameName)
    {
        return new OnTheFlyRomInstallResult
        {
            Status = "installed",
            TargetPath = targetPath,
            PackagePath = packagePath,
            GameName = gameName,
            Installed = true
        };
    }

    public static OnTheFlyRomInstallResult TimedOut(
        string targetPath,
        string packagePath,
        string gameName,
        int timeoutSeconds)
    {
        return new OnTheFlyRomInstallResult
        {
            Status = "timeout",
            Reason = "extraction-timeout",
            TargetPath = targetPath,
            PackagePath = packagePath,
            GameName = gameName,
            TimeoutSeconds = timeoutSeconds
        };
    }
}

internal sealed record IndexedPackRuntimeState(
    bool Reusable,
    string Reason,
    string GamelistPath,
    string MediaRoot);
