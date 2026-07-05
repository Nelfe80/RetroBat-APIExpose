using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class GameSessionEventLogProvider : IProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly IEventBus _eventBus;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<GameSessionEventLogProvider>? _logger;
    private IDisposable? _subscription;
    private SessionLog? _current;

    public GameSessionEventLogProvider(
        IEventBus eventBus,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<GameSessionEventLogProvider>? logger = null)
    {
        _eventBus = eventBus;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ResetLogDirectoryIfNeeded();
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        _logger?.LogInformation("Game session event log provider started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        lock (_sync)
        {
            CloseCurrentSessionLocked("api-stop");
        }

        return Task.CompletedTask;
    }

    public bool IsHealthy() => true;

    private void HandleEvent(EventEnvelope envelope)
    {
        if (!_options.CurrentValue.Logging.GameSessionLogs.Enabled)
        {
            return;
        }

        try
        {
            if (string.Equals(envelope.Type, "ui.game.started", StringComparison.OrdinalIgnoreCase))
            {
                StartSession(envelope);
                return;
            }

            lock (_sync)
            {
                _current?.Write("event", envelope);
                if (string.Equals(envelope.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
                {
                    CloseCurrentSessionLocked("game-ended");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write game session event log for {EventType}", envelope.Type);
        }
    }

    private void StartSession(EventEnvelope envelope)
    {
        var game = ResolveGame(envelope.Payload);
        var systemId = GetString(game, "SystemId");
        var gameName = GetString(game, "GameName");
        var gamePath = GetString(game, "GamePath");
        var romName = Path.GetFileNameWithoutExtension(gamePath);
        if (string.IsNullOrWhiteSpace(romName))
        {
            romName = gameName;
        }

        var fileName = $"session-{Sanitize(systemId)}-{Sanitize(romName)}.jsonl";
        var directory = ResolveLogDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        lock (_sync)
        {
            CloseCurrentSessionLocked("new-game-started");
            _current = new SessionLog(path);
            _current.Write("session.started", new
            {
                systemId,
                gameName,
                gamePath,
                eventType = envelope.Type,
                eventPayload = envelope.Payload
            });
        }

        _logger?.LogInformation("Game session event log started: {Path}", path);
    }

    private string ResolveLogDirectory()
    {
        var configured = _options.CurrentValue.Logging.GameSessionLogs.DirectoryPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = ".log/game-sessions";
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(RetroBatPaths.PluginRoot, configured);
    }

    private void ResetLogDirectoryIfNeeded()
    {
        var options = _options.CurrentValue.Logging.GameSessionLogs;
        if (!options.Enabled || !options.ResetOnStartup)
        {
            return;
        }

        var directory = ResolveLogDirectory();
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
    }

    private void CloseCurrentSessionLocked(string reason)
    {
        if (_current == null)
        {
            return;
        }

        _current.Write("session.closed", new
        {
            reason
        });
        _current.Dispose();
        _logger?.LogInformation("Game session event log closed: {Path} ({Reason})", _current.Path, reason);
        _current = null;
    }

    private static object? ResolveGame(object? payload)
    {
        var context = GetPropertyValue(payload, "Context");
        var running = GetPropertyValue(context, "Running");
        if (running != null)
        {
            return running;
        }

        var selected = GetPropertyValue(context, "Selected");
        if (selected != null)
        {
            return selected;
        }

        var payloadSelected = GetPropertyValue(payload, "Selected");
        if (payloadSelected != null)
        {
            return payloadSelected;
        }

        var payloadRunning = GetPropertyValue(payload, "Running");
        if (payloadRunning != null)
        {
            return payloadRunning;
        }

        return !string.IsNullOrWhiteSpace(GetString(payload, "SystemId")) ||
            !string.IsNullOrWhiteSpace(GetString(payload, "GamePath"))
            ? payload
            : null;
    }

    private static string Sanitize(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var sanitized = new string(chars);
        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        return sanitized.Trim('-', ' ', '.');
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

    private sealed class SessionLog : IDisposable
    {
        private readonly StreamWriter _writer;

        public SessionLog(string path)
        {
            Path = path;
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }

        public string Path { get; }

        public void Write(string kind, object? payload)
        {
            var compactPayload = payload is EventEnvelope envelope
                ? new
                {
                    type = envelope.Type,
                    payload = envelope.Payload
                }
                : payload;

            var line = JsonSerializer.Serialize(new
            {
                kind,
                payload = compactPayload
            }, JsonOptions);
            _writer.WriteLine(line);
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }
}
