using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Providers.MameOutputs;

public class MameOutputsProvider : IProvider
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<MameOutputsProvider>? _logger;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, MameSignal> _signals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _announcedSignals = new(StringComparer.OrdinalIgnoreCase);
    private string _machineName = string.Empty;
    private bool _hadActiveConnection;
    private bool _waitingForServerLogged;
    private bool _serverUnavailableLogged;

    public MameOutputsProvider(IEventBus eventBus, ILogger<MameOutputsProvider>? logger = null)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = RunAsync(_cts.Token);
        _logger?.LogInformation("MameOutputsProvider started in client mode for 127.0.0.1:8000");
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
            try
            {
                await Task.WhenAny(_workerTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            catch
            {
                // Ignore exceptions during shutdown
            }
        }
        
        _logger?.LogInformation("MameOutputsProvider stopped");
    }

    public bool IsHealthy() => _workerTask != null && !_workerTask.IsCompleted;

    public MameOutputEvent GetSnapshot()
    {
        lock (_stateLock)
        {
            return new MameOutputEvent
            {
                Source = "mame.network",
                Port = 8000,
                MachineName = _machineName,
                Signals = _signals.Values
                    .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new MameSignal
                    {
                        Key = s.Key,
                        Value = s.Value,
                        Ts = s.Ts
                    })
                    .ToList()
            };
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                if (!_waitingForServerLogged)
                {
                    _logger?.LogInformation("Connecting to MAME output server on 127.0.0.1:8000");
                    _waitingForServerLogged = true;
                }

                await client.ConnectAsync("127.0.0.1", 8000, token);
                _logger?.LogInformation("Connected to MAME output server");
                _hadActiveConnection = true;
                _waitingForServerLogged = false;
                _serverUnavailableLogged = false;

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!token.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    ProcessLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                if (_hadActiveConnection)
                {
                    _logger?.LogInformation("Disconnected from MAME output server");
                    _hadActiveConnection = false;
                    _waitingForServerLogged = false;
                }

                // No game with output-network is running: this is expected and should stay quiet.
                if (!_serverUnavailableLogged)
                {
                    _logger?.LogDebug("MAME output server not available on 127.0.0.1:8000");
                    _serverUnavailableLogged = true;
                }
            }
            catch (IOException ex) when (ex.InnerException is SocketException socketEx && socketEx.SocketErrorCode == SocketError.ConnectionReset)
            {
                if (_hadActiveConnection)
                {
                    _logger?.LogInformation("MAME output server closed the connection");
                    _hadActiveConnection = false;
                    _waitingForServerLogged = false;
                    _serverUnavailableLogged = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _hadActiveConnection = false;
                _waitingForServerLogged = false;
                _serverUnavailableLogged = false;
                _logger?.LogWarning(ex, "Error reading MAME output stream");
            }

            if (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void ProcessLine(string line)
    {
        var parts = line.Split('=', 2);
        if (parts.Length != 2)
        {
            return;
        }

        var key = parts[0].Trim();
        var rawValue = parts[1].Trim();

        if (key.Equals("mame_start", StringComparison.OrdinalIgnoreCase))
        {
            lock (_stateLock)
            {
                _machineName = rawValue;
                _signals.Clear();
                _announcedSignals.Clear();
            }

            _logger?.LogInformation("MAME output session started for {MachineName}", rawValue);

            _eventBus.PublishAsync(new EventEnvelope
            {
                Type = "mame.session.started",
                Payload = new
                {
                    source = "mame.network",
                    port = 8000,
                    machineName = rawValue,
                    ts = DateTime.Now
                }
            });
            return;
        }

        if (int.TryParse(rawValue, out var val))
        {
            var discoveredNow = false;

            lock (_stateLock)
            {
                discoveredNow = _announcedSignals.Add(key);
                _signals[key] = new MameSignal { Key = key, Value = val, Ts = DateTime.Now };
            }

            if (discoveredNow)
            {
                _logger?.LogInformation("Detected MAME output signal {SignalKey}; listening for updates", key);
            }

            var payload = new MameOutputEvent
            {
                Source = "mame.network",
                Port = 8000,
                MachineName = GetMachineName(),
                Signals = new List<MameSignal>
                {
                    new MameSignal { Key = key, Value = val, Ts = DateTime.Now }
                }
            };

            var evt = new EventEnvelope
            {
                Type = "mame.output.changed",
                Payload = payload
            };

            _eventBus.PublishAsync(evt);
        }
    }

    private string GetMachineName()
    {
        lock (_stateLock)
        {
            return _machineName;
        }
    }
}
