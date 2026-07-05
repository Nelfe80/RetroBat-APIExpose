using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class EsSettingsChangeBus : IHostedService, IEsSettingsChangeBus, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private readonly IEsSettingsStore _settingsStore;
    private readonly ILogger<EsSettingsChangeBus>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Func<EsSettingsChangedEvent, CancellationToken, Task>> _subscribers = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;

    public EsSettingsChangeBus(
        IEsSettingsStore settingsStore,
        ILogger<EsSettingsChangeBus>? logger = null)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public IDisposable Subscribe(Func<EsSettingsChangedEvent, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Guid.NewGuid();
        lock (_gate)
        {
            _subscribers[id] = handler;
        }

        return new Subscription(this, id);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settingsPath = RetroBatPaths.EmulationStationSettingsPath;
        var settingsDirectory = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            _logger?.LogWarning("es_settings.cfg watcher not started: settings directory cannot be resolved.");
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(settingsDirectory);
        _watcher = new FileSystemWatcher(settingsDirectory)
        {
            Filter = Path.GetFileName(settingsPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnSettingsChanged;
        _watcher.Created += OnSettingsChanged;
        _watcher.Renamed += OnSettingsChanged;
        _logger?.LogInformation("Single es_settings.cfg watcher started: {Path}", settingsPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnSettingsChanged;
            _watcher.Created -= OnSettingsChanged;
            _watcher.Renamed -= OnSettingsChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _debounceCts?.Dispose();
        _watcher?.Dispose();
    }

    private void OnSettingsChanged(object sender, FileSystemEventArgs e)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            cts = _debounceCts;
        }

        _ = Task.Run(() => DispatchDebouncedAsync(cts, cts.Token), cts.Token);
    }

    private async Task DispatchDebouncedAsync(CancellationTokenSource source, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, cancellationToken);
            await _settingsStore.WaitForStableFileAsync(cancellationToken);

            Func<EsSettingsChangedEvent, CancellationToken, Task>[] subscribers;
            lock (_gate)
            {
                if (!ReferenceEquals(_debounceCts, source))
                {
                    return;
                }

                subscribers = _subscribers.Values.ToArray();
            }

            if (subscribers.Length == 0)
            {
                return;
            }

            var change = new EsSettingsChangedEvent(
                RetroBatPaths.EmulationStationSettingsPath,
                DateTimeOffset.Now);
            await Task.WhenAll(subscribers.Select(subscriber => NotifySubscriberAsync(subscriber, change, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer es_settings.cfg write or service shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Single es_settings.cfg watcher dispatch failed.");
        }
    }

    private async Task NotifySubscriberAsync(
        Func<EsSettingsChangedEvent, CancellationToken, Task> subscriber,
        EsSettingsChangedEvent change,
        CancellationToken cancellationToken)
    {
        try
        {
            await subscriber(change, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Debounced by a newer es_settings.cfg write or service shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "es_settings.cfg subscriber failed.");
        }
    }

    private void Unsubscribe(Guid id)
    {
        lock (_gate)
        {
            _subscribers.Remove(id);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly EsSettingsChangeBus _owner;
        private readonly Guid _id;
        private bool _disposed;

        public Subscription(EsSettingsChangeBus owner, Guid id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Unsubscribe(_id);
        }
    }
}
