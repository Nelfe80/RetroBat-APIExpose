using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class EsFlowEventLogProvider : IProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly IEventBus _eventBus;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<EsFlowEventLogProvider>? _logger;
    private IDisposable? _subscription;
    private StreamWriter? _writer;
    private string _currentPath = string.Empty;

    public EsFlowEventLogProvider(
        IEventBus eventBus,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<EsFlowEventLogProvider>? logger = null)
    {
        _eventBus = eventBus;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        PrepareLogFile();
        _subscription = _eventBus.Subscribe<EventEnvelope>(HandleEvent);
        _logger?.LogInformation("ES flow event log provider started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }

        return Task.CompletedTask;
    }

    public bool IsHealthy() => true;

    private void HandleEvent(EventEnvelope envelope)
    {
        if (!_options.CurrentValue.Logging.EsFlowLogs.Enabled ||
            !envelope.Type.StartsWith("ui.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                EnsureWriterLocked();
                WriteLocked("event", envelope);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write ES flow event log for {EventType}", envelope.Type);
        }
    }

    private void PrepareLogFile()
    {
        var options = _options.CurrentValue.Logging.EsFlowLogs;
        if (!options.Enabled)
        {
            return;
        }

        var path = ResolveLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RetroBatPaths.PluginRoot);

        if (options.ResetOnStartup && File.Exists(path))
        {
            File.Delete(path);
        }

        lock (_sync)
        {
            _currentPath = path;
            EnsureWriterLocked();
            WriteLocked("flow.started", new
            {
                source = "api-start"
            });
        }
    }

    private string ResolveLogPath()
    {
        var configured = _options.CurrentValue.Logging.EsFlowLogs.FilePath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = ".log/es-flow.jsonl";
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(RetroBatPaths.PluginRoot, configured);
    }

    private void EnsureWriterLocked()
    {
        var path = ResolveLogPath();
        if (_writer != null && string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RetroBatPaths.PluginRoot);
        _currentPath = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    private void WriteLocked(string kind, object? payload)
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
        _writer?.WriteLine(line);
    }
}
