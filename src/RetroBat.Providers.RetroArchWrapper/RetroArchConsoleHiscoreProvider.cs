using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Providers.RetroArchWrapper;

public sealed class RetroArchConsoleHiscoreProvider : IProvider
{
    private static readonly TimeSpan ScoreFreshness = TimeSpan.FromMinutes(30);
    private readonly object _sync = new();
    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IHiscoreThemeWriter _hiscoreThemeWriter;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly ILogger<RetroArchConsoleHiscoreProvider>? _logger;
    private readonly Dictionary<string, ConsoleScoreState> _scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConsoleRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _subscription;
    private GameReference? _activeConsoleGame;

    public RetroArchConsoleHiscoreProvider(
        IEventBus eventBus,
        ApiContext context,
        IHiscoreThemeWriter hiscoreThemeWriter,
        EmulationStationSettingsService settingsService,
        ILogger<RetroArchConsoleHiscoreProvider>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _hiscoreThemeWriter = hiscoreThemeWriter;
        _settingsService = settingsService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        _logger?.LogInformation("RetroArch console hiscore provider started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public bool IsHealthy() => true;

    private void HandleEvent(EventEnvelope envelope)
    {
        try
        {
            if (string.Equals(envelope.Type, "retroarch.memory.changed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(envelope.Type, "retroarch.wrapper.changed", StringComparison.OrdinalIgnoreCase))
            {
                ObserveRuntimeState(envelope.Payload);
                CaptureScore(envelope.Payload);
                return;
            }

            if (string.Equals(envelope.Type, "retroarch.score", StringComparison.OrdinalIgnoreCase))
            {
                CaptureScore(envelope.Payload);
                return;
            }

            if (string.Equals(envelope.Type, "ui.game.started", StringComparison.OrdinalIgnoreCase))
            {
                ResetConsoleScoreForStartedGame(envelope.Payload);
                return;
            }

            if (string.Equals(envelope.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                var writeTask = WriteConsoleScoreForEndedGameAsync();
                _ = writeTask.ContinueWith(task =>
                {
                    if (task.Exception != null)
                    {
                        _logger?.LogWarning(task.Exception, "Console wrapper hiscore write failed for game-end");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Console wrapper hiscore event handling failed for {EventType}", envelope.Type);
        }
    }

    private void ResetConsoleScoreForStartedGame(object? payload)
    {
        var game = ResolveGameFromEventPayload(payload) ?? _context.Ui.Running ?? _context.Ui.Selected;
        if (game == null || IsArcadeLikeSystem(game.SystemId))
        {
            return;
        }

        ResetConsoleScoreForGame(game, "started");
    }

    private void ResetConsoleScoreForGame(GameReference game, string reason)
    {
        if (IsArcadeLikeSystem(game.SystemId))
        {
            return;
        }

        var expectedRom = ResolveWrapperRom(game);
        if (string.IsNullOrWhiteSpace(expectedRom))
        {
            return;
        }

        lock (_sync)
        {
            RemoveScoresForSystem(game.SystemId);
            RemoveRuntimeStatesForSystem(game.SystemId);
            _activeConsoleGame = CloneGameReference(game);
        }

        _logger?.LogDebug(
            "Console wrapper hiscore session reset for {SystemId}/{Rom} ({Reason})",
            game.SystemId,
            expectedRom,
            reason);
    }

    private void CaptureScore(object? payload)
    {
        var signal = GetPropertyValue(payload, "signal", "Signal");
        var channel = GetString(signal, "Channel");
        if (signal != null &&
            !string.Equals(channel, "SCORE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsCanonicalScoreState(payload, signal))
        {
            return;
        }

        var value = GetInt(signal, "Value") ?? GetInt(payload, "Value");
        if (value == null || value.Value <= 0)
        {
            return;
        }

        if (LooksLikeUninitializedScore(signal, payload))
        {
            PublishConsoleScoreDiagnostic("hiscore.console.score.ignored", payload, signal, value.Value, "uninitialized-score-sentinel");
            return;
        }

        var systemId = GetString(payload, "SystemId");
        var rom = GetString(payload, "Rom");
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom))
        {
            return;
        }

        var key = BuildKey(systemId, rom);
        lock (_sync)
        {
            if (!CanAcceptScoreLocked(systemId, rom, signal, value.Value))
            {
                _logger?.LogDebug(
                    "Console wrapper score ignored for {SystemId}/{Rom}: no active gameplay state observed",
                    systemId,
                    rom);
                PublishConsoleScoreDiagnostic("hiscore.console.score.ignored", payload, signal, value.Value, "gameplay-not-active");
                return;
            }

            if (!_scores.TryGetValue(key, out var state))
            {
                state = new ConsoleScoreState(systemId, rom);
                _scores[key] = state;
            }

            state.LastScore = value.Value;
            state.BestScore = Math.Max(state.BestScore, value.Value);
            state.LastSignalKey = GetString(signal, "Key");
            if (string.IsNullOrWhiteSpace(state.LastSignalKey))
            {
                state.LastSignalKey = GetString(payload, "actionType", "ActionType");
            }
            state.LastUpdatedUtc = DateTime.UtcNow;
        }

        PublishConsoleScoreDiagnostic("hiscore.console.score.captured", payload, signal, value.Value, "captured");
    }

    private static bool IsCanonicalScoreState(object? payload, object? signal)
    {
        var actionName = signal == null
            ? GetString(payload, "actionType", "ActionType")
            : GetString(signal, "Name");
        if (!string.Equals(actionName, "SCORE_STATE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeUninitializedScore(object? signal, object? payload)
    {
        var raw = GetString(signal, "RawValueHex");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = GetString(payload, "RawValueHex");
        }

        var rate = GetInt(signal, "Rate") ?? GetInt(payload, "Rate");
        if (rate != 0 || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var hex = raw.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        hex = hex.TrimStart('0');
        if (hex.Length < 4 || hex.Length % 2 != 0)
        {
            return false;
        }

        var firstByte = hex[..2];
        for (var index = 2; index < hex.Length; index += 2)
        {
            if (!string.Equals(hex.Substring(index, 2), firstByte, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void PublishConsoleScoreDiagnostic(string type, object? payload, object? signal, int score, string reason)
    {
        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type = type,
            Payload = new
            {
                Reason = reason,
                Score = score,
                SystemId = GetString(payload, "SystemId"),
                Rom = GetString(payload, "Rom"),
                Key = GetString(signal, "Key"),
                Address = GetString(signal, "Address") is { Length: > 0 } signalAddress
                    ? signalAddress
                    : GetString(payload, "Address"),
                RawValueHex = GetString(signal, "RawValueHex") is { Length: > 0 } signalRaw
                    ? signalRaw
                    : GetString(payload, "RawValueHex"),
                Rate = GetInt(signal, "Rate") ?? GetInt(payload, "Rate"),
                Source = GetString(signal, "SourceDescription") is { Length: > 0 } signalSource
                    ? signalSource
                    : GetString(payload, "sourceCategory", "SourceCategory")
            }
        });
    }

    private void ObserveRuntimeState(object? payload)
    {
        var signal = GetPropertyValue(payload, "signal", "Signal");
        if (signal == null)
        {
            return;
        }

        var channel = GetString(signal, "Channel");
        if (!string.Equals(channel, "STATE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(channel, "ACTION", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var stateName = GetString(signal, "Name");
        if (string.IsNullOrWhiteSpace(stateName))
        {
            return;
        }

        var systemId = GetString(payload, "SystemId");
        var rom = GetString(payload, "Rom");
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom))
        {
            return;
        }

        var gameplayState = ResolveGameplayState(stateName);
        if (gameplayState == null)
        {
            return;
        }

        lock (_sync)
        {
            var key = BuildKey(systemId, rom);
            if (!_runtimeStates.TryGetValue(key, out var state))
            {
                state = new ConsoleRuntimeState(systemId, rom);
                _runtimeStates[key] = state;
            }

            state.HasGameplaySignal = true;
            state.IsGameplayActive = gameplayState.Value;
            state.LastStateName = stateName;
            state.LastUpdatedUtc = DateTime.UtcNow;
        }
    }

    private static bool? ResolveGameplayState(string stateName)
    {
        return stateName.Trim().ToUpperInvariant() switch
        {
            "GAME_PLAYING" => true,
            "RUNNING" => true,
            "PLAYING" => true,
            "TITLE_SCREEN" => false,
            "SELECT_SCREEN" => false,
            "DEMO_MODE" => false,
            "CONTINUE_SCREEN" => false,
            "GAME_OVER" => false,
            "CREDITS_SCREEN" => false,
            "PAUSE_ON" => false,
            _ => null
        };
    }

    private bool CanAcceptScoreLocked(string systemId, string rom, object? signal, int score)
    {
        if (!_runtimeStates.TryGetValue(BuildKey(systemId, rom), out var state) || !state.HasGameplaySignal)
        {
            return true;
        }

        if (state.IsGameplayActive)
        {
            return true;
        }

        // Some MEM definitions report a stale GAME_OVER/TITLE state but still emit valid
        // SCORE_STATE updates while playing. Keep the lifecycle as a diagnostic hint,
        // not a hard gate, once the score itself looks dynamic.
        var rate = GetInt(signal, "Rate");
        return score > 0 && rate.GetValueOrDefault() > 0;
    }

    private async Task WriteConsoleScoreForEndedGameAsync()
    {
        if (!IsExportScoresOnGameEndEnabled())
        {
            await PublishConsoleHiscoreDiagnosticAsync("hiscore.console.write.skipped", "export-disabled", null, null);
            return;
        }

        var game = _context.Ui.Selected ?? _context.Ui.Running ?? _activeConsoleGame;
        if (game == null || IsArcadeLikeSystem(game.SystemId))
        {
            await PublishConsoleHiscoreDiagnosticAsync("hiscore.console.write.skipped", "no-game-context", null, null);
            return;
        }

        var expectedRom = ResolveWrapperRom(game);
        if (string.IsNullOrWhiteSpace(expectedRom))
        {
            await PublishConsoleHiscoreDiagnosticAsync("hiscore.console.write.skipped", "no-rom-context", game, null);
            return;
        }

        ConsoleScoreState? score;
        lock (_sync)
        {
            _scores.TryGetValue(BuildKey(game.SystemId, expectedRom), out score);
            if (score != null && DateTime.UtcNow - score.LastUpdatedUtc > ScoreFreshness)
            {
                score = null;
            }

            if (score == null)
            {
                score = _scores.Values
                .Where(candidate => string.Equals(candidate.SystemId, game.SystemId, StringComparison.OrdinalIgnoreCase))
                .Where(candidate => DateTime.UtcNow - candidate.LastUpdatedUtc <= ScoreFreshness)
                .OrderByDescending(candidate => candidate.LastUpdatedUtc)
                .FirstOrDefault();
            }
        }

        if (score == null || score.BestScore <= 0)
        {
            await PublishConsoleHiscoreDiagnosticAsync("hiscore.console.write.skipped", "no-session-score", game, null);
            return;
        }

        var playerName = ResolvePlayerName();
        var maxHiscore = ResolveMaxHiscore();
        var leaderboard = ReadExistingScores(game.SystemId, score.Rom);
        leaderboard.Add(new HiscoreEntry
        {
            Rank = string.Empty,
            Name = playerName,
            Score = score.BestScore.ToString()
        });

        var rankedScores = leaderboard
            .GroupBy(entry => new
            {
                Name = entry.Name.Trim().ToUpperInvariant(),
                Score = entry.Score.Trim()
            })
            .Select(group => group.First())
            .OrderByDescending(entry => ParseScoreValue(entry.Score))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxHiscore)
            .Select((entry, index) => new HiscoreEntry
            {
                Rank = (index + 1).ToString(),
                Name = string.IsNullOrWhiteSpace(entry.Name) ? playerName : entry.Name,
                Score = entry.Score
            })
            .ToList();

        var result = new HiscoreExtractionResult
        {
            QueryId = game.GameId,
            QueryMd5 = game.Details?.Md5 ?? string.Empty,
            RomName = score.Rom,
            RomPath = game.GamePath,
            System = game.SystemId,
            Game = game.GameName,
            Status = "ok",
            Message = "console wrapper score captured",
            SourceType = "retroarch-wrapper",
            SourceFile = score.LastSignalKey,
            UpdatedAt = DateTime.Now,
            Scores = rankedScores
        };

        await _hiscoreThemeWriter.WriteAsync(game, result);
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "hiscore.updated",
            Payload = result
        });
        _logger?.LogInformation(
            "Console wrapper hiscore written for {SystemId}/{Rom}: player={Player}, score={Score}",
            game.SystemId,
            score.Rom,
            playerName,
            score.BestScore);

        lock (_sync)
        {
            _scores.Remove(BuildKey(score.SystemId, score.Rom));
            _activeConsoleGame = null;
        }
    }

    private async Task PublishConsoleHiscoreDiagnosticAsync(
        string type,
        string reason,
        GameReference? game,
        ConsoleScoreState? score)
    {
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = type,
            Payload = new
            {
                Reason = reason,
                GameSystemId = game?.SystemId ?? string.Empty,
                GameName = game?.GameName ?? string.Empty,
                GamePath = game?.GamePath ?? string.Empty,
                ScoreSystemId = score?.SystemId ?? string.Empty,
                ScoreRom = score?.Rom ?? string.Empty,
                BestScore = score?.BestScore
            }
        });
    }

    private void RemoveScoresForSystem(string systemId)
    {
        var keys = _scores.Keys
            .Where(key => key.StartsWith(systemId.Trim() + "|", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keys)
        {
            _scores.Remove(key);
        }
    }

    private void RemoveRuntimeStatesForSystem(string systemId)
    {
        var keys = _runtimeStates.Keys
            .Where(key => key.StartsWith(systemId.Trim() + "|", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keys)
        {
            _runtimeStates.Remove(key);
        }
    }

    private string ResolvePlayerName()
    {
        var settings = _settingsService.GetAllSettings();
        if (settings.TryGetValue("global.retroachievements.username", out var username) &&
            !string.IsNullOrWhiteSpace(username))
        {
            return username.Trim();
        }

        return "PLAYER";
    }

    private static List<HiscoreEntry> ReadExistingScores(string systemId, string rom)
    {
        var candidates = new[]
        {
            Path.Combine(RetroBatPaths.ThemeGameInfosResourcesRoot, systemId, rom + ".xml"),
            Path.Combine(RetroBatPaths.EmulationStationGameInfosThemeRoot, systemId, rom + ".xml"),
            Path.Combine(RetroBatPaths.ThemeHiscoreResourcesRoot, systemId, rom + ".xml"),
            Path.Combine(RetroBatPaths.EmulationStationThemesRoot, ".hiscore", systemId, rom + ".xml")
        };

        var scores = new List<HiscoreEntry>();
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, @"#\d+\s+(?<name>\S+)\s+(?<score>\d+)"))
            {
                var entry = new HiscoreEntry
                {
                    Rank = string.Empty,
                    Name = match.Groups["name"].Value,
                    Score = match.Groups["score"].Value
                };
                if (!scores.Any(existing =>
                        string.Equals(existing.Name, entry.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Score, entry.Score, StringComparison.Ordinal)))
                {
                    scores.Add(entry);
                }
            }

            if (scores.Count > 0)
            {
                break;
            }
        }

        return scores;
    }

    private static long ParseScoreValue(string score)
    {
        return long.TryParse(score, out var value) ? value : 0;
    }

    private static int ResolveMaxHiscore()
    {
        var esValue = ReadEsSetting("global.apiexpose.game_events.max_high_scores");
        if (int.TryParse(esValue, out var esMaxHiscore))
        {
            return Math.Clamp(esMaxHiscore, 1, 100);
        }

        var path = Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json");
        if (!File.Exists(path))
        {
            return 10;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("ApiExpose", out var apiExpose) &&
                apiExpose.TryGetProperty("GameEventsManager", out var gameEvents) &&
                gameEvents.TryGetProperty("MaxHighScores", out var gameEventsMaxHighScores) &&
                gameEventsMaxHighScores.TryGetInt32(out var gameEventsValue))
            {
                return Math.Clamp(gameEventsValue, 1, 100);
            }

            if (apiExpose.TryGetProperty("Hiscores", out var hiscores) &&
                hiscores.TryGetProperty("MaxHiscore", out var maxHiscore) &&
                maxHiscore.TryGetInt32(out var value))
            {
                return Math.Clamp(value, 1, 100);
            }
        }
        catch
        {
            // Keep the runtime safe if appsettings is temporarily being edited.
        }

        return 10;
    }

    private static bool IsExportScoresOnGameEndEnabled()
    {
        var esValue = ReadEsSetting("global.apiexpose.game_events.export_scores_on_game_end.enabled");
        if (TryParseBool(esValue, out var parsedEsValue))
        {
            return parsedEsValue;
        }

        var path = Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json");
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("ApiExpose", out var apiExpose) &&
                apiExpose.TryGetProperty("GameEventsManager", out var gameEvents) &&
                gameEvents.TryGetProperty("ExportScoresOnGameEndEnabled", out var exportEnabled) &&
                (exportEnabled.ValueKind == JsonValueKind.True || exportEnabled.ValueKind == JsonValueKind.False))
            {
                return exportEnabled.GetBoolean();
            }
        }
        catch
        {
            // Keep game-end safe if appsettings is temporarily being edited.
        }

        return true;
    }

    private static string ReadEsSetting(string key)
    {
        var path = RetroBatPaths.EmulationStationSettingsPath;
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            return document.Root?.Elements()
                .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")
                ?.Value
                ?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParseBool(string? value, out bool result)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool IsArcadeLikeSystem(string? systemId)
    {
        return string.Equals(systemId, "mame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(systemId, "fbneo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(systemId, "fba", StringComparison.OrdinalIgnoreCase)
            || string.Equals(systemId, "hbmame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(systemId, "arcade", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWrapperRom(GameReference game)
    {
        var rawRom = Path.GetFileNameWithoutExtension(game.GamePath ?? game.GameName ?? string.Empty);
        var normalizedRom = NormalizeRomName(rawRom);
        var systemId = game.SystemId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(normalizedRom))
        {
            return normalizedRom;
        }

        var aliasFile = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, "alias.json");
        if (!File.Exists(aliasFile))
        {
            return normalizedRom;
        }

        try
        {
            var aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(aliasFile))
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(rawRom) && aliases.TryGetValue(rawRom, out var directAlias))
            {
                return directAlias;
            }

            var normalizedAlias = aliases.FirstOrDefault(entry =>
                string.Equals(NormalizeRomName(entry.Key), normalizedRom, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(normalizedAlias.Value))
            {
                return normalizedAlias.Value;
            }
        }
        catch
        {
            // Alias is optional; normalized filename remains a safe fallback.
        }

        return normalizedRom;
    }

    private static string NormalizeRomName(string rawRom)
    {
        if (string.IsNullOrWhiteSpace(rawRom))
        {
            return string.Empty;
        }

        var normalized = rawRom.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    private static string BuildKey(string systemId, string rom) => $"{systemId.Trim()}|{rom.Trim()}";

    private static GameReference CloneGameReference(GameReference game)
    {
        return new GameReference
        {
            SystemId = game.SystemId,
            GameId = game.GameId,
            GamePath = game.GamePath,
            GameName = game.GameName,
            Details = game.Details,
            Launch = game.Launch
        };
    }

    private static GameReference? ResolveGameFromEventPayload(object? payload)
    {
        var context = GetPropertyValue(payload, "Context");
        var game = GetPropertyValue(context, "Running")
            ?? GetPropertyValue(context, "Selected")
            ?? GetPropertyValue(payload, "Running")
            ?? GetPropertyValue(payload, "Selected");

        if (game is GameReference typedGame)
        {
            return typedGame;
        }

        var systemId = GetString(game, "SystemId");
        var gamePath = GetString(game, "GamePath");
        var gameName = GetString(game, "GameName");
        if (string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(gamePath) && string.IsNullOrWhiteSpace(gameName))
        {
            systemId = GetString(payload, "SystemId");
            gamePath = GetString(payload, "GamePath");
            gameName = Path.GetFileNameWithoutExtension(gamePath);
        }

        if (string.IsNullOrWhiteSpace(systemId) && string.IsNullOrWhiteSpace(gamePath) && string.IsNullOrWhiteSpace(gameName))
        {
            return null;
        }

        return new GameReference
        {
            SystemId = systemId,
            GamePath = gamePath,
            GameName = gameName,
            GameId = GetString(game, "GameId")
        };
    }

    private static object? GetPropertyValue(object? source, params string[] names)
    {
        if (source == null)
        {
            return null;
        }

        var type = source.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperties()
                .FirstOrDefault(prop => string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property != null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static string GetString(object? source, params string[] names)
    {
        return GetPropertyValue(source, names)?.ToString() ?? string.Empty;
    }

    private static int? GetInt(object? source, params string[] names)
    {
        var value = GetPropertyValue(source, names);
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            _ => int.TryParse(value?.ToString(), out var parsed) ? parsed : null
        };
    }

    private sealed class ConsoleScoreState
    {
        public ConsoleScoreState(string systemId, string rom)
        {
            SystemId = systemId;
            Rom = rom;
        }

        public string SystemId { get; }
        public string Rom { get; }
        public int LastScore { get; set; }
        public int BestScore { get; set; }
        public string LastSignalKey { get; set; } = string.Empty;
        public DateTime LastUpdatedUtc { get; set; }
    }

    private sealed class ConsoleRuntimeState
    {
        public ConsoleRuntimeState(string systemId, string rom)
        {
            SystemId = systemId;
            Rom = rom;
        }

        public string SystemId { get; }
        public string Rom { get; }
        public bool HasGameplaySignal { get; set; }
        public bool IsGameplayActive { get; set; }
        public string LastStateName { get; set; } = string.Empty;
        public DateTime LastUpdatedUtc { get; set; }
    }
}
