using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Le canal vers le vérificateur : APIExpose dit ce que la mémoire du jeu annonce, et lit des
/// nombres.
///
/// C'est le vérificateur qui tient le tuyau, pas APIExpose : le processus qui manipule les
/// images est celui qui décide qui peut lui parler, et ses droits ne nomment qu'un compte.
/// APIExpose s'y connecte en client, et n'ouvre ainsi aucune porte de son côté.
///
/// Sur ce canal ne circulent que des mots et des nombres. Aucune image n'y passe, dans aucun
/// sens : les pixels vont de l'émulateur au vérificateur, directement, sur un autre tuyau
/// qu'APIExpose se contente de nommer.
/// </summary>
public sealed class ScoreVerifierChannel : IScoreVerifierChannel, IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _readLoop;

    public ScoreVerifierChannel(ScoringDiscoveryOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pipeName = options.VerifierPipeName;
        _logger = logger;
    }

    public bool IsAvailable => _pipe?.IsConnected == true && _writer is not null;

    public event Action<string, VerificationOutcome>? ResultReceived;

    /// <summary>
    /// Se connecte au vérificateur. Rend faux s'il n'est pas là : la découverte n'a alors
    /// simplement pas lieu, et rien d'autre dans la borne ne s'en ressent.
    /// </summary>
    public async Task<bool> ConnectAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        if (IsAvailable)
        {
            return true;
        }

        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            _logger?.LogDebug("Verificateur absent sur {Pipe}.", _pipeName);
            return false;
        }

        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        _readLoop = ReadAsync(pipe, _stopping.Token);
        _logger?.LogInformation("Verificateur connecte sur {Pipe}.", _pipeName);
        return true;
    }

    /// <summary>
    /// Lit les lignes du vérificateur.
    ///
    /// Les octets sont lus directement, pas à travers un <c>StreamReader</c> : sur un tuyau,
    /// une lecture déjà engagée par un StreamReader ne se laisse pas annuler, et la fermeture
    /// du tuyau attend alors cette lecture qui n'arrivera jamais. Mesuré le 13 septembre 2026 :
    /// la suite de tests passait, puis l'hôte restait bloqué une minute avant d'être vidé.
    /// Sur un tuyau ouvert en asynchrone, <c>ReadAsync</c> honore le jeton, lui.
    /// </summary>
    private async Task ReadAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var pending = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await pipe.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
            while (true)
            {
                var text = pending.ToString();
                var cut = text.IndexOf('\n');
                if (cut < 0)
                {
                    break;
                }

                var line = text[..cut].TrimEnd('\r');
                pending.Clear();
                pending.Append(text[(cut + 1)..]);

                if (TryParseResult(line, out var token, out var outcome))
                {
                    ResultReceived?.Invoke(token, outcome);
                }
                else if (line.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    // Le vérificateur refuse quelque chose : c'est nous qui avons mal demandé.
                    _logger?.LogWarning("Verificateur : {Line}", line);
                }
            }
        }

        _writer = null;
        _logger?.LogDebug("Verificateur : canal ferme.");
    }

    /// <summary>
    /// Lit <c>RESULT|token|seq|trouve|confiance|stabilite|candidats|version</c>.
    ///
    /// Les nombres arrivent avec un point décimal, quelle que soit la langue de la machine :
    /// le vérificateur les écrit ainsi, et les lire avec la culture courante donnerait 9800
    /// au lieu de 0,98 sur une borne française.
    /// </summary>
    internal static bool TryParseResult(string line, out string token, out VerificationOutcome outcome)
    {
        token = string.Empty;
        outcome = default;
        var parts = line.Split('|');
        if (parts.Length < 8 || !string.Equals(parts[0], "RESULT", StringComparison.Ordinal))
        {
            return false;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(parts[3], out var matched)
            || !double.TryParse(parts[4], System.Globalization.NumberStyles.Float, culture, out var confidence)
            || !double.TryParse(parts[5], System.Globalization.NumberStyles.Float, culture, out var stability)
            || !int.TryParse(parts[6], out var candidates))
        {
            return false;
        }

        token = parts[1];
        outcome = new VerificationOutcome(matched == 1, confidence, stability, candidates, parts[7]);
        return true;
    }

    public Task<bool> ExpectAsync(string token, long value, FrameOrientationDegrees orientation, CancellationToken cancellationToken)
        => SendAsync($"EXPECT|{token}|{value}|{(int)orientation}", cancellationToken);

    public async Task ForgetAsync(CancellationToken cancellationToken)
        => await SendAsync("FORGET", cancellationToken).ConfigureAwait(false);

    private async Task<bool> SendAsync(string line, CancellationToken cancellationToken)
    {
        var writer = _writer;
        if (writer is null || _pipe?.IsConnected != true)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger?.LogDebug("Verificateur : le canal s'est ferme pendant l'ecriture.");
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
        // L'ordre, et ce qu'il a coûté de trouver (13 septembre 2026).
        //
        // Fermer la poignée du tuyau pendant qu'une lecture est en vol fait tomber le
        // processus : essayé, l'hôte de test a planté. Attendre la boucle de lecture, à
        // l'inverse, peut ne jamais rendre la main, la lecture n'ayant plus rien à lire.
        //
        // Ce qui reste marche et ne risque rien : annuler, fermer le FLUX, qui abandonne
        // proprement l'entrée-sortie en attente, et ne pas attendre la boucle. Elle se
        // terminera d'elle-même, et une tâche qui traîne sans poignée ouverte n'empêche
        // rien de s'arrêter.
        await _stopping.CancelAsync().ConfigureAwait(false);
        _writer = null;
        if (_pipe is not null)
        {
            try
            {
                _pipe.Close();
                await _pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Déjà fermé par le vérificateur : c'est le cas ordinaire en fin de partie.
            }

            _pipe = null;
        }

        _readLoop = null;

        _stopping.Dispose();
        _writeLock.Dispose();
    }
}
