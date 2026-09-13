using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Le canal de commande vers le plugin Lua de MAME : un port TCP local, sur lequel ne circule
/// que du texte.
///
/// Même protocole que le wrapper libretro, autre transport, parce que MAME parle en sockets
/// depuis son Lua et ne sait pas ouvrir un tuyau nommé. Là encore, les pixels passent ailleurs :
/// le plugin les envoie directement au vérificateur.
///
/// L'écoute est sur la boucle locale seulement, et sur un port distinct de celui du pont RAM :
/// deux plugins sur le même port, et l'un lirait les commandes de l'autre.
/// </summary>
public sealed class MameCaptureChannel : IEmulatorCaptureChannel, IAsyncDisposable
{
    private readonly int _port;
    private readonly int _framesPort;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private TcpClient? _plugin;
    private StreamWriter? _writer;
    private Task? _acceptLoop;

    public MameCaptureChannel(ScoringDiscoveryOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _port = options.MameControlPort;
        _framesPort = options.MameFramesPort;
        _logger = logger;
    }

    public bool IsAvailable => _plugin?.Connected == true && _writer is not null;

    public void Listen()
    {
        if (_listener is not null)
        {
            return;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _acceptLoop = AcceptAsync(_listener, _stopping.Token);
        }
        catch (SocketException ex)
        {
            _logger?.LogWarning(ex, "Canal de capture MAME indisponible (port {Port}) : la decouverte ne s'armera pas.", _port);
            _listener = null;
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // Une seule partie à la fois : un nouveau plugin remplace l'ancien, qui est celui
            // du jeu precedent.
            var previous = _plugin;
            _plugin = client;
            _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            previous?.Dispose();
            _logger?.LogDebug("Canal de capture MAME : plugin connecte sur {Port}.", _port);
        }
    }

    /// <summary>
    /// Arme en donnant au plugin l'adresse du vérificateur : c'est le plugin qui ouvre la
    /// connexion sortante et y dépose ses images. APIExpose ne relaie aucun octet d'image, il
    /// dit seulement où les porter.
    /// </summary>
    public Task<bool> ArmAsync(string token, int frames, CancellationToken cancellationToken)
        => SendAsync($"ARM|127.0.0.1|{_framesPort}|{token}|{frames}", cancellationToken);

    public async Task DisarmAsync(CancellationToken cancellationToken)
        => await SendAsync("DISARM", cancellationToken).ConfigureAwait(false);

    private async Task<bool> SendAsync(string line, CancellationToken cancellationToken)
    {
        var writer = _writer;
        if (writer is null || _plugin?.Connected != true)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger?.LogDebug("Canal de capture MAME : le plugin a ferme la connexion.");
            _writer = null;
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _writer = null;
        _plugin?.Dispose();
        _plugin = null;
        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // La boucle a été interrompue par l'arrêt : c'est ce qu'on voulait.
            }

            _acceptLoop = null;
        }

        _stopping.Dispose();
        _writeLock.Dispose();
    }
}
