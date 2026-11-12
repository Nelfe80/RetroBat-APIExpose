using NelfePlay.VocalRoom.Protocol;

namespace RetroBat.Api.VocalRoom;

/// <summary>L'exe lance et connecte au pipe (PID du client verifie), avant la poignee de main.</summary>
public interface IVocalRoomProcessus : IAsyncDisposable
{
    int Pid { get; }

    /// <summary>Le cote pilote du protocole, sur le pipe deja connecte.</summary>
    PilotConnection Connexion { get; }

    /// <summary>Termine quand le processus est sorti.</summary>
    Task Sortie { get; }

    /// <summary>Le code de sortie, une fois sorti.</summary>
    int? CodeDeSortie { get; }

    /// <summary>Attend la sortie ; rend son code, ou null si le delai expire.</summary>
    Task<int?> AttendreLaSortieAsync(TimeSpan delai);

    /// <summary>Dernier recours, apres un shutdown sans effet.</summary>
    void Tuer();
}

/// <summary>Lance l'exe comme le contrat le dit (§2 de PROTOCOL.md). Remplace par un faux exe dans les tests.</summary>
public interface IVocalRoomLanceur
{
    /// <summary>Lance et attend la connexion ; leve si l'exe ne se connecte pas, ou si un autre processus le fait.</summary>
    Task<IVocalRoomProcessus> LancerAsync(string exe, string? dossierDuJournal, CancellationToken ct);
}

/// <summary>
/// Le vrai lancement, repris du lot A (<see cref="VocalRoomLaunch"/>) : pipe reserve au compte Windows courant, exe sans
/// fenetre ni console, arguments non secrets, secret d'appairage sur l'entree standard, PID du client verifie.
/// </summary>
public sealed class VocalRoomLanceur : IVocalRoomLanceur
{
    /// <summary>Le temps laisse a l'exe pour quitter l'appel quand l'API disparait (§6.1 : court).</summary>
    public const int DelaiDeSortieMs = 2000;

    private static readonly TimeSpan DelaiDeConnexion = TimeSpan.FromSeconds(10);

    public async Task<IVocalRoomProcessus> LancerAsync(string exe, string? dossierDuJournal, CancellationToken ct)
    {
        var lancement = await VocalRoomLaunch.StartAsync(new LaunchOptions(exe, dossierDuJournal, DelaiDeSortieMs)).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await lancement.AcceptAsync(DelaiDeConnexion).ConfigureAwait(false);
            return new Processus(lancement);
        }
        catch
        {
            await lancement.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Processus(VocalRoomLaunch lancement) : IVocalRoomProcessus
    {
        public int Pid => lancement.Process.Id;

        public PilotConnection Connexion => lancement.Connection!;

        public Task Sortie { get; } = lancement.Process.WaitForExitAsync();

        public int? CodeDeSortie
        {
            get
            {
                try { return lancement.Process.HasExited ? lancement.Process.ExitCode : null; }
                catch (InvalidOperationException) { return null; }
            }
        }

        public Task<int?> AttendreLaSortieAsync(TimeSpan delai) => lancement.WaitExitAsync(delai);

        public void Tuer()
        {
            try
            {
                if (!lancement.Process.HasExited) lancement.Process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public ValueTask DisposeAsync() => lancement.DisposeAsync();
    }
}
