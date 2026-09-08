using System.Net.Sockets;
using System.Text;

namespace RetroBat.Api.Replay.Runtime;

/// <summary>Réponse de GET_STATUS (état + identité du jeu courant).</summary>
public sealed record RaStatus(string State, string System, string Game, string? Crc32)
{
    public bool ContentLoaded => State is "PLAYING" or "PAUSED";
}

/// <summary>Réponse de GET_CONFIG_PARAM active_replay : id flags frame.</summary>
public sealed record RaActiveReplay(long Id, int Flags, long Frame)
{
    public bool Active => Id != 0;
    public bool Recording => Flags == 8;   // validé spike 2026-09-01
    public bool Playing => Flags == 4;
    public static readonly RaActiveReplay Idle = new(0, 0, 0);
}

/// <summary>
/// Pilote l'interface Network Control de RetroArch en UDP (127.0.0.1:55355), déjà
/// activée (network_cmd_enable=true). Commandes validées par le spike : RECORD_REPLAY,
/// HALT_REPLAY, PLAY_REPLAY, SEEK_REPLAY, GET_STATUS, GET_CONFIG_PARAM active_replay.
/// </summary>
public sealed class RetroArchReplayClient
{
    private const string Host = "127.0.0.1";
    private const int Port = 55355;
    private readonly ILogger<RetroArchReplayClient> _logger;

    public RetroArchReplayClient(ILogger<RetroArchReplayClient> logger) => _logger = logger;

    // Commandes d'action (RetroArch ne répond pas).
    public Task RecordAsync(CancellationToken ct) => FireAsync("RECORD_REPLAY", ct);
    public Task HaltAsync(CancellationToken ct) => FireAsync("HALT_REPLAY", ct);
    public Task PlayAsync(CancellationToken ct) => FireAsync("PLAY_REPLAY", ct);
    public Task PauseToggleAsync(CancellationToken ct) => FireAsync("PAUSE_TOGGLE", ct);

    /// <summary>
    /// Photographie l'ecran. RetroArch ecrit le PNG dans son `screenshot_directory` et ne dit
    /// RIEN en retour : c'est a l'appelant de retrouver le fichier apparu. Sert la capture du
    /// moment du record, et plus tard la regeneration de cette meme image depuis un replay.
    /// </summary>
    public Task ScreenshotAsync(CancellationToken ct) => FireAsync("SCREENSHOT", ct);
    public Task NextCheckpointAsync(CancellationToken ct) => FireAsync("NEXT_REPLAY_CHECKPOINT", ct);
    public Task PrevCheckpointAsync(CancellationToken ct) => FireAsync("PREV_REPLAY_CHECKPOINT", ct);

    /// <summary>SEEK_REPLAY &lt;frame&gt; -> "OK &lt;checkpoint&gt;" ou "NO".</summary>
    public Task<string?> SeekAsync(long frame, CancellationToken ct) => QueryAsync($"SEEK_REPLAY {frame}", ct);

    public async Task<RaStatus?> GetStatusAsync(CancellationToken ct)
    {
        var r = await QueryAsync("GET_STATUS", ct).ConfigureAwait(false);
        if (r is null) return null;
        // "GET_STATUS PLAYING mega_drive,Jeu (USA, Europe),crc32=f9394e97"  (le nom peut contenir des virgules)
        var s = r.Trim();
        const string prefix = "GET_STATUS ";
        if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
        var sp = s.IndexOf(' ');
        var state = sp > 0 ? s[..sp] : s;
        if (state is not ("PLAYING" or "PAUSED")) return new RaStatus(state, "", "", null);
        var rest = sp > 0 ? s[(sp + 1)..] : "";
        string? crc = null; var body = rest;
        var crcIdx = rest.LastIndexOf(",crc32=", StringComparison.Ordinal);
        if (crcIdx >= 0) { crc = rest[(crcIdx + 7)..]; body = rest[..crcIdx]; }
        var comma = body.IndexOf(',');
        var system = comma >= 0 ? body[..comma] : body;
        var game = comma >= 0 ? body[(comma + 1)..] : "";
        return new RaStatus(state, system.Trim(), game.Trim(), string.IsNullOrEmpty(crc) ? null : crc.Trim());
    }

    public async Task<RaActiveReplay?> GetActiveReplayAsync(CancellationToken ct)
    {
        var r = await QueryAsync("GET_CONFIG_PARAM active_replay", ct).ConfigureAwait(false);
        if (r is null) return null;
        // "GET_CONFIG_PARAM active_replay 1788241628 8 37"  ou  "... 0 0 0"  ou  "... unsupported"
        var parts = r.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return RaActiveReplay.Idle;
        return long.TryParse(parts[^3], out var id)
            && int.TryParse(parts[^2], out var flags)
            && long.TryParse(parts[^1], out var frame)
            ? new RaActiveReplay(id, flags, frame)
            : RaActiveReplay.Idle;
    }

    public Task<string?> GetVersionAsync(CancellationToken ct) => QueryAsync("VERSION", ct);

    private async Task FireAsync(string cmd, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            var bytes = Encoding.UTF8.GetBytes(cmd);
            await udp.SendAsync(bytes, bytes.Length, Host, Port).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : commande RetroArch {Cmd} échouée", cmd); }
    }

    /// <summary>Envoie une commande et lit la réponse UDP (timeout court). null si pas de réponse.
    /// 250 ms : sur borne chargée on rend la main vite au lieu de bloquer le thread appelant.</summary>
    public async Task<string?> QueryAsync(string cmd, CancellationToken ct, int timeoutMs = 250)
    {
        try
        {
            using var udp = new UdpClient();
            var bytes = Encoding.UTF8.GetBytes(cmd);
            await udp.SendAsync(bytes, bytes.Length, Host, Port).WaitAsync(ct).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(result.Buffer);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { _logger.LogDebug(ex, "Replay : requête RetroArch {Cmd} échouée", cmd); return null; }
    }
}
