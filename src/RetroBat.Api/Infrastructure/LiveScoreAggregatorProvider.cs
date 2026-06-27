using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class LiveScoreAggregatorProvider : IProvider
{
    private static readonly Regex WeightRegex = new(
        @"value\s*\*\s*(?<weight>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PlayerRegex = new(
        @"(?:^|[_\-\s])p(?<player>[1-4])(?:$|[_\-\s])|player[_\-\s]*(?<player2>[1-4])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OutputDigitRegex = new(
        @"(?:score|digit|counter)[_\-\s]*(?<digit>\d+)|(?<digit2>\d+)[_\-\s]*(?:score|digit)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EntryValueRegex = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>""(?:\\""|[^""])*""|0[xX][0-9A-Fa-f]+|-?\d+(?:\.\d+)?|true|false)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEventBus _eventBus;
    private readonly ILogger<LiveScoreAggregatorProvider> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, ScoreAccumulator> _scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _currentPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScoreDefinition> _scoreDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _subscription;

    public LiveScoreAggregatorProvider(IEventBus eventBus, ILogger<LiveScoreAggregatorProvider> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        _logger.LogInformation("LiveScoreAggregatorProvider started");
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
            if (type.Equals("score.live.changed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (type.Equals("ui.game.started", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                ResetForFrontendEvent();
                return;
            }

            if (type.Equals("retroarch.score", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ingame.score.changed", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("retroarch.memory.changed", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ingame.memory.changed", StringComparison.OrdinalIgnoreCase))
            {
                TryUpdateCurrentPlayer(envelope);
                TryHandleMemoryScore(envelope);
                return;
            }

            if (type.Equals("mame.output.changed", StringComparison.OrdinalIgnoreCase))
            {
                TryHandleMameOutputScore(envelope);
                return;
            }

            if (type.StartsWith("retroachievements.", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("ra.", StringComparison.OrdinalIgnoreCase))
            {
                TryHandleRetroAchievementsScore(envelope);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live score aggregation ignored malformed event {EventType}", envelope.Type);
        }
    }

    private void ResetForFrontendEvent()
    {
        lock (_sync)
        {
            _scores.Clear();
            _currentPlayers.Clear();
        }
    }

    private void TryUpdateCurrentPlayer(EventEnvelope envelope)
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

        if (!IsPlayerStateAction(action, channel, description))
        {
            return;
        }

        var systemId = ReadString(payload, "SystemId");
        var rom = ReadString(payload, "Rom");
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom))
        {
            return;
        }

        var player = ReadPlayer(payload, signal) ??
            NormalizePlayerValue(ReadLong(signal, "Value") ?? ReadLong(payload, "Value")) ??
            InferPlayer(action, channel, description);
        if (player is < 1 or > 4)
        {
            return;
        }

        var source = NormalizeMemorySource(ReadString(payload, "Source"));
        lock (_sync)
        {
            _currentPlayers[BuildSessionKey(source, systemId, rom)] = player;
        }
    }

    private void TryHandleMemoryScore(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        var signal = ReadProperty(payload, "signal") ?? ReadProperty(payload, "Signal");
        var action = FirstNonEmpty(
            ReadString(signal, "Name"),
            ReadString(payload, "actionType"),
            ReadString(payload, "ActionType"));
        var channel = ReadString(signal, "Channel");

        if (!IsScoreAction(action, channel, envelope.Type))
        {
            return;
        }

        var systemId = ReadString(payload, "SystemId");
        var rom = ReadString(payload, "Rom");
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(rom))
        {
            return;
        }

        var value = ReadLong(signal, "Value") ?? ReadLong(payload, "Value");
        if (!value.HasValue)
        {
            return;
        }

        var address = FirstNonEmpty(
            ReadString(signal, "Address"),
            ReadString(payload, "Address"));
        var description = FirstNonEmpty(
            ReadString(signal, "SourceDescription"),
            ReadString(payload, "sourceCategory"),
            ReadString(payload, "SourceCategory"));
        var source = NormalizeMemorySource(ReadString(payload, "Source"));
        var explicitPlayer = ReadPlayer(payload, signal) ?? InferPlayerOrNull(action, description, address);
        var player = explicitPlayer ?? ReadCurrentPlayer(source, systemId, rom) ?? 1;
        var definitionFile = ReadString(payload, "DefinitionFile");
        var rawValueHex = FirstNonEmpty(
            ReadString(signal, "RawValueHex"),
            ReadString(payload, "RawValueHex"));
        var scoreKind = ResolveMemoryScoreKind(definitionFile, address, payload, signal);
        var transform = ResolveScorePartTransform(definitionFile, address, description, value.Value, rawValueHex);
        var sourceKey = NormalizeMemoryScoreGroupKey(action, description, address);
        var partKey = FirstNonEmpty(address, action, sourceKey);
        var confidence = transform.Weight > 1 || description.Contains("score", StringComparison.OrdinalIgnoreCase)
            ? "high"
            : "medium";

        var part = new ScorePartState(
            Key: partKey,
            Address: address,
            RawValueHex: rawValueHex,
            Encoding: transform.Encoding,
            Value: transform.Value,
            Weight: transform.Weight,
            Description: description);

        PublishIfChanged(
            source,
            scoreKind,
            systemId,
            rom,
            player,
            sourceKey,
            definitionFile,
            part,
            confidence);
    }

    private void TryHandleMameOutputScore(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        var machineName = ReadString(payload, "MachineName");
        if (string.IsNullOrWhiteSpace(machineName))
        {
            machineName = ReadString(payload, "machineName");
        }

        if (string.IsNullOrWhiteSpace(machineName))
        {
            return;
        }

        foreach (var signal in EnumerateSignals(payload))
        {
            var key = ReadString(signal, "Key");
            if (!IsPotentialScoreOutput(key))
            {
                continue;
            }

            var value = ReadLong(signal, "Value");
            if (!value.HasValue)
            {
                continue;
            }

            var explicitPlayer = ReadPlayer(signal, payload) ?? InferPlayerOrNull(key, string.Empty, key);
            var player = explicitPlayer ?? ReadCurrentPlayer("mame.output", "arcade", machineName) ?? 1;
            var digitWeight = InferOutputDigitWeight(key);
            var isDigitPart = digitWeight.HasValue && value.Value is >= 0 and <= 9;
            var weight = isDigitPart ? digitWeight.GetValueOrDefault(1) : 1;
            var scoreKey = isDigitPart ? NormalizeOutputDigitGroupKey(key) : key;
            var confidence = isDigitPart ? "medium" : "high";

            var part = new ScorePartState(
                Key: isDigitPart ? key : "direct",
                Address: key,
                RawValueHex: string.Empty,
                Encoding: isDigitPart ? "digit" : string.Empty,
                Value: value.Value,
                Weight: weight,
                Description: "MAME output score signal");

            PublishIfChanged(
                "mame.output",
                "game",
                "arcade",
                machineName,
                player,
                scoreKey,
                string.Empty,
                part,
                confidence);
        }
    }

    private void TryHandleRetroAchievementsScore(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        var score = ReadLong(payload, "Score") ??
            ReadLong(payload, "score") ??
            ReadLong(payload, "SoftcoreScore") ??
            ReadLong(payload, "softcoreScore") ??
            ReadLong(payload, "Points") ??
            ReadLong(payload, "points");
        if (!score.HasValue)
        {
            return;
        }

        var systemId = FirstNonEmpty(
            ReadString(payload, "SystemId"),
            ReadString(payload, "systemId"),
            "retroachievements");
        var rom = FirstNonEmpty(
            ReadString(payload, "Rom"),
            ReadString(payload, "rom"),
            ReadString(payload, "GameId"),
            ReadString(payload, "gameId"),
            "unknown");
        var player = ReadPlayer(payload) ?? 1;
        var scoreKind = ResolveRaScoreKind(envelope.Type, payload);
        var sourceKey = FirstNonEmpty(
            ReadString(payload, "SourceKey"),
            ReadString(payload, "sourceKey"),
            ReadString(payload, "LeaderboardId"),
            ReadString(payload, "leaderboardId"),
            ReadString(payload, "AchievementId"),
            ReadString(payload, "achievementId"),
            scoreKind.ToUpperInvariant());

        var part = new ScorePartState(
            Key: sourceKey,
            Address: string.Empty,
            RawValueHex: string.Empty,
            Encoding: scoreKind,
            Value: score.Value,
            Weight: 1,
            Description: envelope.Type ?? "RetroAchievements score");

        PublishIfChanged(
            "retroachievements",
            scoreKind,
            systemId,
            rom,
            player,
            sourceKey,
            string.Empty,
            part,
            "high");
    }

    private void PublishIfChanged(
        string source,
        string scoreKind,
        string systemId,
        string rom,
        int player,
        string sourceKey,
        string definitionFile,
        ScorePartState part,
        string confidence)
    {
        ScoreLiveEvent? scoreEvent = null;
        lock (_sync)
        {
            var accumulatorKey = BuildAccumulatorKey(source, scoreKind, systemId, rom, player, sourceKey);
            if (!_scores.TryGetValue(accumulatorKey, out var accumulator))
            {
                accumulator = new ScoreAccumulator(source, systemId, rom, player, sourceKey);
                _scores[accumulatorKey] = accumulator;
            }

            accumulator.DefinitionFile = definitionFile;
            accumulator.Confidence = ChooseConfidence(accumulator.Confidence, confidence);
            accumulator.Parts[part.Key] = part;

            var score = accumulator.Parts.Values.Sum(item => item.Value * item.Weight);
            if (score < 0 || score == accumulator.LastPublishedScore)
            {
                return;
            }

            accumulator.LastPublishedScore = score;
            scoreEvent = new ScoreLiveEvent
            {
                Source = source,
                ScoreKind = scoreKind,
                SourceKey = sourceKey,
                SystemId = systemId,
                Rom = rom,
                Player = player,
                Score = score,
                RawValue = part.Value,
                Composed = accumulator.Parts.Count > 1 || accumulator.Parts.Values.Any(item => item.Weight != 1),
                Confidence = accumulator.Confidence,
                DefinitionFile = accumulator.DefinitionFile,
                Ts = DateTime.UtcNow,
                Parts = accumulator.Parts.Values
                    .OrderBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new ScoreLivePart
                    {
                        Key = item.Key,
                        Address = item.Address,
                        RawValueHex = item.RawValueHex,
                        Encoding = item.Encoding,
                        Value = item.Value,
                        Weight = item.Weight,
                        Contribution = item.Value * item.Weight,
                        Description = item.Description
                    })
                    .ToList()
            };
        }

        if (scoreEvent is not null)
        {
            _ = _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "score.live.changed",
                Payload = scoreEvent
            });
        }
    }

    private static bool IsScoreAction(string? action, string channel, string eventType)
    {
        if (string.Equals(action, "SCORE_STATE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(channel, "SCORE", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(action) &&
            action.Contains("SCORE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return eventType.Contains("score", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(action) &&
            action.Contains("SCORE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "COIN_GAIN", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "COIN_LOSE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "MONEY_STATE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayerStateAction(string? action, string channel, string description)
    {
        return IsPlayerText(action) ||
            IsPlayerText(channel) ||
            IsPlayerText(description);

        static bool IsPlayerText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("PLAYER", StringComparison.OrdinalIgnoreCase) &&
                (value.Contains("STATE", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("CURRENT", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("PLAYER", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("PLAYER_STATE", StringComparison.OrdinalIgnoreCase));
        }
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

    private static string NormalizeMemoryScoreGroupKey(string? action, string description, string address)
    {
        if (string.Equals(action, "SCORE_STATE", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("score", StringComparison.OrdinalIgnoreCase))
        {
            return "SCORE_STATE";
        }

        return string.IsNullOrWhiteSpace(address) ? action ?? "SCORE_STATE" : address;
    }

    private static string ResolveRaScoreKind(string? eventType, object? payload)
    {
        var explicitKind = FirstNonEmpty(
            ReadString(payload, "ScoreKind"),
            ReadString(payload, "scoreKind"));
        if (!string.IsNullOrWhiteSpace(explicitKind))
        {
            return explicitKind.Trim().ToLowerInvariant();
        }

        var type = eventType ?? string.Empty;
        if (type.Contains("leaderboard", StringComparison.OrdinalIgnoreCase))
        {
            return "leaderboard";
        }

        return "retroachievements";
    }

    private static long InferWeight(string description)
    {
        var match = WeightRegex.Match(description);
        return match.Success && long.TryParse(match.Groups["weight"].Value, out var weight) && weight > 0
            ? weight
            : 1;
    }

    private ScorePartTransform ResolveScorePartTransform(
        string definitionFile,
        string address,
        string description,
        long value,
        string rawValueHex)
    {
        var explicitWeight = InferWeight(description);
        if (explicitWeight > 1)
        {
            return new ScorePartTransform(value, explicitWeight, "formula");
        }

        if (TryResolveScoreMask(description, rawValueHex, value, out var masked))
        {
            return masked;
        }

        if (!TryParseAddress(address, out var numericAddress))
        {
            return new ScorePartTransform(value, explicitWeight, string.Empty);
        }

        var definition = GetScoreDefinition(definitionFile);
        var rule = definition.Rules.FirstOrDefault(item => item.Address == numericAddress);
        if (rule == null)
        {
            return new ScorePartTransform(value, explicitWeight, string.Empty);
        }

        if (TryResolveScoreMask(rule.ScoreMask, rawValueHex, value, out var ruleMasked))
        {
            var encoding = string.IsNullOrWhiteSpace(rule.ScoreEncoding) ? ruleMasked.Encoding : rule.ScoreEncoding;
            return ruleMasked with { Encoding = encoding };
        }

        var scoreRules = definition.Rules
            .Where(item =>
                item.Condition.Equals("change", StringComparison.OrdinalIgnoreCase) &&
                item.Action.Equals("SCORE_STATE", StringComparison.OrdinalIgnoreCase) &&
                item.Description.Equals(rule.Description, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Address)
            .ToList();

        if (scoreRules.Count <= 1 ||
            !scoreRules.All(item => item.Type.Equals("u8", StringComparison.OrdinalIgnoreCase)) ||
            !AreContiguous(scoreRules.Select(item => item.Address)))
        {
            return new ScorePartTransform(value, explicitWeight, string.Empty);
        }

        var index = scoreRules.FindIndex(item => item.Address == numericAddress);
        if (index < 0)
        {
            return new ScorePartTransform(value, explicitWeight, string.Empty);
        }

        var bcdValue = DecodeBcdByte(rawValueHex, value);
        var weight = Pow10((scoreRules.Count - index - 1) * 2);
        return new ScorePartTransform(bcdValue, weight, "bcd-pairs");
    }

    private string ResolveMemoryScoreKind(string definitionFile, string address, object? payload, object? signal)
    {
        var explicitKind = FirstNonEmpty(
            ReadString(signal, "ScoreKind"),
            ReadString(signal, "scoreKind"),
            ReadString(payload, "ScoreKind"),
            ReadString(payload, "scoreKind"));
        if (!string.IsNullOrWhiteSpace(explicitKind))
        {
            return explicitKind.Trim().ToLowerInvariant();
        }

        if (!TryParseAddress(address, out var numericAddress))
        {
            return "game";
        }

        var definition = GetScoreDefinition(definitionFile);
        var rule = definition.Rules.FirstOrDefault(item => item.Address == numericAddress);
        return string.IsNullOrWhiteSpace(rule?.ScoreKind)
            ? "game"
            : rule.ScoreKind.Trim().ToLowerInvariant();
    }

    private static bool TryResolveScoreMask(
        string description,
        string rawValueHex,
        long fallbackValue,
        out ScorePartTransform transform)
    {
        transform = new ScorePartTransform(fallbackValue, 1, string.Empty);
        var match = Regex.Match(
            description,
            @"(?<mask>[0X]{0,12}XX[0X]{0,12})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var mask = match.Groups["mask"].Value.ToUpperInvariant();
        var xxIndex = mask.IndexOf("XX", StringComparison.Ordinal);
        if (xxIndex < 0)
        {
            return false;
        }

        var zeroesAfter = mask[(xxIndex + 2)..].Count(c => c == '0');
        transform = new ScorePartTransform(DecodeBcdByte(rawValueHex, fallbackValue), Pow10(zeroesAfter), "score-mask");
        return true;
    }

    private static bool AreContiguous(IEnumerable<long> addresses)
    {
        var ordered = addresses.OrderBy(item => item).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i] != ordered[i - 1] + 1)
            {
                return false;
            }
        }

        return true;
    }

    private ScoreDefinition GetScoreDefinition(string definitionFile)
    {
        if (string.IsNullOrWhiteSpace(definitionFile) || !File.Exists(definitionFile))
        {
            return ScoreDefinition.Empty;
        }

        lock (_sync)
        {
            if (_scoreDefinitions.TryGetValue(definitionFile, out var cached))
            {
                return cached;
            }

            var definition = ParseScoreDefinition(File.ReadAllText(definitionFile));
            _scoreDefinitions[definitionFile] = definition;
            return definition;
        }
    }

    private static ScoreDefinition ParseScoreDefinition(string text)
    {
        var rules = new List<ScoreRule>();
        foreach (var entry in ExtractEntryTables(text))
        {
            var values = ParseEntryValues(entry);
            if (!TryParseAddress(values.GetValueOrDefault("address"), out var address))
            {
                continue;
            }

            var action = GetEntryString(values, "action").Trim().ToUpperInvariant();
            if (!action.Equals("SCORE_STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rules.Add(new ScoreRule(
                address,
                GetEntryString(values, "type", "u8"),
                GetEntryString(values, "condition", "change"),
                action,
                GetEntryString(values, "desc", action),
                GetEntryString(values, "score_kind", "game"),
                GetEntryString(values, "score_mask"),
                GetEntryString(values, "score_encoding")));
        }

        return new ScoreDefinition(rules);
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

    private static long DecodeBcdByte(string rawValueHex, long fallback)
    {
        var raw = rawValueHex.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[2..];
        }

        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return fallback;
        }

        if (raw.Length > 2)
        {
            raw = raw[^2..];
        }

        if (raw.All(c => c is >= '0' and <= '9') &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bcd))
        {
            return bcd;
        }

        return fallback;
    }

    private static long Pow10(int exponent)
    {
        var value = 1L;
        for (var i = 0; i < exponent; i++)
        {
            value *= 10;
        }

        return value;
    }

    private static int InferPlayer(params string?[] values)
    {
        return InferPlayerOrNull(values) ?? 1;
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

    private int? ReadCurrentPlayer(string source, string systemId, string rom)
    {
        lock (_sync)
        {
            return _currentPlayers.TryGetValue(BuildSessionKey(source, systemId, rom), out var player)
                ? player
                : null;
        }
    }

    private static int? ReadPlayer(params object?[] sources)
    {
        foreach (var source in sources)
        {
            var player =
                ReadLong(source, "Player") ??
                ReadLong(source, "player") ??
                ReadLong(source, "PlayerIndex") ??
                ReadLong(source, "playerIndex") ??
                ReadLong(source, "PlayerId") ??
                ReadLong(source, "playerId");

            if (player is >= 1 and <= 4)
            {
                return (int)player.Value;
            }
        }

        return null;
    }

    private static int? NormalizePlayerValue(long? value)
    {
        return value switch
        {
            >= 1 and <= 4 => (int)value.Value,
            >= 0 and <= 3 => (int)value.Value + 1,
            _ => null
        };
    }

    private static bool IsPotentialScoreOutput(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim().ToLowerInvariant();
        if (normalized.Contains("lamp") ||
            normalized.Contains("led") ||
            normalized.Contains("light") ||
            normalized.Contains("start") ||
            normalized.Contains("coin"))
        {
            return false;
        }

        return normalized.Contains("score") ||
            normalized.Contains("digit") ||
            normalized.Contains("counter");
    }

    private static long? InferOutputDigitWeight(string key)
    {
        var match = OutputDigitRegex.Match(key);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups["digit"].Success ? match.Groups["digit"].Value : match.Groups["digit2"].Value;
        if (!int.TryParse(raw, out var digit) || digit < 0 || digit > 12)
        {
            return null;
        }

        return (long)Math.Pow(10, digit);
    }

    private static string NormalizeOutputDigitGroupKey(string key)
    {
        return OutputDigitRegex.Replace(key, "score_digits").Trim('_', '-', ' ');
    }

    private static string ChooseConfidence(string current, string next)
    {
        static int Rank(string confidence) => confidence.ToLowerInvariant() switch
        {
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };

        return Rank(next) > Rank(current) ? next : current;
    }

    private static string BuildAccumulatorKey(string source, string scoreKind, string systemId, string rom, int player, string sourceKey) =>
        $"{source}|{scoreKind}|{systemId}|{rom}|P{player}|{sourceKey}";

    private static string BuildSessionKey(string source, string systemId, string rom) =>
        $"{source}|{systemId}|{rom}";

    private static IEnumerable<object?> EnumerateSignals(object? payload)
    {
        var signals = ReadProperty(payload, "Signals") ?? ReadProperty(payload, "signals");
        if (signals is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (signals is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }

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

        return source.GetType().GetProperty(propertyName)?.GetValue(source);
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
            JsonElement { ValueKind: JsonValueKind.String } element when long.TryParse(element.GetString(), out var number) => number,
            string text when long.TryParse(text, out var number) => number,
            _ => null
        };
    }

    private sealed class ScoreAccumulator
    {
        public ScoreAccumulator(string source, string systemId, string rom, int player, string sourceKey)
        {
            Source = source;
            SystemId = systemId;
            Rom = rom;
            Player = player;
            SourceKey = sourceKey;
        }

        public string Source { get; }
        public string SystemId { get; }
        public string Rom { get; }
        public int Player { get; }
        public string SourceKey { get; }
        public string DefinitionFile { get; set; } = string.Empty;
        public string Confidence { get; set; } = "low";
        public long? LastPublishedScore { get; set; }
        public Dictionary<string, ScorePartState> Parts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ScorePartState(
        string Key,
        string Address,
        string RawValueHex,
        string Encoding,
        long Value,
        long Weight,
        string Description);

    private sealed record ScorePartTransform(long Value, long Weight, string Encoding);

    private sealed record ScoreRule(long Address, string Type, string Condition, string Action, string Description, string ScoreKind, string ScoreMask, string ScoreEncoding);

    private sealed record ScoreDefinition(IReadOnlyList<ScoreRule> Rules)
    {
        public static ScoreDefinition Empty { get; } = new(Array.Empty<ScoreRule>());
    }
}
