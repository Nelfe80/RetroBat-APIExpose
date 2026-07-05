using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class LiveTimerAggregatorProvider : IProvider
{
    // The second component ticks ~1/s while a level clock is running. If no second update
    // arrives within this window the timer is treated as paused/inactive and not re-composed.
    private static readonly TimeSpan SecondFreshnessGrace = TimeSpan.FromSeconds(3);

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
    private readonly Dictionary<string, LevelTimerState> _levelTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimerDefinition> _timerDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _progressionValues = new(StringComparer.OrdinalIgnoreCase);
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

            if (type.Equals("retroarch.action", StringComparison.OrdinalIgnoreCase))
            {
                TryResetLevelTimerForProgression(envelope.Payload);
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
            _levelTimers.Clear();
            _progressionValues.Clear();
        }
    }

    private void TryResetLevelTimerForProgression(object? payload)
    {
        var action = ReadString(payload, "ActionType");
        if (!action.StartsWith("PROGRESSION_", StringComparison.OrdinalIgnoreCase)) return;
        var value = ReadLong(payload, "Value");
        var systemId = ReadString(payload, "SystemId");
        var rom = ReadString(payload, "Rom");
        if (value == null || string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom)) return;

        var progressionKey = $"{systemId}|{rom}|{action}";
        lock (_sync)
        {
            if (_progressionValues.TryGetValue(progressionKey, out var previous) && previous == value.Value) return;
            _progressionValues[progressionKey] = value.Value;

            // Level changed: drop the level timer's accumulated minute/second so the next
            // level starts from 00:00 (the minute is not carried over across levels).
            foreach (var key in _levelTimers
                         .Where(pair => pair.Value.Role.Equals("level", StringComparison.OrdinalIgnoreCase) &&
                                        pair.Value.SystemId.Equals(systemId, StringComparison.OrdinalIgnoreCase) &&
                                        pair.Value.Rom.Equals(rom, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
                _levelTimers.Remove(key);
            foreach (var key in _lastPublishedValues
                         .Where(pair => pair.Key.Contains($"|{systemId}|{rom}|", StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
                _lastPublishedValues.Remove(key);
        }

        _logger.LogInformation("Reset level timer composition for {SystemId}/{Rom} on {Action}={Value}", systemId, rom, action, value);
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
        var direction = FirstKnownOrDefault("unknown", definitionRule?.Direction ?? string.Empty, ResolveTimerDirection(action, description));
        var role = FirstKnownOrDefault("unknown", definitionRule?.Role ?? string.Empty, ResolveTimerRole(action, description));
        var unit = FirstKnownOrDefault("unknown", definitionRule?.Unit ?? string.Empty, ResolveTimerUnit(action, description, direction));
        var timerKind = FirstKnownOrDefault("game", definitionRule?.Kind ?? string.Empty, ResolveTimerKind(action, description));

        var timerEvent = new TimerLiveEvent
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
            MaxValue = definitionRule?.MaxValue,
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
        };
        var composed = ComposeTimerComponents(timerEvent);
        if (composed != null)
        {
            PublishIfChanged(composed);
        }
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

    private TimerLiveEvent? ComposeTimerComponents(TimerLiveEvent timerEvent)
    {
        if (!timerEvent.Source.Equals("retroarch.wrapper", StringComparison.OrdinalIgnoreCase) &&
            !timerEvent.Source.Equals("mame.lua", StringComparison.OrdinalIgnoreCase) &&
            !timerEvent.Source.Equals("ingame.memory", StringComparison.OrdinalIgnoreCase))
        {
            return timerEvent;
        }

        var isMinute = IsMinuteComponent(timerEvent);
        var isSecond = IsSecondComponent(timerEvent);
        if (!isMinute && !isSecond)
        {
            // Not part of a split MM:SS clock (single-value timer, effect, countdown…) — pass through.
            return timerEvent;
        }

        var now = DateTime.UtcNow;
        var baseKey = BuildTimerComponentBaseKey(timerEvent);
        lock (_sync)
        {
            if (!_levelTimers.TryGetValue(baseKey, out var state))
            {
                state = new LevelTimerState();
                _levelTimers[baseKey] = state;
            }
            state.SystemId = timerEvent.SystemId;
            state.Rom = timerEvent.Rom;
            if (timerEvent.TimerRole.Length > 0 && !timerEvent.TimerRole.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                state.Role = timerEvent.TimerRole;

            // The second is the fast-moving part: it drives the clock and its freshness tells
            // us the timer is active. The minute is slow and authoritative from its own change
            // events (it emits 0 on a level reset and n+1 on a real rollover) — so we keep its
            // last known value with no expiry and never infer a rollover.
            if (isSecond)
            {
                if (timerEvent.Value < 0) return null;
                if (timerEvent.Value > 59)
                {
                    // The "second" slot actually carries a total-seconds value (no minute split).
                    return state.HasMinute ? null : timerEvent;
                }

                state.SecondValue = timerEvent.Value;
                state.SecondUpdatedAtUtc = now;
                state.SecondEvent = timerEvent;

                if (state.HasMinute)
                    return BuildComposedFromState(state, timerEvent, now);

                // Lone second (minute never seen): only present as MM:SS for an elapsed level
                // clock; a countdown/other "second" timer is passed through untouched.
                return CanComposeWithImplicitZeroMinute(timerEvent)
                    ? BuildComposedFromState(state, timerEvent, now)
                    : timerEvent;
            }

            // Minute part: keep its last known value; it is authoritative for the minute digit.
            if (timerEvent.Value < 0 || timerEvent.Value > 99) return null;
            state.MinuteValue = timerEvent.Value;
            state.HasMinute = true;
            state.MinuteEvent = timerEvent;

            // A minute update alone is not a displayable time; compose only with a fresh second.
            return IsSecondFresh(state, now)
                ? BuildComposedFromState(state, timerEvent, now)
                : null;
        }
    }

    private TimerLiveEvent BuildComposedFromState(LevelTimerState state, TimerLiveEvent trigger, DateTime now)
    {
        var minuteValue = state.HasMinute ? state.MinuteValue : 0;
        return BuildComposedTimer(trigger, state.MinuteEvent, minuteValue, state.SecondEvent ?? trigger, now);
    }

    private bool IsSecondFresh(LevelTimerState state, DateTime now)
        => state.SecondEvent != null && now - state.SecondUpdatedAtUtc <= SecondFreshnessGrace;

    private static TimerLiveEvent BuildComposedTimer(TimerLiveEvent trigger, TimerLiveEvent? minute, long minuteValue, TimerLiveEvent second, DateTime now)
    {
        var parts = minute == null
            ? second.Parts.ToList()
            : minute.Parts.Concat(second.Parts).ToList();

        return new TimerLiveEvent
        {
            Source = trigger.Source,
            TimerKind = FirstKnown(trigger.TimerKind, minute?.TimerKind ?? string.Empty, second.TimerKind, "game"),
            TimerRole = FirstKnown(trigger.TimerRole, minute?.TimerRole ?? string.Empty, second.TimerRole, "level"),
            SourceKey = "COMPOSED_" + BuildCompositeSourceKey(minute, second),
            SystemId = trigger.SystemId,
            Rom = trigger.Rom,
            Player = trigger.Player,
            Value = minuteValue * 60 + second.Value,
            RawValue = trigger.Value,
            Direction = FirstKnown(trigger.Direction, second.Direction, minute?.Direction ?? string.Empty, "elapsed"),
            Unit = "seconds",
            Confidence = "high",
            DefinitionFile = trigger.DefinitionFile,
            Ts = now,
            Parts = parts
        };
    }

    private static bool CanComposeWithImplicitZeroMinute(TimerLiveEvent second)
    {
        return second.TimerRole.Equals("level", StringComparison.OrdinalIgnoreCase) &&
            second.Direction.Equals("elapsed", StringComparison.OrdinalIgnoreCase) &&
            (second.Unit.Equals("second", StringComparison.OrdinalIgnoreCase) ||
             second.Unit.Equals("seconds", StringComparison.OrdinalIgnoreCase) ||
             second.Unit.Equals("s", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildCompositeSourceKey(TimerLiveEvent? minute, TimerLiveEvent second)
    {
        var secondKey = NormalizeCompositeSourceKey(second.SourceKey);
        var minuteKey = NormalizeCompositeSourceKey(minute?.SourceKey ?? string.Empty);
        if (secondKey.Length > 0 && (minuteKey.Length == 0 || secondKey.Equals(minuteKey, StringComparison.OrdinalIgnoreCase)))
        {
            return secondKey;
        }

        return FirstNonEmpty(secondKey, minuteKey, "LEVEL_TIMER");
    }

    private static string NormalizeCompositeSourceKey(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return string.Empty;
        }

        var key = sourceKey.Trim();
        var addressIndex = key.IndexOf('@');
        if (addressIndex > 0)
        {
            key = key[..addressIndex];
        }

        return StripTimerPartSuffix(key).Trim().ToUpperInvariant();
    }

    private static bool IsMinuteComponent(TimerLiveEvent item)
    {
        var text = $"{item.SourceKey} {string.Join(' ', item.Parts.Select(part => part.Description))}";
        return item.Unit.Equals("minute", StringComparison.OrdinalIgnoreCase) ||
               item.Unit.Equals("minutes", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("minute", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(text, @"(^|[_\-\s])min(?:ute)?s?($|[_\-\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsSecondComponent(TimerLiveEvent item)
    {
        var text = $"{item.SourceKey} {string.Join(' ', item.Parts.Select(part => part.Description))}";
        return item.Unit.Equals("second", StringComparison.OrdinalIgnoreCase) ||
               item.Unit.Equals("seconds", StringComparison.OrdinalIgnoreCase) ||
               item.Unit.Equals("s", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("second", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(text, @"(^|[_\-\s])sec(?:ond)?s?($|[_\-\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string BuildTimerComponentBaseKey(TimerLiveEvent timerEvent)
        => $"{timerEvent.Source}|{timerEvent.SystemId}|{timerEvent.Rom}|P{timerEvent.Player}|{NormalizeKnown(timerEvent.TimerKind)}";

    private static string NormalizeKnown(string value)
        => string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? "game"
            : value.Trim().ToLowerInvariant();

    private static string FirstKnown(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value) && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                return value.Trim();
        return string.Empty;
    }

    private static string FirstKnownOrDefault(string fallback, params string[] values)
    {
        var known = FirstKnown(values);
        return string.IsNullOrWhiteSpace(known) ? fallback : known;
    }

    private static string StripTimerPartSuffix(string value)
        => Regex.Replace(value ?? string.Empty, @"(?:^|[_\-\s])(minutes?|mins?|seconds?|secs?)$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim('_', '-', ' ');

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

    private static string ResolveTimerUnit(string? action, string description, string direction)
    {
        var text = $"{action} {description}";
        if (text.Contains("frame", StringComparison.OrdinalIgnoreCase))
        {
            return "frame";
        }

        if (text.Contains("second", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("secs", StringComparison.OrdinalIgnoreCase))
        {
            return "second";
        }

        if (text.Contains("minute", StringComparison.OrdinalIgnoreCase))
        {
            return "minute";
        }

        if (text.Contains("tick", StringComparison.OrdinalIgnoreCase))
        {
            return "tick";
        }

        if ((direction.Equals("countdown", StringComparison.OrdinalIgnoreCase) ||
             direction.Equals("inverted_countdown", StringComparison.OrdinalIgnoreCase)) &&
            text.Contains("timer", StringComparison.OrdinalIgnoreCase))
        {
            return "second";
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
                GetEntryString(values, "timer_unit"),
                GetEntryLong(values, "timer_max") ??
                GetEntryLong(values, "timer_max_value") ??
                GetEntryLong(values, "max_value") ??
                GetEntryLong(values, "max")));
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

    private static long? GetEntryLong(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var raw) && TryParseAddress(raw, out var value)
            ? value
            : null;
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
        var normalizedAddress = NormalizeAddressForKey(address);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var normalizedAction = action.Trim().ToUpperInvariant();
            return normalizedAddress.Length == 0
                ? normalizedAction
                : $"{normalizedAction}@{normalizedAddress}";
        }

        return normalizedAddress.Length == 0 ? "TIMER" : normalizedAddress;
    }

    private static string NormalizeAddressForKey(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var normalized = address.Trim().ToUpperInvariant();
        return normalized.StartsWith("0X", StringComparison.Ordinal)
            ? normalized
            : "0X" + normalized.TrimStart('$');
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

    // Accumulated state of one split MM:SS level clock. The minute is kept with no expiry
    // (authoritative from its own change events); the second carries an activity timestamp.
    private sealed class LevelTimerState
    {
        public string SystemId = string.Empty;
        public string Rom = string.Empty;
        public string Role = string.Empty;
        public long MinuteValue;
        public bool HasMinute;
        public TimerLiveEvent? MinuteEvent;
        public long SecondValue;
        public DateTime SecondUpdatedAtUtc;
        public TimerLiveEvent? SecondEvent;
    }

    private sealed record TimerRule(long Address, string Action, string Kind, string Role, string Direction, string Unit, long? MaxValue);

    private sealed record TimerDefinition(IReadOnlyList<TimerRule> Rules)
    {
        public static TimerDefinition Empty { get; } = new(Array.Empty<TimerRule>());
    }
}
