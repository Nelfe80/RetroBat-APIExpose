using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Events;

namespace RetroBat.Api.Hubs;

public class WebSocketConnectionManager
{
    private const int LatestWinsBroadcastDelayMs = 20;
    private const int FrontendSelectionBroadcastDelayMs = 35;
    private readonly List<WebSocketSubscription> _sockets = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _latestWinsLock = new();
    private readonly Dictionary<string, PendingLatestBroadcast> _pendingLatestBroadcasts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _latestWinsAcceptedTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private string? _retainedPanelStateJson;

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
        var json = JsonSerializer.Serialize(message);
        RetainMessage(message, json);

        if (message is EventEnvelope envelope &&
            TryGetLatestWinsBroadcastKey(envelope, out var latestWinsKey))
        {
            QueueLatestWinsBroadcast(latestWinsKey, envelope, json);
            return;
        }

        await BroadcastSerializedAsync(message, json);
    }

    private async Task BroadcastSerializedAsync<T>(T message, string json)
    {
        var buffer = Encoding.UTF8.GetBytes(json);
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
            if (_latestWinsAcceptedTimestamps.TryGetValue(key, out var latestTs) &&
                message.Ts < latestTs)
            {
                cts.Dispose();
                return;
            }

            _latestWinsAcceptedTimestamps[key] = message.Ts;

            if (_pendingLatestBroadcasts.TryGetValue(key, out var previous))
            {
                previousCts = previous.CancellationTokenSource;
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
            await Task.Delay(ResolveLatestWinsBroadcastDelayMs(key), pending.CancellationTokenSource.Token);
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
            await BroadcastSerializedAsync(pending.Message, pending.Json);
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
            "hiscore.updated" => "hiscore.updated",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(key);
    }

    private static int ResolveLatestWinsBroadcastDelayMs(string key)
    {
        return key.StartsWith("frontend.selection", StringComparison.OrdinalIgnoreCase)
            ? FrontendSelectionBroadcastDelayMs
            : LatestWinsBroadcastDelayMs;
    }

    private string? GetRetainedMessageForStream(string stream)
    {
        return string.Equals(stream, "panel", StringComparison.OrdinalIgnoreCase)
            ? _retainedPanelStateJson
            : null;
    }

    private void RetainMessage<T>(T message, string json)
    {
        if (message is EventEnvelope envelope &&
            string.Equals(envelope.Type, "panel.state", StringComparison.OrdinalIgnoreCase) &&
            IsRetainablePanelState(envelope))
        {
            _retainedPanelStateJson = json;
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
        if (string.IsNullOrWhiteSpace(stream))
        {
            return true;
        }

        if (message is not EventEnvelope envelope)
        {
            return true;
        }

        var type = (envelope.Type ?? string.Empty).Trim().ToLowerInvariant();
        return stream switch
        {
            "frontend" => HasAnyPrefix(type, "ui."),
            "marquee" => HasAnyPrefix(type, "marquee."),
            "topper" => HasAnyPrefix(type, "topper."),
            "instruction-card" => HasAnyPrefix(type, "instruction-card."),
            "panel" => HasAnyPrefix(type, "panel.", "theme."),
            "ingame" => HasAnyPrefix(type, "ingame.", "retroarch.", "wrapper."),
            "arcade" => HasAnyPrefix(type, "arcade.", "mame.", "fbneo.", "outputs."),
            "hiscore" => HasAnyPrefix(type, "hiscore.", "score."),
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

    private static string NormalizeStream(string? stream)
    {
        var normalized = (stream ?? string.Empty)
            .Trim()
            .Trim('/')
            .ToLowerInvariant();

        return normalized switch
        {
            "" or "ws" or "global" or "debug" => string.Empty,
            "instructioncard" or "instruction_card" or "instructions" => "instruction-card",
            "scores" or "score" => "hiscore",
            _ => normalized
        };
    }

    private sealed record WebSocketSubscription(WebSocket Socket, string Stream);
    private sealed record PendingLatestBroadcast(EventEnvelope Message, string Json, CancellationTokenSource CancellationTokenSource);
}
