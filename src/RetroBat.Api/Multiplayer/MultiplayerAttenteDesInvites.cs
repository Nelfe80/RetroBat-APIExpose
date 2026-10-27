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

    /// <summary>
    /// L'attente du depart pret, au ralenti : HORS SERVICE (1.9.111, 27/10 au soir). Entre deux bornes eloignees (SF2CE,
    /// hote en francais), le retour a la vitesse normale ne se mesurait pas : une fois l'invite arrive, RetroArch rattrape
    /// l'ecart entre les deux jeux, ses reponses ralentissent, et la bascule repartait au ralenti ; l'invite restait en
    /// avance, le jeu fige au ralenti. Le banc, sur une seule machine sans latence, ne le montrait pas.
    /// </summary>
    public static bool EnService { get; } = false;

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
    internal static TimeSpan SeuilDuRalenti { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Le temps laisse a une bascule de vitesse avant de la mesurer.</summary>
    internal static TimeSpan DelaiDeBascule { get; set; } = TimeSpan.FromMilliseconds(800);

    // LE JOURNAL SUIT LA LANGUE DE LA BORNE (27/10, borne en francais : « NELFEPLAY_Nelfe80_2b56 à rejoint le jeu en tant que
    // joueur 2 (ping: 499 ms) ») : RetroArch traduit ses messages (intl/msg_hash_*.h), et une borne en francais ne voyait
    // jamais l'invite. Ce qui ne se traduit pas : notre pseudo de netplay (NetplaySettings.ComposerPseudo, NELFEPLAY_ et de
    // l'ASCII), et « (ping: N ms) », que RetroArch ajoute en dur a l'arrivee d'un JOUEUR (jamais d'un spectateur, ni de
    // l'hote lui-meme). Dans toutes ses langues, la place vient apres le pseudo (« as player 2 », « with input devices 2, 4 »).
    private static readonly Regex AvecPing = new(@"\[Netplay\]\s+(?<message>.+?)\s+\(ping: -?\d+ ms\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PseudoNelfePlay = new(@"NELFEPLAY_[A-Za-z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Nombre = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // En anglais, pour un autre pseudo (les bancs) : « client_pause has joined as player 2 (ping: 130 ms) », « client_ports has
    // joined with input devices 2, 4 (ping: 46 ms) » ; « [INFO] [Netplay] "client_rech" has disconnected ».
    private static readonly Regex Arrivee = new(@"\[Netplay\]\s+""?(?<nom>.+?)""?\s+has joined (?:as player|with input devices) (?<place>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Depart = new(@"\[Netplay\]\s+""?(?<nom>.+?)""?\s+has (?:disconnected|left)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Les invites a leur place d'apres le journal de l'hote, dans toutes les langues de RetroArch : pseudo et place. L'hote
    /// lui-meme (« You have joined ») et un spectateur ne comptent pas ; un invite parti n'y est plus quand le journal le dit
    /// en anglais (dans les autres langues, son depart se voit au reseau : <see cref="AttendreLeDepartDesInvitesAsync"/>). Pur.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> InvitesALeurPlace(IEnumerable<string> lignes)
    {
        var invites = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ligne in lignes)
        {
            if (AvecPing.Match(ligne.TrimEnd('\r')) is { Success: true } p && PseudoNelfePlay.Match(p.Groups["message"].Value) is { Success: true } pseudo
                && Nombre.Match(p.Groups["message"].Value, pseudo.Index + pseudo.Length) is { Success: true } place)
            {
                invites[pseudo.Value] = int.Parse(place.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (Arrivee.Match(ligne) is { Success: true } a && a.Groups["nom"].Value != "You")
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

    /// <summary>Le temps qu'un lien d'invite au relais doit tenir pour compter comme une arrivee que le journal n'a pas dite.</summary>
    internal static TimeSpan LienQuiTient { get; set; } = TimeSpan.FromSeconds(8);

    // « Queued tunnel link connection » et « Tunnel link connection completed » : RetroArch les ecrit en dur (sans
    // traduction, vu sur la borne en francais de Falken), des que le relais lui annonce un invite, avant meme qu'il se
    // connecte : le premier signe de son arrivee.
    private static readonly string[] LignesDuTunnel = ["[Netplay] Queued tunnel link connection", "[Netplay] Tunnel link connection completed"];

    /// <summary>
    /// LA BOUCLE DU DEPART (1.9.112, idee du user) : tant qu'aucun invite n'est connecte, l'hote recharge son depart pret,
    /// sans pause ni ralenti ; il s'arrete au premier signe d'un invite. Banc banc_pause_relais.py --boucle, par le relais
    /// avec 15 s d'attente : 30 rechargements, l'invite arrive 2,5 s apres la fin de la boucle, les deux jeux sur la meme
    /// image, et le bilan du wrapper ne compte aucun chargement. A trois ou quatre, les invites suivants arrivent plus tard.
    /// </summary>
    public static bool BoucleEnService { get; } = true;

    /// <summary>Le temps entre deux rechargements du depart.</summary>
    internal static TimeSpan RythmeDeLaBoucle { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Un invite se connecte-t-il, d'apres le journal de l'hote : le relais l'annonce, ou il a rejoint sa place. Pur.</summary>
    internal static bool UnInviteSeConnecte(IReadOnlyCollection<string> lignes)
        => lignes.Any(ligne => LignesDuTunnel.Any(signe => ligne.Contains(signe, StringComparison.Ordinal))) || InvitesALeurPlace(lignes).Count > 0;

    /// <summary>
    /// Reprend le depart (<paramref name="reprendre"/>) a chaque tour, au plus <paramref name="patience"/>, tant qu'aucun invite
    /// ne se connecte : ni dans le journal (<see cref="UnInviteSeConnecte"/>), ni au relais (<paramref name="liens"/>). Un
    /// depart qui ne se reprend pas arrete la boucle (la partie continue telle quelle). Rend le nombre de reprises.
    /// </summary>
    public async Task<int> BouclerLeDepartAsync(Func<CancellationToken, Task<bool>> reprendre, string journal, Func<int?>? liens,
        TimeSpan patience, CancellationToken ct)
    {
        var limite = DateTime.UtcNow + patience;
        var tours = 0;
        while (DateTime.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();
            if (UnInviteSeConnecte(LireLesLignes(journal).ToList()) || liens?.Invoke() > 0)
            {
                return tours;
            }

            if (!await reprendre(ct).ConfigureAwait(false))
            {
                return tours;
            }

            tours++;
            await Task.Delay(RythmeDeLaBoucle, ct).ConfigureAwait(false);
        }

        return tours;
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
    /// plus <paramref name="patience"/>. En secours, des liens au relais (<paramref name="liens"/>, voir
    /// <see cref="RetroBat.Api.Netplay.RetroArchInvite.LiensDesInvitesAsync"/>) assez nombreux pendant <see cref="LienQuiTient"/>
    /// valent une arrivee que le journal n'aurait pas dite. Rend le nombre d'invites vus.
    /// </summary>
    public async Task<int> AttendreLesInvitesAsync(string journal, int attendus, TimeSpan patience, Func<int?>? liens, CancellationToken ct)
    {
        var limite = DateTime.UtcNow + patience;
        DateTime? liesDepuis = null;
        while (true)
        {
            var vus = InvitesALeurPlace(LireLesLignes(journal)).Count;
            var lies = liens?.Invoke() ?? 0;
            liesDepuis = lies >= attendus ? liesDepuis ?? DateTime.UtcNow : null;
            if (vus < attendus && liesDepuis is { } depuis && DateTime.UtcNow - depuis >= LienQuiTient)
            {
                return Math.Max(vus, lies);
            }

            if (vus >= attendus || DateTime.UtcNow >= limite)
            {
                return vus;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rend quand le dernier invite est parti : un lien d'invite au relais vu (<paramref name="liens"/>), puis plus aucun
    /// deux releves de suite, a ce <paramref name="rythme"/>. C'est le reseau, que la langue de la borne ne change pas (banc
    /// du 27/10 : le lien tombe une seconde apres le depart). Un reseau qui n'a jamais montre de lien (adresse du relais
    /// differente, table illisible) ne ferme rien : seul le journal en anglais le dit alors. Jusque-la, attend ;
    /// l'annulation (fin de partie) l'arrete.
    /// </summary>
    public async Task AttendreLeDepartDesInvitesAsync(string journal, Func<int?>? liens, TimeSpan rythme, CancellationToken ct)
    {
        var vusAuJournal = false;
        var vusAuReseau = false;
        var sansLien = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var presents = InvitesALeurPlace(LireLesLignes(journal)).Count;
            var lies = liens?.Invoke();
            vusAuJournal |= presents > 0;
            vusAuReseau |= lies > 0;
            if (vusAuReseau && lies is { } nombre)
            {
                sansLien = nombre == 0 ? sansLien + 1 : 0;
                if (sansLien >= 2)
                {
                    return;
                }
            }
            else if (vusAuJournal && !vusAuReseau && presents == 0)
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
