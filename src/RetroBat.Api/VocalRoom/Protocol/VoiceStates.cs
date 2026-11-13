// COPIE VERSIONNEE du contrat IPC de NelfePlay-VocalRoom (depot prive Nelfe80/NelfePlay-VocalRoom,
// src/NelfePlay.VocalRoom.Protocol, commit f3f0095, protocole 1). Ne pas modifier ici : changer le depot VocalRoom,
// puis recopier (voir VocalRoomProtocolSource et OUTILS_COMMUNS.md, « Vocal Discord »).
namespace NelfePlay.VocalRoom.Protocol;

/// <summary>Etats vocaux du §11 du CDC, tels qu'ils passent sur le fil (<see cref="VoiceStates.Wire"/>).</summary>
public enum VoiceState
{
    Stopped,
    Starting,
    AuthRequired,
    Ready,
    LobbyPending,
    Connecting,
    InCall,
    Reconnecting,
    Detaching,
    Error,
}

/// <summary>
/// Noms sur le fil et transitions permises. Regle du CDC : seule une confirmation du SDK produit <c>IN_CALL</c>
/// (jamais « StartCall rendu = connecte ») ; la machine le garantit en n'acceptant IN_CALL que depuis CONNECTING ou
/// RECONNECTING.
/// </summary>
public static class VoiceStates
{
    private static readonly Dictionary<VoiceState, string> Names = new()
    {
        [VoiceState.Stopped] = "STOPPED",
        [VoiceState.Starting] = "STARTING",
        [VoiceState.AuthRequired] = "AUTH_REQUIRED",
        [VoiceState.Ready] = "READY",
        [VoiceState.LobbyPending] = "LOBBY_PENDING",
        [VoiceState.Connecting] = "CONNECTING",
        [VoiceState.InCall] = "IN_CALL",
        [VoiceState.Reconnecting] = "RECONNECTING",
        [VoiceState.Detaching] = "DETACHING",
        [VoiceState.Error] = "ERROR",
    };

    private static readonly Dictionary<VoiceState, VoiceState[]> Allowed = new()
    {
        [VoiceState.Stopped] = [VoiceState.Starting],
        [VoiceState.Starting] = [VoiceState.AuthRequired, VoiceState.Ready, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.AuthRequired] = [VoiceState.Ready, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.Ready] = [VoiceState.LobbyPending, VoiceState.Connecting, VoiceState.AuthRequired, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.LobbyPending] = [VoiceState.Connecting, VoiceState.Detaching, VoiceState.AuthRequired, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.Connecting] = [VoiceState.InCall, VoiceState.Reconnecting, VoiceState.Detaching, VoiceState.AuthRequired, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.InCall] = [VoiceState.Reconnecting, VoiceState.Detaching, VoiceState.AuthRequired, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.Reconnecting] = [VoiceState.InCall, VoiceState.Detaching, VoiceState.AuthRequired, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.Detaching] = [VoiceState.Ready, VoiceState.AuthRequired, VoiceState.Error, VoiceState.Stopped],
        [VoiceState.Error] = [VoiceState.Starting, VoiceState.AuthRequired, VoiceState.Ready, VoiceState.Stopped],
    };

    public static string Wire(this VoiceState s) => Names[s];

    public static bool TryParse(string? wire, out VoiceState state)
    {
        foreach (var (k, v) in Names)
        {
            if (v == wire) { state = k; return true; }
        }
        state = VoiceState.Stopped;
        return false;
    }

    public static bool CanMove(VoiceState from, VoiceState to) => from == to || Allowed[from].Contains(to);

    /// <summary>Etats ou une session (room_id, generation) est attachee.</summary>
    public static bool HasSession(VoiceState s) =>
        s is VoiceState.LobbyPending or VoiceState.Connecting or VoiceState.InCall or VoiceState.Reconnecting or VoiceState.Detaching;
}

/// <summary>Machine a etats : refuse une transition interdite au lieu de la subir.</summary>
public sealed class VoiceStateMachine
{
    public VoiceState Current { get; private set; } = VoiceState.Stopped;

    public event Action<VoiceState, VoiceState>? Changed;

    /// <summary>Passe a <paramref name="to"/> si la transition est permise. Rend false sinon (etat inchange).</summary>
    public bool TryMove(VoiceState to)
    {
        if (!VoiceStates.CanMove(Current, to)) return false;
        if (Current == to) return true;
        var from = Current;
        Current = to;
        Changed?.Invoke(from, to);
        return true;
    }
}
