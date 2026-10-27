using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE DEPART ATTEND TOUT LE MONDE (27/10, Tetris TGM2 : « les pieces tombent deja quand le 2e joueur arrive »). Le depart
/// pret se pose chez l'hote AVANT que les invites se connectent : le match commencait seul, et l'invite arrivait quelques
/// secondes plus tard, ses premieres pieces deja tombees. L'hote met donc le jeu en pause juste apres son depart pret, et
/// le reprend quand le journal de RetroArch dit que chaque invite a rejoint sa place.
/// <list type="bullet">
/// <item>Un RetroArch en pause ne traite pas le reseau : la connexion de l'invite attend dans la file du systeme, et
/// l'invite abandonne au bout de 12 s (banc du 27/10). Pendant l'attente, l'hote avance donc d'une image toutes les
/// 500 ms (FRAMEADVANCE) : assez pour accueillir l'invite, pas pour faire avancer le jeu (TGM2 reste sur « ROUND 1 GO »,
/// le chrono a 02:40:00). L'invite rejoint en pause ; a la reprise, memoire identique, aucune desynchronisation.</item>
/// <item>RetroArch refuse toute pause en netplay sans <c>netplay_allow_pausing</c>, meme seul (banc du 27/10) : l'hote
/// le recoit le temps de la partie (le lanceur ne reecrit pas cette cle). Son journal est celui que le lanceur lui donne
/// a chaque lancement (<c>--log-file es_launch_stdout.log -verbose</c>, MULTIPLAYER compris), vide a son demarrage.</item>
/// <item>Recharger l'etat a l'arrivee de l'invite ne marche pas : l'invite recoit « Netplay state load out of order » et se
/// deconnecte (banc du 27/10).</item>
/// <item>La pause permise, un joueur pourrait figer la partie de l'autre : apres la reprise, une pause qui dure est levee.</item>
/// </list>
/// </summary>
public sealed class MultiplayerAttenteDesInvites
{
    private readonly ICommandesRetroArch _commandes;

    public MultiplayerAttenteDesInvites(ICommandesRetroArch commandes) => _commandes = commandes;

    /// <summary>
    /// Le journal de RetroArch : le lanceur le lui donne a chaque lancement (<c>--log-file</c>), et un <c>log_dir</c> pose dans
    /// retroarch.cfg n'y change rien (vu le 27/10 dans emulatorLauncher.log).
    /// </summary>
    public static string CheminDuJournal => RetroBatPaths.EsLaunchStdoutLogPath;

    /// <summary>Les reglages de RetroArch le temps de la partie, rendus apres comme ceux de la place.</summary>
    public static IReadOnlyDictionary<string, string> Reglages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["netplay_allow_pausing"] = "true",
    };

    // « [INFO] [Netplay] client_pause has joined as player 2 (ping: 130 ms) », ou avec deux ports (Title Fight) « client_ports
    // has joined with input devices 2, 4 (ping: 46 ms) » ; « [INFO] [Netplay] "client_rech" has disconnected ».
    private static readonly Regex Arrivee = new(@"\[Netplay\]\s+""?(?<nom>.+?)""?\s+has joined (?:as player|with input devices) (?<place>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Depart = new(@"\[Netplay\]\s+""?(?<nom>.+?)""?\s+has (?:disconnected|left)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Les invites a leur place d'apres le journal de l'hote : pseudo et place de RetroArch. L'hote lui-meme (« You have
    /// joined ») et un spectateur ne comptent pas ; un invite parti n'y est plus. Pur.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> InvitesALeurPlace(IEnumerable<string> lignes)
    {
        var invites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ligne in lignes)
        {
            if (Arrivee.Match(ligne) is { Success: true } a && a.Groups["nom"].Value != "You")
            {
                invites[a.Groups["nom"].Value] = int.Parse(a.Groups["place"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (Depart.Match(ligne) is { Success: true } d)
            {
                invites.Remove(d.Groups["nom"].Value);
            }
        }

        return invites;
    }

    /// <summary>L'etat de RetroArch d'apres GET_STATUS : PLAYING, PAUSED, autre chose, ou null sans reponse. Pur.</summary>
    internal static string? Etat(string? reponse)
    {
        if (reponse is null)
        {
            return null;
        }

        var morceaux = reponse.Trim().Split(' ', 3);
        return morceaux.Length >= 2 && morceaux[0] == "GET_STATUS" ? morceaux[1] : null;
    }

    private async Task<string?> EtatAsync(CancellationToken ct)
        => Etat(await _commandes.DemanderAsync("GET_STATUS", ct).ConfigureAwait(false));

    /// <summary>Met le jeu en pause et le verifie. Faux si RetroArch ne s'est pas arrete (la partie part alors comme avant).</summary>
    public async Task<bool> MettreEnPauseAsync(CancellationToken ct)
    {
        if (await EtatAsync(ct).ConfigureAwait(false) != "PLAYING")
        {
            return false;
        }

        await _commandes.EnvoyerAsync("PAUSE_TOGGLE", ct).ConfigureAwait(false);
        for (var essai = 0; essai < 6; essai++)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            if (await EtatAsync(ct).ConfigureAwait(false) == "PAUSED")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>L'hote en pause avance d'une image a ce rythme pendant l'attente : son netplay accueille l'invite.</summary>
    internal static readonly TimeSpan RythmeDesImages = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Attend que <paramref name="attendus"/> invites aient rejoint leur place dans le journal (relu toutes les 500 ms), au
    /// plus <paramref name="patience"/>, en avancant d'une image a chaque fois. Rend le nombre d'invites vus.
    /// </summary>
    public async Task<int> AttendreLesInvitesAsync(string journal, int attendus, TimeSpan patience, CancellationToken ct)
    {
        var limite = DateTime.UtcNow + patience;
        var vus = 0;
        while (true)
        {
            vus = InvitesALeurPlace(LireLesLignes(journal)).Count;
            if (vus >= attendus || DateTime.UtcNow >= limite)
            {
                return vus;
            }

            await _commandes.EnvoyerAsync("FRAMEADVANCE", ct).ConfigureAwait(false);
            await Task.Delay(RythmeDesImages, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Le journal en cours d'ecriture, lu sans gener RetroArch ; vide s'il n'existe pas encore.</summary>
    private static IEnumerable<string> LireLesLignes(string journal)
    {
        try
        {
            using var flux = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lecteur = new StreamReader(flux);
            return lecteur.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Reprend le jeu s'il est en pause, et le verifie. Faux s'il est reste en pause.</summary>
    public async Task<bool> ReprendreAsync(CancellationToken ct)
    {
        for (var essai = 0; essai < 3; essai++)
        {
            var etat = await EtatAsync(ct).ConfigureAwait(false);
            if (etat == "PLAYING")
            {
                return true;
            }

            if (etat == "PAUSED")
            {
                await _commandes.EnvoyerAsync("PAUSE_TOGGLE", ct).ConfigureAwait(false);
            }

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        return await EtatAsync(ct).ConfigureAwait(false) == "PLAYING";
    }

    /// <summary>
    /// Apres la reprise, jusqu'a l'annulation (fin de partie) : une pause vue deux fois de suite, a une seconde d'ecart, est
    /// levee. Rend le nombre de pauses levees.
    /// </summary>
    public async Task<int> GarderSansPauseAsync(CancellationToken ct)
    {
        var levees = 0;
        var vue = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var etat = await EtatAsync(ct).ConfigureAwait(false);
                if (etat == "PAUSED" && vue)
                {
                    await _commandes.EnvoyerAsync("PAUSE_TOGGLE", ct).ConfigureAwait(false);
                    levees++;
                    vue = false;
                    continue;
                }

                vue = etat == "PAUSED";
            }
        }
        catch (OperationCanceledException)
        {
            // La partie est finie.
        }

        return levees;
    }
}
