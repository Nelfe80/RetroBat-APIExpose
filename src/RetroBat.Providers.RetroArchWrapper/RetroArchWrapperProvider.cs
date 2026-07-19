using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Providers.RetroArchWrapper;

public class RetroArchWrapperProvider : IProvider
{
    public const string DefaultPipeName = "RetroBatArcadePipe";

    private static readonly Regex RuntimeRegex = new(
        @"^\[(?<clock>\d{2}:\d{2}:\d{2}\.\d{3})\]\s+\[ADDR:(?<addr>[^\]]+)\]\s+\[VAL:(?<raw>[^\]]+)\]\s+\[UDP_OUT\]\s+(?:TYPE:)?(?<channel>[A-Z]+)\s*:\s*(?<name>[A-Z0-9_]+)\s+\|\s+SOURCE:(?<source>.*?)\s+\|\s+VALUE:(?<value>-?\d+)(?:\s+\|\s+RATE:(?<rate>-?\d+))?(?:\s+\|\s+FAMILY:(?<family>[A-Za-z0-9_.-]+))?(?:\s+\|\s+COLOR:(?<color>[A-Za-z0-9_-]+))?(?:\s+\|\s+PLAYER:(?<player>\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IIngameSourceArbitrationService _arbitration;
    private readonly ILogger<RetroArchWrapperProvider>? _logger;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, RetroArchRuntimeSignal> _signals = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private bool _connected;
    private DateTime? _lastMessageAt;
    private string _lastRawMessage = string.Empty;

    public RetroArchWrapperProvider(
        IEventBus eventBus,
        ApiContext context,
        IIngameSourceArbitrationService arbitration,
        ILogger<RetroArchWrapperProvider>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _arbitration = arbitration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = RunAsync(_cts.Token);
        _logger?.LogInformation("RetroArchWrapperProvider started for pipe {PipeName}", GetPipePath());
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
        {
            _cts.Cancel();
        }

        if (_workerTask != null)
        {
            try
            {
                await Task.WhenAny(_workerTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            catch
            {
                // Ignore shutdown exceptions.
            }
        }
    }

    public bool IsHealthy() => _workerTask != null && !_workerTask.IsCompleted;

    public RetroArchRuntimeSnapshot GetSnapshot()
    {
        var definition = ResolveDefinition();
        lock (_stateLock)
        {
            return new RetroArchRuntimeSnapshot
            {
                Source = "retroarch.wrapper.pipe",
                Pipe = GetPipePath(),
                Connected = _connected,
                SystemId = definition.SystemId,
                Rom = definition.Rom,
                DefinitionFile = definition.DefinitionFile,
                LastMessageAt = _lastMessageAt,
                LastRawMessage = _lastRawMessage,
                Signals = _signals.Values
                    .OrderBy(signal => signal.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(CloneSignal)
                    .ToList()
            };
        }
    }

    public RetroArchDefinitionSnapshot GetDefinitionSnapshot() => ResolveDefinition();

    /// <summary>
    /// Resolves a definition for an EXPLICIT system/rom pair with the exact
    /// same logic as the current-context path: alias.json first, normalized
    /// rom name, and arcade-like system fallback. This is what remote
    /// consumers (tournament manager, Live Contest) must use — a naive
    /// <c>&lt;system&gt;/&lt;rom&gt;.MEM</c> path never matches curated files.
    /// </summary>
    public RetroArchDefinitionSnapshot ResolveDefinitionFor(string rawRom, string systemId)
    {
        RetroArchDefinitionSnapshot? fallback = null;
        foreach (var candidateSystemId in ResolveDefinitionSystemCandidates(systemId))
        {
            var candidate = ResolveDefinition(rawRom, candidateSystemId);
            fallback ??= candidate;
            if (candidate.DefinitionExists)
            {
                return candidate;
            }
        }

        return fallback ?? new RetroArchDefinitionSnapshot
        {
            SystemId = systemId,
            Rom = NormalizeRomName(rawRom),
            DefinitionFile = string.Empty,
            AliasFile = string.Empty,
            AliasMatched = false,
            DefinitionExists = false
        };
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                DefaultPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                SetConnected(true);
                await PublishConnectionEventAsync("retroarch.wrapper.connected", cancellationToken);

                using var reader = new StreamReader(pipe);
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    await ProcessLineAsync(line, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogInformation(ex, "RetroArch wrapper pipe disconnected");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Error while reading RetroArch wrapper pipe");
            }
            finally
            {
                var wasConnected = SetConnected(false);
                if (wasConnected)
                {
                    await PublishConnectionEventAsync("retroarch.wrapper.disconnected", cancellationToken);
                }
            }
        }
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        var definition = ResolveDefinition();
        var parsed = ParseRuntimeSignal(line);

        lock (_stateLock)
        {
            _lastRawMessage = line;
            _lastMessageAt = DateTime.UtcNow;
            if (parsed != null)
            {
                _signals[parsed.Key] = parsed;
            }
        }

        if (parsed == null)
        {
            _logger?.LogDebug(
                "Ignoring non-runtime wrapper line for {SystemId}/{Rom}: {RawLine}",
                definition.SystemId,
                definition.Rom,
                line);
            return;
        }

        if (_arbitration.ShouldSuppressRetroArchWrapper(definition.SystemId, definition.Rom, definition.DefinitionFile))
        {
            _logger?.LogDebug(
                "Suppressing RetroArch wrapper runtime signal because MAME Lua is active for {SystemId}/{Rom}: {RawLine}",
                definition.SystemId,
                definition.Rom,
                line);
            return;
        }

        var payload = new
        {
            Source = "retroarch.wrapper.pipe",
            Pipe = GetPipePath(),
            definition.SystemId,
            definition.Rom,
            definition.DefinitionFile,
            signal = parsed
        };

        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "retroarch.memory.changed",
            Payload = payload
        });

        var projectedType = parsed.Channel switch
        {
            "ACTION" => "retroarch.action",
            "STATE" => "retroarch.state",
            "SCORE" => "retroarch.score",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(projectedType))
        {
            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = projectedType,
                Payload = new
                {
                    Source = "retroarch.wrapper.pipe",
                    Pipe = GetPipePath(),
                    definition.SystemId,
                    definition.Rom,
                    definition.DefinitionFile,
                    actionType = parsed.Name,
                    sourceCategory = parsed.SourceDescription,
                    parsed.Value,
                    parsed.Rate,
                    parsed.Address,
                    parsed.RawValueHex,
                    family = parsed.Family,
                    color = parsed.Color,
                    player = parsed.Player
                }
            });
        }
    }

    private RetroArchDefinitionSnapshot ResolveDefinition()
    {
        var game = _context.Ui.Running ?? _context.Ui.Selected;
        var systemId = ResolveSystemId(game);
        var rawRom = Path.GetFileNameWithoutExtension(game?.GamePath ?? game?.GameName ?? string.Empty);
        RetroArchDefinitionSnapshot? fallback = null;

        foreach (var candidateSystemId in ResolveDefinitionSystemCandidates(systemId))
        {
            var candidate = ResolveDefinition(rawRom, candidateSystemId);
            fallback ??= candidate;
            if (candidate.DefinitionExists)
            {
                return candidate;
            }
        }

        return fallback ?? new RetroArchDefinitionSnapshot
        {
            SystemId = systemId,
            Rom = NormalizeRomName(rawRom),
            DefinitionFile = string.Empty,
            AliasFile = string.Empty,
            AliasMatched = false,
            DefinitionExists = false
        };
    }

    private RetroArchDefinitionSnapshot ResolveDefinition(string rawRom, string systemId)
    {
        var normalizedRom = NormalizeRomName(rawRom);
        var aliasFile = string.IsNullOrWhiteSpace(systemId)
            ? string.Empty
            : Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, "alias.json");

        var aliasMatched = false;
        if (!string.IsNullOrWhiteSpace(aliasFile) && File.Exists(aliasFile))
        {
            try
            {
                var aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(aliasFile))
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(rawRom) && aliases.TryGetValue(rawRom, out var aliasTarget))
                {
                    normalizedRom = aliasTarget;
                    aliasMatched = true;
                }
                else
                {
                    var aliasEntry = aliases.FirstOrDefault(entry =>
                        string.Equals(NormalizeRomName(entry.Key), normalizedRom, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(aliasEntry.Value))
                    {
                        normalizedRom = aliasEntry.Value;
                        aliasMatched = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read wrapper alias file {AliasFile}", aliasFile);
            }
        }

        var definitionFile = string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(normalizedRom)
            ? string.Empty
            : Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, normalizedRom + ".MEM");

        return new RetroArchDefinitionSnapshot
        {
            SystemId = systemId,
            Rom = normalizedRom,
            DefinitionFile = definitionFile,
            AliasFile = aliasFile,
            AliasMatched = aliasMatched,
            DefinitionExists = !string.IsNullOrWhiteSpace(definitionFile) && File.Exists(definitionFile)
        };
    }

    private RetroArchRuntimeSignal? ParseRuntimeSignal(string line)
    {
        var match = RuntimeRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        int? value = int.TryParse(match.Groups["value"].Value, out var parsedValue) ? parsedValue : null;
        int? rate = int.TryParse(match.Groups["rate"].Value, out var parsedRate) ? parsedRate : null;
        var channel = match.Groups["channel"].Value.Trim().ToUpperInvariant();
        var name = match.Groups["name"].Value.Trim().ToUpperInvariant();

        return new RetroArchRuntimeSignal
        {
            Key = $"{channel}.{name}",
            Channel = channel,
            Name = name,
            SourceDescription = match.Groups["source"].Value.Trim(),
            Address = match.Groups["addr"].Value.Trim(),
            RawValueHex = match.Groups["raw"].Value.Trim(),
            Value = value,
            Rate = rate,
            Family = match.Groups["family"].Value.Trim().ToLowerInvariant(),
            Color = match.Groups["color"].Value.Trim().ToLowerInvariant(),
            Player = int.TryParse(match.Groups["player"].Value, out var parsedPlayer) ? parsedPlayer : null,
            RawLine = line,
            Ts = DateTime.UtcNow
        };
    }

    private bool SetConnected(bool connected)
    {
        lock (_stateLock)
        {
            var previous = _connected;
            if (connected && !previous)
            {
                _signals.Clear();
                _lastMessageAt = null;
                _lastRawMessage = string.Empty;
            }
            _connected = connected;
            return previous;
        }
    }

    private async Task PublishConnectionEventAsync(string eventType, CancellationToken cancellationToken)
    {
        var definition = ResolveDefinition();
        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = eventType,
            Payload = new
            {
                Source = "retroarch.wrapper.pipe",
                Pipe = GetPipePath(),
                definition.SystemId,
                definition.Rom,
                definition.DefinitionFile
            }
        });
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

    private static IReadOnlyList<string> ResolveDefinitionSystemCandidates(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string> { systemId.Trim() };
        var normalized = systemId.Trim().ToLowerInvariant();
        if (IsArcadeLikeSystem(normalized) &&
            !string.Equals(normalized, "arcade", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("arcade");
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsArcadeLikeSystem(string systemId)
    {
        return systemId is
            "mame" or
            "fbneo" or
            "fba" or
            "neogeo" or
            "cps1" or
            "cps2" or
            "cps3" or
            "cave" or
            "atomiswave" or
            "naomi" or
            "naomi2";
    }

    private static string ResolveSystemId(GameReference? game)
    {
        if (game == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(game.SystemId) &&
            !string.Equals(game.SystemId, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return game.SystemId;
        }

        if (!string.IsNullOrWhiteSpace(game.Launch?.System))
        {
            return game.Launch.System.Trim();
        }

        var romPath = game.GamePath ?? string.Empty;
        try
        {
            var romsRoot = Path.GetFullPath(RetroBatPaths.RomsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullGamePath = Path.GetFullPath(romPath);
            if (fullGamePath.StartsWith(romsRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullGamePath[romsRoot.Length..];
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length > 1)
                {
                    return parts[0];
                }
            }
        }
        catch
        {
            // Ignore path inference errors and fall back to unknown.
        }

        return game.SystemId;
    }

    private static string GetPipePath() => @"\\.\pipe\" + DefaultPipeName;

    private static RetroArchRuntimeSignal CloneSignal(RetroArchRuntimeSignal signal)
    {
        return new RetroArchRuntimeSignal
        {
            Key = signal.Key,
            Channel = signal.Channel,
            Name = signal.Name,
            SourceDescription = signal.SourceDescription,
            Address = signal.Address,
            RawValueHex = signal.RawValueHex,
            Value = signal.Value,
            Rate = signal.Rate,
            RawLine = signal.RawLine,
            Ts = signal.Ts
        };
    }
}
