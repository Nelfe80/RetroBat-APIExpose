// COPIE VERSIONNEE du contrat IPC de NelfePlay-VocalRoom (depot prive Nelfe80/NelfePlay-VocalRoom,
// src/NelfePlay.VocalRoom.Protocol, commit f3f0095, protocole 1). Ne pas modifier ici : changer le depot VocalRoom,
// puis recopier (voir VocalRoomProtocolSource et OUTILS_COMMUNS.md, « Vocal Discord »).
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace NelfePlay.VocalRoom.Protocol;

/// <summary>
/// Qui est a l'autre bout du pipe. Complement de l'ACL (<see cref="PipeOptions.CurrentUserOnly"/>) et du secret
/// d'appairage : le PID seul ne suffit pas (§12 du CDC), mais il ecarte un processus qui aurait pris le nom du pipe.
/// </summary>
public static class PipePeers
{
    /// <summary>Cote APIExpose : PID du client connecte (doit etre l'exe qu'elle a lance).</summary>
    public static int? ClientProcessId(NamedPipeServerStream server) =>
        GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) ? (int)pid : null;

    /// <summary>Cote VocalRoom : PID du serveur du pipe (doit etre le parent annonce par --parent-pid).</summary>
    public static int? ServerProcessId(NamedPipeClientStream client) =>
        GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid) ? (int)pid : null;

    /// <summary>
    /// Le serveur tel qu'APIExpose doit le creer : une seule instance, octets, asynchrone, reserve au compte Windows
    /// courant (ACL posee par .NET).
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName) =>
        new(pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>Le client tel que VocalRoom l'ouvre : refuse un serveur qui n'appartient pas au compte courant.</summary>
    public static NamedPipeClientStream CreateClient(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);
}
