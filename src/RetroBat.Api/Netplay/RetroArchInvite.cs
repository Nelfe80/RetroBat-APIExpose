using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroBat.Api.Netplay;

/// <summary>
/// LA PARTIE DE L'INVITE, VUE DE SA CONNEXION AU RELAIS (2026-10-02).
///
/// LE MOT DE PASSE. Un client RetroArch ne lit pas son mot de passe dans sa configuration : des que
/// l'hote en exige un, il ouvre la saisie « Enter netplay server password » et envoie ce qu'on y
/// tape (netplay_frontend.c, RetroArch 1.22.2). `netplay_password` ne sert qu'a l'hote, pour
/// verifier. Sans saisie, l'hote coupe, et le client affiche « Failed to receive nickname from
/// host ». La borne tape donc le mot de passe a la place du joueur : des caracteres postes a la
/// fenetre de RetroArch (WM_CHAR), puis Entree. RetroArch ne journalise pas l'ouverture de la
/// saisie ; on la deduit de sa connexion au relais, et on tape plusieurs fois. C'est sans danger :
/// hors saisie, RetroArch ignore un caractere qui ne vient pas d'une touche (code RETROK_UNKNOWN),
/// et un message poste n'est pas une touche vue par DirectInput, donc rien n'atteint le jeu.
///
/// LE DEPART DE L'HOTE. Quand l'hote ferme sa partie, RetroArch ecrit « Netplay disconnected » et
/// continue le jeu EN LOCAL chez l'invite : le personnage de l'hote ne recoit plus rien et reste
/// immobile (test du 2026-10-02 avec RetroLife). La connexion au relais tombe a ce moment-la ; si
/// RetroArch tourne encore, la partie en ligne est finie, et la borne le dit puis ferme le jeu.
/// </summary>
internal static class RetroArchInvite
{
    /// <summary>Le temps laisse a RetroArch pour charger le jeu et joindre le relais.</summary>
    private static readonly TimeSpan PatienceConnexion = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Les essais, comptes depuis la connexion au relais : la saisie s'ouvre quand l'en-tete de
    /// l'hote arrive, un aller-retour par le relais plus tard. Un essai qui tombe avant n'a aucun
    /// effet, celui qui suit la remplit ; apres une saisie reussie, les suivants sont ignores.
    /// </summary>
    internal static readonly TimeSpan[] Essais =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15),
    };

    /// <summary>Une connexion absente deux lectures de suite (une par seconde) est tombee.</summary>
    internal const int LecturesAvantCoupure = 2;

    private const uint WmChar = 0x0102;
    private const int PortDesCommandes = 55355;

    /// <summary>
    /// Suit la partie de l'invite : attend que RetroArch joigne le relais, tape le mot de passe s'il
    /// y en a un, puis guette la coupure. <paramref name="connexionPerdue"/> est appele une fois,
    /// si la connexion tombe alors que RetroArch tourne encore.
    /// </summary>
    public static async Task SuivreAsync(string relais, int port, string motDePasse, Func<Task> connexionPerdue,
        ILogger logger, CancellationToken ct = default)
    {
        if (port <= 0) return;
        try
        {
            var adresses = await AdressesAsync(relais, ct).ConfigureAwait(false);
            var limite = DateTime.UtcNow + PatienceConnexion;
            while (!ConnecteAuRelais(adresses, port))
            {
                if (DateTime.UtcNow > limite)
                {
                    logger.LogInformation("Netplay : RetroArch ne s'est pas connecte au relais en {Secondes} s.",
                        (int)PatienceConnexion.TotalSeconds);
                    return;
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }

            if (motDePasse.Length > 0)
            {
                var connexion = DateTime.UtcNow;
                for (var i = 0; i < Essais.Length; i++)
                {
                    var attente = connexion + Essais[i] - DateTime.UtcNow;
                    if (attente > TimeSpan.Zero) await Task.Delay(attente, ct).ConfigureAwait(false);
                    if (!ConnecteAuRelais(adresses, port)) break;
                    if (Taper(motDePasse))
                    {
                        logger.LogInformation("Netplay : mot de passe tape dans RetroArch (essai {Essai}/{Total}).", i + 1, Essais.Length);
                    }
                }
            }

            var absences = 0;
            while (!ct.IsCancellationRequested)
            {
                absences = ConnecteAuRelais(adresses, port) ? 0 : absences + 1;
                if (absences >= LecturesAvantCoupure)
                {
                    if (RetroArchTourne())
                    {
                        logger.LogInformation("Netplay : la connexion a l'hote est tombee, RetroArch tourne encore : fin de la partie en ligne.");
                        await connexionPerdue().ConfigureAwait(false);
                    }
                    return;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Netplay : suivi de la partie de l'invite interrompu.");
        }
    }

    /// <summary>Ferme RetroArch proprement (commande QUIT, comme LiveContest) : il sauvegarde en sortant.</summary>
    public static async Task FermerAsync()
    {
        using var udp = new UdpClient();
        var commande = Encoding.ASCII.GetBytes("QUIT");
        await udp.SendAsync(commande, commande.Length, "127.0.0.1", PortDesCommandes).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyCollection<IPAddress>> AdressesAsync(string relais, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relais)) return [];
        try
        {
            var trouvees = await Dns.GetHostAddressesAsync(relais.Trim(), ct).ConfigureAwait(false);
            return trouvees.Select(Normaliser).ToHashSet();
        }
        catch (Exception)
        {
            // Sans resolution, le port suffit : aucun autre programme de la borne ne parle au 55435.
            return [];
        }
    }

    private static IPAddress Normaliser(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private static bool ConnecteAuRelais(IReadOnlyCollection<IPAddress> adresses, int port)
    {
        try
        {
            return ConnexionVers(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections(), adresses, port);
        }
        catch (NetworkInformationException)
        {
            // Table illisible : on la tient pour presente, une coupure ne se devine pas.
            return true;
        }
    }

    /// <summary>Une connexion etablie vers le relais (son adresse si elle est connue, et son port).</summary>
    internal static bool ConnexionVers(IEnumerable<TcpConnectionInformation> connexions, IReadOnlyCollection<IPAddress> adresses, int port)
        => connexions.Any(c => c.State == TcpState.Established
                               && c.RemoteEndPoint.Port == port
                               && (adresses.Count == 0 || adresses.Contains(Normaliser(c.RemoteEndPoint.Address))));

    /// <summary>Les caracteres postes a la fenetre de RetroArch : le mot de passe, puis Entree.</summary>
    internal static IReadOnlyList<char> Frappes(string motDePasse) => (motDePasse + "\r").ToCharArray();

    private static bool Taper(string motDePasse)
    {
        var fenetre = FenetreDeRetroArch();
        if (fenetre == IntPtr.Zero) return false;
        foreach (var c in Frappes(motDePasse))
        {
            if (!PostMessage(fenetre, WmChar, (IntPtr)c, (IntPtr)1)) return false;
        }
        return true;
    }

    private static bool RetroArchTourne()
    {
        var processus = Process.GetProcessesByName("retroarch");
        foreach (var p in processus) p.Dispose();
        return processus.Length > 0;
    }

    private static IntPtr FenetreDeRetroArch()
    {
        var processus = Process.GetProcessesByName("retroarch");
        try
        {
            foreach (var p in processus)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch (Exception)
                {
                    // Termine entre-temps.
                }
            }
            return IntPtr.Zero;
        }
        finally
        {
            foreach (var p in processus) p.Dispose();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
