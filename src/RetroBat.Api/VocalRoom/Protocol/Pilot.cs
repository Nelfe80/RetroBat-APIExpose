// COPIE VERSIONNEE du contrat IPC de NelfePlay-VocalRoom (depot prive Nelfe80/NelfePlay-VocalRoom,
// src/NelfePlay.VocalRoom.Protocol, commit f3f0095, protocole 1). Ne pas modifier ici : changer le depot VocalRoom,
// puis recopier (voir VocalRoomProtocolSource et OUTILS_COMMUNS.md, « Vocal Discord »).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace NelfePlay.VocalRoom.Protocol;

/// <summary>
/// Le cote PILOTE du protocole (ce qu'APIExpose fera au lot B) sur un pipe deja connecte : poignee de main, commandes
/// correlees par request_id, evenements. Sert au simulateur et aux tests ; APIExpose peut reprendre ce fichier.
/// </summary>
public sealed class PilotConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _secret;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<JsonObject> _challenge = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<JsonObject> _events = [];
    private int _nextId;
    private Task? _readLoop;

    private PilotConnection(Stream stream, byte[] secret)
    {
        _stream = stream;
        _secret = secret;
    }

    /// <summary>Tous les evenements recus (handshake.challenge compris), dans l'ordre.</summary>
    public IReadOnlyList<JsonObject> Events { get { lock (_events) return _events.ToArray(); } }

    /// <summary>Appele a chaque evenement recu (sur la tache de lecture).</summary>
    public event Action<JsonObject>? EventReceived;

    /// <summary>Termine quand l'exe ferme le pipe.</summary>
    public Task Closed => _closed.Task;

    public string? ClientNonce { get; private set; }

    /// <summary>La preuve rendue par l'exe dans la reponse a hello etait-elle juste ?</summary>
    public bool ClientProofValid { get; private set; }

    public static PilotConnection Start(Stream connected, byte[] secret)
    {
        var p = new PilotConnection(connected, secret);
        p._readLoop = Task.Run(p.ReadLoopAsync);
        return p;
    }

    /// <summary>Attend <c>handshake.challenge</c>, envoie <c>hello</c> et verifie la preuve de l'exe.</summary>
    public async Task<JsonObject> HelloAsync(TimeSpan timeout, byte[]? proofSecret = null, string? applicationId = null)
    {
        JsonObject challenge = await _challenge.Task.WaitAsync(timeout).ConfigureAwait(false);
        ClientNonce = Messages.Str(Messages.Obj(challenge, "payload"), "client_nonce");
        if (!Pairing.IsValidNonce(ClientNonce)) throw new InvalidOperationException("handshake.challenge sans nonce valide");

        string serverNonce = Pairing.NewNonce();
        var payload = new JsonObject
        {
            ["protocol_min"] = ProtocolInfo.MinVersion,
            ["protocol_max"] = ProtocolInfo.MaxVersion,
            ["server_nonce"] = serverNonce,
            ["proof"] = Pairing.ServerProof(proofSecret ?? _secret, ClientNonce!, serverNonce),
        };
        if (applicationId is not null) payload["application_id"] = applicationId;

        JsonObject r = await SendAsync(Commands.Hello, payload, timeout).ConfigureAwait(false);
        string? clientProof = Messages.Str(Messages.Obj(r, "result"), "proof");
        ClientProofValid = Messages.Bool(r, "ok") == true
            && Pairing.ProofEquals(Pairing.ClientProof(_secret, ClientNonce!, serverNonce), clientProof);
        return r;
    }

    public string NextRequestId() => $"c-{Interlocked.Increment(ref _nextId):D4}";

    /// <summary>Envoie une commande et attend sa reponse (correlee par request_id).</summary>
    public Task<JsonObject> SendAsync(string command, JsonObject? payload, TimeSpan timeout, string? requestId = null) =>
        SendRawAsync(Messages.Command(requestId ?? NextRequestId(), command, payload), timeout);

    /// <summary>Envoie un message tel quel (tests : request_id rejoue, version fausse...).</summary>
    public async Task<JsonObject> SendRawAsync(JsonObject message, TimeSpan timeout)
    {
        string id = Messages.Str(message, "request_id") ?? throw new ArgumentException("request_id manquant");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(message).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Ecrit des octets bruts (tests : trame trop grande).</summary>
    public async Task WriteBytesAsync(byte[] bytes)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try { await _stream.WriteAsync(bytes).ConfigureAwait(false); await _stream.FlushAsync().ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    /// <summary>Attend un evenement qui satisfait <paramref name="match"/> (deja recu ou a venir).</summary>
    public async Task<JsonObject> WaitEventAsync(Func<JsonObject, bool> match, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(JsonObject e) { if (match(e)) tcs.TrySetResult(e); }
        EventReceived += Handler;
        try
        {
            foreach (var e in Events) if (match(e)) return e;
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            EventReceived -= Handler;
        }
    }

    private async Task WriteAsync(JsonObject message)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try { await Framing.WriteAsync(_stream, message, _cts.Token).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                JsonObject? m = await Framing.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (m is null) break;
                if (Messages.IsResponse(m))
                {
                    string? id = Messages.Str(m, "request_id");
                    if (id is not null && _pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(m);
                }
                else if (Messages.IsEvent(m))
                {
                    lock (_events) _events.Add(m);
                    if (Messages.Str(m, "event") == NelfePlay.VocalRoom.Protocol.Events.HandshakeChallenge) _challenge.TrySetResult(m);
                    EventReceived?.Invoke(m);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or FramingException)
        {
        }
        finally
        {
            _closed.TrySetResult();
            foreach (var t in _pending.Values) t.TrySetException(new IOException("pipe ferme"));
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        try { _stream.Dispose(); } catch { }
        if (_readLoop is not null) { try { await _readLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}

/// <summary>Options de lancement de l'exe par le pilote.</summary>
public sealed record LaunchOptions(
    string ExePath,
    string? LogDir = null,
    int ExitGraceMs = 500,
    TimeSpan? ConnectTimeout = null,
    byte[]? SecretOverride = null,
    bool SendSecret = true);

/// <summary>
/// Lancement tel qu'APIExpose le fera (§6.1) : pipe reserve au compte courant, exe sans fenetre, arguments non secrets,
/// secret sur stdin, verification que le client du pipe est bien le processus lance.
/// </summary>
public sealed class VocalRoomLaunch : IAsyncDisposable
{
    private readonly StringBuilder _stderr = new();

    public required Process Process { get; init; }
    public required NamedPipeServerStream Pipe { get; init; }
    public required string PipeName { get; init; }
    public required byte[] Secret { get; init; }
    public PilotConnection? Connection { get; private set; }

    /// <summary>Journal technique de l'exe (stderr), deja expurge par l'exe.</summary>
    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

    public static async Task<VocalRoomLaunch> StartAsync(LaunchOptions o)
    {
        string pipeName = Pairing.NewPipeName();
        var pipe = PipePeers.CreateServer(pipeName);
        byte[] secret = Pairing.NewSecret();

        var psi = new ProcessStartInfo(o.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in new[] { "--managed", "--pipe", pipeName, "--parent-pid", Environment.ProcessId.ToString(), "--protocol", ProtocolInfo.Version.ToString(), "--exit-grace-ms", o.ExitGraceMs.ToString() })
            psi.ArgumentList.Add(a);
        if (o.LogDir is not null) { psi.ArgumentList.Add("--log-dir"); psi.ArgumentList.Add(o.LogDir); }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("lancement impossible");
        var launch = new VocalRoomLaunch { Process = process, Pipe = pipe, PipeName = pipeName, Secret = secret };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (launch._stderr) launch._stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        process.OutputDataReceived += (_, _) => { };
        process.BeginOutputReadLine();

        if (o.SendSecret)
        {
            await process.StandardInput.WriteLineAsync(Pairing.EncodeSecret(o.SecretOverride ?? secret)).ConfigureAwait(false);
        }
        process.StandardInput.Close();
        return launch;
    }

    /// <summary>Attend la connexion de l'exe et verifie son PID ; rend la connexion pilote.</summary>
    public async Task<PilotConnection> AcceptAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await Pipe.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
        int? pid = PipePeers.ClientProcessId(Pipe);
        if (pid != Process.Id) throw new InvalidOperationException($"client du pipe inattendu (pid {pid}, attendu {Process.Id})");
        Connection = PilotConnection.Start(Pipe, Secret);
        return Connection;
    }

    /// <summary>Attend la fin du processus ; rend son code de sortie, ou null si le delai expire.</summary>
    public async Task<int?> WaitExitAsync(TimeSpan timeout)
    {
        try
        {
            await Process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return Process.ExitCode;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null) await Connection.DisposeAsync().ConfigureAwait(false);
        else Pipe.Dispose();
        try { if (!Process.HasExited) Process.Kill(); } catch { }
        Process.Dispose();
    }
}
