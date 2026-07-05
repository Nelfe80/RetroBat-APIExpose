using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class IngameGameplayStateService : IProvider
{
    private static readonly TimeSpan GameplayStateTtl = TimeSpan.FromSeconds(90);

    private static readonly Regex NonGameplayRichPresenceRegex = new(
        @"\b(?:demo|demonstration|attract|title\s+screen|main\s+menu|menu|intro|opening|continue\s+screen|game\s+over|credits)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly IEventBus _eventBus;
    private readonly ILogger<IngameGameplayStateService> _logger;
    private IDisposable? _subscription;
    private GameplaySnapshot _snapshot = new();

    public IngameGameplayStateService(IEventBus eventBus, ILogger<IngameGameplayStateService> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        _logger.LogInformation("IngameGameplayStateService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public bool IsHealthy() => true;

    public bool ShouldSuppressRichPresence(string richPresence)
    {
        if (string.IsNullOrWhiteSpace(richPresence) ||
            !NonGameplayRichPresenceRegex.IsMatch(richPresence))
        {
            return false;
        }

        lock (_sync)
        {
            return _snapshot.IsGameplayActive &&
                DateTime.UtcNow - _snapshot.LastUpdatedUtc <= GameplayStateTtl;
        }
    }

    public GameplaySnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    private void HandleEvent(EventEnvelope envelope)
    {
        try
        {
            var type = envelope.Type ?? string.Empty;
            if (type.Equals("ui.game.started", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("ui.game.ended", StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                return;
            }

            if (!type.Equals("retroarch.memory.changed", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("ingame.memory.changed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ObserveMemoryState(envelope.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gameplay state ignored malformed event {EventType}", envelope.Type);
        }
    }

    private void Reset()
    {
        lock (_sync)
        {
            _snapshot = new GameplaySnapshot();
        }
    }

    private void ObserveMemoryState(object? payload)
    {
        var signal = ReadProperty(payload, "signal") ?? ReadProperty(payload, "Signal");
        if (signal == null)
        {
            return;
        }

        var channel = ReadString(signal, "Channel");
        if (!channel.Equals("STATE", StringComparison.OrdinalIgnoreCase) &&
            !channel.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var stateName = ReadString(signal, "Name");
        var gameplayState = ResolveGameplayState(stateName);
        if (gameplayState == null)
        {
            return;
        }

        lock (_sync)
        {
            _snapshot = new GameplaySnapshot
            {
                IsGameplayActive = gameplayState.Value,
                StateName = stateName,
                SystemId = ReadString(payload, "SystemId"),
                Rom = ReadString(payload, "Rom"),
                DefinitionFile = ReadString(payload, "DefinitionFile"),
                LastUpdatedUtc = DateTime.UtcNow
            };
        }
    }

    private static bool? ResolveGameplayState(string stateName)
    {
        return stateName.Trim().ToUpperInvariant() switch
        {
            "GAME_PLAYING" => true,
            "RUNNING" => true,
            "PLAYING" => true,
            "IN_GAME" => true,
            "TITLE_SCREEN" => false,
            "SELECT_SCREEN" => false,
            "DEMO_MODE" => false,
            "ATTRACT_MODE" => false,
            "MENU" => false,
            "MAIN_MENU" => false,
            "CONTINUE_SCREEN" => false,
            "GAME_OVER" => false,
            "CREDITS_SCREEN" => false,
            "PAUSE_ON" => false,
            _ => null
        };
    }

    private static object? ReadProperty(object? source, params string[] names)
    {
        if (source is JsonElement element)
        {
            foreach (var name in names)
            {
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty(name, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        var type = source?.GetType();
        if (type == null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var property = type.GetProperty(name);
            var value = property?.GetValue(source);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static string ReadString(object? source, params string[] names)
    {
        if (source is JsonElement element)
        {
            return ReadString(element, names);
        }

        var type = source?.GetType();
        if (type == null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            var property = type.GetProperty(name);
            var value = property?.GetValue(source);
            if (value != null)
            {
                return value.ToString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string ReadString(JsonElement? source, params string[] names)
    {
        if (source is not { ValueKind: JsonValueKind.Object } element)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            return value.ToString();
        }

        return string.Empty;
    }
}

public sealed record GameplaySnapshot
{
    public bool IsGameplayActive { get; init; }
    public string StateName { get; init; } = string.Empty;
    public string SystemId { get; init; } = string.Empty;
    public string Rom { get; init; } = string.Empty;
    public string DefinitionFile { get; init; } = string.Empty;
    public DateTime LastUpdatedUtc { get; init; } = DateTime.MinValue;
}
