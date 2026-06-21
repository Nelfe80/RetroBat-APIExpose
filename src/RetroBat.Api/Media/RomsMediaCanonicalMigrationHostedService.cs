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

namespace RetroBat.Api.Media;

public sealed class RomsMediaCanonicalMigrationHostedService : IHostedService
{
    private static readonly JsonSerializerOptions LogJsonOptions = new() { WriteIndented = false };
    private static readonly string[] LegacyFolders = ["images", "videos", "manuals", "themehb", "themes"];
    private static readonly string[] SupportedExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".pdf", ".mp4", ".mkv", ".avi", ".webm", ".zip"
    ];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".webm"];
    private static readonly IReadOnlyDictionary<string, string> DisabledSettingsAfterRefusal =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["global.apiexpose.local_media_manager.enabled"] = "0",
            ["global.apiexpose.local_media_manager.populate_all_requested"] = "0",
            ["global.apiexpose.local_media_manager.remove_roms_media_after_canonical_migration"] = "0",
            ["global.apiexpose.scraping.auto_enabled"] = "0",
            ["global.apiexpose.scraping.screenscraper.enabled"] = "0",
            ["global.apiexpose.scraping.queue.enabled"] = "0",
            ["global.apiexpose.romset.pack_installer.enabled"] = "0",
            ["global.apiexpose.romset.pack_installer.unzip_roms"] = "0",
            ["global.apiexpose.romset.pack_installer.on_the_fly.enabled"] = "0",
            ["global.apiexpose.romset.pack_installer.on_the_fly.trigger"] = "never",
            ["global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end"] = "0",
            ["global.apiexpose.romset.pack_installer.on_the_fly.reset_after_game_end_delay_ms"] = "12000",
            ["global.apiexpose.collections_pack_manager.pack_installer.enabled"] = "0"
        };

    private static readonly (string Suffix, string Kind)[] SuffixKinds =
    [
        ("-video-normalized", MediaKinds.VideoNormalized),
        ("-screenmarqueesmall", MediaKinds.ScreenMarqueeSmall),
        ("-screenmarquee", MediaKinds.ScreenMarquee),
        ("-wheelcarbon", MediaKinds.WheelCarbon),
        ("-wheelsteel", MediaKinds.WheelSteel),
        ("-boxtexture", MediaKinds.BoxTexture),
        ("-steamgrid", MediaKinds.SteamGrid),
        ("-mixrbv1", MediaKinds.MixRbv1),
        ("-mixrbv2", MediaKinds.MixRbv2),
        ("-thumbnail", MediaKinds.Thumbnail),
        ("-screenshot", MediaKinds.Thumbnail),
        ("-titleshot", MediaKinds.Image),
        ("-boxside", MediaKinds.BoxSide),
        ("-figurine", MediaKinds.Figurine),
        ("-cartridge", MediaKinds.Cartridge),
        ("-support2d", MediaKinds.Cartridge),
        ("-supporttexture", MediaKinds.Label),
        ("-support-texture", MediaKinds.Label),
        ("-label", MediaKinds.Label),
        ("-themehb", MediaKinds.ThemeHb),
        ("-marquee", MediaKinds.Marquee),
        ("-boxback", MediaKinds.BoxBack),
        ("-boxfront", MediaKinds.BoxFront),
        ("-box2d", MediaKinds.BoxFront),
        ("-box3d", MediaKinds.Box3d),
        ("-box", MediaKinds.BoxFront),
        ("-fanart", MediaKinds.Fanart),
        ("-bezel", MediaKinds.Bezel),
        ("-image", MediaKinds.Image),
        ("-thumb", MediaKinds.Thumbnail),
        ("-logo", MediaKinds.Logo),
        ("-wheel", MediaKinds.Wheel),
        ("-flyer", MediaKinds.Flyer),
        ("-manual", MediaKinds.Manual),
        ("-magazine", MediaKinds.Magazine),
        ("-video", MediaKinds.Video),
        ("-map", MediaKinds.Map),
        ("-mix", MediaKinds.MixRbv2)
    ];

    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly EsProjectionService _projectionService;
    private readonly LocalGamelistUpdateService _localGamelistUpdateService;
    private readonly IStartupOverlayService _startupOverlayService;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly IEsSettingsStore _settingsStore;
    private readonly ILogger<RomsMediaCanonicalMigrationHostedService>? _logger;
    private IReadOnlyList<string>? _migrationWaitMessages;
    private CancellationTokenSource? _startupMigrationCts;
    private Task? _startupMigrationTask;

    public RomsMediaCanonicalMigrationHostedService(
        ApiExposeRuntimeOptionsService runtimeOptions,
        IOptionsMonitor<ApiExposeOptions> options,
        SystemIdNormalizer systemIdNormalizer,
        GameNameNormalizer gameNameNormalizer,
        EsProjectionService projectionService,
        LocalGamelistUpdateService localGamelistUpdateService,
        IStartupOverlayService startupOverlayService,
        IEmulationStationNotificationService notificationService,
        IEsSettingsStore settingsStore,
        ILogger<RomsMediaCanonicalMigrationHostedService>? logger = null)
    {
        _runtimeOptions = runtimeOptions;
        _options = options;
        _systemIdNormalizer = systemIdNormalizer;
        _gameNameNormalizer = gameNameNormalizer;
        _projectionService = projectionService;
        _localGamelistUpdateService = localGamelistUpdateService;
        _startupOverlayService = startupOverlayService;
        _notificationService = notificationService;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runtimeOptions.ShouldSkipInteractiveStartupWork())
        {
            _logger?.LogInformation("Legacy roms media migration skipped: APIExpose test mode disables interactive startup work.");
            return Task.CompletedTask;
        }

        _startupMigrationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startupMigrationTask = Task.Run(
            () => RunStartupMigrationWithLoggingAsync(_startupMigrationCts.Token),
            CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task RunStartupMigrationWithLoggingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunStartupMigrationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug("Legacy roms media migration startup cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Legacy roms media migration startup failed.");
        }
    }

    private async Task RunStartupMigrationAsync(CancellationToken cancellationToken)
    {
        if (!_runtimeOptions.IsLocalMediaManagerEnabled())
        {
            return;
        }

        if (!_runtimeOptions.ShouldRemoveRomsMediaAfterCanonicalMigration())
        {
            return;
        }

        var inventory = await ComputeLegacyInventorySnapshotAsync(cancellationToken);
        if (inventory.FileCount == 0)
        {
            await SaveLegacyInventorySnapshotAsync(inventory, cancellationToken);
            return;
        }

        if (await IsLegacyInventoryUnchangedAsync(inventory, cancellationToken))
        {
            _logger?.LogInformation(
                "Legacy roms media migration skipped: inventory unchanged. Files={FileCount}, Bytes={TotalBytes}, Fingerprint={Fingerprint}",
                inventory.FileCount,
                inventory.TotalBytes,
                inventory.Fingerprint);
            return;
        }

        if (!ShowMigrationConfirmation())
        {
            DisableImpactedFeaturesAfterRefusal();
            _logger?.LogInformation("Legacy roms media migration skipped by user. Impacted APIExpose ES settings were disabled.");
            return;
        }

        var result = await MigrateAsync(cancellationToken);
        await UpdateGamelistsAfterMigrationAsync(result, cancellationToken);
        if (!result.GamelistUpdateFailed)
        {
            var postMigrationInventory = await ComputeLegacyInventorySnapshotAsync(cancellationToken);
            await SaveLegacyInventorySnapshotAsync(postMigrationInventory, cancellationToken);
        }

        _logger?.LogInformation(
            "Legacy roms media migration completed. Scanned={Scanned}, Migrated={Migrated}, Moved={Moved}, UserPriorityMoved={UserPriorityMoved}, Deleted={Deleted}, ExistingSameHash={ExistingSameHash}, Skipped={Skipped}, BytesFreed={BytesFreed}, EmptyDirectoriesDeleted={EmptyDirectoriesDeleted}, GamelistsUpdated={GamelistsUpdated}, GamelistMediaTagsUpdated={GamelistMediaTagsUpdated}",
            result.Scanned,
            result.Migrated,
            result.Moved,
            result.UserPriorityMoved,
            result.Deleted,
            result.ExistingSameHash,
            result.Skipped,
            result.BytesFreed,
            result.EmptyDirectoriesDeleted,
            result.GamelistsUpdated,
            result.GamelistMediaTagsUpdated);
        await NotifySummaryAsync(result, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _startupMigrationCts?.Cancel();
        if (_startupMigrationTask is { IsCompleted: false } startupMigrationTask)
        {
            try
            {
                await startupMigrationTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("Legacy roms media migration did not stop within the shutdown timeout.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Expected when the startup migration observes the shutdown token.
            }
        }

        try
        {
            var inventory = await ComputeLegacyInventorySnapshotAsync(cancellationToken);
            await SaveLegacyInventorySnapshotAsync(inventory, cancellationToken);
            _logger?.LogInformation(
                "Legacy roms media migration shutdown inventory saved. Files={FileCount}, Bytes={TotalBytes}, Fingerprint={Fingerprint}",
                inventory.FileCount,
                inventory.TotalBytes,
                inventory.Fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Unable to save legacy roms media migration inventory at shutdown.");
        }
    }

    private async Task<LegacyInventorySnapshot> ComputeLegacyInventorySnapshotAsync(CancellationToken cancellationToken)
    {
        var entries = new List<LegacyInventoryEntry>();
        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return LegacyInventorySnapshot.Empty();
        }

        foreach (var systemDirectory in Directory.EnumerateDirectories(RetroBatPaths.RomsRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frontendSystemId = Path.GetFileName(systemDirectory);
            foreach (var folder in LegacyFolders)
            {
                var folderPath = Path.Combine(systemDirectory, folder);
                if (!Directory.Exists(folderPath))
                {
                    continue;
                }

                foreach (var sourcePath in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extension = Path.GetExtension(sourcePath);
                    if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(RetroBatPaths.RomsRoot, sourcePath)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    var length = GetFileLength(sourcePath);
                    var contentHash = await JsonMediaAliasStore.ComputeSha256Async(sourcePath, cancellationToken);
                    entries.Add(new LegacyInventoryEntry(frontendSystemId, folder, relativePath, length, contentHash));
                }
            }
        }

        var fingerprint = ComputeInventoryFingerprint(entries);
        return new LegacyInventorySnapshot(
            SchemaVersion: 1,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            FileCount: entries.Count,
            TotalBytes: entries.Sum(entry => entry.Length),
            Fingerprint: fingerprint);
    }

    private async Task<bool> IsLegacyInventoryUnchangedAsync(LegacyInventorySnapshot inventory, CancellationToken cancellationToken)
    {
        try
        {
            var path = GetLegacyInventorySnapshotPath();
            if (!File.Exists(path))
            {
                return false;
            }

            await using var stream = File.OpenRead(path);
            var previous = await JsonSerializer.DeserializeAsync<LegacyInventorySnapshot>(stream, LogJsonOptions, cancellationToken);
            return previous is not null &&
                previous.SchemaVersion == inventory.SchemaVersion &&
                previous.FileCount == inventory.FileCount &&
                previous.TotalBytes == inventory.TotalBytes &&
                string.Equals(previous.Fingerprint, inventory.Fingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Unable to read legacy roms media migration inventory snapshot.");
            return false;
        }
    }

    private static async Task SaveLegacyInventorySnapshotAsync(LegacyInventorySnapshot inventory, CancellationToken cancellationToken)
    {
        try
        {
            var path = GetLegacyInventorySnapshotPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, inventory, LogJsonOptions, cancellationToken);
        }
        catch
        {
            // The snapshot is an optimization only; migration correctness does not depend on it.
        }
    }

    private static string ComputeInventoryFingerprint(IEnumerable<LegacyInventoryEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(entry.RelativePath.ToLowerInvariant())
                .Append('|')
                .Append(entry.Length)
                .Append('|')
                .Append(entry.ContentSha256.ToUpperInvariant())
                .Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string GetLegacyInventorySnapshotPath()
    {
        return Path.Combine(RetroBatPaths.PluginRoot, "logs", "roms-media-canonical-migration.snapshot.json");
    }

    private bool ShowMigrationConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var result = DialogResult.No;
        var thread = new Thread(() =>
        {
            result = MessageBox.Show(
                BuildMigrationWarningMessage(),
                "APIExpose - Canonical media migration",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2,
                MessageBoxOptions.DefaultDesktopOnly);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return result == DialogResult.Yes;
    }

    private static string BuildMigrationWarningMessage()
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            "FR - APIExpose va migrer les anciens medias presents dans roms/<system>/images, videos, manuals, themehb et themes vers media/user en tant qu'originaux utilisateur prioritaires. Les gamelist pointeront ensuite vers media/. Les fichiers deja presents sont compares par hash SHA-256 : les doublons identiques sont supprimes de roms, les variantes utilisateur existantes sont preservees en previous-user-*. Choisissez NON pour ne rien migrer maintenant ; APIExpose desactivera aussi le Local Media Manager, l'auto scraping et les installateurs de packs impactes.",
            "EN - APIExpose will migrate legacy media found in roms/<system>/images, videos, manuals, themehb and themes to media/user as prioritized user originals. Gamelists will then point to media/. Existing files are checked with SHA-256 hashes: identical duplicates are removed from roms, existing user variants are preserved as previous-user-*. Choose NO to skip migration now; APIExpose will also disable the impacted Local Media Manager, auto scraping and pack installer features.",
            "ES - APIExpose migrara los medios antiguos encontrados en roms/<system>/images, videos, manuals, themehb y themes a media/user como originales de usuario prioritarios. Las gamelist apuntaran despues a media/. Los archivos existentes se comparan con hash SHA-256: los duplicados identicos se eliminan de roms, las variantes de usuario existentes se conservan como previous-user-*. Elija NO para omitir la migracion ahora; APIExpose tambien desactivara el Local Media Manager, el auto scraping y los instaladores de packs afectados.");
    }

    private void DisableImpactedFeaturesAfterRefusal()
    {
        try
        {
            WriteEsSettings(DisabledSettingsAfterRefusal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Unable to disable impacted APIExpose ES settings after migration refusal.");
        }
    }

    private void WriteEsSettings(IReadOnlyDictionary<string, string> settings)
    {
        _settingsStore.Update(document =>
        {
            var root = document.Root ?? throw new InvalidOperationException("es_settings.cfg root is missing.");
            var changed = false;
            foreach (var (key, value) in settings)
            {
                changed |= SetStringSetting(root, key, value);
            }

            if (changed)
            {
                root.Add(new XText(Environment.NewLine));
            }

            return changed;
        });
    }

    private static bool SetStringSetting(XElement root, string key, string value)
    {
        var existing = root.Elements()
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var current = existing.Attribute("value")?.Value ?? string.Empty;
            if (string.Equals(current, value, StringComparison.Ordinal))
            {
                return false;
            }

            existing.SetAttributeValue("value", value);
            return true;
        }

        root.Add(new XText(Environment.NewLine + "  "));
        root.Add(new XElement("string", new XAttribute("name", key), new XAttribute("value", value)));
        return true;
    }

    private async Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        var result = new MigrationResult();
        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            return result;
        }

        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var systemPlans = Directory.EnumerateDirectories(RetroBatPaths.RomsRoot, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(BuildSystemMigrationPlan)
            .Where(plan => !string.IsNullOrWhiteSpace(plan.CanonicalSystemId))
            .ToList();
        var totalSystems = Math.Max(1, systemPlans.Count);
        var totalFiles = Math.Max(1, systemPlans.Sum(plan => plan.Files.Count));
        var processedSystems = 0;
        var processedFiles = 0;
        UpdateMigrationProgress(0, totalFiles, "scan", 0, totalSystems, startedAtUtc, stopwatch);

        foreach (var plan in systemPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateMigrationProgress(processedFiles, totalFiles, plan.FrontendSystemId, processedSystems + 1, totalSystems, startedAtUtc, stopwatch);
            var totalSystemFiles = Math.Max(1, plan.Files.Count);
            var processedSystemFiles = 0;
            if (plan.Files.Count > 0)
            {
                result.SystemsWithLegacyMedia.Add(plan.FrontendSystemId);
                UpdateMigrationProgress(processedFiles, totalFiles, plan.FrontendSystemId, processedSystemFiles, totalSystemFiles, startedAtUtc, stopwatch);
            }

            foreach (var entry in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Scanned++;

                if (!TryBuildCanonicalMigrationTarget(plan.CanonicalSystemId, entry.FolderName, entry.SourcePath, out var gameSlug, out var kind, out var systemTargetPath, out var userTargetPath, out var skipReason))
                {
                    result.Skipped++;
                    await WriteAuditAsync("skipped-unrecognized", entry.SourcePath, string.Empty, plan.FrontendSystemId, plan.CanonicalSystemId, string.Empty, string.Empty, new { reason = skipReason }, cancellationToken);
                }
                else
                {
                    await MigrateFileAsync(entry.SourcePath, systemTargetPath, userTargetPath, plan.FrontendSystemId, plan.CanonicalSystemId, gameSlug, kind, result, cancellationToken);
                }

                processedSystemFiles++;
                processedFiles++;
                if (processedFiles == 1 || processedFiles % 10 == 0 || processedSystemFiles == totalSystemFiles || processedFiles == totalFiles)
                {
                    UpdateMigrationProgress(processedFiles, totalFiles, plan.FrontendSystemId, processedSystemFiles, totalSystemFiles, startedAtUtc, stopwatch);
                }
            }

            foreach (var folder in LegacyFolders)
            {
                TryDeleteEmptyDirectory(Path.Combine(plan.SystemDirectory, folder), result);
            }

            processedSystems++;
            UpdateMigrationProgress(processedFiles, totalFiles, plan.FrontendSystemId, processedSystems, totalSystems, startedAtUtc, stopwatch);
        }

        return result;
    }

    private async Task UpdateGamelistsAfterMigrationAsync(MigrationResult result, CancellationToken cancellationToken)
    {
        if (result.Scanned == 0)
        {
            return;
        }

        try
        {
            _startupOverlayService.UpdateStartupProgress(
                "startup_roms_media_migration",
                0,
                1,
                "gamelist canonical links");

            var systems = result.SystemsWithLegacyMedia
                .OrderBy(system => system, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var system in systems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var update = await _localGamelistUpdateService.UpdateAsync(
                    new RetroBat.Api.Controllers.LocalGamelistUpdateRequest
                    {
                        Scope = "system",
                        SystemId = system
                    },
                    cancellationToken);

                result.GamelistsProcessed += update.SystemsProcessed;
                result.GamelistsUpdated += update.SystemsUpdated;
                result.GamelistGamesUpdated += update.GamesUpdated;
                result.GamelistMediaTagsUpdated += update.MediaTagsUpdated;
                result.GamelistSystemsFailed += update.SystemsFailed;
            }

            await WriteAuditAsync(
                "gamelists-updated",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new
                {
                    systems,
                    result.GamelistsProcessed,
                    result.GamelistsUpdated,
                    result.GamelistSystemsFailed,
                    result.GamelistGamesUpdated,
                    result.GamelistMediaTagsUpdated
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result.GamelistUpdateFailed = true;
            await WriteAuditAsync(
                "gamelists-update-failed",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new { error = ex.Message },
                cancellationToken);
            _logger?.LogWarning(ex, "Unable to rewrite gamelists after canonical media migration.");
        }
    }

    private SystemMigrationPlan BuildSystemMigrationPlan(string systemDirectory)
    {
        var frontendSystemId = Path.GetFileName(systemDirectory);
        var canonicalSystemId = _systemIdNormalizer.Normalize(frontendSystemId);
        var files = string.IsNullOrWhiteSpace(canonicalSystemId)
            ? new List<LegacyMediaFile>()
            : EnumerateLegacySystemFiles(systemDirectory).ToList();
        return new SystemMigrationPlan(systemDirectory, frontendSystemId, canonicalSystemId, files);
    }

    private void UpdateMigrationProgress(
        int processedFiles,
        int totalFiles,
        string frontendSystemId,
        int systemCurrent,
        int systemTotal,
        DateTime startedAtUtc,
        Stopwatch stopwatch)
    {
        var eta = FormatEstimatedRemaining(processedFiles, totalFiles, startedAtUtc);
        var elapsed = FormatDuration(stopwatch.Elapsed);
        var tip = ResolveMigrationWaitMessage(processedFiles);
        var detail = $"{frontendSystemId} {systemCurrent}/{Math.Max(1, systemTotal)} - ETA {eta} - elapsed {elapsed}";
        if (!string.IsNullOrWhiteSpace(tip))
        {
            detail += Environment.NewLine + tip;
        }

        _startupOverlayService.UpdateStartupProgress(
            "startup_roms_media_migration",
            Math.Max(0, processedFiles),
            Math.Max(1, totalFiles),
            detail);
    }

    private string ResolveMigrationWaitMessage(int processedFiles)
    {
        var messages = _migrationWaitMessages ??= LoadMigrationWaitMessages();
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        var index = Math.Abs(processedFiles / 25) % messages.Count;
        return messages[index];
    }

    private static IReadOnlyList<string> LoadMigrationWaitMessages()
    {
        var path = Path.Combine(RetroBatPaths.PluginRoot, "resources", "startup-overlay", "splashscreen_wait_messages.json");
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            var language = ResolveLanguage();
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("items", out var itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var messages = itemsElement
                .EnumerateArray()
                .Where(item =>
                    item.TryGetProperty("lang", out var langElement) &&
                    string.Equals(langElement.GetString()?.Trim(), language, StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("text", out var textElement) &&
                    !string.IsNullOrWhiteSpace(textElement.GetString()))
                .Select(item => item.GetProperty("text").GetString()!.Trim())
                .ToList();

            return messages.Count == 0 && !string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase)
                ? LoadMigrationWaitMessagesForLanguage(itemsElement, "fr")
                : messages;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> LoadMigrationWaitMessagesForLanguage(JsonElement itemsElement, string language)
    {
        return itemsElement
            .EnumerateArray()
            .Where(item =>
                item.TryGetProperty("lang", out var langElement) &&
                string.Equals(langElement.GetString()?.Trim(), language, StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("text", out var textElement) &&
                !string.IsNullOrWhiteSpace(textElement.GetString()))
            .Select(item => item.GetProperty("text").GetString()!.Trim())
            .ToList();
    }

    private static string ResolveLanguage()
    {
        try
        {
            var settingsService = new RetroBat.Domain.Services.EmulationStationSettingsService();
            var scrapingSettings = settingsService.GetScrapingSettings();
            if (!string.IsNullOrWhiteSpace(scrapingSettings.Language))
            {
                return scrapingSettings.Language.Split('_', '-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "fr";
            }
        }
        catch
        {
            // Wait messages are cosmetic only.
        }

        return "fr";
    }

    private static string FormatEstimatedRemaining(int processedFiles, int totalFiles, DateTime startedAtUtc)
    {
        if (processedFiles <= 0 || totalFiles <= 0)
        {
            return "--:--";
        }

        var elapsed = DateTime.UtcNow - startedAtUtc;
        if (elapsed.TotalSeconds < 1d)
        {
            return "--:--";
        }

        var remainingFiles = Math.Max(0, totalFiles - processedFiles);
        var secondsPerFile = elapsed.TotalSeconds / Math.Max(1, processedFiles);
        return FormatDuration(TimeSpan.FromSeconds(remainingFiles * secondsPerFile));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1d)
        {
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static IEnumerable<LegacyMediaFile> EnumerateLegacySystemFiles(string systemDirectory)
    {
        foreach (var folder in LegacyFolders)
        {
            var folderPath = Path.Combine(systemDirectory, folder);
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            foreach (var sourcePath in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                yield return new LegacyMediaFile(folder, sourcePath);
            }
        }
    }

    private bool TryBuildCanonicalMigrationTarget(
        string canonicalSystemId,
        string legacyFolder,
        string sourcePath,
        out string gameSlug,
        out string kind,
        out string systemTargetPath,
        out string userTargetPath,
        out string skipReason)
    {
        gameSlug = string.Empty;
        kind = string.Empty;
        systemTargetPath = string.Empty;
        userTargetPath = string.Empty;
        skipReason = string.Empty;

        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            skipReason = "unsupported-extension";
            return false;
        }

        var fileStem = Path.GetFileNameWithoutExtension(sourcePath);
        if (!TryParseProjectedMediaName(legacyFolder, fileStem, extension, out var projectionBaseName, out kind))
        {
            skipReason = "unrecognized-name";
            return false;
        }

        gameSlug = _gameNameNormalizer.NormalizeGameSlug(projectionBaseName, null);
        if (string.IsNullOrWhiteSpace(gameSlug))
        {
            skipReason = "empty-game-slug";
            return false;
        }

        systemTargetPath = _projectionService.GetCanonicalImportPath(canonicalSystemId, gameSlug, kind, sourcePath);
        if (string.IsNullOrWhiteSpace(systemTargetPath))
        {
            skipReason = "empty-target-path";
            return false;
        }

        if (!IsUnderRoot(systemTargetPath, RetroBatPaths.MediaSystemsRoot))
        {
            skipReason = "target-outside-media-root";
            return false;
        }

        userTargetPath = ToUserCanonicalPath(systemTargetPath);
        if (!IsUnderRoot(userTargetPath, RetroBatPaths.MediaUserSystemsRoot))
        {
            skipReason = "user-target-outside-media-root";
            return false;
        }

        return true;
    }

    private static string ToUserCanonicalPath(string systemTargetPath)
    {
        var relative = Path.GetRelativePath(RetroBatPaths.MediaSystemsRoot, systemTargetPath);
        return Path.Combine(RetroBatPaths.MediaUserSystemsRoot, relative);
    }

    private static bool TryParseProjectedMediaName(string legacyFolder, string fileStem, string extension, out string projectionBaseName, out string kind)
    {
        projectionBaseName = string.Empty;
        kind = string.Empty;

        foreach (var (suffix, candidateKind) in SuffixKinds)
        {
            if (!fileStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsKindAllowedForFolder(candidateKind, legacyFolder))
            {
                return false;
            }

            projectionBaseName = fileStem[..^suffix.Length];
            kind = candidateKind;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        if (legacyFolder.Equals("themehb", StringComparison.OrdinalIgnoreCase) ||
            legacyFolder.Equals("themes", StringComparison.OrdinalIgnoreCase))
        {
            projectionBaseName = fileStem;
            kind = MediaKinds.ThemeHb;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        if (TryResolveDefaultKind(legacyFolder, extension, out kind))
        {
            projectionBaseName = fileStem;
            return !string.IsNullOrWhiteSpace(projectionBaseName);
        }

        return false;
    }

    private static bool TryResolveDefaultKind(string legacyFolder, string extension, out string kind)
    {
        kind = string.Empty;
        var normalizedFolder = legacyFolder.ToLowerInvariant();
        if (normalizedFolder is "videos" && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Video;
            return true;
        }

        if (normalizedFolder is "manuals" && extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Manual;
            return true;
        }

        if (normalizedFolder is "images" && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            kind = MediaKinds.Thumbnail;
            return true;
        }

        return false;
    }

    private static bool IsKindAllowedForFolder(string kind, string legacyFolder)
    {
        return legacyFolder.ToLowerInvariant() switch
        {
            "images" => kind is not MediaKinds.Video and not MediaKinds.VideoNormalized and not MediaKinds.Manual and not MediaKinds.ThemeHb,
            "videos" => kind is MediaKinds.Video or MediaKinds.VideoNormalized,
            "manuals" => kind is MediaKinds.Manual,
            "themehb" or "themes" => kind is MediaKinds.ThemeHb,
            _ => false
        };
    }

    private async Task MigrateFileAsync(
        string sourcePath,
        string systemTargetPath,
        string userTargetPath,
        string frontendSystemId,
        string canonicalSystemId,
        string gameSlug,
        string kind,
        MigrationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userTargetPath)!);

            if (File.Exists(userTargetPath))
            {
                var sourceHash = await JsonMediaAliasStore.ComputeSha256Async(sourcePath, cancellationToken);
                var userHash = await JsonMediaAliasStore.ComputeSha256Async(userTargetPath, cancellationToken);
                if (string.Equals(sourceHash, userHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.ExistingSameHash++;
                    var duplicateBytes = GetFileLength(sourcePath);
                    File.Delete(sourcePath);
                    result.Deleted++;
                    result.BytesFreed += duplicateBytes;

                    await WriteAuditAsync("existing-user-same-hash", sourcePath, userTargetPath, frontendSystemId, canonicalSystemId, gameSlug, kind, null, cancellationToken);
                    return;
                }

                var previousUserPath = BuildUniqueSiblingPath(userTargetPath, ".previous-user");
                File.Move(userTargetPath, previousUserPath);
                var sourceBytes = GetFileLength(sourcePath);
                File.Move(sourcePath, userTargetPath);
                result.Moved++;
                result.BytesFreed += sourceBytes;
                result.Migrated++;
                result.UserPriorityMoved++;

                await WriteAuditAsync(
                    "migrated-user-priority-replaced-existing-user",
                    sourcePath,
                    userTargetPath,
                    frontendSystemId,
                    canonicalSystemId,
                    gameSlug,
                    kind,
                    new { previousUserPath, sourceHash, userHash },
                    cancellationToken);
                return;
            }

            if (File.Exists(systemTargetPath))
            {
                var sourceHash = await JsonMediaAliasStore.ComputeSha256Async(sourcePath, cancellationToken);
                var targetHash = await JsonMediaAliasStore.ComputeSha256Async(systemTargetPath, cancellationToken);
                if (string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.ExistingSameHash++;
                    var duplicateBytes = GetFileLength(sourcePath);
                    File.Delete(sourcePath);
                    result.Deleted++;
                    result.BytesFreed += duplicateBytes;

                    await WriteAuditAsync("existing-same-hash", sourcePath, systemTargetPath, frontendSystemId, canonicalSystemId, gameSlug, kind, null, cancellationToken);
                    return;
                }
            }

            var movedBytes = GetFileLength(sourcePath);
            File.Move(sourcePath, userTargetPath);
            result.Moved++;
            result.BytesFreed += movedBytes;
            result.Migrated++;
            result.UserPriorityMoved++;

            await WriteAuditAsync("migrated-user-priority", sourcePath, userTargetPath, frontendSystemId, canonicalSystemId, gameSlug, kind, new { systemTargetPath }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Skipped++;
            await WriteAuditAsync("failed", sourcePath, userTargetPath, frontendSystemId, canonicalSystemId, gameSlug, kind, new { systemTargetPath, error = ex.Message }, cancellationToken);
            _logger?.LogWarning(ex, "Legacy roms media migration failed for {SourcePath}", sourcePath);
        }
    }

    private static string BuildUniqueSiblingPath(string path, string infix)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var candidate = Path.Combine(directory, $"{fileName}{infix}-{timestamp}{extension}");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{fileName}{infix}-{timestamp}-{suffix}{extension}");
            suffix++;
        }

        return candidate;
    }

    private async Task NotifySummaryAsync(MigrationResult result, CancellationToken cancellationToken)
    {
        if (result.Scanned == 0)
        {
            return;
        }

        var freed = FormatBytes(result.BytesFreed);
        var gamelistLine = result.GamelistUpdateFailed
            ? "Gamelists : erreur de mise a jour, voir logs"
            : $"Gamelists mises a jour : {result.GamelistsUpdated}/{result.GamelistsProcessed} ({result.GamelistMediaTagsUpdated} tags medias)";
        var message = $"Migration medias terminee\nEspace libere : {freed}\nFichiers deplaces : {result.Moved}\nDont originaux utilisateur : {result.UserPriorityMoved}\nDoublons supprimes : {result.Deleted}\nDossiers vides supprimes : {result.EmptyDirectoriesDeleted}\n{gamelistLine}";
        await _notificationService.MessageBoxAsync(message, cancellationToken);
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }

    private static async Task WriteAuditAsync(
        string status,
        string sourcePath,
        string targetPath,
        string frontendSystemId,
        string canonicalSystemId,
        string gameSlug,
        string kind,
        object? details,
        CancellationToken cancellationToken)
    {
        try
        {
            var logPath = Path.Combine(RetroBatPaths.PluginRoot, "logs", "roms-media-canonical-migration.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                status,
                sourcePath,
                targetPath,
                frontendSystemId,
                canonicalSystemId,
                gameSlug,
                kind,
                details
            }, LogJsonOptions);
            await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken);
        }
        catch
        {
            // Audit is diagnostic only.
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteEmptyDirectory(string directoryPath, MigrationResult result)
    {
        try
        {
            if (!Directory.Exists(directoryPath) ||
                Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                return;
            }

            Directory.Delete(directoryPath);
            result.EmptyDirectoriesDeleted++;
        }
        catch
        {
            // Empty directory cleanup is opportunistic only.
        }
    }

    private sealed class MigrationResult
    {
        public int Scanned { get; set; }
        public int Migrated { get; set; }
        public int Moved { get; set; }
        public int Deleted { get; set; }
        public int ExistingSameHash { get; set; }
        public int Conflicts { get; set; }
        public int UserPriorityMoved { get; set; }
        public int Skipped { get; set; }
        public long BytesFreed { get; set; }
        public int EmptyDirectoriesDeleted { get; set; }
        public int GamelistsProcessed { get; set; }
        public int GamelistsUpdated { get; set; }
        public int GamelistSystemsFailed { get; set; }
        public int GamelistGamesUpdated { get; set; }
        public int GamelistMediaTagsUpdated { get; set; }
        public bool GamelistUpdateFailed { get; set; }
        public HashSet<string> SystemsWithLegacyMedia { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LegacyMediaFile(string FolderName, string SourcePath);
    private sealed record LegacyInventorySnapshot(
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        int FileCount,
        long TotalBytes,
        string Fingerprint)
    {
        public static LegacyInventorySnapshot Empty()
        {
            return new LegacyInventorySnapshot(1, DateTimeOffset.UtcNow, 0, 0, ComputeInventoryFingerprint(Array.Empty<LegacyInventoryEntry>()));
        }
    }

    private sealed record LegacyInventoryEntry(
        string FrontendSystemId,
        string FolderName,
        string RelativePath,
        long Length,
        string ContentSha256);

    private sealed record SystemMigrationPlan(
        string SystemDirectory,
        string FrontendSystemId,
        string CanonicalSystemId,
        List<LegacyMediaFile> Files);
}
