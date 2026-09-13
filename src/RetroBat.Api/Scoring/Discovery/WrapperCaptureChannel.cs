using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Le canal de commande vers le wrapper libretro : un tuyau nommé, tenu par APIExpose, sur
/// lequel ne circule que du texte.
///
/// Les pixels ne passent pas par ici et ne passeront jamais : le wrapper les envoie
/// directement au vérificateur, sur un autre tuyau qu'APIExpose se contente de nommer. Ce
/// tuyau-ci ne porte que <c>ARM</c> et <c>DISARM</c>, et son sens unique le dit : APIExpose
/// écrit, le wrapper lit.
///
/// Le tuyau n'autorise qu'un compte Windows. Celui de la découverte RAM, ouvert à tout
/// processus local, ne sert pas de modèle : même du texte qui dit quand une partie est
/// observée mérite de ne pas être lisible par n'importe quoi.
/// </summary>
public sealed class WrapperCaptureChannel : IEmulatorCaptureChannel, IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Task? _waitForWrapper;

    public WrapperCaptureChannel(ScoringDiscoveryOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pipeName = options.WrapperControlPipeName;
        _logger = logger;
    }

    /// <summary>Le wrapper a ouvert le tuyau : il y a un émulateur à commander.</summary>
    public bool IsAvailable => _pipe?.IsConnected == true;

    /// <summary>
    /// Ouvre le tuyau et attend, sans bloquer, qu'un wrapper s'y présente. Rien n'est capturé
    /// tant que personne n'est là, et personne n'est là tant qu'aucun jeu libretro ne tourne.
    /// </summary>
    public void Listen()
    {
        if (_pipe is not null)
        {
            return;
        }

        try
        {
            var me = WindowsIdentity.GetCurrent().User
                     ?? throw new InvalidOperationException("Aucun compte Windows a qui restreindre le tuyau.");
            var security = new PipeSecurity();
            security.SetOwner(me);
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

            _pipe = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.Out,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 4096,
                pipeSecurity: security);

            _waitForWrapper = WaitAsync(_pipe);
        }
        catch (Exception ex)
        {
            // Un tuyau déjà pris, ou un compte introuvable : la découverte n'a simplement pas
            // lieu. Rien d'autre dans la borne ne doit s'en ressentir.
            _logger?.LogWarning(ex, "Canal de capture indisponible ({Pipe}) : la decouverte ne s'armera pas.", _pipeName);
            _pipe = null;
        }
    }

    private async Task WaitAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            _logger?.LogDebug("Canal de capture : wrapper connecte sur {Pipe}.", _pipeName);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Le tuyau a été fermé pendant l'attente : rien à signaler.
        }
    }

    public Task<bool> ArmAsync(string token, int frames, CancellationToken cancellationToken)
        => SendAsync($"ARM|{token}|{frames}", cancellationToken);

    public async Task DisarmAsync(CancellationToken cancellationToken)
        => await SendAsync("DISARM", cancellationToken).ConfigureAwait(false);

    private async Task<bool> SendAsync(string line, CancellationToken cancellationToken)
    {
        if (_writer is null || _pipe?.IsConnected != true)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Le jeu s'est ferme : le wrapper est parti avec. On le note et on rend faux,
            // le coordinateur en tirera les consequences.
            _logger?.LogDebug("Canal de capture : le wrapper a ferme le tuyau.");
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
        _writer = null;
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        if (_waitForWrapper is not null)
        {
            try
            {
                // Bornée pour la même raison que côté vérificateur : l'arrêt de la borne ne
                // dépend pas d'une attente engagée sur un tuyau qu'on vient de fermer.
                await Task.WhenAny(_waitForWrapper, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch
            {
                // L'attente a été interrompue par la fermeture : c'est ce qu'on voulait.
            }

            _waitForWrapper = null;
        }

        _writeLock.Dispose();
    }
}
