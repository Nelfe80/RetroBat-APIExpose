using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class MameLuaIngameProvider : IProvider
{
    private static readonly string[] DefinitionSystemFallbacks = ["arcade", "mame"];

    private static readonly Regex EntryValueRegex = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>""(?:\\""|[^""])*""|0[xX][0-9A-Fa-f]+|-?\d+(?:\.\d+)?|true|false)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEventBus _eventBus;
    private readonly IIngameSourceArbitrationService _arbitration;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<MameLuaIngameProvider> _logger;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, MameLuaSessionSnapshot> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public MameLuaIngameProvider(
        IEventBus eventBus,
        IIngameSourceArbitrationService arbitration,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<MameLuaIngameProvider> logger)
    {
        _eventBus = eventBus;
        _arbitration = arbitration;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = RunAsync(_cts.Token);
        _logger.LogInformation("MameLuaIngameProvider bridge starting on 127.0.0.1:{Port}", GetPort());
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
            await Task.WhenAny(_workerTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        }
    }

    public bool IsHealthy() => _workerTask != null && !_workerTask.IsCompleted;

    private int GetPort()
    {
        var port = _options.CurrentValue.GameEventsManager.MameLuaIngamePort;
        return port is > 0 and < 65536 ? port : 12347;
    }

    private async Task RunAsync(CancellationToken token)
    {
        // Le plugin Lua MAME (emu.file "socket.host:port") ne sait etre que
        // CLIENT sortant : APIExpose doit donc tenir l'ecoute locale. Les deux
        // bouts etaient clients jusqu'ici -> aucune session ne s'etablissait.
        while (!token.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, GetPort());
                listener.Start();
                _logger.LogInformation("MAME Lua ingame bridge listening on 127.0.0.1:{Port}", GetPort());

                while (!token.IsCancellationRequested)
                {
                    // chaque client dans sa tache : une session ouverte ne doit
                    // jamais bloquer l'accept (double instance MAME, sonde...)
                    var client = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleClientAsync(client, token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "MAME Lua ingame session ended.");
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    }, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "MAME Lua ingame bridge cannot listen on {Port}; retrying.", GetPort());
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "MAME Lua ingame bridge failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            finally
            {
                listener?.Stop();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var clientScope = client;
        client.NoDelay = true;

        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        MameLuaDefinition? definition = null;
        var previousValues = new Dictionary<int, long>();

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await writer.WriteLineAsync("HELLO?");

            while (!token.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split('|');
                var command = parts[0].Trim();
                if (command.Equals("HELLO", StringComparison.OrdinalIgnoreCase))
                {
                    var rom = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    var gameName = parts.Length > 2 ? parts[2].Trim() : rom;
                    definition = ResolveDefinition(rom);
                    previousValues.Clear();

                    await writer.WriteLineAsync("CLEAR");
                    foreach (var target in definition.Targets)
                    {
                        await writer.WriteLineAsync($"WATCH|{target.Id}|0x{target.RuntimeAddress:X}|{target.Type}");
                    }

                    await writer.WriteLineAsync($"READY|{definition.Targets.Count}");
                    await PublishSessionStartedAsync(definition, gameName);
                    UpdateSession(endpoint, definition, connected: true, lastRawLine: line);

                    _logger.LogInformation(
                        "MAME Lua ingame session started: rom={Rom} resolved={ResolvedRom} watches={WatchCount} definition={DefinitionFile}",
                        rom,
                        definition.Rom,
                        definition.Targets.Count,
                        definition.DefinitionFile);
                }
                else if (command.Equals("VALUE", StringComparison.OrdinalIgnoreCase) && definition != null)
                {
                    if (parts.Length < 3 ||
                        !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId) ||
                        !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        continue;
                    }

                    UpdateSession(endpoint, definition, connected: true, lastRawLine: line);
                    if (!previousValues.TryGetValue(targetId, out var oldValue))
                    {
                        previousValues[targetId] = value;
                        continue;
                    }

                    if (oldValue == value)
                    {
                        continue;
                    }

                    previousValues[targetId] = value;
                    await EvaluateTargetAsync(definition, targetId, oldValue, value);
                }
                else if (command.Equals("BYE", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAME Lua ingame client disconnected unexpectedly.");
        }
        finally
        {
            if (definition != null)
            {
                UpdateSession(endpoint, definition, connected: false, lastRawLine: string.Empty);
                await PublishSessionStoppedAsync(definition);
            }
        }
    }

    private async Task EvaluateTargetAsync(MameLuaDefinition definition, int targetId, long oldValue, long value)
    {
        foreach (var rule in definition.Rules.Where(rule => rule.TargetId == targetId))
        {
            var trigger = ShouldTrigger(rule, oldValue, value, out var emittedValue, out var rate);
            if (!trigger || rule.NoLog)
            {
                continue;
            }

            var payload = new
            {
                Source = "mame.lua",
                SystemId = definition.SystemId,
                definition.Rom,
                definition.DefinitionFile,
                signal = new
                {
                    Key = rule.Action,
                    Channel = "ACTION",
                    Name = rule.Action,
                    SourceDescription = rule.Description,
                    Address = $"0x{rule.Address:X}",
                    RuntimeAddress = $"0x{rule.RuntimeAddress:X}",
                    RawValueHex = $"0x{value:X}",
                    Value = emittedValue,
                    Rate = rate,
                    Color = rule.Color,
                    Family = rule.Family,
                    Ts = DateTime.UtcNow
                },
                actionType = rule.Action,
                sourceCategory = rule.Description,
                Value = emittedValue,
                Rate = rate,
                Address = $"0x{rule.Address:X}",
                RuntimeAddress = $"0x{rule.RuntimeAddress:X}",
                RawValueHex = $"0x{value:X}",
                color = rule.Color,
                family = rule.Family,
                player = rule.Player
            };

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "ingame.memory.changed",
                Payload = payload
            });

            await _eventBus.PublishAsync(new EventEnvelope
            {
                Type = rule.Action.Contains("SCORE", StringComparison.OrdinalIgnoreCase)
                    ? "ingame.score.changed"
                    : "ingame.action",
                Payload = payload
            });
        }
    }

    private static bool ShouldTrigger(MameLuaRule rule, long oldValue, long value, out long emittedValue, out long rate)
    {
        // Aligne sur wrapper.cpp : le mask est applique a la valeur lue pour
        // toutes les conditions (les conditions bit_* comparent sur le mask brut).
        if (rule.Mask is long readMask && rule.Condition is not ("bit_true" or "bit_false"))
        {
            value &= readMask;
            oldValue &= readMask;
        }

        emittedValue = value;
        rate = value - oldValue;

        var condition = rule.Condition.ToLowerInvariant();

        if (rule.HasRange)
        {
            var oldComparable = ToBcdLikeInt(oldValue);
            var newComparable = ToBcdLikeInt(value);
            rate = newComparable - oldComparable;
            emittedValue = newComparable;

            var directionOk = condition switch
            {
                "decrease" => rate < 0,
                "increase" => rate > 0,
                _ => rate != 0
            };
            if (!directionOk)
            {
                return false;
            }

            var delta = Math.Abs(rate);
            if (rule.Min.HasValue && delta < rule.Min.Value)
            {
                return false;
            }

            if (rule.Max.HasValue && delta > rule.Max.Value)
            {
                return false;
            }

            return true;
        }

        return condition switch
        {
            "any" or "change" => value != oldValue,
            "increase" => value > oldValue,
            "decrease" => value < oldValue,
            "eq" or "equal" or "equals" => rule.Value.HasValue && value == rule.Value.Value && oldValue != rule.Value.Value,
            "neq" or "ne" or "not_equal" => rule.Value.HasValue && value != rule.Value.Value && value != oldValue,
            "gt" => rule.Value.HasValue && value > rule.Value.Value,
            "lt" => rule.Value.HasValue && value < rule.Value.Value,
            "bit_true" => rule.Mask is long mask && (value & mask) == mask && (oldValue & mask) != mask,
            "bit_false" => rule.Mask is long mask && (value & mask) == 0 && (oldValue & mask) != 0,
            _ => value != oldValue
        };
    }

    private static long ToBcdLikeInt(long value)
    {
        var hex = value.ToString("X", CultureInfo.InvariantCulture);
        return long.TryParse(hex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : value;
    }

    private MameLuaDefinition ResolveDefinition(string rawRom)
    {
        MameLuaDefinition? fallback = null;
        foreach (var systemId in DefinitionSystemFallbacks)
        {
            var definition = ResolveDefinition(rawRom, systemId);
            fallback ??= definition;
            if (definition.DefinitionExists)
            {
                return definition;
            }
        }

        return fallback ?? ResolveDefinition(rawRom, "arcade");
    }

    private MameLuaDefinition ResolveDefinition(string rawRom, string systemId)
    {
        var rom = NormalizeRom(rawRom);
        var aliasFile = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, "alias.json");
        var aliasMatched = false;

        if (File.Exists(aliasFile))
        {
            try
            {
                var loadedAliases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(aliasFile))
                    ?? new Dictionary<string, string>();
                // alias.json porte des variantes de casse volontaires : le
                // constructeur insensible a la casse JETTE sur doublons,
                // TryAdd (premiere occurrence gagne) est requis.
                var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in loadedAliases)
                {
                    aliases.TryAdd(pair.Key, pair.Value);
                }
                if (TryResolveAlias(aliases, rawRom, rom, out var alias))
                {
                    rom = NormalizeAliasKey(alias);
                    aliasMatched = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read MAME Lua ingame alias file {AliasFile}", aliasFile);
            }
        }

        var definitionFile = Path.Combine(RetroBatPaths.RamResourcesRoot, systemId, rom + ".MEM");
        var rules = File.Exists(definitionFile)
            ? ParseRules(File.ReadAllText(definitionFile))
            : new List<MameLuaRule>();

        var targets = new List<MameLuaTarget>();
        var targetMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usesHighRamBank = rules.Any(rule => rule.Address is >= 0xFF0000 and <= 0xFFFFFF);

        foreach (var rule in rules)
        {
            var runtimeAddress = ToMameRuntimeAddress(systemId, rule.Address, usesHighRamBank);
            rule.RuntimeAddress = runtimeAddress;

            // Les rules no_log/no_survey ne publient jamais : ne pas faire lire
            // leur adresse au pont Lua a chaque frame (TargetId 0 = jamais evaluee).
            if (rule.NoLog || rule.NoSurvey)
            {
                rule.TargetId = 0;
                continue;
            }

            var key = $"{runtimeAddress:X}|{rule.Type}";
            if (!targetMap.TryGetValue(key, out var targetId))
            {
                targetId = targets.Count + 1;
                targetMap[key] = targetId;
                targets.Add(new MameLuaTarget(targetId, rule.Address, runtimeAddress, rule.Type));
            }

            rule.TargetId = targetId;
        }

        return new MameLuaDefinition(
            systemId,
            rawRom,
            rom,
            definitionFile,
            aliasFile,
            aliasMatched,
            File.Exists(definitionFile),
            targets,
            rules);
    }

    private static List<MameLuaRule> ParseRules(string text)
    {
        var rules = new List<MameLuaRule>();
        var entries = ExtractEntryTables(text);
        foreach (var entry in entries)
        {
            var values = ParseEntryValues(entry.Table);
            if (!TryParseLong(values.GetValueOrDefault("address"), out var address))
            {
                continue;
            }

            var action = GetString(values, "action");
            if (string.IsNullOrWhiteSpace(action))
            {
                continue;
            }

            var condition = GetString(values, "condition");
            rules.Add(new MameLuaRule
            {
                Address = address,
                Type = GetString(values, "type", "u8"),
                Condition = string.IsNullOrWhiteSpace(condition) ? "change" : condition,
                Action = action.Trim().ToUpperInvariant(),
                Description = GetString(values, "desc", action),
                Family = entry.Family,
                Color = GetString(values, "color"),
                NoLog = TryParseBool(values.GetValueOrDefault("no_log"), out var noLog) && noLog,
                NoSurvey = TryParseBool(values.GetValueOrDefault("no_survey"), out var noSurvey) && noSurvey,
                Min = TryParseLong(values.GetValueOrDefault("min"), out var min) ? min : null,
                Max = TryParseLong(values.GetValueOrDefault("max"), out var max) ? max : null,
                Value = TryParseLong(values.GetValueOrDefault("value"), out var exactValue) ? exactValue : null,
                Mask = TryParseLong(values.GetValueOrDefault("mask"), out var mask) ? mask : null,
                Player = TryParseLong(values.GetValueOrDefault("player"), out var player) ? player : null
            });
        }

        return rules;
    }

    private static List<MameLuaEntryTable> ExtractEntryTables(string text)
    {
        var results = new List<MameLuaEntryTable>();
        var stack = new Stack<string>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"' )
            {
                i = SkipString(text, i);
                continue;
            }

            var key = TryReadTableKeyBeforeBrace(text, i);
            if (key != null)
            {
                stack.Push(key);
            }

            if (text[i] != '{')
            {
                continue;
            }

            var end = FindMatchingBrace(text, i);
            if (end <= i)
            {
                continue;
            }

            var table = text.Substring(i, end - i + 1);
            if (Regex.IsMatch(table, @"^\{\s*address\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                results.Add(new MameLuaEntryTable(table, string.Join(".", stack.Reverse().Where(s => !IsStructuralKey(s)))));
            }

            if (key != null && stack.Count > 0)
            {
                stack.Pop();
            }
        }

        return results;
    }

    private static string? TryReadTableKeyBeforeBrace(string text, int index)
    {
        if (text[index] != '{')
        {
            return null;
        }

        var prefixStart = Math.Max(0, index - 120);
        var prefix = text.Substring(prefixStart, index - prefixStart);
        var bracket = Regex.Match(prefix, @"\[""(?<key>[^""]+)""\]\s*=\s*$");
        if (bracket.Success)
        {
            return bracket.Groups["key"].Value;
        }

        var plain = Regex.Match(prefix, @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*$");
        return plain.Success ? plain.Groups["key"].Value : null;
    }

    private static bool IsStructuralKey(string key) =>
        key.Equals("events", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("game", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("rom", StringComparison.OrdinalIgnoreCase);

    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int SkipString(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static Dictionary<string, string> ParseEntryValues(string entry)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in EntryValueRegex.Matches(entry))
        {
            values[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        return values;
    }

    private static string GetString(Dictionary<string, string> values, string key, string fallback = "")
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"");
        }

        return value;
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        return !string.IsNullOrWhiteSpace(raw) &&
            bool.TryParse(raw, out value);
    }

    private static bool TryParseLong(string? raw, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static long ToMameRuntimeAddress(string systemId, long logicalAddress, bool usesHighRamBank)
    {
        if (!systemId.Equals("arcade", StringComparison.OrdinalIgnoreCase))
        {
            return logicalAddress;
        }

        // Le rebasage se fait desormais dans le plugin Lua, contre la carte
        // memoire REELLE de la machine (Neo-Geo 0x100000, CPS 0xFF0000...) :
        // les heuristiques statiques ci-devant rebasaient a l'aveugle.
        _ = usesHighRamBank;
        return logicalAddress;
    }

    private static string NormalizeRom(string rom)
    {
        var normalized = (rom ?? string.Empty).Trim();
        if (normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetFileNameWithoutExtension(normalized);
        }

        return normalized.ToLowerInvariant();
    }

    private static bool TryResolveAlias(
        IReadOnlyDictionary<string, string> aliases,
        string rawRom,
        string normalizedRom,
        out string alias)
    {
        if (!string.IsNullOrWhiteSpace(rawRom) && aliases.TryGetValue(rawRom, out alias!))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedRom) && aliases.TryGetValue(normalizedRom, out alias!))
        {
            return true;
        }

        var normalizedKey = NormalizeAliasKey(rawRom);
        if (!string.IsNullOrWhiteSpace(normalizedKey) && aliases.TryGetValue(normalizedKey, out alias!))
        {
            return true;
        }

        foreach (var entry in aliases)
        {
            if (string.Equals(NormalizeAliasKey(entry.Key), normalizedRom, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeAliasKey(entry.Key), normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                alias = entry.Value;
                return true;
            }
        }

        alias = string.Empty;
        return false;
    }

    private static string NormalizeAliasKey(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        var archiveDisplay = Regex.Match(
            normalized,
            @"^(?<name>.+?)\.(?:zip|7z)\s*<",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (archiveDisplay.Success)
        {
            normalized = archiveDisplay.Groups["name"].Value;
        }
        else if (normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetFileNameWithoutExtension(normalized);
        }

        normalized = normalized.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    private async Task PublishSessionStartedAsync(MameLuaDefinition definition, string gameName)
    {
        _arbitration.MarkMameLuaSessionStarted(definition.SystemId, definition.Rom, definition.DefinitionFile);

        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "ingame.mame.session.started",
            Payload = new
            {
                Source = "mame.lua",
                definition.SystemId,
                definition.RawRom,
                definition.Rom,
                GameName = gameName,
                definition.DefinitionFile,
                definition.DefinitionExists,
                WatchCount = definition.Targets.Count
            }
        });
    }

    private async Task PublishSessionStoppedAsync(MameLuaDefinition definition)
    {
        _arbitration.MarkMameLuaSessionStopped(definition.SystemId, definition.Rom, definition.DefinitionFile);

        await _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "ingame.mame.session.stopped",
            Payload = new
            {
                Source = "mame.lua",
                definition.SystemId,
                definition.RawRom,
                definition.Rom,
                definition.DefinitionFile
            }
        });
    }

    private void UpdateSession(string endpoint, MameLuaDefinition definition, bool connected, string lastRawLine)
    {
        lock (_stateLock)
        {
            _sessions[endpoint] = new MameLuaSessionSnapshot(
                endpoint,
                connected,
                definition.Rom,
                definition.DefinitionFile,
                definition.DefinitionExists,
                definition.Targets.Count,
                DateTime.UtcNow,
                lastRawLine);
        }
    }

    private sealed record MameLuaTarget(int Id, long Address, long RuntimeAddress, string Type);

    private sealed record MameLuaDefinition(
        string SystemId,
        string RawRom,
        string Rom,
        string DefinitionFile,
        string AliasFile,
        bool AliasMatched,
        bool DefinitionExists,
        IReadOnlyList<MameLuaTarget> Targets,
        IReadOnlyList<MameLuaRule> Rules);

    private sealed class MameLuaRule
    {
        public int TargetId { get; set; }
        public long Address { get; init; }
        public long RuntimeAddress { get; set; }
        public string Type { get; init; } = "u8";
        public string Condition { get; init; } = "change";
        public string Action { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Family { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
        public bool NoLog { get; init; }
        public bool NoSurvey { get; init; }
        public long? Min { get; init; }
        public long? Max { get; init; }
        public long? Value { get; init; }
        public long? Mask { get; init; }
        public long? Player { get; init; }
        public bool HasRange => Min.HasValue || Max.HasValue;
    }

    private sealed record MameLuaEntryTable(string Table, string Family);

    private sealed record MameLuaSessionSnapshot(
        string Endpoint,
        bool Connected,
        string Rom,
        string DefinitionFile,
        bool DefinitionExists,
        int WatchCount,
        DateTime LastMessageAt,
        string LastRawLine);
}
