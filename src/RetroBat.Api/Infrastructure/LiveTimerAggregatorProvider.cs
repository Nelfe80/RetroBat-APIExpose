using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class LiveTimerAggregatorProvider : IProvider
{
    private static readonly Regex PlayerRegex = new(
        @"(?:^|[_\-\s])p(?<player>[1-4])(?:$|[_\-\s])|player[_\-\s]*(?<player2>[1-4])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EntryValueRegex = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>""(?:\\""|[^""])*""|0[xX][0-9A-Fa-f]+|-?\d+(?:\.\d+)?|true|false)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEventBus _eventBus;
    private readonly ILogger<LiveTimerAggregatorProvider> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _lastPublishedValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimerDefinition> _timerDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _subscription;

    public LiveTimerAggregatorProvider(IEventBus eventBus, ILogger<LiveTimerAggregatorProvider> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        _logger.LogInformation("LiveTimerAggregatorProvider started");
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
            var type = envelope.Type ?? string.Empty;
            if (type.Equals("timer.live.changed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (type.Equals("ui.game.started", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                return;
            }

            if (type.Equals("retroarch.memory.changed", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ingame.memory.changed", StringComparison.OrdinalIgnoreCase))
            {
                TryHandleMemoryTimer(envelope);
                return;
            }

            if (type.StartsWith("retroachievements.", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("ra.", StringComparison.OrdinalIgnoreCase))
            {
                TryHandleRetroAchievementsTimer(envelope);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live timer aggregation ignored malformed event {EventType}", envelope.Type);
        }
    }

    private void Reset()
    {
        lock (_sync)
        {
            _lastPublishedValues.Clear();
        }
    }

    private void TryHandleMemoryTimer(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        var signal = ReadProperty(payload, "signal") ?? ReadProperty(payload, "Signal");
        var action = FirstNonEmpty(
            ReadString(signal, "Name"),
            ReadString(payload, "actionType"),
            ReadString(payload, "ActionType"));
        var channel = ReadString(signal, "Channel");
        var description = FirstNonEmpty(
            ReadString(signal, "SourceDescription"),
            ReadString(payload, "sourceCategory"),
            ReadString(payload, "SourceCategory"));

        if (!IsTimerAction(action, channel, description))
        {
            return;
        }

        var value = ReadLong(signal, "Value") ?? ReadLong(payload, "Value");
        if (!value.HasValue)
        {
            return;
        }

        var systemId = ReadString(payload, "SystemId");
        var rom = ReadString(payload, "Rom");
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom))
        {
            return;
        }

        var address = FirstNonEmpty(
            ReadString(signal, "Address"),
            ReadString(payload, "Address"));
        var rawValueHex = FirstNonEmpty(
            ReadString(signal, "RawValueHex"),
            ReadString(payload, "RawValueHex"));
        var source = NormalizeMemorySource(ReadString(payload, "Source"));
        var player = ReadPlayer(payload, signal) ?? InferPlayerOrNull(action, description, address);
        var sourceKey = NormalizeTimerSourceKey(action, description, address);
        var definitionFile = ReadString(payload, "DefinitionFile");
        var definitionRule = ResolveTimerRule(definitionFile, address, action);
        var direction = FirstNonEmpty(definitionRule?.Direction, ResolveTimerDirection(action, description));
        var role = FirstNonEmpty(definitionRule?.Role, ResolveTimerRole(action, description));
        var unit = FirstNonEmpty(definitionRule?.Unit, ResolveTimerUnit(description));
        var timerKind = FirstNonEmpty(definitionRule?.Kind, ResolveTimerKind(action, description));

        PublishIfChanged(new TimerLiveEvent
        {
            Source = source,
            TimerKind = timerKind,
            TimerRole = role,
            SourceKey = sourceKey,
            SystemId = systemId,
            Rom = rom,
            Player = player,
            Value = value.Value,
            RawValue = value.Value,
            Direction = direction,
            Unit = unit,
            Confidence = description.Contains("timer", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("time", StringComparison.OrdinalIgnoreCase)
                    ? "high"
                    : "medium",
            DefinitionFile = definitionFile,
            Ts = DateTime.UtcNow,
            Parts =
            [
                new TimerLivePart
                {
                    Key = FirstNonEmpty(address, action, sourceKey),
                    Address = address,
                    RawValueHex = rawValueHex,
                    Encoding = string.Empty,
                    Value = value.Value,
                    Description = description
                }
            ]
        });
    }

    private void TryHandleRetroAchievementsTimer(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        var value = ReadLong(payload, "Value") ??
            ReadLong(payload, "value") ??
            ReadLong(payload, "Timer") ??
            ReadLong(payload, "timer") ??
            ReadLong(payload, "Progress") ??
            ReadLong(payload, "progress");
        if (!value.HasValue)
        {
            return;
        }

        var type = envelope.Type ?? string.Empty;
        if (!type.Contains("progress", StringComparison.OrdinalIgnoreCase) &&
            !type.Contains("timer", StringComparison.OrdinalIgnoreCase) &&
            !type.Contains("leaderboard", StringComparison.OrdinalIgnoreCase) &&
            !ReadString(payload, "TimerKind").Contains("retroachievements", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var timerKind = type.Contains("leaderboard", StringComparison.OrdinalIgnoreCase)
            ? "leaderboard"
            : "retroachievements";
        var sourceKey = FirstNonEmpty(
            ReadString(payload, "SourceKey"),
            ReadString(payload, "sourceKey"),
            ReadString(payload, "AchievementId"),
            ReadString(payload, "achievementId"),
            ReadString(payload, "LeaderboardId"),
            ReadString(payload, "leaderboardId"),
            timerKind.ToUpperInvariant());

        PublishIfChanged(new TimerLiveEvent
        {
            Source = "retroachievements",
            TimerKind = timerKind,
            TimerRole = timerKind,
            SourceKey = sourceKey,
            SystemId = FirstNonEmpty(ReadString(payload, "SystemId"), ReadString(payload, "systemId"), "retroachievements"),
            Rom = FirstNonEmpty(ReadString(payload, "Rom"), ReadString(payload, "rom"), ReadString(payload, "GameId"), ReadString(payload, "gameId"), "unknown"),
            Player = ReadPlayer(payload),
            Value = value.Value,
            RawValue = value.Value,
            MaxValue = ReadLong(payload, "MaxValue") ?? ReadLong(payload, "maxValue"),
            Direction = FirstNonEmpty(ReadString(payload, "Direction"), ReadString(payload, "direction"), "unknown"),
            Unit = FirstNonEmpty(ReadString(payload, "Unit"), ReadString(payload, "unit"), "unknown"),
            Confidence = "high",
            Ts = DateTime.UtcNow,
            Parts =
            [
                new TimerLivePart
                {
                    Key = sourceKey,
                    Value = value.Value,
                    Description = envelope.Type ?? "RetroAchievements timer"
                }
            ]
        });
    }

    private void PublishIfChanged(TimerLiveEvent timerEvent)
    {
        ApplyDerivedTimerFields(timerEvent);
        var key = BuildTimerKey(timerEvent);
        lock (_sync)
        {
            if (_lastPublishedValues.TryGetValue(key, out var previous) && previous == timerEvent.Value)
            {
                return;
            }

            _lastPublishedValues[key] = timerEvent.Value;
        }

        _ = _eventBus.PublishAsync(new EventEnvelope
        {
            Type = "timer.live.changed",
            Payload = timerEvent
        });
    }

    private static void ApplyDerivedTimerFields(TimerLiveEvent timerEvent)
    {
        if (!timerEvent.MaxValue.HasValue || timerEvent.MaxValue.Value <= 0)
        {
            return;
        }

        var max = timerEvent.MaxValue.Value;
        var clamped = Math.Clamp(timerEvent.Value, 0, max);
        timerEvent.Progress01 = Math.Clamp((double)clamped / max, 0d, 1d);
        if (timerEvent.Direction.Equals("countdown", StringComparison.OrdinalIgnoreCase))
        {
            timerEvent.Remaining = clamped;
            timerEvent.Urgency01 = 1d - timerEvent.Progress01;
        }
        else if (timerEvent.Direction.Equals("inverted_countdown", StringComparison.OrdinalIgnoreCase))
        {
            timerEvent.Remaining = Math.Max(0, max - clamped);
            timerEvent.Urgency01 = timerEvent.Progress01;
        }
    }

    private static bool IsTimerAction(string? action, string channel, string description)
    {
        var text = $"{action} {channel} {description}";
        return text.Contains("TIMER", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TIME", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COUNTDOWN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CLOCK", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTimerKind(string? action, string description)
    {
        var text = $"{action} {description}";
        if (text.Contains("INVINC", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SPEED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SHIELD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COMBO", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COOLDOWN", StringComparison.OrdinalIgnoreCase))
        {
            return "effect";
        }

        return "game";
    }

    private static string ResolveTimerRole(string? action, string description)
    {
        var text = $"{action} {description}";
        if (text.Contains("ROUND", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MATCH", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("VERSUS", StringComparison.OrdinalIgnoreCase))
        {
            return "versus";
        }

        if (text.Contains("PUZZLE", StringComparison.OrdinalIgnoreCase))
        {
            return "puzzle";
        }

        if (text.Contains("LEVEL", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("STAGE", StringComparison.OrdinalIgnoreCase))
        {
            return "level";
        }

        if (ResolveTimerKind(action, description).Equals("effect", StringComparison.OrdinalIgnoreCase))
        {
            return "powerup";
        }

        return "unknown";
    }

    private static string ResolveTimerDirection(string? action, string description)
    {
        var text = $"{action} {description}";
        if (text.Contains("INVERTED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COUNT UP LIMIT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LIMIT INCREASE", StringComparison.OrdinalIgnoreCase))
        {
            return "inverted_countdown";
        }

        if (text.Contains("REMAIN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LEFT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COUNTDOWN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TIME LOW", StringComparison.OrdinalIgnoreCase))
        {
            return "countdown";
        }

        if (text.Contains("ELAPSED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COUNT UP", StringComparison.OrdinalIgnoreCase))
        {
            return "elapsed";
        }

        return "unknown";
    }

    private static string ResolveTimerUnit(string description)
    {
        if (description.Contains("frame", StringComparison.OrdinalIgnoreCase))
        {
            return "frame";
        }

        if (description.Contains("second", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("secs", StringComparison.OrdinalIgnoreCase))
        {
            return "second";
        }

        if (description.Contains("minute", StringComparison.OrdinalIgnoreCase))
        {
            return "minute";
        }

        if (description.Contains("tick", StringComparison.OrdinalIgnoreCase))
        {
            return "tick";
        }

        return "unknown";
    }

    private TimerRule? ResolveTimerRule(string definitionFile, string address, string action)
    {
        if (string.IsNullOrWhiteSpace(definitionFile) ||
            string.IsNullOrWhiteSpace(address) ||
            !TryParseAddress(address, out var numericAddress))
        {
            return null;
        }

        var definition = GetTimerDefinition(definitionFile);
        return definition.Rules.FirstOrDefault(item =>
            item.Address == numericAddress &&
            (string.IsNullOrWhiteSpace(action) || item.Action.Equals(action, StringComparison.OrdinalIgnoreCase)));
    }

    private TimerDefinition GetTimerDefinition(string definitionFile)
    {
        if (string.IsNullOrWhiteSpace(definitionFile) || !File.Exists(definitionFile))
        {
            return TimerDefinition.Empty;
        }

        lock (_sync)
        {
            if (_timerDefinitions.TryGetValue(definitionFile, out var cached))
            {
                return cached;
            }

            var definition = ParseTimerDefinition(File.ReadAllText(definitionFile));
            _timerDefinitions[definitionFile] = definition;
            return definition;
        }
    }

    private static TimerDefinition ParseTimerDefinition(string text)
    {
        var rules = new List<TimerRule>();
        foreach (var entry in ExtractEntryTables(text))
        {
            var values = ParseEntryValues(entry);
            if (!TryParseAddress(values.GetValueOrDefault("address"), out var address))
            {
                continue;
            }

            var action = GetEntryString(values, "action").Trim().ToUpperInvariant();
            if (!IsTimerAction(action, string.Empty, GetEntryString(values, "desc")))
            {
                continue;
            }

            rules.Add(new TimerRule(
                address,
                action,
                GetEntryString(values, "timer_kind"),
                GetEntryString(values, "timer_role"),
                GetEntryString(values, "timer_direction"),
                GetEntryString(values, "timer_unit")));
        }

        return new TimerDefinition(rules);
    }

    private static IEnumerable<string> ExtractEntryTables(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                i = SkipString(text, i);
                continue;
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
                yield return table;
            }
        }
    }

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

    private static string GetEntryString(Dictionary<string, string> values, string key, string fallback = "")
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

    private static bool TryParseAddress(string? raw, out long value)
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

    private static string NormalizeMemorySource(string source)
    {
        if (source.Equals("mame.lua", StringComparison.OrdinalIgnoreCase))
        {
            return "mame.lua";
        }

        if (source.Contains("wrapper", StringComparison.OrdinalIgnoreCase))
        {
            return "retroarch.wrapper";
        }

        return string.IsNullOrWhiteSpace(source) ? "ingame.memory" : source;
    }

    private static string NormalizeTimerSourceKey(string? action, string description, string address)
    {
        if (!string.IsNullOrWhiteSpace(action))
        {
            return action.Trim().ToUpperInvariant();
        }

        return string.IsNullOrWhiteSpace(address) ? "TIMER" : address;
    }

    private static int? ReadPlayer(params object?[] sources)
    {
        foreach (var source in sources)
        {
            var explicitPlayer = ReadLong(source, "Player") ?? ReadLong(source, "player");
            if (explicitPlayer is >= 1 and <= 4)
            {
                return (int)explicitPlayer.Value;
            }
        }

        return null;
    }

    private static int? InferPlayerOrNull(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var match = PlayerRegex.Match(value);
            if (!match.Success)
            {
                continue;
            }

            var raw = match.Groups["player"].Success
                ? match.Groups["player"].Value
                : match.Groups["player2"].Value;
            if (int.TryParse(raw, out var player) && player is >= 1 and <= 4)
            {
                return player;
            }
        }

        return null;
    }

    private static string BuildTimerKey(TimerLiveEvent timerEvent) =>
        $"{timerEvent.Source}|{timerEvent.TimerKind}|{timerEvent.SystemId}|{timerEvent.Rom}|P{timerEvent.Player}|{timerEvent.SourceKey}";

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

        var propertyInfo = source.GetType().GetProperty(propertyName);
        return propertyInfo?.GetValue(source);
    }

    private static string ReadString(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            null => string.Empty,
            string text => text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => (element.GetString() ?? string.Empty).Trim(),
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetRawText().Trim(),
            JsonElement { ValueKind: JsonValueKind.True } => bool.TrueString,
            JsonElement { ValueKind: JsonValueKind.False } => bool.FalseString,
            _ => (value.ToString() ?? string.Empty).Trim()
        };
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

    private static long? ReadLong(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private sealed record TimerRule(long Address, string Action, string Kind, string Role, string Direction, string Unit);

    private sealed record TimerDefinition(IReadOnlyList<TimerRule> Rules)
    {
        public static TimerDefinition Empty { get; } = new(Array.Empty<TimerRule>());
    }
}
