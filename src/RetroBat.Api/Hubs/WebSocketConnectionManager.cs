using System.Net.WebSockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Events;

namespace RetroBat.Api.Hubs;

public class WebSocketConnectionManager
{
    private const int LatestWinsBroadcastDelayMs = 20;
    private const int FrontendSelectionBroadcastDelayMs = 35;
    private const double EwmaAlpha = 0.25d;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly List<WebSocketSubscription> _sockets = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _latestWinsLock = new();
    private readonly Dictionary<string, PendingLatestBroadcast> _pendingLatestBroadcasts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LatestWinsThrottleState> _latestWinsStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _latestWinsAcceptedTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _latestWinsAcceptedSequences = new(StringComparer.OrdinalIgnoreCase);
    private string? _retainedPanelStateJson;
    private string? _retainedScoreJson;
    private string? _retainedTimerJson;
    private string? _retainedRetroAchievementsJson;

    public WebSocketConnectionManager(IOptionsMonitor<ApiExposeOptions> options)
    {
        _options = options;
    }

    public async Task AddSocketAsync(WebSocket socket)
    {
        await AddSocketAsync(socket, string.Empty);
    }

    public async Task AddSocketAsync(WebSocket socket, string? stream)
    {
        var normalizedStream = NormalizeStream(stream);
        string? retainedMessage;
        await _lock.WaitAsync();
        try
        {
            _sockets.Add(new WebSocketSubscription(socket, normalizedStream));
            retainedMessage = GetRetainedMessageForStream(normalizedStream);
        }
        finally
        {
            _lock.Release();
        }

        if (!string.IsNullOrWhiteSpace(retainedMessage))
        {
            var sent = await TrySendJsonAsync(socket, retainedMessage);
            if (!sent)
            {
                await RemoveSocketAsync(socket);
            }
        }
    }

    public async Task RemoveSocketAsync(WebSocket socket)
    {
        await _lock.WaitAsync();
        try
        {
            _sockets.RemoveAll(subscription => ReferenceEquals(subscription.Socket, socket));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task BroadcastAsync<T>(T message)
    {
        var esEventDispatchAtUtc = IsEsEventMessage(message) ? DateTime.UtcNow : (DateTime?)null;
        var json = SerializeForBroadcast(message, esEventDispatchAtUtc);
        RetainMessage(message, json);

        if (message is EventEnvelope envelope &&
            TryGetLatestWinsBroadcastKey(envelope, out var latestWinsKey))
        {
            QueueLatestWinsBroadcast(latestWinsKey, envelope, json);
            return;
        }

        await BroadcastSerializedAsync(message, json, esEventDispatchAtUtc);
    }

    private async Task BroadcastSerializedAsync<T>(T message, string json, DateTime? esEventDispatchAtUtc = null)
    {
        var sharedBuffer = Encoding.UTF8.GetBytes(json);
        var timestampEachSend = IsEsEventMessage(message) && esEventDispatchAtUtc.HasValue;
        WebSocketSubscription[] sockets;

        await _lock.WaitAsync();
        try
        {
            sockets = _sockets.ToArray();
        }
        finally
        {
            _lock.Release();
        }

        var disconnected = new List<WebSocket>();
        foreach (var subscription in sockets)
        {
            var socket = subscription.Socket;
            if (socket.State != WebSocketState.Open)
            {
                disconnected.Add(socket);
                continue;
            }

            if (!ShouldSendToStream(subscription.Stream, message))
            {
                continue;
            }

            try
            {
                var buffer = timestampEachSend
                    ? Encoding.UTF8.GetBytes(SerializeForBroadcast(message, esEventDispatchAtUtc, DateTime.UtcNow))
                    : sharedBuffer;
                await SendBytesAsync(socket, buffer);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                disconnected.Add(socket);
            }
        }

        if (disconnected.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            foreach (var socket in disconnected)
            {
                _sockets.RemoveAll(subscription => ReferenceEquals(subscription.Socket, socket));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void QueueLatestWinsBroadcast(string key, EventEnvelope message, string json)
    {
        CancellationTokenSource? previousCts = null;
        var cts = new CancellationTokenSource();
        var pending = new PendingLatestBroadcast(message, json, cts);

        lock (_latestWinsLock)
        {
            if (IsStaleLatestWinsEventLocked(key, message))
            {
                cts.Dispose();
                return;
            }

            MarkLatestWinsEventAcceptedLocked(key, message);
            var state = GetLatestWinsStateLocked(key);

            if (_pendingLatestBroadcasts.TryGetValue(key, out var previous))
            {
                previousCts = previous.CancellationTokenSource;
                state.CoalescedSinceSend++;
            }

            _pendingLatestBroadcasts[key] = pending;
        }

        previousCts?.Cancel();
        _ = Task.Run(() => FlushLatestWinsBroadcastAsync(key, pending), cts.Token);
    }

    private async Task FlushLatestWinsBroadcastAsync(string key, PendingLatestBroadcast pending)
    {
        try
        {
            var delayMs = 0;
            lock (_latestWinsLock)
            {
                delayMs = ResolveLatestWinsBroadcastDelayMs(key, GetLatestWinsStateLocked(key));
            }

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, pending.CancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            pending.CancellationTokenSource.Dispose();
            return;
        }

        lock (_latestWinsLock)
        {
            if (!_pendingLatestBroadcasts.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, pending) ||
                pending.CancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            _pendingLatestBroadcasts.Remove(key);
        }

        try
        {
            var perf = Stopwatch.StartNew();
            await BroadcastSerializedAsync(pending.Message, pending.Json);
            perf.Stop();
            UpdateLatestWinsState(key, pending.Message, perf.Elapsed);
        }
        finally
        {
            pending.CancellationTokenSource.Dispose();
        }
    }

    private static bool TryGetLatestWinsBroadcastKey(EventEnvelope envelope, out string key)
    {
        var type = (envelope.Type ?? string.Empty).Trim().ToLowerInvariant();
        key = type switch
        {
            "ui.system.selected.raw" or "ui.game.selected.raw" => "frontend.selection.raw",
            "ui.system.selected" or "ui.game.selected" => "frontend.selection",
            "panel.state" => "panel.state",
            "cpo.panel.config.selected" => "panel.config.selected",
            "marquee.snapshot" or "marquee.snapshot.updated" => "marquee.snapshot",
            "topper.snapshot" => "topper.snapshot",
            "instruction-card.snapshot" => "instruction-card.snapshot",
            "score.live.changed" => BuildLatestWinsPayloadKey("score.live", envelope.Payload),
            "timer.live.changed" => BuildLatestWinsPayloadKey("timer.live", envelope.Payload),
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(key);
    }

    private int ResolveLatestWinsBroadcastDelayMs(string key, LatestWinsThrottleState state)
    {
        if (!IsAdaptiveVisualKey(key))
        {
            return key.StartsWith("frontend.selection", StringComparison.OrdinalIgnoreCase)
                ? FrontendSelectionBroadcastDelayMs
                : LatestWinsBroadcastDelayMs;
        }

        var now = DateTime.UtcNow;
        if (state.LastSentUtc == default)
        {
            return 0;
        }

        var intervalMs = ResolveAdaptiveVisualIntervalMs(state);
        var elapsedMs = (int)Math.Max(0, (now - state.LastSentUtc).TotalMilliseconds);
        return Math.Max(0, intervalMs - elapsedMs);
    }

    private int ResolveAdaptiveVisualIntervalMs(LatestWinsThrottleState state)
    {
        var options = _options.CurrentValue.WebSocket;
        var floor = Math.Clamp(options.VisualMinIntervalFloorMs, 1, 1000);
        var ceiling = Math.Clamp(options.VisualMinIntervalCeilingMs, floor, 2000);
        if (!options.VisualAdaptiveThrottleEnabled)
        {
            return Math.Clamp(options.VisualFinalFlushMs, floor, ceiling);
        }

        var targetAge = Math.Clamp(options.VisualTargetAgeMs, floor, ceiling);
        var finalFlush = Math.Clamp(options.VisualFinalFlushMs, floor, ceiling);
        var observedAge = state.AverageAgeMs > 0 ? state.AverageAgeMs : targetAge;
        var observedSend = state.AverageSendMs > 0 ? state.AverageSendMs : LatestWinsBroadcastDelayMs;
        var pressure = state.CoalescedSinceSend >= 4 ? targetAge : floor;
        var interval = (int)Math.Round(Math.Max(Math.Max(floor, observedAge * 0.8d), Math.Max(observedSend * 4d, pressure)));
        if (state.CoalescedSinceSend == 0)
        {
            interval = Math.Min(interval, finalFlush);
        }

        return Math.Clamp(interval, floor, ceiling);
    }

    private void UpdateLatestWinsState(string key, EventEnvelope message, TimeSpan sendDuration)
    {
        if (!IsAdaptiveVisualKey(key))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var ageMs = ResolveEventAgeMs(message, now);
        lock (_latestWinsLock)
        {
            var state = GetLatestWinsStateLocked(key);
            state.LastSentUtc = now;
            state.AverageAgeMs = UpdateAverage(state.AverageAgeMs, ageMs);
            state.AverageSendMs = UpdateAverage(state.AverageSendMs, Math.Max(0, sendDuration.TotalMilliseconds));
            state.CoalescedSinceSend = 0;
        }
    }

    private static double UpdateAverage(double current, double observed)
    {
        return current <= 0 ? observed : (current * (1d - EwmaAlpha)) + (observed * EwmaAlpha);
    }

    private LatestWinsThrottleState GetLatestWinsStateLocked(string key)
    {
        if (!_latestWinsStates.TryGetValue(key, out var state))
        {
            state = new LatestWinsThrottleState();
            _latestWinsStates[key] = state;
        }

        return state;
    }

    private bool IsStaleLatestWinsEventLocked(string key, EventEnvelope message)
    {
        if (_options.CurrentValue.WebSocket.VisualStaleDropEnabled)
        {
            var sequence = ReadLongProperty(message.Payload, "Sequence");
            if (sequence > 0 &&
                _latestWinsAcceptedSequences.TryGetValue(key, out var latestSequence) &&
                sequence < latestSequence)
            {
                return true;
            }
        }

        return _latestWinsAcceptedTimestamps.TryGetValue(key, out var latestTs) &&
            NormalizeUtc(message.Ts) < latestTs &&
            ReadLongProperty(message.Payload, "Sequence") <= 0;
    }

    private void MarkLatestWinsEventAcceptedLocked(string key, EventEnvelope message)
    {
        var sequence = ReadLongProperty(message.Payload, "Sequence");
        if (sequence > 0 &&
            (!_latestWinsAcceptedSequences.TryGetValue(key, out var latestSequence) || sequence > latestSequence))
        {
            _latestWinsAcceptedSequences[key] = sequence;
        }

        var ts = NormalizeUtc(message.Ts);
        if (!_latestWinsAcceptedTimestamps.TryGetValue(key, out var latestTs) || ts > latestTs)
        {
            _latestWinsAcceptedTimestamps[key] = ts;
        }
    }

    private static bool IsAdaptiveVisualKey(string key)
    {
        return key.StartsWith("frontend.selection", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "panel.state", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "panel.config.selected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "marquee.snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "topper.snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "instruction-card.snapshot", StringComparison.OrdinalIgnoreCase);
    }

    private string? GetRetainedMessageForStream(string stream)
    {
        if (string.Equals(stream, "panel", StringComparison.OrdinalIgnoreCase))
        {
            return _retainedPanelStateJson;
        }

        if (string.Equals(stream, "score", StringComparison.OrdinalIgnoreCase))
        {
            return _retainedScoreJson;
        }

        if (string.Equals(stream, "timer", StringComparison.OrdinalIgnoreCase))
        {
            return _retainedTimerJson;
        }

        if (string.Equals(stream, "retroachievements", StringComparison.OrdinalIgnoreCase))
        {
            return _retainedRetroAchievementsJson;
        }

        return null;
    }

    private void RetainMessage<T>(T message, string json)
    {
        if (message is EventEnvelope envelope &&
            string.Equals(envelope.Type, "panel.state", StringComparison.OrdinalIgnoreCase) &&
            IsRetainablePanelState(envelope))
        {
            _retainedPanelStateJson = json;
        }

        if (message is EventEnvelope scoreEnvelope &&
            string.Equals(scoreEnvelope.Type, "score.live.changed", StringComparison.OrdinalIgnoreCase))
        {
            _retainedScoreJson = json;
        }

        if (message is EventEnvelope timerEnvelope &&
            string.Equals(timerEnvelope.Type, "timer.live.changed", StringComparison.OrdinalIgnoreCase))
        {
            _retainedTimerJson = json;
        }

        if (message is EventEnvelope raEnvelope &&
            (string.Equals(raEnvelope.Type, "retroachievements.session.updated", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(raEnvelope.Type, "retroachievements.game.identified", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(raEnvelope.Type, "retroachievements.patch.loaded", StringComparison.OrdinalIgnoreCase)))
        {
            _retainedRetroAchievementsJson = json;
        }
    }

    private static bool IsRetainablePanelState(EventEnvelope envelope)
    {
        if (string.Equals(envelope.NodeId, "panel-curator", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = envelope.Payload;
        var source = ReadStringProperty(payload, "Source");
        if (source.StartsWith("panel_curator.", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("panel-curator.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var activePanel = ReadProperty(payload, "ActivePanel") ?? ReadProperty(payload, "activePanel");
        var activePanelId = ReadStringProperty(activePanel, "Id");
        return !string.Equals(activePanelId, "panel-curator-preview", StringComparison.OrdinalIgnoreCase);
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

        var propertyInfo = source.GetType().GetProperty(propertyName);
        return propertyInfo?.GetValue(source);
    }

    private static long ReadLongProperty(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            null => 0,
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when long.TryParse(element.GetString(), out var number) => number,
            string text when long.TryParse(text, out var number) => number,
            _ => 0
        };
    }

    private static DateTime? ReadDateTimeProperty(object? source, string propertyName)
    {
        var value = ReadProperty(source, propertyName);
        return value switch
        {
            DateTime date => NormalizeUtc(date),
            DateTimeOffset date => date.UtcDateTime,
            JsonElement { ValueKind: JsonValueKind.String } element when DateTime.TryParse(element.GetString(), out var date) => NormalizeUtc(date),
            string text when DateTime.TryParse(text, out var date) => NormalizeUtc(date),
            _ => null
        };
    }

    private static double ResolveEventAgeMs(EventEnvelope message, DateTime nowUtc)
    {
        var latency = ReadProperty(message.Payload, "Latency");
        var receivedAtUtc = ReadDateTimeProperty(latency, "ReceivedAtUtc") ??
            ReadDateTimeProperty(message.Payload, "ReceivedAtUtc") ??
            NormalizeUtc(message.Ts);
        return Math.Max(0, (nowUtc - receivedAtUtc).TotalMilliseconds);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string ReadStringProperty(object? source, string propertyName)
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

    private static async Task<bool> TrySendJsonAsync(WebSocket socket, string json)
    {
        if (socket.State != WebSocketState.Open)
        {
            return false;
        }

        try
        {
            await SendBytesAsync(socket, Encoding.UTF8.GetBytes(json));
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    private static async Task SendBytesAsync(WebSocket socket, byte[] buffer)
    {
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await socket.SendAsync(
            new ArraySegment<byte>(buffer, 0, buffer.Length),
            WebSocketMessageType.Text,
            true,
            sendTimeout.Token);
    }

    private static bool ShouldSendToStream<T>(string stream, T message)
    {
        if (message is not EventEnvelope envelope)
        {
            return true;
        }

        var type = (envelope.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(stream))
        {
            return !IsEsEventType(type);
        }

        return stream switch
        {
            "frontend" => HasAnyPrefix(type, "ui."),
            "esevent" => HasAnyPrefix(type, "esevent."),
            "marquee" => HasAnyPrefix(type, "marquee."),
            "topper" => HasAnyPrefix(type, "topper."),
            "instruction-card" => HasAnyPrefix(type, "instruction-card."),
            "panel" => HasAnyPrefix(type, "panel.", "theme."),
            "ingame" => HasAnyPrefix(type, "ingame.", "retroarch.", "wrapper."),
            "arcade" => HasAnyPrefix(type, "arcade.", "mame.", "fbneo.", "outputs."),
            "score" => HasAnyPrefix(type, "score.live."),
            "timer" => HasAnyPrefix(type, "timer.live."),
            "retroachievements" => HasAnyPrefix(type, "retroachievements."),
            "hiscore" => HasAnyPrefix(type, "hiscore."),
            "media" => HasAnyPrefix(type, "media."),
            "roms" => HasAnyPrefix(type, "roms.", "rom-pack.", "romset."),
            "system" => HasAnyPrefix(type, "startup.", "health.", "version.", "hub.", "config.", "maintenance.", "rules.", "notifications."),
            "control" => HasAnyPrefix(type, "commands.", "intent.", "es-control."),
            _ => false
        };
    }

    private static bool HasAnyPrefix(string value, params string[] prefixes)
    {
        return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEsEventType(string? type)
    {
        return (type ?? string.Empty)
            .Trim()
            .StartsWith("esevent.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEsEventMessage<T>(T message)
    {
        return message is EventEnvelope envelope && IsEsEventType(envelope.Type);
    }

    private static string SerializeForBroadcast<T>(T message, DateTime? esEventDispatchAtUtc = null, DateTime? esEventSendAtUtc = null)
    {
        if (message is not EventEnvelope envelope || !IsEsEventType(envelope.Type))
        {
            return JsonSerializer.Serialize(message);
        }

        return JsonSerializer.Serialize(new EventEnvelope
        {
            Type = envelope.Type,
            Ts = envelope.Ts,
            NodeId = envelope.NodeId,
            CorrelationId = envelope.CorrelationId,
            Payload = EnrichEsEventPayload(envelope.Payload, esEventDispatchAtUtc ?? DateTime.UtcNow, esEventSendAtUtc)
        });
    }

    private static object EnrichEsEventPayload(object? payload, DateTime dispatchAtUtc, DateTime? sendAtUtc)
    {
        var node = payload is null ? new JsonObject() : JsonSerializer.SerializeToNode(payload);

        if (node is JsonObject payloadObject)
        {
            payloadObject["WsDispatchAtUtc"] = dispatchAtUtc;
            if (sendAtUtc.HasValue)
            {
                payloadObject["WsSendAtUtc"] = sendAtUtc.Value;
            }

            return payloadObject;
        }

        var wrapper = new JsonObject
        {
            ["Value"] = node,
            ["WsDispatchAtUtc"] = dispatchAtUtc
        };

        if (sendAtUtc.HasValue)
        {
            wrapper["WsSendAtUtc"] = sendAtUtc.Value;
        }

        return wrapper;
    }

    private static string NormalizeStream(string? stream)
    {
        var normalized = (stream ?? string.Empty)
            .Trim()
            .Trim('/')
            .ToLowerInvariant();

        return normalized switch
        {
            "" or "ws" or "global" or "debug" => string.Empty,
            "es-event" or "es_event" or "esevents" or "es-events" => "esevent",
            "instructioncard" or "instruction_card" or "instructions" => "instruction-card",
            "score" or "scores" => "score",
            "timer" or "timers" => "timer",
            "retroachievement" or "retroachievements" or "cheevos" => "retroachievements",
            "highscore" or "highscores" or "hiscores" => "hiscore",
            _ => normalized
        };
    }

    private static string BuildLatestWinsPayloadKey(string prefix, object? payload)
    {
        var systemId = ReadStringProperty(payload, "SystemId");
        var rom = ReadStringProperty(payload, "Rom");
        var player = ReadStringProperty(payload, "Player");
        var source = ReadStringProperty(payload, "Source");
        var sourceKey = ReadStringProperty(payload, "SourceKey");
        var kind = prefix.StartsWith("score", StringComparison.OrdinalIgnoreCase)
            ? ReadStringProperty(payload, "ScoreKind")
            : ReadStringProperty(payload, "TimerKind");
        return $"{prefix}:{source}:{kind}:{systemId}:{rom}:P{player}:{sourceKey}";
    }

    private sealed record WebSocketSubscription(WebSocket Socket, string Stream);
    private sealed record PendingLatestBroadcast(EventEnvelope Message, string Json, CancellationTokenSource CancellationTokenSource);
    private sealed class LatestWinsThrottleState
    {
        public DateTime LastSentUtc { get; set; }
        public double AverageAgeMs { get; set; }
        public double AverageSendMs { get; set; }
        public int CoalescedSinceSend { get; set; }
    }
}
