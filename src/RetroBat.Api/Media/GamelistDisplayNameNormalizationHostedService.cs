using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class GamelistDisplayNameNormalizationHostedService : IHostedService
{
    private const string ProgressKey = "startup_gamelist_display_names";
    private const string PhaseName = "display-name-normalization";
    private const int StateVersion = 1;
    private const string NormalizerVersion = "20260522-display-name-v1";
    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly IGamelistStore _gamelistStore;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly IStartupOverlayService _startupOverlayService;
    private readonly ILogger<GamelistDisplayNameNormalizationHostedService>? _logger;

    public GamelistDisplayNameNormalizationHostedService(
        GameNameNormalizer gameNameNormalizer,
        IGamelistStore gamelistStore,
        GamelistUpdateService gamelistUpdateService,
        IStartupOverlayService startupOverlayService,
        ILogger<GamelistDisplayNameNormalizationHostedService>? logger = null)
    {
        _gameNameNormalizer = gameNameNormalizer;
        _gamelistStore = gamelistStore;
        _gamelistUpdateService = gamelistUpdateService;
        _startupOverlayService = startupOverlayService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (IsFrontendProcessRunning())
            {
                _startupOverlayService.UpdateStartupProgress(ProgressKey, 1, 1, "skipped - ES active");
                await AppendLogAsync(
                    "startup-display-name-normalization-skipped",
                    new { reason = "frontend-process-running" },
                    cancellationToken);
                await StartupGamelistPreparationLog.AppendAsync(
                    PhaseName,
                    "skipped",
                    new { reason = "frontend-process-running", elapsedMs = stopwatch.ElapsedMilliseconds },
                    cancellationToken);
                return;
            }

            var result = NormalizeStartupDisplayNames(cancellationToken);
            await AppendLogAsync("startup-display-name-normalization", result, cancellationToken);
            await StartupGamelistPreparationLog.AppendAsync(
                PhaseName,
                "completed",
                result with { ElapsedMs = stopwatch.ElapsedMilliseconds },
                cancellationToken);
            if (result.UpdatedGames > 0)
            {
                _logger?.LogInformation(
                    "Startup gamelist display name normalization updated {GameCount} games in {SystemCount} systems.",
                    result.UpdatedGames,
                    result.UpdatedSystems);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Startup gamelist display name normalization failed.");
            await AppendLogAsync(
                "startup-display-name-normalization-failed",
                new { exceptionType = ex.GetType().FullName, ex.Message },
                CancellationToken.None);
            await StartupGamelistPreparationLog.AppendAsync(
                PhaseName,
                "failed",
                new { exceptionType = ex.GetType().FullName, ex.Message, elapsedMs = stopwatch.ElapsedMilliseconds },
                CancellationToken.None);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private StartupDisplayNameNormalizationResult NormalizeStartupDisplayNames(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            _startupOverlayService.UpdateStartupProgress(ProgressKey, 1, 1, "no roms");
            return new StartupDisplayNameNormalizationResult(0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
        }

        var gamelistPaths = Directory.EnumerateDirectories(RetroBatPaths.RomsRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(systemDirectory => Path.Combine(systemDirectory, "gamelist.xml"))
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (gamelistPaths.Length == 0)
        {
            _startupOverlayService.UpdateStartupProgress(ProgressKey, 1, 1, "no gamelist");
            return new StartupDisplayNameNormalizationResult(0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
        }

        var processedSystems = 0;
        var skippedSystems = 0;
        var updatedSystems = 0;
        var processedGames = 0;
        var updatedGames = 0;
        var state = StartupGamelistPreparationStateStore.Load();
        var stateDirty = false;
        var cacheMissReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var gamelistPath in gamelistPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedSystems++;
            var systemId = Path.GetFileName(Path.GetDirectoryName(gamelistPath) ?? string.Empty);
            _startupOverlayService.UpdateStartupProgress(
                ProgressKey,
                processedSystems - 1,
                gamelistPaths.Length,
                string.IsNullOrWhiteSpace(systemId) ? "gamelist" : systemId);

            if (!string.IsNullOrWhiteSpace(systemId))
            {
                var cacheStatus = StartupGamelistPreparationStateStore.GetSystemPhaseCacheStatus(
                    state,
                    systemId,
                    PhaseName,
                    gamelistPath,
                    StateVersion,
                    NormalizerVersion);
                if (cacheStatus.IsClean)
                {
                    skippedSystems++;
                    continue;
                }

                Increment(cacheMissReasons, cacheStatus.Reason);
            }

            var systemUpdates = NormalizeGamelistDisplayNames(gamelistPath, cancellationToken, out var systemGames);
            processedGames += systemGames;
            if (!string.IsNullOrWhiteSpace(systemId))
            {
                StartupGamelistPreparationStateStore.MarkSystemPhaseClean(
                    state,
                    systemId,
                    PhaseName,
                    gamelistPath,
                    StateVersion,
                    NormalizerVersion);
                stateDirty = true;
            }

            if (systemUpdates <= 0)
            {
                continue;
            }

            updatedSystems++;
            updatedGames += systemUpdates;
        }

        _startupOverlayService.UpdateStartupProgress(
            ProgressKey,
            gamelistPaths.Length,
            gamelistPaths.Length,
            updatedGames > 0 ? $"{updatedGames} noms" : skippedSystems > 0 ? $"cache OK ({skippedSystems})" : "ok");

        if (stateDirty)
        {
            StartupGamelistPreparationStateStore.Save(state);
        }

        return new StartupDisplayNameNormalizationResult(
            processedSystems,
            skippedSystems,
            updatedSystems,
            processedGames,
            updatedGames,
            0,
            cacheMissReasons);
    }

    private static void Increment(IDictionary<string, int> values, string key)
    {
        values[key] = values.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private static bool IsFrontendProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("emulationstation").Length > 0 ||
                Process.GetProcessesByName("RetroBat").Length > 0;
        }
        catch
        {
            return true;
        }
    }

    private int NormalizeGamelistDisplayNames(
        string gamelistPath,
        CancellationToken cancellationToken,
        out int processedGames)
    {
        processedGames = 0;
        lock (_gamelistStore.GetLock(gamelistPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = _gamelistStore.Load(gamelistPath, LoadOptions.PreserveWhitespace, cancellationToken);
            var root = document?.Root;
            if (document == null || root == null)
            {
                return 0;
            }

            var updated = 0;
            foreach (var gameNode in root.Elements("game"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedGames++;
                var path = gameNode.Element("path")?.Value?.Trim() ?? string.Empty;
                var nameElement = gameNode.Element("name");
                var currentName = nameElement?.Value?.Trim() ?? string.Empty;
                var normalizedName = _gameNameNormalizer.NormalizeDisplayName(currentName, path);
                if (string.IsNullOrWhiteSpace(normalizedName) ||
                    string.Equals(currentName, normalizedName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (nameElement == null)
                {
                    gameNode.Add(new XElement("name", normalizedName));
                }
                else
                {
                    nameElement.Value = normalizedName;
                }

                updated++;
            }

            if (updated > 0)
            {
                _gamelistUpdateService.SaveExternalGamelistDocument(
                    document,
                    gamelistPath,
                    "display-name-normalization",
                    cancellationToken);
            }

            return updated;
        }
    }

    private static async Task AppendLogAsync(string status, object details, CancellationToken cancellationToken)
    {
        try
        {
            var logPath = Path.Combine(RetroBatPaths.PluginRoot, ".log", "gamelist-display-name-normalization.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? RetroBatPaths.PluginRoot);
            var line = JsonSerializer.Serialize(
                new
                {
                    at = DateTimeOffset.Now,
                    status,
                    details
                },
                LogJsonOptions);
            await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken);
        }
        catch
        {
            // Logging must not block API startup.
        }
    }

    private sealed record StartupDisplayNameNormalizationResult(
        int ProcessedSystems,
        int SkippedSystems,
        int UpdatedSystems,
        int ProcessedGames,
        int UpdatedGames,
        long ElapsedMs,
        IReadOnlyDictionary<string, int> CacheMissReasons);
}
