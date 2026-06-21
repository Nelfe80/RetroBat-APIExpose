using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Providers.Hi2Txt;

public class Hi2TxtProvider : IProvider
{
    private static readonly string[] CandidateMameFamilyFolders =
    {
        "mame",
        "mame2000",
        "mame2003",
        "mame2003-plus",
        "mame2010",
        "mame2014",
        "mame2016"
    };

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IHiscoreService _hiscoreService;
    private readonly IHiscoreThemeWriter _hiscoreThemeWriter;
    private readonly ILogger<Hi2TxtProvider>? _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastNotifications = new(StringComparer.OrdinalIgnoreCase);

    public Hi2TxtProvider(
        IEventBus eventBus,
        ApiContext context,
        IHiscoreService hiscoreService,
        IHiscoreThemeWriter hiscoreThemeWriter,
        ILogger<Hi2TxtProvider>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _hiscoreService = hiscoreService;
        _hiscoreThemeWriter = hiscoreThemeWriter;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetupWatcher(RetroBatPaths.SavesRoot, includeSubdirectories: true);
        SetupWatcher(Path.Combine(RetroBatPaths.RetroBatRoot, "bios"), includeSubdirectories: true);

        _logger?.LogInformation("Hi2TxtProvider watchers started with {WatcherCount} watcher(s)", _watchers.Count);
        
        return Task.CompletedTask;
    }

    private void SetupWatcher(string path, bool includeSubdirectories)
    {
        if (Directory.Exists(path))
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                Filter = "*.*",
                IncludeSubdirectories = includeSubdirectories,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            _watchers.Add(watcher);
        }
        else
        {
            _logger?.LogInformation("Hi2Txt directory not found at startup: {Path}", path);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedHiscoreArtifact(e.FullPath, out var romName))
        {
            return;
        }

        var notificationKey = e.FullPath;
        var now = DateTime.UtcNow;
        if (_lastNotifications.TryGetValue(notificationKey, out var lastSeen) &&
            (now - lastSeen) < TimeSpan.FromMilliseconds(750))
        {
            return;
        }

        _lastNotifications[notificationKey] = now;
        _ = Task.Run(() => ProcessFileChangeAsync(e.FullPath, romName));
    }

    private async Task ProcessFileChangeAsync(string changedPath, string romName)
    {
        try
        {
            _logger?.LogInformation("Hi/Nvram artifact changed: {FullPath} for ROM {RomName}", changedPath, romName);

            var targetGame = ResolveTargetGame(romName, changedPath);
            var result = await _hiscoreService.ExtractAsync(targetGame);

            if (result.Status == "not_found")
            {
                _logger?.LogDebug("No live hiscore extracted for {RomName} after change in {ChangedPath}", romName, changedPath);
                return;
            }

            await _hiscoreThemeWriter.WriteAsync(targetGame, result);

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "hiscore.updated",
                Payload = result
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing live hiscore update for {ChangedPath}", changedPath);
        }
    }

    private GameReference ResolveTargetGame(string romName, string changedPath)
    {
        var activeGame = new[] { _context.Ui.Running, _context.Ui.Selected }
            .FirstOrDefault(game => game != null && IsMatchingRom(game, romName));
        if (activeGame != null)
        {
            return activeGame;
        }

        var inferredSystemId = InferSystemId(changedPath);
        return new GameReference
        {
            SystemId = inferredSystemId,
            GameId = romName,
            GameName = romName,
            GamePath = Path.Combine(RetroBatPaths.RomsRoot, inferredSystemId, romName + ".zip")
        };
    }

    private static bool IsMatchingRom(GameReference game, string romName)
    {
        return string.Equals(Path.GetFileNameWithoutExtension(game.GamePath), romName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(game.GameId, romName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(game.GameName, romName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedHiscoreArtifact(string fullPath, out string romName)
    {
        romName = string.Empty;

        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName.EndsWith(".hi", StringComparison.OrdinalIgnoreCase))
        {
            romName = Path.GetFileNameWithoutExtension(fileName);
            return !string.IsNullOrWhiteSpace(romName);
        }

        if (fileName.Equals("nvram", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("saveram", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("eeprom", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("earom", StringComparison.OrdinalIgnoreCase))
        {
            romName = new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? string.Empty).Name;
            return !string.IsNullOrWhiteSpace(romName);
        }

        return false;
    }

    private static string InferSystemId(string fullPath)
    {
        var normalizedPath = Path.GetFullPath(fullPath);
        foreach (var familyFolder in CandidateMameFamilyFolders)
        {
            var marker = $"{Path.DirectorySeparatorChar}{familyFolder}{Path.DirectorySeparatorChar}";
            if (normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return familyFolder.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase) switch
                {
                    "mame2003plus" => "mame",
                    _ => familyFolder
                };
            }
        }

        return "mame";
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var watcher in _watchers.ToArray())
        {
            StopWatcher(watcher);
        }

        _watchers.Clear();
        _logger?.LogInformation("Hi2TxtProvider watchers stopped");
        
        return Task.CompletedTask;
    }

    private void StopWatcher(FileSystemWatcher? watcher)
    {
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged;
            watcher.Created -= OnFileChanged;
            watcher.Dispose();
        }
    }

    public bool IsHealthy() => true;
}
