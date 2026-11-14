using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE CONTROLE AVANT LE FILM (decision du user, 14/11) : plus aucune pause durable. Le 04/11, la borne du user, marquee
/// « modeste » pour 30 jours par le garde-fou de cadence, n'a plus rien filme ni diffuse pendant dix jours. Desormais, la
/// machine et la connexion se verifient sur l'instant, avant chaque film et avant chaque candidature au direct ; une partie
/// refusee ne retient jamais la suivante.
/// <list type="bullet">
/// <item>La machine (avant le film) : memoire presque epuisee, ou jeu deja ralenti une fois la partie stabilisee. Le
/// processeur ne retient jamais un film (14/11, 1.9.181) : mesure pendant le lancement (RetroArch, le Deck), il est
/// toujours au plus haut, et la borne de reference (i3-N305) joue normalement entre 80 et 100 %. Seule une vraie baisse
/// de cadence du jeu dit que la machine ne suit pas.</item>
/// <item>La connexion (avant la candidature au direct) : pas de reseau, ou le site trop lent a repondre. Le debit montant
/// n'est pas mesure par l'API : le site choisit le debit du passage (bitrate_kbps) et l'exe du direct s'y tient.</item>
/// </list>
/// Seuils prudents : ils n'arretent que les cas nets, une borne chargee mais jouable filme. Une cadence pas encore mesuree
/// ne retient rien : le film part, et le garde-fou de cadence (<see cref="GardeDeCapture"/>) coupe le film de la partie
/// si le jeu, une fois stabilise, tourne sous 90 % de sa cadence annoncee ou ralentit pendant.
/// </summary>
public static class ControleAvantFilm
{
    /// <summary>Memoire physique libre minimale (Mo) : en dessous, l'encodeur du film ferait echanger la machine sur disque.</summary>
    public const double MemoireLibreMinMo = 300;

    /// <summary>
    /// Le jeu ralenti une fois la partie stabilisee : cadence mesuree sous 90 % de la cadence annoncee par le coeur. Le
    /// garde-fou de la capture applique le meme seuil pendant le film (<see cref="GardeDeCapture"/>), en plus de ses 92 % de
    /// la meilleure cadence vue.
    /// </summary>
    public const double CadenceMinRatio = 0.90;

    /// <summary>En dessous, le jeu est en pause ou dans un menu : la cadence ne dit rien de la machine.</summary>
    public const double CadencePause = 20;

    /// <summary>Au-dela, l'avance rapide : rien a juger.</summary>
    public const double CadenceAvanceRapide = 75;

    /// <summary>Ouverture d'une connexion vers le site (ms) : au-dela, le direct partirait saccade.</summary>
    public const double LatenceMaxMs = 1500;

    /// <summary>
    /// Le temps de jeu avant de juger la cadence : les premieres secondes d'une partie (chargement du coeur, du Deck, premiere
    /// image) ne disent rien de la machine.
    /// </summary>
    public const double StabilisationS = 6;

    /// <summary>Duree de la mesure du processeur (journal seulement).</summary>
    public static readonly TimeSpan MesureDuProcesseur = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Pourquoi la machine ne filmera pas cette partie, ou null. Une mesure absente (null) ne retient rien : une cadence pas
    /// encore mesuree laisse partir le film. <paramref name="processeurPct"/> ne retient jamais rien (14/11, 1.9.181) : il
    /// n'est la que pour le journal. <paramref name="cadence"/> est celle du jeu stabilise (<see cref="StabilisationS"/>),
    /// jamais celle du lancement. Pur.
    /// </summary>
    public static string? RaisonMachine(double? processeurPct, double? memoireLibreMo, double? cadence, double? cadenceAnnoncee)
    {
        _ = processeurPct;
        if (memoireLibreMo is { } libre && libre < MemoireLibreMinMo)
            return "memoire presque pleine (" + Texte(libre) + " Mo libres, seuil " + Texte(MemoireLibreMinMo) + " Mo)";
        if (cadence is { } c && cadenceAnnoncee is { } annoncee && annoncee > 0
            && c >= CadencePause && c <= CadenceAvanceRapide && c < annoncee * CadenceMinRatio)
            return "jeu ralenti (" + Texte(c) + " images/s pour " + Texte(annoncee) + ")";
        return null;
    }

    /// <summary>Pourquoi la connexion ne porte pas un direct, ou null. Une latence inconnue ne retient rien. Pur.</summary>
    public static string? RaisonConnexion(bool reseauDisponible, double? latenceMs, bool siteJoint)
    {
        if (!reseauDisponible) return "pas de reseau";
        if (!siteJoint) return "site injoignable";
        if (latenceMs is { } ms && ms > LatenceMaxMs)
            return "site trop lent (" + Texte(ms) + " ms, seuil " + Texte(LatenceMaxMs) + " ms)";
        return null;
    }

    /// <summary>
    /// La cadence annoncee par le coeur, si elle peut servir de reference : entre la cadence de pause et l'avance rapide, et
    /// au moins celle des machines PAL (40 a 75 images/s) ; null sinon. Pur.
    /// </summary>
    public static double? CadenceDeReference(double? cadenceAnnoncee)
        => cadenceAnnoncee is { } a && a >= 40 && a <= CadenceAvanceRapide ? a : null;

    /// <summary>La charge du processeur pour le journal : « 99.8 % » ou « inconnue ». Pur.</summary>
    public static string TexteDuProcesseur(double? processeurPct)
        => processeurPct is { } p ? Texte(p) + " %" : "inconnue";

    /// <summary>La cadence mesuree entre deux releves de frames, ou null si elle ne se lit pas. Pur.</summary>
    public static double? Cadence(long? frameAvant, long? frameApres, double secondes)
        => frameAvant is { } a && frameApres is { } b && b > a && secondes >= 0.25 ? (b - a) / secondes : null;

    /// <summary>La charge du processeur (%) sur <paramref name="fenetre"/>, ou null hors Windows ou en echec.</summary>
    public static async Task<double?> ChargeDuProcesseurAsync(TimeSpan fenetre, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var inactif1, out var noyau1, out var utilisateur1)) return null;
        await Task.Delay(fenetre, ct).ConfigureAwait(false);
        if (!GetSystemTimes(out var inactif2, out var noyau2, out var utilisateur2)) return null;
        return Charge(inactif2 - inactif1, (noyau2 - noyau1) + (utilisateur2 - utilisateur1));
    }

    /// <summary>La charge (%) d'apres les temps du systeme : le temps du noyau compte deja le temps inactif. Pur.</summary>
    internal static double? Charge(long inactif, long total)
        => total <= 0 || inactif < 0 || inactif > total ? null : Math.Round(100.0 * (total - inactif) / total, 1);

    /// <summary>La memoire physique libre (Mo), ou null hors Windows ou en echec.</summary>
    public static double? MemoireLibreMo()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var etat = new EtatMemoire { Taille = (uint)Marshal.SizeOf<EtatMemoire>() };
            return GlobalMemoryStatusEx(ref etat) ? etat.PhysiqueLibre / (1024.0 * 1024.0) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// La connexion vers le site, sur l'instant : reseau present, et le temps d'ouvrir une connexion vers lui (3 s au plus).
    /// Une poignee de paquets, aucun envoi de donnees.
    /// </summary>
    public static async Task<string?> VerifierLaConnexionAsync(string adresseDuSite, CancellationToken ct)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return RaisonConnexion(false, null, false);
        if (!Uri.TryCreate(adresseDuSite, UriKind.Absolute, out var site)) return null;
        try
        {
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient();
            var chrono = Stopwatch.StartNew();
            await tcp.ConnectAsync(site.Host, site.Port, delai.Token).ConfigureAwait(false);
            return RaisonConnexion(true, chrono.Elapsed.TotalMilliseconds, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RaisonConnexion(true, null, false);
        }
        catch (SocketException)
        {
            return RaisonConnexion(true, null, false);
        }
    }

    private static string Texte(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct EtatMemoire
    {
        public uint Taille;
        public uint Charge;
        public ulong PhysiqueTotale;
        public ulong PhysiqueLibre;
        public ulong FichierTotal;
        public ulong FichierLibre;
        public ulong VirtuelleTotale;
        public ulong VirtuelleLibre;
        public ulong VirtuelleEtendueLibre;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref EtatMemoire etat);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long inactif, out long noyau, out long utilisateur);
}
