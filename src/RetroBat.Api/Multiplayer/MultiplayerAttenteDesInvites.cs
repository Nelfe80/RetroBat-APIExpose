using System.Diagnostics;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE DEPART ATTEND TOUT LE MONDE (27/10, Tetris TGM2 : « les pieces tombent deja quand le 2e joueur arrive »). Le depart
/// pret se pose chez l'hote AVANT que les invites se connectent : le match commencait seul, et l'invite arrivait quelques
/// secondes plus tard, ses premieres pieces deja tombees.
/// <list type="bullet">
/// <item>LA PAUSE (1.9.105) : l'hote se mettait en pause et avancait d'une image toutes les 500 ms pour accueillir l'invite.
/// Un invite connecte pendant cette pause ne jouait plus une seule image apres la reprise, et RetroArch 1.22.2 le raccrochait
/// 6 s plus tard (Title Fight puis SF2CE entre deux bornes, le 27/10 au soir ; reproduit au banc par le relais comme en
/// connexion directe). Hors service en 1.9.108.</item>
/// <item>RECHARGER LE DEPART une fois l'invite arrive ne marche pas : il recoit « Netplay state load out of order » et
/// decroche, meme dans une partie saine (banc du 27/10).</item>
/// <item>LE RALENTI (1.9.109) : l'hote passe au ralenti x30 (<c>slowmotion_ratio</c> et la commande SLOWMOTION) jusqu'a
/// l'arrivee des invites. RetroArch continue de jouer des images, donc de traiter le reseau : l'invite rejoint, et a la
/// vitesse normale les deux jeux tournent sur la meme image (banc_pause_relais.py --ralenti=30, captures identiques). Quinze
/// secondes d'attente font une demi-seconde de jeu. Le ralenti passe par la synchronisation sur le son : sans son, il
/// n'agit pas. Le bilan du wrapper n'y voit ni avance rapide ni triche, et le garde-fou de la capture tient ce qui tourne
/// sous 20 images par seconde pour une pause.</item>
/// <item>Une partie certifiee (le 1CC MULTI, a venir) ne commence qu'au retour a la vitesse normale : le ralenti n'y entre
/// jamais.</item>
/// </list>
/// La vitesse se lit au temps de reponse de GET_STATUS : RetroArch ne traite ses commandes qu'entre deux images, ~16 ms a
/// vitesse normale, ~500 ms au ralenti x30 (sonde_ralenti.py). Une bascule perdue laisserait le match au ralenti : chaque
/// bascule se verifie, et la partie est gardee a vitesse normale jusqu'a sa fin.
/// </summary>
public sealed class MultiplayerAttenteDesInvites
{
    private readonly ICommandesRetroArch _commandes;

    public MultiplayerAttenteDesInvites(ICommandesRetroArch commandes) => _commandes = commandes;

    /// <summary>L'attente du depart pret, au ralenti.</summary>
    public static bool EnService { get; } = true;

    /// <summary>
    /// Le journal de RetroArch : le lanceur le lui donne a chaque lancement (<c>--log-file</c>), et un <c>log_dir</c> pose dans
    /// retroarch.cfg n'y change rien (vu le 27/10 dans emulatorLauncher.log).
    /// </summary>
    public static string CheminDuJournal => RetroBatPaths.EsLaunchStdoutLogPath;

    /// <summary>Les reglages de RetroArch le temps de la partie, rendus apres comme ceux de la place.</summary>
    public static IReadOnlyDictionary<string, string> Reglages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["slowmotion_ratio"] = "30.000000",
    };

    /// <summary>Au-dela, GET_STATUS repond au rythme du ralenti (~500 ms) et non entre deux images (~16 ms).</summary>
    internal static readonly TimeSpan SeuilDuRalenti = TimeSpan.FromMilliseconds(150);

    /// <summary>Le temps laisse a une bascule de vitesse avant de la mesurer.</summary>
    internal static TimeSpan DelaiDeBascule { get; set; } = TimeSpan.FromMilliseconds(800);

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

    /// <summary>
    /// Le jeu est-il au ralenti ? Le temps de reponse median de trois GET_STATUS, compare a <see cref="SeuilDuRalenti"/> ;
    /// null si RetroArch ne repond pas ou ne joue pas.
    /// </summary>
    internal async Task<bool?> AuRalentiAsync(CancellationToken ct)
    {
        var mesures = new List<TimeSpan>();
        for (var i = 0; i < 3; i++)
        {
            var chrono = Stopwatch.StartNew();
            if (Etat(await _commandes.DemanderAsync("GET_STATUS", ct, 1500).ConfigureAwait(false)) != "PLAYING")
            {
                return null;
            }

            mesures.Add(chrono.Elapsed);
        }

        mesures.Sort();
        return mesures[1] > SeuilDuRalenti;
    }

    /// <summary>
    /// Bascule la vitesse jusqu'a obtenir <paramref name="ralenti"/>, deux bascules au plus. Rend vrai si la vitesse voulue
    /// est mesuree.
    /// </summary>
    private async Task<bool> BasculerAsync(bool ralenti, CancellationToken ct)
    {
        for (var essai = 0; essai < 2; essai++)
        {
            await _commandes.EnvoyerAsync("SLOWMOTION", ct).ConfigureAwait(false);
            await Task.Delay(DelaiDeBascule, ct).ConfigureAwait(false);
            if (await AuRalentiAsync(ct).ConfigureAwait(false) == ralenti)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Met le jeu au ralenti et le verifie. Faux si RetroArch ne joue pas ou n'a pas ralenti (la partie part alors comme avant).</summary>
    public async Task<bool> MettreAuRalentiAsync(CancellationToken ct)
    {
        if (await AuRalentiAsync(ct).ConfigureAwait(false) is not false)
        {
            return false;
        }

        if (await BasculerAsync(true, ct).ConfigureAwait(false))
        {
            return true;
        }

        // Une bascule qui ne se mesure pas ne laisse pas le jeu dans un etat douteux.
        if (await AuRalentiAsync(ct).ConfigureAwait(false) == true)
        {
            await BasculerAsync(false, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Remet le jeu a vitesse normale, et le verifie. Faux s'il est reste au ralenti.</summary>
    public async Task<bool> RetrouverLaVitesseAsync(CancellationToken ct)
    {
        for (var essai = 0; essai < 3; essai++)
        {
            switch (await AuRalentiAsync(ct).ConfigureAwait(false))
            {
                case false:
                    return true;
                case true:
                    if (await BasculerAsync(false, ct).ConfigureAwait(false))
                    {
                        return true;
                    }

                    break;
                default:
                    await Task.Delay(DelaiDeBascule, ct).ConfigureAwait(false);
                    break;
            }
        }

        return await AuRalentiAsync(ct).ConfigureAwait(false) == false;
    }

    /// <summary>
    /// Attend que <paramref name="attendus"/> invites aient rejoint leur place dans le journal (relu toutes les 500 ms), au
    /// plus <paramref name="patience"/>. Rend le nombre d'invites vus.
    /// </summary>
    public async Task<int> AttendreLesInvitesAsync(string journal, int attendus, TimeSpan patience, CancellationToken ct)
    {
        var limite = DateTime.UtcNow + patience;
        while (true)
        {
            var vus = InvitesALeurPlace(LireLesLignes(journal)).Count;
            if (vus >= attendus || DateTime.UtcNow >= limite)
            {
                return vus;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rend quand le dernier invite est parti : au moins un invite a rejoint sa place dans le journal de l'hote (relu a ce
    /// <paramref name="rythme"/>), puis plus aucun n'y est. Jusque-la, attend ; l'annulation (fin de partie) l'arrete.
    /// </summary>
    public async Task AttendreLeDepartDesInvitesAsync(string journal, TimeSpan rythme, CancellationToken ct)
    {
        var vus = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var presents = InvitesALeurPlace(LireLesLignes(journal)).Count;
            if (presents > 0)
            {
                vus = true;
            }
            else if (vus)
            {
                return;
            }

            await Task.Delay(rythme, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Apres le retour a la vitesse normale, jusqu'a l'annulation (fin de partie) : un ralenti mesure deux fois de suite, a
    /// <paramref name="rythme"/> d'ecart, est leve (une bascule perdue, une touche de ralenti). Rend le nombre de ralentis leves.
    /// </summary>
    public async Task<int> GarderLaVitesseAsync(TimeSpan rythme, CancellationToken ct)
    {
        var levees = 0;
        var vu = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(rythme, ct).ConfigureAwait(false);
                var ralenti = await AuRalentiAsync(ct).ConfigureAwait(false) == true;
                if (ralenti && vu)
                {
                    if (await BasculerAsync(false, ct).ConfigureAwait(false))
                    {
                        levees++;
                    }

                    vu = false;
                    continue;
                }

                vu = ralenti;
            }
        }
        catch (OperationCanceledException)
        {
            // La partie est finie.
        }

        return levees;
    }
}
